import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ScoutError } from '@scout/shared';
import { describe, expect, it } from 'vitest';

import { SCOUT_BRIDGE_NOT_AVAILABLE_CODE, ScoutBridgeService } from './scout-bridge';

describe('ScoutBridgeService fake bridge', () => {
  it('rejects detectClients with a ScoutError when window.scout is not defined', async () => {
    // jsdom exposes window globally, but preload never ran in this environment.
    expect((globalThis as { window?: { scout?: unknown } }).window?.scout).toBeUndefined();

    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });

    const service = TestBed.inject(ScoutBridgeService);

    await expect(service.detectClients()).rejects.toBeInstanceOf(ScoutError);
    await expect(service.detectClients()).rejects.toMatchObject({
      code: SCOUT_BRIDGE_NOT_AVAILABLE_CODE,
    });
  });
});

describe('ScoutBridgeService over the preload bridge', () => {
  it('localListen hands the lane to the preload bridge', async () => {
    const asked: unknown[] = [];
    Object.defineProperty(window, 'scout', {
      configurable: true,
      value: {
        localListen: async (req?: unknown) => {
          asked.push(req);
          return { id: 'live-others' };
        },
      },
    });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });

    try {
      const answer = await TestBed.inject(ScoutBridgeService).localListen({ lane: 'others' });

      expect(answer).toEqual({ id: 'live-others' });
      expect(asked).toEqual([{ lane: 'others' }]);
    } finally {
      Reflect.deleteProperty(window, 'scout');
    }
  });
});
