import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { CredentialDialogComponent } from './credential-dialog.component';

function create(): ComponentFixture<CredentialDialogComponent> {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(CredentialDialogComponent);
  fixture.componentInstance.featureId = 'github';
  fixture.componentInstance.displayName = 'GitHub';
  fixture.detectChanges();
  return fixture;
}

describe('CredentialDialogComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('disables submit when the textarea is empty', () => {
    const fixture = create();
    const submit = fixture.nativeElement.querySelector(
      '[data-test="credential-submit"]',
    ) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
  });

  it('emits submit with the typed value packed under "raw"', () => {
    const fixture = create();
    const emissions: Record<string, string>[] = [];
    fixture.componentInstance.submit.subscribe((v: Record<string, string>) => emissions.push(v));

    const textarea = fixture.nativeElement.querySelector(
      '[data-test="credential-textarea"]',
    ) as HTMLTextAreaElement;
    textarea.value = '{"token":"abc"}';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const submit = fixture.nativeElement.querySelector(
      '[data-test="credential-submit"]',
    ) as HTMLButtonElement;
    expect(submit.disabled).toBe(false);
    submit.click();

    expect(emissions).toEqual([{ raw: '{"token":"abc"}' }]);
  });

  it('emits cancel when the cancel button is clicked', () => {
    const fixture = create();
    let cancelled = 0;
    fixture.componentInstance.cancel.subscribe(() => cancelled++);

    const btn = fixture.nativeElement.querySelector(
      '[data-test="credential-cancel"]',
    ) as HTMLButtonElement;
    btn.click();

    expect(cancelled).toBe(1);
  });

  it('renders the display name in the title', () => {
    const fixture = create();
    const title = fixture.nativeElement.querySelector('[data-test="dialog-title"]') as HTMLElement;
    expect(title.textContent?.trim()).toBe('GitHub credentials');
  });
});
