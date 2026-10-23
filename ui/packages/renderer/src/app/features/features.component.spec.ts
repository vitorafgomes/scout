import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type FeatureDto,
  type GitHubStatus,
} from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { FeaturesComponent } from './features.component';

const github: FeatureDto = {
  id: 'github',
  displayName: 'GitHub',
  description: 'GitHub integration',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: true,
  status: 'enabled',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: null,
  setupSteps: null,
};

const kubernetes: FeatureDto = {
  id: 'kubernetes',
  displayName: 'Kubernetes',
  description: 'Kubernetes integration',
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

const notConnected: GitHubStatus = {
  configured: true,
  connected: false,
  method: null,
  login: null,
  warning: null,
  installed: false,
  installUrl: 'https://github.com/apps/scout-chatops/installations/new',
  pending: null,
  error: null,
};

function asRendererReceives(channel: string, err: Error): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(serializeError(err))}`,
  );
}

async function setup(): Promise<{
  fixture: ComponentFixture<FeaturesComponent>;
  bridge: {
    featuresList: ReturnType<typeof vi.fn>;
    featureEnable: ReturnType<typeof vi.fn>;
    featureDisable: ReturnType<typeof vi.fn>;
    featureSetCredentials: ReturnType<typeof vi.fn>;
    featureSetup: ReturnType<typeof vi.fn>;
    featureDeleteCredentials: ReturnType<typeof vi.fn>;
    githubStatus: ReturnType<typeof vi.fn>;
  };
}> {
  const bridge = {
    featuresList: vi.fn(async () => ({ features: [github, kubernetes] })),
    featureEnable: vi.fn(async () => github),
    featureDisable: vi.fn(async () => ({ ...github, enabled: false, status: 'disabled' as const })),
    featureSetCredentials: vi.fn(async () => github),
    featureSetup: vi.fn(async () => github),
    featureDeleteCredentials: vi.fn(async () => kubernetes),
    githubStatus: vi.fn(async () => notConnected),
  };

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });

  const fixture = TestBed.createComponent(FeaturesComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge };
}

describe('FeaturesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders one card per feature returned by the bridge', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="card-github"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="card-kubernetes"]')).not.toBeNull();
  });

  it('shows the GitHub and Kubernetes features in the grid with the honest notes under it', async () => {
    const { fixture } = await setup();

    const grid = fixture.nativeElement.querySelector('[data-test="features-grid"]') as HTMLElement;
    const notes = fixture.nativeElement.querySelector(
      '[data-test="features-notes"]',
    ) as HTMLElement;
    const lede = fixture.nativeElement.querySelector('.lede') as HTMLElement;

    expect(grid.textContent).toContain('GitHub');
    expect(grid.textContent).toContain('Kubernetes');
    expect(notes.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'GitHub is used by the chat bot once connected. Other features cannot be turned on yet; their credentials are kept only while Scout is open.',
    );
    expect(lede.textContent).not.toContain('keyring');
  });

  it('a card says turning the feature on is not available yet when the control plane does not implement it', async () => {
    const bridge = {
      featuresList: vi.fn(async () => ({
        features: [{ ...kubernetes, hasCredentials: true, status: 'disabled' as const }],
      })),
      featureEnable: vi.fn(async () => {
        throw asRendererReceives('scout:features.enable', new ControlPlaneNotImplementedError());
      }),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="toggle-kubernetes"]') as HTMLInputElement
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const err = fixture.nativeElement.querySelector('[data-test="err-kubernetes"]') as HTMLElement;
    expect(err.textContent?.trim()).toBe('Turning this feature on is not available yet.');
  });

  it('opens the credential dialog when a card requests it', async () => {
    const { fixture } = await setup();

    const cta = fixture.nativeElement.querySelector(
      '[data-test="cta-kubernetes"]',
    ) as HTMLButtonElement;
    cta.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).not.toBeNull();
  });

  it('forwards dialog submit to store.setup and closes the dialog', async () => {
    const { fixture, bridge } = await setup();

    (
      fixture.nativeElement.querySelector('[data-test="cta-kubernetes"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const ta = fixture.nativeElement.querySelector(
      '[data-test="credential-textarea"]',
    ) as HTMLTextAreaElement;
    ta.value = '{"token":"abc"}';
    ta.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="credential-submit"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.featureSetup).toHaveBeenCalledWith({
      id: 'kubernetes',
      credentials: { raw: '{"token":"abc"}' },
      settings: {},
    });
    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).toBeNull();
  });

  it('forwards toggle event to store.toggle', async () => {
    const enabledKubernetes = {
      ...kubernetes,
      hasCredentials: true,
      enabled: true,
      status: 'enabled' as const,
    };
    const bridge = {
      featuresList: vi.fn(async () => ({ features: [github, enabledKubernetes] })),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(async () => ({ ...enabledKubernetes, enabled: false })),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-test="toggle-kubernetes"]') as HTMLInputElement
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(bridge.featureDisable).toHaveBeenCalledWith({ id: 'kubernetes' });
  });

  it('says the control plane is not running and hides the grid when it is unavailable', async () => {
    const unavailableErr = asRendererReceives(
      'scout:features.list',
      new ControlPlaneUnavailableError('socket did not appear'),
    );
    const bridge = {
      featuresList: vi.fn(async () => {
        throw unavailableErr;
      }),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
    };

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });

    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-test="backend-unavailable"]',
    ) as HTMLElement;
    expect(notice.textContent?.trim()).toBe(
      'The Scout control plane is not running, so features cannot be shown.',
    );
    expect(fixture.nativeElement.querySelector('[data-test="features-grid"]')).toBeNull();
  });
});
