import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { FeatureDto } from '@scout/shared';

import { FeaturesStore } from './features.store';
import { ScoutBridgeService } from './scout-bridge';

interface BridgeStub {
  featuresList: ReturnType<typeof vi.fn>;
  featureEnable: ReturnType<typeof vi.fn>;
  featureDisable: ReturnType<typeof vi.fn>;
  featureSetCredentials: ReturnType<typeof vi.fn>;
  featureSetup: ReturnType<typeof vi.fn>;
  featureDeleteCredentials: ReturnType<typeof vi.fn>;
}

function makeBridge(overrides: Partial<BridgeStub> = {}): BridgeStub {
  return {
    featuresList: vi.fn(async () => ({ features: [] as FeatureDto[] })),
    featureEnable: vi.fn(),
    featureDisable: vi.fn(),
    featureSetCredentials: vi.fn(),
    featureSetup: vi.fn(),
    featureDeleteCredentials: vi.fn(),
    ...overrides,
  };
}

function provide(bridge: BridgeStub): FeaturesStore {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  return TestBed.inject(FeaturesStore);
}

const githubDisabled: FeatureDto = {
  id: 'github',
  displayName: 'GitHub',
  description: 'GitHub integration',
  requiresCredentials: true,
  hasCredentials: false,
  enabled: false,
  status: 'needs-credentials',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
};

const githubEnabled: FeatureDto = {
  ...githubDisabled,
  hasCredentials: true,
  enabled: true,
  status: 'enabled',
};

describe('FeaturesStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('load populates features from the bridge', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubDisabled] })),
    });
    const store = provide(bridge);

    await store.load();

    expect(bridge.featuresList).toHaveBeenCalledTimes(1);
    expect(store.features().length).toBe(1);
    expect(store.features()[0]!.id).toBe('github');
    expect(store.features()[0]!.busy).toBe(false);
  });

  it('toggle flips enabled optimistically and confirms with bridge response', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubDisabled] })),
      featureEnable: vi.fn(async () => githubEnabled),
    });
    const store = provide(bridge);
    await store.load();

    await store.toggle('github');

    expect(bridge.featureEnable).toHaveBeenCalledWith({ id: 'github' });
    const view = store.features()[0]!;
    expect(view.enabled).toBe(true);
    expect(view.status).toBe('enabled');
    expect(view.busy).toBe(false);
  });

  it('toggle rolls back on error and sets status="error"', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubDisabled] })),
      featureEnable: vi.fn(async () => {
        throw new Error('boom');
      }),
    });
    const store = provide(bridge);
    await store.load();

    await store.toggle('github');

    const view = store.features()[0]!;
    expect(view.enabled).toBe(false); // rolled back
    expect(view.status).toBe('error');
    expect(view.lastError).toBe('boom');
  });

  it('storeCredentials replaces the feature with bridge response (hasCredentials true)', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubDisabled] })),
      featureSetCredentials: vi.fn(async () => ({
        ...githubDisabled,
        hasCredentials: true,
        status: 'disabled' as const,
      })),
    });
    const store = provide(bridge);
    await store.load();

    await store.storeCredentials('github', { raw: '{}' });

    expect(bridge.featureSetCredentials).toHaveBeenCalledWith({
      id: 'github',
      body: { raw: '{}' },
    });
    expect(store.features()[0]!.hasCredentials).toBe(true);
  });

  it('setup forwards credentials and settings to the bridge and replaces the feature', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubDisabled] })),
      featureSetup: vi.fn(async () => ({
        ...githubDisabled,
        hasCredentials: true,
        status: 'disabled' as const,
      })),
    });
    const store = provide(bridge);
    await store.load();

    await store.setup(
      'github',
      { pat: 'ghp_abc' },
      { default_repo_url: 'https://github.com/acme/repo' },
    );

    expect(bridge.featureSetup).toHaveBeenCalledWith({
      id: 'github',
      credentials: { pat: 'ghp_abc' },
      settings: { default_repo_url: 'https://github.com/acme/repo' },
    });
    expect(store.features()[0]!.hasCredentials).toBe(true);
  });

  it('clearCredentials resets the feature to needs-credentials when bridge says so', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => ({ features: [githubEnabled] })),
      featureDeleteCredentials: vi.fn(async () => githubDisabled),
    });
    const store = provide(bridge);
    await store.load();

    await store.clearCredentials('github');

    expect(bridge.featureDeleteCredentials).toHaveBeenCalledWith({ id: 'github' });
    const view = store.features()[0]!;
    expect(view.hasCredentials).toBe(false);
    expect(view.status).toBe('needs-credentials');
  });

  it('load swallows errors and surfaces lastError signal', async () => {
    const bridge = makeBridge({
      featuresList: vi.fn(async () => {
        throw new Error('list-failed');
      }),
    });
    const store = provide(bridge);

    await store.load();

    expect(store.lastError()).toBe('list-failed');
    expect(store.features()).toEqual([]);
  });
});
