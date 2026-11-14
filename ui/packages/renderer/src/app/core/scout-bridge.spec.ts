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

  it('the Docker methods hand each call to its preload method and its answer back unchanged', async () => {
    const calls: string[] = [];
    const answer = (name: string, value: unknown) => async () => {
      calls.push(name);
      return value;
    };
    const listeners: unknown[] = [];
    Object.defineProperty(window, 'scout', {
      configurable: true,
      value: {
        dockerState: answer('state', { state: 'running', version: '28.5.1' }),
        dockerImages: answer('images', { images: [] }),
        dockerPull: async (req: unknown) => {
          calls.push(`pull ${JSON.stringify(req)}`);
          return { error: null };
        },
        dockerWorkFolder: answer('folder', { path: '/w', bytes: 3, runs: 1 }),
        dockerClear: answer('clear', { refused: null, folder: { path: '/w', bytes: 0, runs: 0 } }),
        dockerRepositories: answer('repositories', { repositories: [] }),
        dockerPullImage: async (req: unknown) => {
          calls.push(`pullImage ${JSON.stringify(req)}`);
          return { error: null };
        },
        dockerLastRun: answer('lastRun', { run: null }),
        onDockerPullProgress: (listener: unknown) => {
          listeners.push(listener);
          return () => undefined;
        },
      },
    });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });

    try {
      const bridge = TestBed.inject(ScoutBridgeService);
      const listener = (): void => undefined;

      expect(await bridge.dockerState()).toEqual({ state: 'running', version: '28.5.1' });
      expect(await bridge.dockerImages()).toEqual({ images: [] });
      expect(await bridge.dockerPull({ stack: 'Go' })).toEqual({ error: null });
      expect(await bridge.dockerWorkFolder()).toEqual({ path: '/w', bytes: 3, runs: 1 });
      expect(await bridge.dockerClear()).toEqual({
        refused: null,
        folder: { path: '/w', bytes: 0, runs: 0 },
      });
      expect(await bridge.dockerPullImage({ reference: 'node:20@sha256:x' })).toEqual({
        error: null,
      });
      expect(await bridge.dockerRepositories()).toEqual({ repositories: [] });
      expect(await bridge.dockerLastRun()).toEqual({ run: null });
      bridge.onDockerPullProgress(listener);
      expect(calls).toEqual([
        'state',
        'images',
        'pull {"stack":"Go"}',
        'folder',
        'clear',
        'pullImage {"reference":"node:20@sha256:x"}',
        'repositories',
        'lastRun',
      ]);
      expect(listeners).toEqual([listener]);
    } finally {
      Reflect.deleteProperty(window, 'scout');
    }
  });
});
