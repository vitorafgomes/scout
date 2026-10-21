import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { SettingsComponent } from './settings.component';

const TIMEZONE_STORAGE_KEY = 'scout:settings.timezone';

interface FakeBridge {
  appGetVersion: ReturnType<typeof vi.fn>;
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
  vaultDelete: ReturnType<typeof vi.fn>;
  workerStop: ReturnType<typeof vi.fn>;
}

function makeBridge(): FakeBridge {
  return {
    appGetVersion: vi.fn(async () => ({ version: '1.2.3' })),
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: false })),
    vaultDelete: vi.fn(async () => ({ deleted: true })),
    workerStop: vi.fn(async () => ({ exitCode: 0 })),
  };
}

async function setup(): Promise<{
  fixture: ComponentFixture<SettingsComponent>;
  bridge: FakeBridge;
}> {
  const bridge = makeBridge();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: '', children: [] }]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(SettingsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, bridge };
}

describe('SettingsComponent — Time zone card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(TIMEZONE_STORAGE_KEY);
  });

  it('renders the machine-detected time zone', async () => {
    const { fixture } = await setup();
    const detected = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    const el = fixture.nativeElement.querySelector('[data-test="tz-detected"]') as HTMLElement;
    expect(el).not.toBeNull();
    expect(el.textContent?.trim()).toBe(detected);
  });

  it('auto-selects the machine-detected zone as the effective value on init', async () => {
    const { fixture } = await setup();
    const detected = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    const input = fixture.nativeElement.querySelector('[data-test="tz-input"]') as
      | HTMLSelectElement
      | HTMLInputElement;
    expect(input).not.toBeNull();
    if (input instanceof HTMLSelectElement) {
      // The <option> matching the machine zone must be the selected one, not the
      // first alphabetical entry — a [value] binding alone does not select it.
      const selected = Array.from(input.options).find((o) => o.selected);
      expect(selected?.value).toBe(detected);
    }
    expect(input.value).toBe(detected);
  });

  it('Save calls vaultStore with the settings.timezone key', async () => {
    const { fixture, bridge } = await setup();
    const saveBtn = fixture.nativeElement.querySelector(
      '[data-test="tz-save"]',
    ) as HTMLButtonElement;
    saveBtn.click();
    await fixture.whenStable();

    expect(bridge.vaultStore).toHaveBeenCalledTimes(1);
    expect(bridge.vaultStore).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'settings.timezone' }),
    );
  });

  it('Save persists the value to localStorage and shows the restart note', async () => {
    const { fixture } = await setup();
    const saveBtn = fixture.nativeElement.querySelector(
      '[data-test="tz-save"]',
    ) as HTMLButtonElement;
    saveBtn.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const detected = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    expect(window.localStorage.getItem(TIMEZONE_STORAGE_KEY)).toBe(detected);
    const saved = fixture.nativeElement.querySelector('[data-test="tz-saved"]');
    expect(saved).not.toBeNull();
  });

  it('Use machine default calls vaultDelete and clears the localStorage override', async () => {
    window.localStorage.setItem(TIMEZONE_STORAGE_KEY, 'Europe/London');
    const { fixture, bridge } = await setup();
    const resetBtn = fixture.nativeElement.querySelector(
      '[data-test="tz-reset"]',
    ) as HTMLButtonElement;
    resetBtn.click();
    await fixture.whenStable();

    expect(bridge.vaultDelete).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'settings.timezone' }),
    );
    expect(window.localStorage.getItem(TIMEZONE_STORAGE_KEY)).toBeNull();
  });
});
