import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { NoticeService } from './notice.service';
import { ScoutBridgeService } from './scout-bridge';

const recorded: unknown[] = [];
const bridge = {
  noticeRecord: vi.fn(async (req: unknown) => {
    recorded.push(req);
  }),
};

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
    recorded.length = 0;
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
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

  it('only an error notice is recorded in the notification center; info and success notices are only shown', async () => {
    const notice = TestBed.inject(NoticeService);

    notice.show('Copied 3 lines', 'success');
    notice.show('Log cleared');
    notice.show('Could not save', 'error');
    await settle();

    expect(recorded).toEqual([{ message: 'Could not save', kind: 'error' }]);
  });

  it('an error notice still shows when main cannot record it', async () => {
    bridge.noticeRecord.mockRejectedValueOnce(new Error('bridge/not-available'));
    const notice = TestBed.inject(NoticeService);

    notice.show('Could not save', 'error');
    await settle();

    expect(notices()).toEqual([
      { text: 'Could not save', classes: ['sc-notice', 'sc-notice--error'] },
    ]);
  });

  it('a notice with an action shows its button; pressing it runs the action once and closes the notice', async () => {
    const notice = TestBed.inject(NoticeService);
    const retry = vi.fn();

    notice.show('Could not stop the Scout worker. Try again.', 'error', 8000, {
      label: 'Retry',
      run: retry,
    });
    await settle();
    const button = document.querySelector<HTMLButtonElement>(
      '.cdk-overlay-container .sc-notice button',
    );
    const label = button?.textContent?.trim();
    button?.click();
    await settle();

    expect({ label, runs: retry.mock.calls.length, open: notices() }).toEqual({
      label: 'Retry',
      runs: 1,
      open: [],
    });
  });

  it('a notice with an action is announced politely and leaves the focus where it was', async () => {
    const before = document.createElement('button');
    document.body.appendChild(before);
    before.focus();
    const notice = TestBed.inject(NoticeService);

    notice.show('Could not stop the Scout worker. Try again.', 'error', 8000, {
      label: 'Retry',
      run: () => undefined,
    });
    await settle();
    const live = document
      .querySelector('.cdk-overlay-container .sc-notice [aria-live]')
      ?.getAttribute('aria-live');
    const focused = document.activeElement === before;
    before.remove();

    expect({ live, focused }).toEqual({ live: 'polite', focused: true });
  });
});
