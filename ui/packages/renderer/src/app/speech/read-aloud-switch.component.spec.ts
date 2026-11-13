import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ICONS } from '../core/icon.component';
import { ScoutBridgeService } from '../core/scout-bridge';
import { READ_ALOUD_STORAGE_KEY, ReadAloud } from './read-aloud';
import { ReadAloudSwitchComponent } from './read-aloud-switch.component';

async function render() {
  const bridge = {
    onLocalTurns: vi.fn(() => () => undefined),
    localGetTurns: vi.fn(async () => []),
    onLocalTranscript: vi.fn(() => () => undefined),
  };
  TestBed.configureTestingModule({
    imports: [ReadAloudSwitchComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ReadAloudSwitchComponent);
  await fixture.whenStable();
  const root = fixture.nativeElement as HTMLElement;
  const button = () => root.querySelector('[data-test="read-aloud-switch"]') as HTMLButtonElement;
  const iconPath = () => root.querySelector('sc-icon path')!.getAttribute('d');
  return { fixture, root, button, iconPath, readAloud: TestBed.inject(ReadAloud) };
}

describe('the "Read answers aloud" icon toggle', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('is a button named "Read answers aloud" with the same tooltip and no text label', async () => {
    const t = await render();

    expect(t.button().tagName).toBe('BUTTON');
    expect(t.button().getAttribute('aria-label')).toBe('Read answers aloud');
    expect(t.button().getAttribute('title')).toBe('Read answers aloud');
    expect(t.button().textContent?.trim()).toBe('');
  });

  it('is not pressed and shows the speaker-off icon at first, then pressed with the speaker after a click', async () => {
    const t = await render();
    expect(t.button().getAttribute('aria-pressed')).toBe('false');
    expect(t.iconPath()).toBe(ICONS['speaker-off']);

    t.button().click();
    await t.fixture.whenStable();

    expect(t.button().getAttribute('aria-pressed')).toBe('true');
    expect(t.iconPath()).toBe(ICONS.speaker);

    t.button().click();
    await t.fixture.whenStable();

    expect(t.button().getAttribute('aria-pressed')).toBe('false');
    expect(t.iconPath()).toBe(ICONS['speaker-off']);
  });

  it('keeps the choice in storage, on and off', async () => {
    const t = await render();

    t.button().click();
    await t.fixture.whenStable();
    expect(localStorage.getItem(READ_ALOUD_STORAGE_KEY)).toBe('on');

    t.button().click();
    await t.fixture.whenStable();
    expect(localStorage.getItem(READ_ALOUD_STORAGE_KEY)).toBe('off');
  });

  it('starts pressed when the stored choice is on', async () => {
    localStorage.setItem(READ_ALOUD_STORAGE_KEY, 'on');
    const t = await render();

    expect(t.button().getAttribute('aria-pressed')).toBe('true');
    expect(t.iconPath()).toBe(ICONS.speaker);
  });

  it('shows the reason the voice could not read in an alert beside the button', async () => {
    const error = signal<string | null>(null);
    TestBed.configureTestingModule({
      imports: [ReadAloudSwitchComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: ReadAloud,
          useValue: { enabled: signal(true), error, setEnabled: vi.fn() },
        },
      ],
    });
    const fixture = TestBed.createComponent(ReadAloudSwitchComponent);
    await fixture.whenStable();
    const alert = () => fixture.nativeElement.querySelector('[data-test="voice-error"]');
    expect(alert()).toBeNull();

    error.set('The voice is not installed.');
    await fixture.whenStable();

    expect(alert().textContent.trim()).toBe('The voice is not installed.');
    expect(alert().getAttribute('role')).toBe('alert');
  });
});
