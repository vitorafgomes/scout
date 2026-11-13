import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, Component, inject, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { confirmAction, type ConfirmDialogData } from './confirm-dialog.component';
import { ICONS } from './icon.component';
import { LAID_OUT } from './laid-out.testing';

@Component({
  selector: 'sc-confirm-host',
  standalone: true,
  template: `<button type="button" data-test="opener">Delete</button>`,
})
class HostComponent {
  readonly dialog = inject(MatDialog);
}

const DELETE: ConfirmDialogData = {
  title: 'Delete this meeting?',
  body: 'Its lines and questions are removed from this computer. It cannot be undone.',
  confirmLabel: 'Delete',
};

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

async function settle(): Promise<void> {
  for (let i = 0; i < 3; i += 1) await TestBed.inject(ApplicationRef).whenStable();
}

async function open(data: ConfirmDialogData = DELETE): Promise<{
  readonly result: Promise<boolean>;
  readonly opener: HTMLButtonElement;
}> {
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  const opener = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
    '[data-test="opener"]',
  )!;
  opener.focus();
  const result = confirmAction(fixture.componentInstance.dialog, data);
  await settle();
  return { result, opener };
}

function el(test: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`[data-test="${test}"]`);
  if (found === null) throw new Error(`no ${test} in the dialog`);
  return found;
}

describe("the shared confirmation is the artboards' dialog card", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
  });

  afterEach(async () => {
    TestBed.inject(MatDialog).closeAll();
    await settle();
  });

  it('it opens 440 px wide, 120 px from the top, on the sc-dialog card and the themed scrim, as an alert dialog named by its title and described by its body', async () => {
    await open();
    const pane = document.querySelector<HTMLElement>('.cdk-overlay-pane')!;
    const dialog = document.querySelector<HTMLElement>('[role="alertdialog"]')!;

    expect({
      panel: pane.classList.contains('sc-dialog'),
      backdrop: document
        .querySelector('.cdk-overlay-backdrop')
        ?.classList.contains('sc-dialog-backdrop'),
      width: pane.style.width,
      top: pane.style.marginTop,
      title: document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent,
      body: document.getElementById(dialog.getAttribute('aria-describedby') ?? '')?.textContent,
    }).toEqual({
      panel: true,
      backdrop: true,
      width: '440px',
      top: '120px',
      title: DELETE.title,
      body: DELETE.body,
    });
  });

  it('the card pads 20 px with its parts 12 px apart, the title 17 px at 600, the body in the second text colour at 1.5 lines, the buttons 36 px pills padded 16 px', async () => {
    await open();
    const dialog = document.querySelector<HTMLElement>('[role="alertdialog"]')!;
    const card = dialog.querySelector('.card')!;
    const actions = el('confirm-ok').parentElement!;

    expect({
      card: [declared(card, 'padding'), declared(card, 'gap')],
      title: [
        declared(dialog.querySelector('h2')!, 'font-size'),
        declared(dialog.querySelector('h2')!, 'font-weight'),
      ],
      body: [
        declared(dialog.querySelector('p')!, 'color'),
        declared(dialog.querySelector('p')!, 'line-height'),
      ],
      heights: [
        declared(actions, '--mat-button-filled-container-height'),
        declared(actions, '--mat-button-outlined-container-height'),
      ],
      sides: [
        declared(actions, '--mat-button-filled-horizontal-padding'),
        declared(actions, '--mat-button-outlined-horizontal-padding'),
      ],
    }).toEqual({
      card: ['20px', '12px'],
      title: ['17px', '600'],
      body: ['var(--sc-fg-2)', '1.5'],
      heights: ['36px', '36px'],
      sides: ['16px', '16px'],
    });
  });

  for (const [given, glyph] of [
    [undefined, ICONS.trash],
    ['trash', ICONS.trash],
    ['plug-off', ICONS['plug-off']],
  ] as const) {
    it(`the destructive button is white on the danger red with ${given ?? 'the default'} glyph before its label`, async () => {
      await open({ ...DELETE, ...(given === undefined ? {} : { confirmIcon: given }) });
      const ok = el('confirm-ok');

      expect({
        container: declared(ok, '--mat-button-filled-container-color'),
        label: declared(ok, '--mat-button-filled-label-text-color'),
        glyph: ok.querySelector('svg path')?.getAttribute('d'),
        text: ok.textContent?.trim(),
      }).toEqual({
        container: 'var(--sc-danger)',
        label: 'var(--sc-on-danger)',
        glyph,
        text: 'Delete',
      });
    });
  }

  it('Cancel has the focus as it opens, with no tint and no focus ring of its own: the global focus-visible ring is the one it shows', async () => {
    await open();
    const cancel = el('confirm-cancel');

    const source = readFileSync('src/app/core/confirm-dialog.component.ts', 'utf8');
    const css = source.slice(source.indexOf('  styles:'));

    expect({
      focused: document.activeElement === cancel,
      tint: declared(cancel, '--mat-button-outlined-focus-state-layer-opacity'),
      ownRing: /:focus|(?<![\w-])outline\s*:/.test(css),
    }).toEqual({ focused: true, tint: '0', ownRing: false });
  });

  it('its styles paint only with the theme tokens', () => {
    const source = readFileSync('src/app/core/confirm-dialog.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(
      Array.from(
        css.matchAll(
          /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
        ),
        (match) => match[0],
      ),
    ).toEqual([]);
  });

  for (const [answer, confirmed] of [
    ['confirm-ok', true],
    ['confirm-cancel', false],
  ] as const) {
    it(`${answer} closes it, resolves ${confirmed} and gives the focus back to what opened it`, async () => {
      const { result, opener } = await open();

      el(answer).click();
      await settle();

      expect({ confirmed: await result, focusBack: document.activeElement === opener }).toEqual({
        confirmed,
        focusBack: true,
      });
    });
  }

  it('Escape closes it as Cancel does: nothing is confirmed', async () => {
    const { result } = await open();

    document.querySelector('[role="alertdialog"]')!.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Escape',
        keyCode: 27,
        bubbles: true,
      } as KeyboardEventInit),
    );
    await settle();

    expect(await result).toBe(false);
  });

  it('a confirmation opened over another names its own title, not the first one', async () => {
    await open();
    await open({ ...DELETE, title: 'Disconnect Slack?' });
    const dialog = Array.from(document.querySelectorAll<HTMLElement>('[role="alertdialog"]')).at(
      -1,
    )!;

    expect(document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent).toBe(
      'Disconnect Slack?',
    );
  });
});
