import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { WizardFooterComponent } from './wizard-footer.component';

function render(inputs: {
  readonly showBack: boolean;
  readonly primary: boolean;
  readonly blockedReason: string | null;
}): ComponentFixture<WizardFooterComponent> {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(WizardFooterComponent);
  fixture.componentRef.setInput('showBack', inputs.showBack);
  fixture.componentRef.setInput('primary', inputs.primary);
  fixture.componentRef.setInput('blockedReason', inputs.blockedReason);
  fixture.componentRef.setInput('hint', 'A hint');
  fixture.detectChanges();
  return fixture;
}

function el(fixture: ComponentFixture<WizardFooterComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

describe('The setup footer as in the prototype: Back, the hint and Continue', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const rows: readonly {
    readonly showBack: boolean;
    readonly primary: boolean;
    readonly blockedReason: string | null;
    readonly look: 'btn--primary' | 'btn--secondary' | 'btn--blocked';
  }[] = [
    { showBack: true, primary: true, blockedReason: null, look: 'btn--primary' },
    { showBack: true, primary: false, blockedReason: null, look: 'btn--secondary' },
    { showBack: false, primary: true, blockedReason: null, look: 'btn--primary' },
    { showBack: true, primary: true, blockedReason: 'Save a token first.', look: 'btn--blocked' },
    { showBack: false, primary: false, blockedReason: 'Wait for it.', look: 'btn--blocked' },
  ];

  for (const row of rows) {
    const name = `Back ${row.showBack ? 'shown' : 'hidden'}, ${row.primary ? 'primary' : 'secondary'}, ${row.blockedReason === null ? 'can continue' : 'blocked'}`;

    it(`${name}: Continue is ${row.look}, the reason describes it only while blocked, and only an enabled Continue is sent`, () => {
      const fixture = render(row);
      const back = vi.fn();
      const next = vi.fn();
      fixture.componentInstance.back.subscribe(back);
      fixture.componentInstance.continue.subscribe(next);
      const button = el(fixture, 'wizard-continue') as HTMLButtonElement;

      button.click();
      el(fixture, 'wizard-back')?.click();

      expect({
        back: el(fixture, 'wizard-back')?.textContent?.trim() ?? null,
        hint: el(fixture, 'wizard-hint')?.textContent,
        label: button.textContent?.trim(),
        arrowLast: button
          .querySelector('.mdc-button__label')
          ?.lastElementChild?.tagName.toLowerCase(),
        look: [...button.classList].filter((c) => c.startsWith('btn--')),
        disabled: button.disabled,
        describedBy: button.getAttribute('aria-describedby'),
        reason: el(fixture, 'wizard-blocked')?.textContent ?? null,
        sent: [back.mock.calls.length, next.mock.calls.length],
      }).toEqual({
        back: row.showBack ? 'Back' : null,
        hint: 'A hint',
        label: 'Continue',
        arrowLast: 'sc-icon',
        look: [row.look],
        disabled: row.blockedReason !== null,
        describedBy: row.blockedReason === null ? null : 'wizard-blocked',
        reason: row.blockedReason,
        sent: [row.showBack ? 1 : 0, row.blockedReason === null ? 1 : 0],
      });
    });
  }

  it('Back is disabled while the step is busy, and a disabled Back sends nothing', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(WizardFooterComponent);
    fixture.componentRef.setInput('backDisabled', true);
    fixture.detectChanges();
    const back = vi.fn();
    fixture.componentInstance.back.subscribe(back);
    const button = el(fixture, 'wizard-back') as HTMLButtonElement;

    button.click();

    expect([button.disabled, back.mock.calls.length]).toEqual([true, 0]);
  });

  for (const [primary, blockedReason, next] of [
    [true, null, 'filled'],
    [false, null, 'outlined'],
    [true, 'Save a token first.', 'filled'],
    [false, 'Wait for it.', 'filled'],
  ] as const) {
    it(`Back and Continue are Material buttons: Back outlined, Continue ${next} when ${primary ? 'primary' : 'secondary'} and ${blockedReason === null ? 'free to continue' : 'blocked'}`, () => {
      const fixture = render({ showBack: true, primary, blockedReason });
      const appearance = (test: string): string[] =>
        ['mat-mdc-unelevated-button', 'mat-mdc-outlined-button'].filter((name) =>
          el(fixture, test)?.classList.contains(name),
        );

      expect({
        back: appearance('wizard-back'),
        next: appearance('wizard-continue'),
      }).toEqual({
        back: ['mat-mdc-outlined-button'],
        next: [next === 'filled' ? 'mat-mdc-unelevated-button' : 'mat-mdc-outlined-button'],
      });
    });
  }
});
