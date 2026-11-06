import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { beforeEach, describe, expect, it } from 'vitest';

import { NoticeService } from './notice.service';

function notices(): { text: string; classes: string[] }[] {
  return Array.from(
    document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
  ).map((el) => ({
    text: (el.textContent ?? '').trim(),
    classes: Array.from(el.classList).filter((c) => c.startsWith('sc-notice')),
  }));
}

async function settle(): Promise<void> {
  await TestBed.inject(ApplicationRef).whenStable();
}

describe('NoticeService', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
  });

  it('the snackbar opens from the Material overlay with the message and the kind', async () => {
    const notice = TestBed.inject(NoticeService);

    notice.show('Writes allowed for YouTube', 'success');
    await settle();

    expect(notices()).toEqual([
      { text: 'Writes allowed for YouTube', classes: ['sc-notice', 'sc-notice--success'] },
    ]);
  });

  it('a notice without a kind is an info notice', async () => {
    const notice = TestBed.inject(NoticeService);

    notice.show('Saved');
    await settle();

    expect(notices()).toEqual([{ text: 'Saved', classes: ['sc-notice', 'sc-notice--info'] }]);
  });

  it('dismiss closes the open notice', async () => {
    const notice = TestBed.inject(NoticeService);
    notice.show('Writes revoked for YouTube', 'info');
    await settle();
    const shownBefore = notices().length;

    notice.dismiss();
    await settle();

    expect(shownBefore).toBe(1);
    expect(notices()).toEqual([]);
  });
});
