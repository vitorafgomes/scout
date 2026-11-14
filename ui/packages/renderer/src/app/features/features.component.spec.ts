import { provideRouter } from '@angular/router';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { readFileSync } from 'node:fs';

import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneNotImplementedError,
  ControlPlaneUnauthenticatedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  type FeatureDto,
  type FeatureId,
  type GitHubStatus,
  type KubernetesStatus,
  type YouTubeStatus,
} from '@scout/shared';

import { DIALOG_STYLES } from './credential-dialog.component';

import { FeaturesStore } from '../core/features.store';
import { LAID_OUT } from '../core/laid-out.testing';
import { MAIN_WHISPER_TABLE } from '../core/whisper-table.testing';
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
  repositories: null,
  installUrl: 'https://github.com/apps/scout-chatops/installations/new',
  pending: null,
  error: null,
};

function asRendererReceives(channel: string, err: Error): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(serializeError(err))}`,
  );
}

function inPanel(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
  const panel = fixture.nativeElement.querySelector(
    '[data-test="feature-panel"]:not([hidden])',
  ) as HTMLElement | null;
  return panel?.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

async function openDetails(
  fixture: ComponentFixture<FeaturesComponent>,
  id: string,
): Promise<void> {
  const details = fixture.nativeElement.querySelector(
    `[data-test="features-grid"] [data-test="details-${id}"]`,
  ) as HTMLElement | null;
  if (details === null) throw new Error(`the ${id} card has no Details`);
  details.click();
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
  if (inPanel(fixture, `panel-${id}`)?.hidden !== false)
    throw new Error(`Details did not open the ${id} panel`);
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
      provideRouter([]),
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
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

  it('shows the GitHub and Kubernetes features in the grid under the lede that points to Approvals', async () => {
    const { fixture } = await setup();

    const grid = fixture.nativeElement.querySelector('[data-test="features-grid"]') as HTMLElement;
    const lede = fixture.nativeElement.querySelector('[data-test="features-lede"]') as HTMLElement;
    const approvals = fixture.nativeElement.querySelector(
      '[data-test="features-approvals-link"]',
    ) as HTMLAnchorElement;

    expect(grid.textContent).toContain('GitHub');
    expect(grid.textContent).toContain('Kubernetes');
    expect(lede.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Connect Scout to your tools. A feature that writes asks for your permission first; actions that cannot be undone wait for you in Approvals.',
    );
    expect(approvals.getAttribute('href')).toBe('/review');
    expect(fixture.nativeElement.querySelector('[data-test="features-notes"]')).toBeNull();
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
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
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
    await openDetails(fixture, 'youtube');

    expect(
      inPanel(fixture, 'panel-youtube')?.querySelector('[data-test="youtube-connect-section"]'),
    ).not.toBeNull();
    expect(card.querySelector('[data-test="badge-youtube"]')?.textContent?.trim()).toBe(
      'Connected',
    );
    expect(card.querySelector('[data-test="toggle-youtube"]')).toBeNull();
    expect(card.querySelector('[data-test="cta-youtube"]')).toBeNull();
    expect(inPanel(fixture, 'toggle-youtube')).toBeNull();
    expect(inPanel(fixture, 'cta-youtube')).toBeNull();
    expect(card.textContent).not.toContain('Needs credentials');
  });

  it('the This computer card shows its name and On, its voice line, and no toggle, no credentials or allowed-users warning', async () => {
    const desktop: FeatureDto = {
      ...github,
      id: 'desktop',
      displayName: 'This computer',
      description: 'Lets Scout see your screen and hear your microphone when you ask it to.',
      requiresCredentials: false,
      hasCredentials: false,
      enabled: false,
      status: 'disabled',
      scopeMatrix: [
        { scope: 'Screen', summary: 'read', actions: ['read'], access: 'read' },
        { scope: 'Microphone', summary: 'read', actions: ['read'], access: 'read' },
      ],
    };
    const bridge = {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => ({ features: [desktop] })),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      accessGet: vi.fn(async () => ({ userIds: [] })),
      whisperListInstalled: vi.fn(async () => ({ installed: [] })),
      onWhisperDownloadProgress: vi.fn(() => () => undefined),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
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
    const card = fixture.nativeElement.querySelector('[data-test="card-desktop"]') as HTMLElement;
    await openDetails(fixture, 'desktop');

    expect(card.querySelector('.title')?.textContent?.trim()).toBe('This computer');
    expect(card.querySelector('[data-test="badge-desktop"]')?.textContent?.trim()).toBe('On');
    expect(card.querySelector('[data-test="fact-desktop"]')?.textContent?.trim()).toBe(
      'Voice model: not installed',
    );
    expect(inPanel(fixture, 'toggle-desktop')).toBeNull();
    expect(inPanel(fixture, 'cta-desktop')).toBeNull();
    expect(inPanel(fixture, 'access-warning-desktop')).toBeNull();
    expect(inPanel(fixture, 'voice-line')?.textContent?.trim()).toBe('Voice model: not installed');
    expect(inPanel(fixture, 'allow-writes-desktop')).toBeNull();
    expect(inPanel(fixture, 'label-Screen')?.textContent?.trim()).toBe('Screen');
    expect(inPanel(fixture, 'label-Microphone')?.textContent?.trim()).toBe('Microphone');
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
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await openDetails(fixture, 'other');

    (inPanel(fixture, 'toggle-other') as HTMLInputElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const err = inPanel(fixture, 'err-other') as HTMLElement;
    expect(err.textContent?.trim()).toBe('Turning this feature on is not available yet.');
    expect(
      fixture.nativeElement.querySelector('[data-test="fact-other"]')?.textContent?.trim(),
    ).toBe('Turning this feature on is not available yet.');
  });

  it('opens the credential dialog when a card requests it', async () => {
    const { fixture } = await setup();
    await openDetails(fixture, 'other');

    const cta = inPanel(fixture, 'cta-other') as HTMLButtonElement;
    cta.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="credential-dialog"]')).not.toBeNull();
  });

  it('forwards dialog submit to store.setup and closes the dialog', async () => {
    const { fixture, bridge } = await setup();
    await openDetails(fixture, 'other');

    (inPanel(fixture, 'cta-other') as HTMLButtonElement).click();
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
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await openDetails(fixture, 'other');

    (inPanel(fixture, 'toggle-other') as HTMLInputElement).click();
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
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
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
      'The Scout control plane is not running, so the features cannot be shown.',
    );
    expect(fixture.nativeElement.querySelector('[data-test="features-grid"]')).toBeNull();
  });

  describe('write permissions', () => {
    const writeRows = [
      {
        scope: 'youtube.readonly',
        summary: 'Read the channel',
        actions: ['Read the channel'],
        access: 'read' as const,
      },
      {
        scope: 'drafts',
        summary: 'Save scripts',
        actions: ['Save scripts'],
        access: 'write' as const,
      },
      {
        scope: 'youtube.upload',
        summary: 'Upload a video',
        actions: ['Upload a video'],
        access: 'irreversible' as const,
      },
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
        noticeRecord: vi.fn(async () => undefined),
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
          provideRouter([]),
          { provide: InteractivityChecker, useValue: LAID_OUT },
          { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
          { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
          { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
          FeaturesStore,
        ],
      });
      const fixture = TestBed.createComponent(FeaturesComponent);
      await settle(fixture);
      await openDetails(fixture, features[0]!.id);
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
      const target = testId.startsWith('write-confirmation-')
        ? fixture.nativeElement.querySelector(`[data-test="${testId}"]`)
        : inPanel(fixture, testId);
      expect(target, `${testId} is on the screen`).not.toBeNull();
      (target as HTMLElement).click();
      await settle(fixture);
    }

    function dialog(fixture: ComponentFixture<FeaturesComponent>): HTMLElement | null {
      return fixture.nativeElement.querySelector('[data-test="write-confirmation-dialog"]');
    }

    function toasts(): { text: string; kind: string | undefined }[] {
      return Array.from(
        document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
      ).map((el) => ({
        text: (el.textContent ?? '').trim(),
        kind: Array.from(el.classList).find((c) => c.startsWith('sc-notice--')),
      }));
    }

    function grantLine(fixture: ComponentFixture<FeaturesComponent>): string {
      const line = inPanel(fixture, 'grant-youtube') as HTMLElement;
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
        'Scout will be allowed to perform these writes on your behalf. Actions marked irreversible will still wait for your approval, one by one, in Approvals.',
      );
      expect(rowsShown).toEqual(['write-row-drafts', 'write-row-youtube.upload']);
      expect(enableCallsBeforeAllow).toBe(0);
      expect(bridge.featureEnable.mock.calls).toEqual([[{ id: 'other', confirmWrites: true }]]);
      expect(dialog(fixture)).toBeNull();
      expect((inPanel(fixture, 'err-other') as HTMLElement).textContent?.trim()).toBe(
        'Turning this feature on is not available yet.',
      );
    });

    it('cancelling the confirmation leaves the feature off', async () => {
      const { fixture, bridge } = await mount([writer]);

      await click(fixture, 'toggle-other');
      await click(fixture, 'write-confirmation-cancel');

      const toggle = inPanel(fixture, 'toggle-other') as HTMLInputElement;
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

    it('allowing the writes shows a toast naming the feature, revoking shows another', async () => {
      const { fixture } = await mount([youtube]);

      await click(fixture, 'allow-writes-youtube');
      const beforeAllow = toasts();
      await click(fixture, 'write-confirmation-allow');
      const afterAllow = toasts();
      await click(fixture, 'revoke-writes-youtube');

      expect(beforeAllow).toEqual([]);
      expect(afterAllow).toEqual([
        { text: 'Writes allowed for YouTube', kind: 'sc-notice--success' },
      ]);
      expect(toasts()).toEqual([{ text: 'Writes revoked for YouTube', kind: 'sc-notice--info' }]);
      expect(grantLine(fixture)).toBe('Write permission: not granted');
    });

    it('a grant the control plane refuses shows no toast and keeps the error on the card', async () => {
      const { fixture } = await mount([youtube], {
        featureWriteGrant: vi.fn(async () => {
          throw new Error('The control plane refused the grant.');
        }),
      });

      await click(fixture, 'allow-writes-youtube');
      await click(fixture, 'write-confirmation-allow');

      expect(toasts()).toEqual([]);
      expect((inPanel(fixture, 'grant-err-youtube') as HTMLElement).textContent?.trim()).toBe(
        'The control plane refused the grant.',
      );
      expect(grantLine(fixture)).toBe('Write permission: not granted');
    });

    it('a revoke the control plane refuses shows no toast and keeps the error on the card', async () => {
      const { fixture } = await mount(
        [{ ...youtube, granted: true, grantedAt: '2026-11-01T09:00:00Z' }],
        {
          featureWriteGrant: vi.fn(async () => {
            throw new Error('The control plane refused the revoke.');
          }),
        },
      );

      await click(fixture, 'revoke-writes-youtube');

      expect(toasts()).toEqual([]);
      expect((inPanel(fixture, 'grant-err-youtube') as HTMLElement).textContent?.trim()).toBe(
        'The control plane refused the revoke.',
      );
    });
  });
});

const COLOUR_LITERAL =
  /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g;

function componentStyles(file: string): string {
  const source = readFileSync(`src/app/features/${file}`, 'utf8');
  const start = source.indexOf('  styles:');
  expect(start, `${file} declares its styles`).toBeGreaterThanOrEqual(0);
  return source.slice(start, source.indexOf('\n})\n', start));
}

describe('Features follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [part, css] of [
    ['the Features screen', () => componentStyles('features.component.ts')],
    ['the feature cards', () => componentStyles('feature-card.component.ts')],
    ['the feature details', () => componentStyles('feature-details.component.ts')],
    ['the feature side panel', () => componentStyles('feature-panel.component.ts')],
    ['the feature logo', () => componentStyles('feature-logo.component.ts')],
    ['the GitHub connect section', () => componentStyles('github-connect.component.ts')],
    ['the Kubernetes connect section', () => componentStyles('kubernetes-connect.component.ts')],
    ['the YouTube connect section', () => componentStyles('youtube-connect.component.ts')],
    ['the Docker card', () => componentStyles('docker-card.component.ts')],
    ['the Desktop voice line', () => componentStyles('desktop-voice.component.ts')],
    ['the voice model dialog', () => componentStyles('voice-model-dialog.component.ts')],
    [
      'the write confirmation dialog',
      () => componentStyles('write-confirmation-dialog.component.ts'),
    ],
    ['the credential and write dialogs', () => DIALOG_STYLES],
  ] as const) {
    it(`${part} paint only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      const styles = css();

      expect(styles).toContain('var(--sc-');
      expect(styles).not.toContain('color-scheme');
      expect(Array.from(styles.matchAll(COLOUR_LITERAL), (match) => match[0])).toEqual([]);
    });
  }

  function bridgeFor(list: () => Promise<{ features: FeatureDto[] }>) {
    return {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(list),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(async () => ({ ...controlPlaneOnly, hasCredentials: false })),
      accessGet: vi.fn(async () => ({ userIds: ['42'] })),
      noticeRecord: vi.fn(async () => undefined),
    };
  }

  async function mount(bridge: ReturnType<typeof bridgeFor>) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    await settle(fixture);
    return fixture;
  }

  async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    for (let i = 0; i < 4; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
      await TestBed.inject(ApplicationRef).whenStable();
    }
    fixture.detectChanges();
  }

  function q(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
  }

  it('shows a progress bar until the control plane answers, then the cards', async () => {
    let answer: (value: { features: FeatureDto[] }) => void = () => undefined;
    const bridge = bridgeFor(() => new Promise((resolve) => (answer = resolve)));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    fixture.detectChanges();
    const loading = [q(fixture, 'features-loading') !== null, q(fixture, 'features-grid') !== null];
    const skeletons = fixture.nativeElement.querySelectorAll(
      '[data-test="features-loading"] [data-test="features-skeleton"]',
    ).length;

    answer({ features: [controlPlaneOnly] });
    await settle(fixture);

    expect(loading).toEqual([true, false]);
    expect(skeletons).toBe(5);
    expect(componentStyles('features.component.ts')).toMatch(
      /\.skeletons \{[^}]*grid-auto-rows: 200px/,
    );
    expect([q(fixture, 'features-loading'), q(fixture, 'card-other') !== null]).toEqual([
      null,
      true,
    ]);
  });

  it('a reload keeps the cards mounted, with no progress bar over them', async () => {
    let calls = 0;
    let answer: (value: { features: FeatureDto[] }) => void = () => undefined;
    const bridge = bridgeFor(() => {
      calls += 1;
      if (calls === 1) return Promise.resolve({ features: [controlPlaneOnly] });
      return new Promise((resolve) => (answer = resolve));
    });
    const fixture = await mount(bridge);
    const before = q(fixture, 'card-other');

    q(fixture, 'features-refresh')!.click();
    fixture.detectChanges();
    const during = [q(fixture, 'card-other'), q(fixture, 'features-loading')];
    answer({ features: [controlPlaneOnly] });
    await settle(fixture);

    expect(before).not.toBeNull();
    expect(during).toEqual([before, null]);
    expect(q(fixture, 'card-other')).toBe(before);
  });

  it('Refresh, with its icon, reads the feature list again', async () => {
    const bridge = bridgeFor(async () => ({ features: [controlPlaneOnly] }));
    const fixture = await mount(bridge);

    q(fixture, 'features-refresh')!.click();
    await settle(fixture);

    expect([
      q(fixture, 'features-refresh')!.querySelector('sc-icon') !== null,
      q(fixture, 'features-refresh')!.textContent?.trim(),
    ]).toEqual([true, 'Refresh']);
    expect(bridge.featuresList).toHaveBeenCalledTimes(2);
  });

  it('a control plane that is down offers Try again, which shows the cards once it answers', async () => {
    let down = true;
    const bridge = bridgeFor(async () => {
      if (down)
        throw asRendererReceives(
          'scout:features.list',
          new ControlPlaneUnavailableError('socket reset'),
        );
      return { features: [controlPlaneOnly] };
    });
    const fixture = await mount(bridge);
    const before = q(fixture, 'backend-unavailable')?.textContent?.trim();

    down = false;
    q(fixture, 'features-retry')!.click();
    await settle(fixture);

    expect(before).toBe('The Scout control plane is not running, so the features cannot be shown.');
    expect(q(fixture, 'backend-unavailable')).toBeNull();
    expect(q(fixture, 'card-other')).not.toBeNull();
  });

  it('a feature list the control plane refuses for another reason than being down, such as a rejected session token, says why on the screen', async () => {
    const bridge = bridgeFor(async () => {
      throw asRendererReceives(
        'scout:features.list',
        new ControlPlaneUnauthenticatedError('The control plane rejected the session token.'),
      );
    });

    const fixture = await mount(bridge);

    expect(
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('[role="alert"]'),
        (node) => node.textContent?.trim() ?? '',
      ).join(' | '),
    ).toContain('The control plane rejected the session token.');
  });

  it('Try again on a refused feature list reads it again and shows the cards once the control plane answers', async () => {
    let refused = true;
    const bridge = bridgeFor(async () => {
      if (refused)
        throw asRendererReceives(
          'scout:features.list',
          new ControlPlaneUnauthenticatedError('The control plane rejected the session token.'),
        );
      return { features: [controlPlaneOnly] };
    });
    const fixture = await mount(bridge);
    const before = q(fixture, 'features-error')?.textContent?.trim();

    refused = false;
    q(fixture, 'features-retry')!.click();
    await settle(fixture);

    expect({
      before,
      after: q(fixture, 'features-error'),
      card: q(fixture, 'card-other') !== null,
    }).toEqual({
      before: 'The features could not be read: The control plane rejected the session token.',
      after: null,
      card: true,
    });
  });

  for (const answer of ['confirm-cancel', 'confirm-ok'] as const) {
    it(`Clear credentials asks first with Cancel focused; ${answer === 'confirm-ok' ? 'Clear credentials deletes them' : 'Cancel keeps them'}`, async () => {
      const bridge = bridgeFor(async () => ({
        features: [{ ...controlPlaneOnly, hasCredentials: true }],
      }));
      const fixture = await mount(bridge);
      await openDetails(fixture, 'other');

      inPanel(fixture, 'clear-other')!.click();
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
      (document.querySelector(`[data-test="${answer}"]`) as HTMLElement).click();
      await settle(fixture);

      expect([title, focused]).toEqual(['Clear the Other credentials?', 'confirm-cancel']);
      expect(bridge.featureDeleteCredentials.mock.calls.length).toBe(
        answer === 'confirm-ok' ? 1 : 0,
      );
    });
  }
});

describe('the Features grid and its side panel', () => {
  const PATH = '/home/owner/.kube/config';
  const writeRows = [
    { scope: 'Contents', summary: 'Read files', actions: ['Read files'], access: 'read' as const },
    {
      scope: 'Pull request reviews',
      summary: 'Post a review',
      actions: ['Post a review'],
      access: 'write' as const,
    },
    {
      scope: 'Pull request verdicts',
      summary: 'Approve',
      actions: ['Approve'],
      access: 'irreversible' as const,
    },
  ];
  const kubernetesReads = [
    {
      scope: 'Namespaces',
      summary: 'List the namespaces of each context you chose',
      actions: ['List the namespaces of each context you chose'],
      access: 'read' as const,
    },
    {
      scope: 'Pods',
      summary: 'Read pod status and restarts; never logs or secrets',
      actions: ['Read pod status and restarts; never logs or secrets'],
      access: 'read' as const,
    },
    {
      scope: 'Deployments',
      summary: 'Read replicas and rollout status',
      actions: ['Read replicas and rollout status'],
      access: 'read' as const,
    },
    {
      scope: 'Events',
      summary: 'Read recent events',
      actions: ['Read recent events'],
      access: 'read' as const,
    },
  ];
  const catalog = (id: FeatureId, displayName: string): FeatureDto => ({
    ...github,
    id,
    displayName,
    description: `${displayName} from chat.`,
    writeClass: id === 'github' || id === 'youtube' ? 'executive' : 'none',
    scopeMatrix:
      id === 'github' || id === 'youtube'
        ? writeRows
        : id === 'kubernetes'
          ? kubernetesReads
          : null,
  });
  const features = [
    catalog('github', 'GitHub'),
    catalog('kubernetes', 'Kubernetes'),
    catalog('youtube', 'YouTube'),
    catalog('desktop', 'This computer'),
  ];
  const connectedGitHub: GitHubStatus = {
    ...notConnected,
    connected: true,
    method: 'browser',
    login: 'vitorafgomes',
    installed: true,
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

  function bridgeWith(overrides: Record<string, unknown> = {}) {
    return {
      onFeaturesChanged: vi.fn(() => () => undefined),
      featuresList: vi.fn(async () => ({ features })),
      featureEnable: vi.fn(),
      featureDisable: vi.fn(),
      featureSetCredentials: vi.fn(),
      featureSetup: vi.fn(),
      featureDeleteCredentials: vi.fn(),
      featureWriteGrant: vi.fn(async () => ({ ...features[0]!, granted: true })),
      noticeRecord: vi.fn(async () => undefined),
      accessGet: vi.fn(async () => ({ userIds: ['111'] })),
      appOpenExternal: vi.fn(async () => undefined),
      githubStatus: vi.fn(async () => connectedGitHub),
      githubSignIn: vi.fn(async () => undefined),
      githubBotStatus: vi.fn(async () => ({
        state: 'none',
        login: null,
        installUrl: null,
        settingsUrl: null,
        repositories: [],
        writes: [],
        error: null,
      })),
      kubernetesStatus: vi.fn(async () => kubernetesDisconnected),
      kubernetesDetect: vi.fn(async () => ({
        path: PATH,
        contexts: [
          { name: 'dev', server: 'https://dev.example.com', auth: 'token', execCommand: null },
          { name: 'prod', server: 'https://prod.example.com', auth: 'token', execCommand: null },
        ],
        error: null,
      })),
      youtubeStatus: vi.fn(async () => youtubeConnected),
      whisperListInstalled: vi.fn(async () => ({ installed: [] })),
      onWhisperDownloadProgress: vi.fn(() => () => undefined),
      dockerState: vi.fn(async () => ({ state: 'running', version: '28.5.1' })),
      dockerImages: vi.fn(async () => ({
        images: [
          { stack: '.NET', reference: 'mcr.microsoft.com/dotnet/sdk:10.0', present: true },
          { stack: 'Node', reference: 'node:22', present: false },
        ],
      })),
      dockerWorkFolder: vi.fn(async () => ({ path: '/w', bytes: 0, runs: 0 })),
      dockerRepositories: vi.fn(async () => ({ repositories: [] })),
      dockerLastRun: vi.fn(async () => ({ run: null })),
      vaultGetPublic: vi.fn(async () => ({ value: null })),
      vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
      workerRestart: vi.fn(async () => ({ outcome: 'now' })),
      onDockerPullProgress: vi.fn(() => () => undefined),
      ...overrides,
    };
  }

  async function settle(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    for (let i = 0; i < 6; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    await TestBed.inject(ApplicationRef).whenStable();
    fixture.detectChanges();
  }

  async function mount(bridge = bridgeWith()) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: InteractivityChecker, useValue: LAID_OUT },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        FeaturesStore,
      ],
    });
    const fixture = TestBed.createComponent(FeaturesComponent);
    await settle(fixture);
    return { fixture, bridge };
  }

  function grid(fixture: ComponentFixture<FeaturesComponent>): HTMLElement {
    return fixture.nativeElement.querySelector('[data-test="features-grid"]') as HTMLElement;
  }

  function panel(fixture: ComponentFixture<FeaturesComponent>): HTMLElement {
    return fixture.nativeElement.querySelector('[data-test="feature-panel"]') as HTMLElement;
  }

  function onCard(fixture: ComponentFixture<FeaturesComponent>, test: string): string | null {
    const el = grid(fixture).querySelector(`[data-test="${test}"]`) as HTMLElement | null;
    return el === null ? null : (el.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  async function press(fixture: ComponentFixture<FeaturesComponent>, el: Element | null) {
    if (el === null) throw new Error('the control is not on the screen');
    (el as HTMLElement).click();
    await settle(fixture);
  }

  function zIndex(css: string, selector: string): number {
    const at = css.indexOf(`${selector} {`);
    const block = css.slice(at, css.indexOf('}', at));
    return Number(/z-index: (\d+)/.exec(block)?.[1] ?? Number.NaN);
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('every Features card is the same fixed-height card, Docker right after GitHub, with no long section on any card', async () => {
    const { fixture } = await mount();
    const children = [...grid(fixture).children];
    const long = [
      '[data-test="github-connect-section"]',
      '[data-test="github-bot-section"]',
      '[data-test^="docker-section-"]',
      '[data-test="kubernetes-connect-section"]',
      '[data-test="youtube-connect-section"]',
      '[data-test="desktop-voice"]',
      '[data-test^="reads-"]',
      '[data-test^="permissions-"]',
    ];
    const css = componentStyles('features.component.ts');

    expect(children.map((c) => c.tagName)).toEqual(Array(5).fill('SC-FEATURE-CARD'));
    expect(children.map((c) => c.querySelector('article')?.getAttribute('data-test'))).toEqual([
      'card-github',
      'card-docker',
      'card-kubernetes',
      'card-youtube',
      'card-desktop',
    ]);
    expect(css).toMatch(/\.features-grid \{[^}]*grid-auto-rows: 200px/);
    expect(long.filter((sel) => grid(fixture).querySelector(sel) !== null)).toEqual([]);
    expect(long.filter((sel) => panel(fixture).querySelector(sel) === null)).toEqual([]);
  });

  it('each card sums its feature up from the live state: one fact, the permission counts and one action', async () => {
    const { fixture } = await mount();
    const row = (id: string) => [
      onCard(fixture, `fact-${id}`),
      [...grid(fixture).querySelectorAll(`[data-test="perms-${id}"] li`)]
        .map((li) => li.textContent?.trim())
        .join(', '),
      onCard(fixture, `act-${id}`),
    ];

    expect(row('github')).toEqual([
      'Connected as vitorafgomes · Writes not allowed',
      'Read 1, Write 1, Cannot be undone 1',
      'Allow writes',
    ]);
    expect(row('docker')).toEqual([
      'Images: 1 of 2 downloaded',
      "Runs also need GitHub's write permission",
      'Turn on',
    ]);
    expect(row('kubernetes')).toEqual([`2 contexts found in ${PATH}`, 'Read 4', null]);
    expect(row('youtube')).toEqual([
      'Connected to Scout Test Channel · Writes not allowed',
      'Read 1, Write 1, Cannot be undone 1',
      'Allow writes',
    ]);
    expect(row('desktop')).toEqual(['Voice model: not installed', '', 'Download']);
  });

  it('GitHub not connected: Connect with GitHub on the card signs in and opens the panel where the code shows', async () => {
    let status: GitHubStatus = notConnected;
    const bridge = bridgeWith({
      githubStatus: vi.fn(async () => status),
      githubSignIn: vi.fn(async () => {
        status = {
          ...notConnected,
          pending: { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device' },
        };
      }),
    });
    const { fixture } = await mount(bridge);
    const before = [onCard(fixture, 'fact-github'), onCard(fixture, 'act-github')];

    await press(fixture, grid(fixture).querySelector('[data-test="act-github"]'));
    await settle(fixture);

    expect(before).toEqual([
      'Not connected · Scout shows a one-time code to enter on GitHub',
      'Connect with GitHub',
    ]);
    expect(bridge.githubSignIn).toHaveBeenCalledTimes(1);
    expect(panel(fixture).hidden).toBe(false);
    expect(inPanel(fixture, 'github-user-code')?.textContent?.trim()).toBe('WDJB-MJHT');
    expect(onCard(fixture, 'fact-github')).toBe('Enter the code WDJB-MJHT on GitHub');
  });

  for (const [id, name] of [
    ['github', 'GitHub'],
    ['docker', 'Docker'],
    ['kubernetes', 'Kubernetes'],
    ['youtube', 'YouTube'],
    ['desktop', 'This computer'],
  ] as const) {
    it(`Details on ${name} opens the side panel on ${name}'s sections only, and says it is expanded`, async () => {
      const { fixture } = await mount();
      const details = grid(fixture).querySelector(`[data-test="details-${id}"]`);
      const closedAtFirst = panel(fixture).hidden;

      await press(fixture, details);
      const sections = [...panel(fixture).querySelectorAll('[data-test^="panel-"]')]
        .filter((s) => !(s as HTMLElement).hidden)
        .map((s) => s.getAttribute('data-test'));

      expect(closedAtFirst).toBe(true);
      expect(panel(fixture).hidden).toBe(false);
      expect(inPanel(fixture, 'feature-panel-title')?.textContent?.trim()).toBe(name);
      expect(sections).toEqual([`panel-${id}`]);
      expect(inPanel(fixture, `feature-logo-${id}`)).not.toBeNull();
      expect(details?.getAttribute('aria-expanded')).toBe('true');
    });
  }

  it("the Docker panel says the machine needs nothing but Docker, under Docker's chip", async () => {
    const { fixture } = await mount();

    await press(fixture, grid(fixture).querySelector('[data-test="details-docker"]'));

    expect(inPanel(fixture, 'feature-panel-desc')?.textContent?.trim()).toBe(
      "This computer needs nothing but Docker. The .NET, Node, Bun, Python, Go and Java SDKs come inside the container, so Scout runs a project's tests without installing anything on your machine.",
    );
    expect(inPanel(fixture, 'feature-panel-chip')?.textContent?.trim()).toBe(
      'Running · Docker 28.5.1',
    );
  });

  for (const [how, close] of [
    [
      'its close button',
      (f: ComponentFixture<FeaturesComponent>) => inPanel(f, 'feature-panel-close')!.click(),
    ],
    [
      'Esc',
      (f: ComponentFixture<FeaturesComponent>) =>
        inPanel(f, 'github-connect-section')!.dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
        ),
    ],
    [
      'a click on the scrim',
      (f: ComponentFixture<FeaturesComponent>) =>
        (f.nativeElement.querySelector('[data-test="feature-panel-scrim"]') as HTMLElement).click(),
    ],
  ] as const) {
    it(`${how} closes the panel and gives the focus back to the card's Details`, async () => {
      const { fixture } = await mount();
      const details = grid(fixture).querySelector('[data-test="details-github"]') as HTMLElement;
      await press(fixture, details);
      const focusedWhileOpen = document.activeElement?.getAttribute('data-test');

      close(fixture);
      await settle(fixture);

      expect(focusedWhileOpen).toBe('feature-panel-close');
      expect(panel(fixture).hidden).toBe(true);
      expect(document.activeElement).toBe(details);
      expect(details.getAttribute('aria-expanded')).toBe('false');
    });
  }

  it('Allow writes on the card opens the panel, and Allow writes there opens the write confirmation above the panel', async () => {
    const { fixture } = await mount();

    await press(fixture, grid(fixture).querySelector('[data-test="act-github"]'));
    const panelOpen = !panel(fixture).hidden && inPanel(fixture, 'panel-github')?.hidden === false;
    expect(panelOpen).toBe(true);
    await press(fixture, inPanel(fixture, 'allow-writes-github'));
    const confirmation = fixture.nativeElement.querySelector(
      '[data-test="write-confirmation-dialog"]',
    ) as HTMLElement | null;
    const panelSource = readFileSync('src/app/features/feature-panel.component.ts', 'utf8');

    expect(confirmation).not.toBeNull();
    expect(panel(fixture).contains(confirmation)).toBe(false);
    expect(
      panel(fixture).compareDocumentPosition(confirmation!) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(zIndex(DIALOG_STYLES, '.backdrop')).toBeGreaterThan(zIndex(panelSource, '.panel'));
    expect(zIndex(panelSource, '.panel')).toBeGreaterThan(zIndex(panelSource, '.scrim'));
  });

  it('a toast for a grant made in the panel shows outside the panel, in the overlay above it', async () => {
    const { fixture } = await mount();
    await press(fixture, grid(fixture).querySelector('[data-test="details-github"]'));

    await press(fixture, inPanel(fixture, 'allow-writes-github'));
    await press(
      fixture,
      fixture.nativeElement.querySelector('[data-test="write-confirmation-allow"]'),
    );
    const toast = document.querySelector('.cdk-overlay-container .sc-notice') as HTMLElement | null;

    expect(toast?.textContent?.trim()).toBe('Writes allowed for GitHub');
    expect(panel(fixture).hidden).toBe(false);
    expect(panel(fixture).contains(toast)).toBe(false);
  });

  const dockerStates = [
    {
      name: 'not installed',
      state: { state: 'notInstalled' },
      chip: 'Not installed',
      fact: 'Docker is not installed on this computer.',
      act: 'How to install Docker',
    },
    {
      name: 'not running',
      state: { state: 'notRunning' },
      chip: 'Installed, not running',
      fact: 'Docker is installed but not running. Start Docker Desktop or the Docker service.',
      act: 'Refresh',
    },
    {
      name: 'running',
      state: { state: 'running', version: '28.5.1' },
      chip: 'Running · Docker 28.5.1',
      fact: 'Images: 1 of 2 downloaded',
      act: 'Turn on',
    },
    {
      name: 'unreadable',
      state: 'fail',
      chip: 'Unknown',
      fact: 'Scout could not ask Docker for its state. Try Refresh.',
      act: 'Refresh',
    },
  ] as const;
  for (const row of dockerStates) {
    it(`Docker ${row.name}: the card's chip reads "${row.chip}", its fact "${row.fact}", its action ${row.act ?? 'none'}`, async () => {
      const { fixture } = await mount(
        bridgeWith({
          dockerState: vi.fn(async () => {
            if (row.state === 'fail') throw new Error('docker did not answer');
            return row.state;
          }),
        }),
      );

      expect([
        onCard(fixture, 'badge-docker'),
        onCard(fixture, 'fact-docker'),
        onCard(fixture, 'act-docker'),
      ]).toEqual([row.chip, row.fact, row.act]);
    });
  }

  it('Docker running with its runs off and GitHub connected: Turn on on the card saves the switch, restarts Scout, and the switch in the panel reads on', async () => {
    const bridge = bridgeWith();
    const { fixture } = await mount(bridge);

    await press(fixture, grid(fixture).querySelector('[data-test="act-docker"]'));
    await press(fixture, grid(fixture).querySelector('[data-test="details-docker"]'));

    expect(bridge.vaultStore.mock.calls).toEqual([[{ key: 'sandbox.enabled', value: 'true' }]]);
    expect(bridge.workerRestart).toHaveBeenCalledTimes(1);
    expect(onCard(fixture, 'act-docker')).toBeNull();
    expect(
      inPanel(fixture, 'docker-switch')
        ?.querySelector('[role="switch"]')
        ?.getAttribute('aria-checked'),
    ).toBe('true');
  });

  it('a Turn on the vault refuses keeps the card offering it and says why in the panel', async () => {
    const bridge = bridgeWith({
      vaultStore: vi.fn(async () => {
        throw new Error('The vault is locked.');
      }),
    });
    const { fixture } = await mount(bridge);

    await press(fixture, grid(fixture).querySelector('[data-test="act-docker"]'));
    await press(fixture, grid(fixture).querySelector('[data-test="details-docker"]'));

    expect(onCard(fixture, 'act-docker')).toBe('Turn on');
    expect(inPanel(fixture, 'docker-switch-error')?.textContent?.trim()).toBe(
      'The vault is locked.',
    );
    expect(bridge.workerRestart).not.toHaveBeenCalled();
  });

  it("Docker not installed: How to install Docker on the card opens Docker's install guide through main", async () => {
    const bridge = bridgeWith({ dockerState: vi.fn(async () => ({ state: 'notInstalled' })) });
    const { fixture } = await mount(bridge);

    await press(fixture, grid(fixture).querySelector('[data-test="act-docker"]'));

    expect(bridge.appOpenExternal.mock.calls).toEqual([
      [{ url: 'https://docs.docker.com/get-started/get-docker/' }],
    ]);
  });
  it('GitHub with its writes allowed: the card says so and offers no Allow writes', async () => {
    const { fixture } = await mount(
      bridgeWith({
        featuresList: vi.fn(async () => ({
          features: [{ ...features[0]!, granted: true }, ...features.slice(1)],
        })),
      }),
    );

    expect([onCard(fixture, 'fact-github'), onCard(fixture, 'act-github')]).toEqual([
      'Connected as vitorafgomes · Writes allowed',
      null,
    ]);
  });

  it('YouTube not connected: Sign in with Google on the card starts the sign-in in the browser', async () => {
    let status: YouTubeStatus = { ...youtubeConnected, connected: false, method: null };
    const youtubeSignIn = vi.fn(async () => {
      status = { ...status, pending: true };
    });
    const { fixture } = await mount(
      bridgeWith({ youtubeStatus: vi.fn(async () => status), youtubeSignIn }),
    );
    const before = [onCard(fixture, 'fact-youtube'), onCard(fixture, 'act-youtube')];

    await press(fixture, grid(fixture).querySelector('[data-test="act-youtube"]'));

    expect(before).toEqual([
      'Not connected · sign in with Google or add an API key',
      'Sign in with Google',
    ]);
    expect(youtubeSignIn).toHaveBeenCalledTimes(1);
    expect(onCard(fixture, 'fact-youtube')).toBe('Waiting for the browser...');
  });

  it('a feature without credentials: Add credentials on the card opens the credential dialog for it', async () => {
    const other: FeatureDto = {
      ...catalog('kubernetes', 'Other'),
      id: 'other' as FeatureId,
      description: 'Other integration',
      hasCredentials: false,
      status: 'needs-credentials',
    };
    const { fixture } = await mount(
      bridgeWith({ featuresList: vi.fn(async () => ({ features: [...features, other] })) }),
    );
    const before = onCard(fixture, 'act-other');

    await press(fixture, grid(fixture).querySelector('[data-test="act-other"]'));

    expect(before).toBe('Add credentials');
    expect(
      fixture.nativeElement.querySelector('[data-test="dialog-title"]')?.textContent?.trim(),
    ).toBe('Connect Other');
  });

  describe('This computer: the card runs the voice model action its panel offers', () => {
    afterEach(() => {
      document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
    });

    function voiceBridge(download: (variant: string) => Promise<void>, list?: () => Promise<void>) {
      const installed: string[] = [];
      const whisperDownloadModel = vi.fn(async (req: { variant: string; requestId: string }) => {
        await download(req.variant);
        installed.push(req.variant);
        return { variant: req.variant, path: '/m' };
      });
      const bridge = bridgeWith({
        whisperListInstalled: vi.fn(async () => {
          await list?.();
          return {
            installed: installed.map((variant) => ({ variant, verified: true })),
            inUse: installed[0] ?? null,
          };
        }),
        whisperModelTable: vi.fn(async () => MAIN_WHISPER_TABLE),
        whisperDownloadModel,
      });
      return { bridge, whisperDownloadModel };
    }

    it('no voice model: Download on the card opens the voice model dialog, and its Download downloads the model picked there', async () => {
      const { bridge, whisperDownloadModel } = voiceBridge(async () => undefined);
      const { fixture } = await mount(bridge);

      await press(fixture, grid(fixture).querySelector('[data-test="act-desktop"]'));
      const dialog = document.querySelector('[data-test="voice-download"]') !== null;
      document.querySelector<HTMLInputElement>('[data-test="voice-pick-small"]')?.click();
      await settle(fixture);
      document.querySelector<HTMLElement>('[data-test="voice-download"]')?.click();
      await settle(fixture);

      expect(dialog).toBe(true);
      expect(whisperDownloadModel.mock.calls.map(([req]) => req.variant)).toEqual(['small']);
      expect(onCard(fixture, 'fact-desktop')).toBe('Voice model: small installed');
    });

    it('a download that failed: Retry on the card downloads the same model again', async () => {
      let calls = 0;
      const { bridge, whisperDownloadModel } = voiceBridge(async () => {
        calls += 1;
        if (calls === 1)
          throw new Error(
            '[scout:whisper/download-failed] The model download stopped: connection reset.',
          );
      });
      const { fixture } = await mount(bridge);
      await press(fixture, grid(fixture).querySelector('[data-test="act-desktop"]'));
      document.querySelector<HTMLInputElement>('[data-test="voice-pick-small"]')?.click();
      await settle(fixture);
      document.querySelector<HTMLElement>('[data-test="voice-download"]')?.click();
      await settle(fixture);
      const failed = [onCard(fixture, 'fact-desktop'), onCard(fixture, 'act-desktop')];

      await press(fixture, grid(fixture).querySelector('[data-test="act-desktop"]'));

      expect(failed).toEqual([
        'Could not download the small voice model: The model download stopped: connection reset.',
        'Retry',
      ]);
      expect(whisperDownloadModel.mock.calls.map(([req]) => req.variant)).toEqual([
        'small',
        'small',
      ]);
      expect(onCard(fixture, 'fact-desktop')).toBe('Voice model: small installed');
    });

    it('a voice list main could not read: Try again on the card reads it again', async () => {
      let reads = 0;
      const { bridge } = voiceBridge(
        async () => undefined,
        async () => {
          reads += 1;
          if (reads === 1) throw new Error('main did not answer');
        },
      );
      const { fixture } = await mount(bridge);
      const before = [onCard(fixture, 'fact-desktop'), onCard(fixture, 'act-desktop')];

      await press(fixture, grid(fixture).querySelector('[data-test="act-desktop"]'));

      expect(before).toEqual(['Scout could not read the voice models.', 'Try again']);
      expect([onCard(fixture, 'fact-desktop'), onCard(fixture, 'act-desktop')]).toEqual([
        'Voice model: not installed',
        'Download',
      ]);
    });
  });

  it('Docker not running: Refresh on the card asks Docker again and the card shows its answer', async () => {
    let state: { state: string; version?: string } = { state: 'notRunning' };
    const bridge = bridgeWith({ dockerState: vi.fn(async () => state) });
    const { fixture } = await mount(bridge);
    const before = [onCard(fixture, 'badge-docker'), onCard(fixture, 'act-docker')];
    state = { state: 'running', version: '28.5.1' };

    await press(fixture, grid(fixture).querySelector('[data-test="act-docker"]'));

    expect(before).toEqual(['Installed, not running', 'Refresh']);
    expect(onCard(fixture, 'badge-docker')).toBe('Running · Docker 28.5.1');
  });

  it('Docker running with its runs on and an image a repository needs missing: Download images on the card downloads that image', async () => {
    const dockerPullImage = vi.fn(async (_req: { reference: string }) => ({ error: null }));
    const bridge = bridgeWith({
      vaultGetPublic: vi.fn(async () => ({ value: 'true' })),
      dockerRepositories: vi.fn(async () => ({
        repositories: [
          {
            repository: 'octocat/hello',
            commit: 'abc1234def5678',
            stack: 'Node',
            image: 'node:22',
            runs: 'Node 22',
            declared: [],
            refusal: null,
            detectedAt: '2026-11-07T10:00:00+00:00',
          },
        ],
      })),
      dockerPullImage,
    });
    const { fixture } = await mount(bridge);
    const before = onCard(fixture, 'act-docker');

    await press(fixture, grid(fixture).querySelector('[data-test="act-docker"]'));

    expect(before).toBe('Download images');
    expect(dockerPullImage.mock.calls).toEqual([[{ reference: 'node:22' }]]);
  });
  it("YouTube connected: Allow writes on the card opens the side panel on YouTube's sections", async () => {
    const { fixture } = await mount();
    const before = onCard(fixture, 'act-youtube');

    await press(fixture, grid(fixture).querySelector('[data-test="act-youtube"]'));

    expect(before).toBe('Allow writes');
    expect([
      panel(fixture).hidden,
      inPanel(fixture, 'panel-youtube')?.hidden,
      inPanel(fixture, 'feature-panel-title')?.textContent?.trim(),
    ]).toEqual([false, false, 'YouTube']);
  });

  it('the Scout GitHub App not configured: the card says so and its Connect with GitHub cannot be clicked', async () => {
    const { fixture } = await mount(
      bridgeWith({ githubStatus: vi.fn(async () => ({ ...notConnected, configured: false })) }),
    );
    const act = grid(fixture).querySelector('[data-test="act-github"]') as HTMLButtonElement;

    expect([onCard(fixture, 'fact-github'), act.textContent?.trim(), act.disabled]).toEqual([
      'The Scout GitHub App is not configured yet.',
      'Connect with GitHub',
      true,
    ]);
  });

  it('YouTube without a Google OAuth client: the card says so and its Sign in with Google cannot be clicked', async () => {
    const { fixture } = await mount(
      bridgeWith({
        youtubeStatus: vi.fn(async () => ({
          ...youtubeConnected,
          configured: false,
          connected: false,
          method: null,
        })),
      }),
    );
    const act = grid(fixture).querySelector('[data-test="act-youtube"]') as HTMLButtonElement;

    expect([onCard(fixture, 'fact-youtube'), act.textContent?.trim(), act.disabled]).toEqual([
      'Not connected · add an API key, or a Google OAuth client to sign in',
      'Sign in with Google',
      true,
    ]);
  });
});
