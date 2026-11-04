import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneNotImplementedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type FeatureDto,
  type FeatureId,
  type GitHubStatus,
  type KubernetesStatus,
  type YouTubeStatus,
} from '@scout/shared';

import { WRITE_CONFIRMATION_TEXT } from './write-confirmation-dialog.component';

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
  writeClass: 'none',
  granted: false,
  grantedAt: null,
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
  writeClass: 'none',
  granted: false,
  grantedAt: null,
};

// A feature whose card still uses the control-plane toggle and credential dialog; both
// catalog features now have a main-owned connection instead.
const controlPlaneOnly: FeatureDto = {
  ...kubernetes,
  id: 'other' as FeatureId,
  displayName: 'Other',
  description: 'Other integration',
};

const kubernetesDisconnected: KubernetesStatus = { path: null, contexts: [], connected: false };

const kubernetesBridge = {
  accessGet: vi.fn(async () => ({ userIds: [] })),
  kubernetesStatus: vi.fn(async () => kubernetesDisconnected),
  kubernetesDetect: vi.fn(async () => ({ path: null, contexts: [], error: null })),
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
    onFeaturesChanged: vi.fn(() => () => undefined),
    featuresList: vi.fn(async () => ({ features: [github, kubernetes, controlPlaneOnly] })),
    featureEnable: vi.fn(async () => github),
    featureDisable: vi.fn(async () => ({ ...github, enabled: false, status: 'disabled' as const })),
    featureSetCredentials: vi.fn(async () => github),
    featureSetup: vi.fn(async () => github),
    featureDeleteCredentials: vi.fn(async () => controlPlaneOnly),
    githubStatus: vi.fn(async () => notConnected),
    ...kubernetesBridge,
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
      'GitHub, Kubernetes and YouTube are used by the chat bot once connected. A feature that writes lists its write permissions on its card and writes only after you allow them; actions that cannot be undone wait for you on the Review screen.',
    );
    expect(lede.textContent).not.toContain('keyring');
  });

  it('the YouTube card shows the connection instead of the control-plane toggle', async () => {
    const youtube: FeatureDto = {
      ...kubernetes,
      id: 'youtube',
      displayName: 'YouTube',
      description: 'YouTube integration',
    };
    const youtubeStatus: YouTubeStatus = {
      configured: true,
      connected: true,
      method: 'oauth',
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
      pending: false,
      error: null,
      clientConfigPath: '/user-data/google-oauth-client.json',
      source: 'file',
      scopes: [],
    };
    const bridge = {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => ({ features: [youtube] })),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      youtubeStatus: vi.fn(async () => youtubeStatus),
      accessGet: vi.fn(async () => ({ userIds: ['111'] })),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    const card = fixture.nativeElement.querySelector('[data-test="card-youtube"]') as HTMLElement;

    expect(card.querySelector('[data-test="youtube-connect-section"]')).not.toBeNull();
    expect(card.querySelector('[data-test="badge-youtube"]')?.textContent?.trim()).toBe(
      'Connected',
    );
    expect(card.querySelector('[data-test="toggle-youtube"]')).toBeNull();
    expect(card.querySelector('[data-test="cta-youtube"]')).toBeNull();
    expect(card.textContent).not.toContain('Needs credentials');
  });

  it('a card says turning the feature on is not available yet when the control plane does not implement it', async () => {
    const bridge = {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => ({
        features: [{ ...controlPlaneOnly, hasCredentials: true, status: 'disabled' as const }],
      })),
      featureEnable: vi.fn(async () => {
        throw asRendererReceives('scout:features.enable', new ControlPlaneNotImplementedError());
      }),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
      ...kubernetesBridge,
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

    (fixture.nativeElement.querySelector('[data-test="toggle-other"]') as HTMLInputElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const err = fixture.nativeElement.querySelector('[data-test="err-other"]') as HTMLElement;
    expect(err.textContent?.trim()).toBe('Turning this feature on is not available yet.');
  });

  it('opens the credential dialog when a card requests it', async () => {
    const { fixture } = await setup();

    const cta = fixture.nativeElement.querySelector('[data-test="cta-other"]') as HTMLButtonElement;
    cta.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).not.toBeNull();
  });

  it('forwards dialog submit to store.setup and closes the dialog', async () => {
    const { fixture, bridge } = await setup();

    (fixture.nativeElement.querySelector('[data-test="cta-other"]') as HTMLButtonElement).click();
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
      id: 'other',
      credentials: { raw: '{"token":"abc"}' },
      settings: {},
    });
    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).toBeNull();
  });

  it('forwards toggle event to store.toggle', async () => {
    const enabledOther = {
      ...controlPlaneOnly,
      hasCredentials: true,
      enabled: true,
      status: 'enabled' as const,
    };
    const bridge = {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => ({ features: [github, enabledOther] })),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(async () => ({ ...enabledOther, enabled: false })),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
      ...kubernetesBridge,
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

    (fixture.nativeElement.querySelector('[data-test="toggle-other"]') as HTMLInputElement).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(bridge.featureDisable).toHaveBeenCalledWith({ id: 'other' });
  });

  it('says the control plane is not running and hides the grid when it is unavailable', async () => {
    const unavailableErr = asRendererReceives(
      'scout:features.list',
      new ControlPlaneUnavailableError('socket did not appear'),
    );
    const bridge = {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => {
        throw unavailableErr;
      }),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      githubStatus: vi.fn(async () => notConnected),
      ...kubernetesBridge,
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

  describe('write permissions', () => {
    const writeRows = [
      { scope: 'youtube.readonly', actions: ['Read the channel'], access: 'read' as const },
      { scope: 'drafts', actions: ['Save scripts'], access: 'write' as const },
      { scope: 'youtube.upload', actions: ['Upload a video'], access: 'irreversible' as const },
    ];
    const writer: FeatureDto = {
      ...controlPlaneOnly,
      hasCredentials: true,
      status: 'disabled',
      scopeMatrix: writeRows,
      writeClass: 'executive',
    };
    const youtube: FeatureDto = {
      ...kubernetes,
      id: 'youtube',
      displayName: 'YouTube',
      hasCredentials: true,
      status: 'disabled',
      scopeMatrix: writeRows,
      writeClass: 'executive',
    };
    const youtubeConnected: YouTubeStatus = {
      configured: true,
      connected: true,
      method: 'oauth',
      channelId: 'UCscoutTestChannel',
      channelTitle: 'Scout Test Channel',
      pending: false,
      error: null,
      clientConfigPath: '/user-data/google-oauth-client.json',
      source: 'file',
      scopes: [],
    };

    async function mount(features: FeatureDto[], overrides: Record<string, unknown> = {}) {
      const bridge = {
        onFeaturesChanged: vi.fn(() => () => undefined),
        featuresList: vi.fn(async () => ({ features })),
        featureEnable: vi.fn(async () => {
          throw asRendererReceives('scout:features.enable', new ControlPlaneNotImplementedError());
        }),
        featureDisable: vi.fn(),
        featureSetCredentials: vi.fn(),
        featureSetup: vi.fn(),
        featureDeleteCredentials: vi.fn(),
        featureWriteGrant: vi.fn(async (req: { granted: boolean }) => ({
          ...youtube,
          granted: req.granted,
          grantedAt: req.granted ? '2026-11-02T10:00:00Z' : null,
        })),
        youtubeStatus: vi.fn(async () => youtubeConnected),
        accessGet: vi.fn(async () => ({ userIds: ['111'] })),
        ...overrides,
      };
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
          FeaturesStore,
        ],
      });
      const fixture = TestBed.createComponent(FeaturesComponent);
      await settle(fixture);
      return { fixture, bridge };
    }

    async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      fixture.detectChanges();
    }

    async function click(fixture: ComponentFixture<FeaturesComponent>, testId: string) {
      const target = fixture.nativeElement.querySelector(`[data-test="${testId}"]`);
      expect(target, `${testId} is on the screen`).not.toBeNull();
      (target as HTMLElement).click();
      await settle(fixture);
    }

    function dialog(fixture: ComponentFixture<FeaturesComponent>): HTMLElement | null {
      return fixture.nativeElement.querySelector('[data-test="write-confirmation-dialog"]');
    }

    function grantLine(fixture: ComponentFixture<FeaturesComponent>): string {
      const line = fixture.nativeElement.querySelector(
        '[data-test="grant-youtube"]',
      ) as HTMLElement;
      return (line.textContent ?? '').replace(/\s+/g, ' ').trim();
    }

    it('turning on a write-capable feature asks for confirmation and only then calls enable with confirmWrites', async () => {
      const { fixture, bridge } = await mount([writer]);

      await click(fixture, 'toggle-other');
      const shown = dialog(fixture);
      const rowsShown = Array.from(shown?.querySelectorAll('[data-test^="write-row-"]') ?? []).map(
        (row) => row.getAttribute('data-test'),
      );
      const enableCallsBeforeAllow = bridge.featureEnable.mock.calls.length;
      await click(fixture, 'write-confirmation-allow');

      expect(shown?.querySelector('[data-test="write-confirmation-text"]')?.textContent).toBe(
        WRITE_CONFIRMATION_TEXT,
      );
      expect(rowsShown).toEqual(['write-row-drafts', 'write-row-youtube.upload']);
      expect(enableCallsBeforeAllow).toBe(0);
      expect(bridge.featureEnable.mock.calls).toEqual([[{ id: 'other', confirmWrites: true }]]);
      expect(dialog(fixture)).toBeNull();
      expect(
        (
          fixture.nativeElement.querySelector('[data-test="err-other"]') as HTMLElement
        ).textContent?.trim(),
      ).toBe('Turning this feature on is not available yet.');
    });

    it('cancelling the confirmation leaves the feature off', async () => {
      const { fixture, bridge } = await mount([writer]);

      await click(fixture, 'toggle-other');
      await click(fixture, 'write-confirmation-cancel');

      const toggle = fixture.nativeElement.querySelector(
        '[data-test="toggle-other"]',
      ) as HTMLInputElement;
      expect(dialog(fixture)).toBeNull();
      expect(bridge.featureEnable).not.toHaveBeenCalled();
      expect(toggle.checked).toBe(false);
    });

    it('a read-only feature toggles without the dialog', async () => {
      const readOnly: FeatureDto = {
        ...controlPlaneOnly,
        hasCredentials: true,
        status: 'disabled',
      };
      const { fixture, bridge } = await mount([readOnly], {
        featureEnable: vi.fn(async () => ({ ...readOnly, enabled: true, status: 'enabled' })),
      });

      await click(fixture, 'toggle-other');

      expect(dialog(fixture)).toBeNull();
      expect(bridge.featureEnable.mock.calls).toEqual([[{ id: 'other', confirmWrites: false }]]);
    });

    it('Allow writes asks for confirmation and only then grants with confirmWrites', async () => {
      const { fixture, bridge } = await mount([youtube]);

      await click(fixture, 'allow-writes-youtube');
      const shown = dialog(fixture) !== null;
      const grantCallsBeforeAllow = bridge.featureWriteGrant.mock.calls.length;
      await click(fixture, 'write-confirmation-allow');

      expect(shown).toBe(true);
      expect(grantCallsBeforeAllow).toBe(0);
      expect(bridge.featureWriteGrant.mock.calls).toEqual([
        [{ id: 'youtube', granted: true, confirmWrites: true }],
      ]);
      expect(grantLine(fixture)).toBe('Write permission: granted on 2026-11-02');
    });

    it('cancelling leaves the grant off', async () => {
      const { fixture, bridge } = await mount([youtube]);

      await click(fixture, 'allow-writes-youtube');
      await click(fixture, 'write-confirmation-cancel');

      expect(dialog(fixture)).toBeNull();
      expect(bridge.featureWriteGrant).not.toHaveBeenCalled();
      expect(grantLine(fixture)).toBe('Write permission: not granted');
    });

    it('Revoke writes clears the grant without a dialog', async () => {
      const { fixture, bridge } = await mount([
        { ...youtube, granted: true, grantedAt: '2026-11-01T09:00:00Z' },
      ]);

      await click(fixture, 'revoke-writes-youtube');

      expect(dialog(fixture)).toBeNull();
      expect(bridge.featureWriteGrant.mock.calls).toEqual([
        [{ id: 'youtube', granted: false, confirmWrites: false }],
      ]);
      expect(grantLine(fixture)).toBe('Write permission: not granted');
    });
  });
});
