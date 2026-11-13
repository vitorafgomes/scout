import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ThemeChoice,
  type ThemeState,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from './scout-bridge';
import { ThemeStore } from './theme.store';

function fakeBridge(answer: ThemeState | Error) {
  const listeners: ((state: ThemeState) => void)[] = [];
  let current = answer;
  return {
    listeners,
    push: (state: ThemeState) => listeners.forEach((listener) => listener(state)),
    refuseNext: false,
    themeGet: vi.fn(async () => {
      if (current instanceof Error) throw current;
      return current;
    }),
    themeSet: vi.fn(async function (this: { refuseNext: boolean }, choice: ThemeChoice) {
      if (this.refuseNext) {
        const serialized = serializeError(
          new ScoutError('theme/invalid-choice', 'Scout could not use that theme.'),
        );
        throw new Error(encodeIpcErrorMessage(serialized));
      }
      current = { choice, resolved: choice === 'light' ? 'light' : 'dark' };
      return current;
    }),
    onTheme: vi.fn((listener: (state: ThemeState) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
  };
}

function store(bridge: ReturnType<typeof fakeBridge>): ThemeStore {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  return TestBed.inject(ThemeStore);
}

const painted = (): string | null => document.documentElement.getAttribute('data-theme');

describe('ThemeStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.documentElement.removeAttribute('data-theme');
  });

  it('paints nothing and knows no choice before it starts, so the stylesheet follows the system', () => {
    const theme = store(fakeBridge({ choice: 'dark', resolved: 'dark' }));

    expect(theme.choice()).toBeNull();
    expect(painted()).toBeNull();
  });

  for (const answer of [
    { choice: 'light', resolved: 'light' },
    { choice: 'dark', resolved: 'dark' },
    { choice: 'system', resolved: 'light' },
    { choice: 'system', resolved: 'dark' },
  ] as const) {
    it(`main's answer ${answer.choice} paints data-theme ${answer.resolved} on html`, async () => {
      const theme = store(fakeBridge(answer));

      theme.start();
      await Promise.resolve();
      await Promise.resolve();

      expect(theme.choice()).toBe(answer.choice);
      expect(painted()).toBe(answer.resolved);
    });
  }

  it('a push from main, such as the system turning light, repaints the window', async () => {
    const bridge = fakeBridge({ choice: 'system', resolved: 'dark' });
    const theme = store(bridge);
    theme.start();
    await Promise.resolve();

    bridge.push({ choice: 'system', resolved: 'light' });

    expect(painted()).toBe('light');
    expect(theme.choice()).toBe('system');
  });

  it('starting twice follows main once', () => {
    const bridge = fakeBridge({ choice: 'dark', resolved: 'dark' });
    const theme = store(bridge);

    theme.start();
    theme.start();

    expect(bridge.listeners.length).toBe(1);
    expect(bridge.themeGet).toHaveBeenCalledTimes(1);
  });

  it('main not answering leaves the window to the system preference', async () => {
    const theme = store(fakeBridge(new Error('bridge/not-available')));

    theme.start();
    await Promise.resolve();
    await Promise.resolve();

    expect(theme.choice()).toBeNull();
    expect(painted()).toBeNull();
  });

  it("choosing a theme paints main's answer, not the click", async () => {
    const theme = store(fakeBridge({ choice: 'dark', resolved: 'dark' }));

    await theme.set('light');

    expect(theme.choice()).toBe('light');
    expect(painted()).toBe('light');
    expect(theme.error()).toBeNull();
  });

  it('main refusing a choice keeps the current theme and says why, and the next good choice clears it', async () => {
    const bridge = fakeBridge({ choice: 'dark', resolved: 'dark' });
    const theme = store(bridge);
    theme.start();
    await Promise.resolve();
    await Promise.resolve();

    bridge.refuseNext = true;
    await theme.set('light');
    const refused = { choice: theme.choice(), painted: painted(), error: theme.error() };
    bridge.refuseNext = false;
    await theme.set('light');

    expect(refused).toEqual({
      choice: 'dark',
      painted: 'dark',
      error: 'Scout could not use that theme.',
    });
    expect(theme.error()).toBeNull();
    expect(painted()).toBe('light');
  });
});
