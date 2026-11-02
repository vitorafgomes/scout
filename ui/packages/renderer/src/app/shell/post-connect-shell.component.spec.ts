import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Component } from '@angular/core';
import { beforeEach, describe, expect, it } from 'vitest';

import type { UpdateNotice } from '@scout/shared';

import { MenuPreferences } from '../core/menu-preferences';
import { ScoutBridgeService } from '../core/scout-bridge';
import { PostConnectShellComponent } from './post-connect-shell.component';

@Component({ selector: 'sc-stub-llm', standalone: true, template: 'llm-page' })
class StubLlm {}
@Component({ selector: 'sc-stub-logs', standalone: true, template: 'logs-page' })
class StubLogs {}
@Component({ selector: 'sc-stub-settings', standalone: true, template: 'settings-page' })
class StubSettings {}

function updateBridge(notice: UpdateNotice | null) {
  return {
    updateGetState: async () => ({
      checkEnabled: true,
      lastCheckAt: null,
      lastResult: null,
      notice,
      packaged: true,
    }),
    onUpdateNotice: () => () => undefined,
    onUpdateProgress: () => () => undefined,
  } as unknown as ScoutBridgeService;
}

async function setup(bridge?: ScoutBridgeService): Promise<{
  fixture: ComponentFixture<PostConnectShellComponent>;
  router: Router;
}> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      ...(bridge === undefined ? [] : [{ provide: ScoutBridgeService, useValue: bridge }]),
      provideRouter([
        { path: 'chat', component: StubLlm },
        { path: 'llm', component: StubLlm },
        { path: 'logs', component: StubLogs },
        { path: 'settings', component: StubSettings },
      ]),
    ],
  });
  const fixture = TestBed.createComponent(PostConnectShellComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, router: TestBed.inject(Router) };
}

describe('PostConnectShellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('shows no update bar while main knows no newer version', async () => {
    const { fixture } = await setup(updateBridge(null));

    expect(fixture.nativeElement.querySelector('[data-test="update-notice"]')).toBeNull();
  });

  it('shows the update bar above the page while a newer version is known', async () => {
    const { fixture } = await setup(
      updateBridge({
        currentVersion: '0.1.0',
        latestVersion: '0.2.0',
        notes: '',
        releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        downloadUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
        publishedAt: '2026-11-01T18:00:00Z',
        canInstall: false,
      }),
    );

    const bar = fixture.nativeElement.querySelector('[data-test="update-notice"]') as HTMLElement;
    expect(bar).not.toBeNull();
    expect(bar.textContent).toContain('Scout 0.2.0 is available. You have 0.1.0.');
    const main = fixture.nativeElement.querySelector('main.main') as HTMLElement;
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(main.contains(bar)).toBe(false);
  });

  it('shows Logs in the menu while "Show Logs in the menu" is on', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="nav-logs"]')).not.toBeNull();
  });

  it('drops Logs from the menu once the owner turned "Show Logs in the menu" off', async () => {
    localStorage.setItem('scout:show-logs', '0');
    const { fixture } = await setup();
    expect(TestBed.inject(MenuPreferences).showLogs()).toBe(false);

    const labels = Array.from(fixture.nativeElement.querySelectorAll('.nav-label')).map((el) =>
      ((el as HTMLElement).textContent ?? '').trim(),
    );
    expect(labels).toEqual(['Chat', 'AI Sources', 'Models', 'Features', 'Review', 'Settings']);
  });

  it('renders Chat first, then AI Sources, Models, Features, Review, Logs, Settings, with no Status entry', async () => {
    const { fixture } = await setup();

    const links = fixture.nativeElement.querySelectorAll('.nav-link');
    expect(links.length).toBe(7);
    const labels = Array.from(links).map((el) =>
      ((el as HTMLElement).querySelector('.nav-label')?.textContent ?? '').trim(),
    );
    expect(labels).toEqual([
      'Chat',
      'AI Sources',
      'Models',
      'Features',
      'Review',
      'Logs',
      'Settings',
    ]);
  });

  it('exposes data-test attributes for every nav item', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="nav-chat"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-sources"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-models"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-features"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="nav-review"]')?.getAttribute('href'),
    ).toBe('/review');
    expect(fixture.nativeElement.querySelector('[data-test="nav-status"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-logs"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="nav-settings"]')).not.toBeNull();
  });

  it('marks the matching nav item as active when navigated', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/chat');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active).not.toBeNull();
    expect(active?.getAttribute('data-test')).toBe('nav-chat');
  });

  it('switches the active marker when route changes', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/logs');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    let active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active?.getAttribute('data-test')).toBe('nav-logs');

    await router.navigateByUrl('/settings');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    active = fixture.nativeElement.querySelector('.nav-link--active');
    expect(active?.getAttribute('data-test')).toBe('nav-settings');
  });

  it('renders the Scout brand', async () => {
    const { fixture } = await setup();
    const brand = fixture.nativeElement.querySelector('.brand');
    expect(brand?.textContent?.trim()).toBe('Scout');
  });
});
