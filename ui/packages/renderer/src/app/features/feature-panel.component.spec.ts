import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { readFileSync } from 'node:fs';
import { beforeEach, describe, expect, it } from 'vitest';

import { LAID_OUT } from '../core/laid-out.testing';

import { FeaturePanelComponent } from './feature-panel.component';

@Component({
  standalone: true,
  imports: [FeaturePanelComponent],
  template: `
    <button type="button" data-test="outside">Outside</button>
    <sc-feature-panel
      [open]="open()"
      logo="github"
      name="GitHub"
      [chip]="{ label: 'Connected', tone: 'ok' }"
      description="Query repositories from chat."
      (closed)="closes = closes + 1"
    >
      <button type="button" data-test="inside">Inside</button>
    </sc-feature-panel>
  `,
})
class HostComponent {
  readonly open = signal(false);
  closes = 0;
}

async function host(open: boolean): Promise<ComponentFixture<HostComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.componentInstance.open.set(open);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

function q(fixture: ComponentFixture<HostComponent>, test: string): HTMLElement {
  const el = fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
  if (el === null) throw new Error(`${test} is not on the screen`);
  return el;
}

describe('the Features side panel', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('closed, neither the panel nor its scrim shows', async () => {
    const fixture = await host(false);

    expect([q(fixture, 'feature-panel').hidden, q(fixture, 'feature-panel-scrim').hidden]).toEqual([
      true,
      true,
    ]);
  });

  it('open, it is a modal dialog named by its title, with the feature mark, the chip, the description and what it holds', async () => {
    const fixture = await host(true);
    const panel = q(fixture, 'feature-panel');

    expect({
      hidden: panel.hidden,
      scrim: q(fixture, 'feature-panel-scrim').hidden,
      role: panel.getAttribute('role'),
      modal: panel.getAttribute('aria-modal'),
      title: document
        .getElementById(panel.getAttribute('aria-labelledby') ?? '')
        ?.textContent?.trim(),
      desc: document
        .getElementById(panel.getAttribute('aria-describedby') ?? '')
        ?.textContent?.trim(),
      chip: q(fixture, 'feature-panel-chip').textContent?.trim(),
      logo: panel.querySelector('header [data-test="feature-logo-github"]') !== null,
      holds: panel.contains(q(fixture, 'inside')),
    }).toEqual({
      hidden: false,
      scrim: false,
      role: 'dialog',
      modal: 'true',
      title: 'GitHub',
      desc: 'Query repositories from chat.',
      chip: 'Connected',
      logo: true,
      holds: true,
    });
  });

  it('opening moves the focus to its close button, which names the feature and Esc', async () => {
    const fixture = await host(false);
    q(fixture, 'outside').focus();

    fixture.componentInstance.open.set(true);
    await TestBed.inject(ApplicationRef).whenStable();
    const close = q(fixture, 'feature-panel-close');

    expect(document.activeElement).toBe(close);
    expect([close.getAttribute('aria-label'), close.getAttribute('title')]).toEqual([
      'Close the GitHub details',
      'Close (Esc)',
    ]);
  });

  it('the close button, Esc inside the panel and a click on the scrim each ask to close it', async () => {
    const fixture = await host(true);

    q(fixture, 'feature-panel-close').click();
    q(fixture, 'inside').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    q(fixture, 'feature-panel-scrim').click();

    expect(fixture.componentInstance.closes).toBe(3);
  });

  it('Esc outside the panel, as in a confirmation it opened, does not close it', async () => {
    const fixture = await host(true);

    q(fixture, 'outside').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );

    expect(fixture.componentInstance.closes).toBe(0);
  });

  it('holds the focus: tabbing past its last control comes back to its first', async () => {
    const fixture = await host(true);
    const panel = q(fixture, 'feature-panel');
    const anchors = [...fixture.nativeElement.querySelectorAll('.cdk-focus-trap-anchor')];
    const after = anchors.find(
      (a) => panel.compareDocumentPosition(a) & Node.DOCUMENT_POSITION_FOLLOWING,
    ) as HTMLElement | undefined;
    q(fixture, 'inside').focus();

    after?.focus();

    expect(after).toBeDefined();
    expect(document.activeElement).toBe(q(fixture, 'feature-panel-close'));
  });

  it('slides in from the right, and not at all when the system asks for reduced motion', () => {
    const source = readFileSync('src/app/features/feature-panel.component.ts', 'utf8');

    expect(source).toMatch(/\.panel \{[^}]*animation: sc-panel-in/);
    expect(source).toMatch(/@keyframes sc-panel-in \{\s*from \{\s*transform: translateX\(100%\)/);
    expect(source).toMatch(
      /@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.panel \{\s*animation: none;/,
    );
  });
});
