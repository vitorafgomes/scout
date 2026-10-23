import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type {
  FeatureDto,
  GitHubStatus,
  KubernetesDetection,
  KubernetesStatus,
  KubernetesUseRequest,
} from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import { FeaturesComponent } from './features.component';

const PATH = '/home/me/.kube/config';

const feature = (id: 'github' | 'kubernetes'): FeatureDto => ({
  id,
  displayName: id === 'github' ? 'GitHub' : 'Kubernetes',
  description: '',
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
});

const github: GitHubStatus = {
  configured: true,
  connected: false,
  method: null,
  login: null,
  warning: null,
  installed: false,
  installUrl: null,
  pending: null,
  error: null,
};

const detected: KubernetesDetection = {
  path: PATH,
  error: null,
  contexts: [
    { name: 'dev', cluster: 'dev', server: 'https://dev.example.com', auth: 'token' },
    {
      name: 'eks',
      cluster: 'prod',
      server: 'https://prod.example.com',
      auth: 'exec',
      execCommand: 'aws eks get-token --cluster-name prod',
    },
  ],
};

const disconnected: KubernetesStatus = { path: null, contexts: [], connected: false };

function makeBridge(
  detection: KubernetesDetection,
  first: KubernetesStatus = disconnected,
  allowedUserIds: readonly string[] = [],
) {
  let current = first;
  return {
    accessGet: vi.fn(async () => ({ userIds: allowedUserIds })),
    featuresList: vi.fn(async () => ({ features: [feature('github'), feature('kubernetes')] })),
    githubStatus: vi.fn(async () => github),
    kubernetesStatus: vi.fn(async () => current),
    kubernetesDetect: vi.fn(async () => detection),
    kubernetesUse: vi.fn(async (req: KubernetesUseRequest) => {
      current = { path: PATH, contexts: req.contexts, connected: true };
      return current;
    }),
    kubernetesDisconnect: vi.fn(async () => {
      current = disconnected;
      return current;
    }),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<FeaturesComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
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
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<FeaturesComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

async function type(
  fixture: ComponentFixture<FeaturesComponent>,
  test: string,
  value: string,
): Promise<void> {
  const input = el(fixture, test) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

function text(fixture: ComponentFixture<FeaturesComponent>, test: string): string {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('Kubernetes card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('asks before using the configuration on this computer and uses nothing until the owner picks contexts', async () => {
    const bridge = makeBridge(detected);
    const fixture = await render(bridge);

    expect(text(fixture, 'kubernetes-question')).toBe(
      'Use the Kubernetes configuration on this computer?',
    );
    expect(text(fixture, 'kubernetes-path')).toBe(PATH);
    expect((el(fixture, 'kubernetes-use') as HTMLButtonElement).disabled).toBe(true);
    expect(el(fixture, 'cta-kubernetes')).toBeNull();
    expect(text(fixture, 'badge-kubernetes')).toBe('Not connected');

    (el(fixture, 'kubernetes-context-eks') as HTMLInputElement).click();
    await settle(fixture);
    expect(bridge.kubernetesUse).not.toHaveBeenCalled();

    (el(fixture, 'kubernetes-use') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.kubernetesUse).toHaveBeenCalledExactlyOnceWith({
      contexts: [{ name: 'eks', label: 'eks' }],
    });
    expect(text(fixture, 'kubernetes-connected')).toBe(`Using eks from ${PATH}`);
    expect(text(fixture, 'badge-kubernetes')).toBe('Connected');
  });

  it('gives each ticked context a label prefilled with its name and uses the label the owner typed', async () => {
    const bridge = makeBridge(detected);
    const fixture = await render(bridge);

    expect(el(fixture, 'kubernetes-label-dev')).toBeNull();
    (el(fixture, 'kubernetes-context-dev') as HTMLInputElement).click();
    await settle(fixture);
    const label = el(fixture, 'kubernetes-label-dev') as HTMLInputElement;
    expect(label.value).toBe('dev');

    await type(fixture, 'kubernetes-label-dev', ' Dev EU ');
    (el(fixture, 'kubernetes-use') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.kubernetesUse).toHaveBeenCalledExactlyOnceWith({
      contexts: [{ name: 'dev', label: 'Dev EU' }],
    });
    expect(text(fixture, 'kubernetes-connected')).toBe(`Using Dev EU from ${PATH}`);
  });

  it('an invalid or duplicate label blocks Use with an inline error', async () => {
    const bridge = makeBridge(detected);
    const fixture = await render(bridge);
    (el(fixture, 'kubernetes-context-dev') as HTMLInputElement).click();
    (el(fixture, 'kubernetes-context-eks') as HTMLInputElement).click();
    await settle(fixture);
    const useButton = (): HTMLButtonElement => el(fixture, 'kubernetes-use') as HTMLButtonElement;

    await type(fixture, 'kubernetes-label-eks', 'DEV');
    expect(text(fixture, 'kubernetes-label-error')).toBe('The label DEV is used more than once.');
    expect(useButton().disabled).toBe(true);

    await type(fixture, 'kubernetes-label-eks', 'prod/eu');
    expect(text(fixture, 'kubernetes-label-error')).toBe(
      'The label for eks must be 1 to 32 letters, digits, spaces or hyphens.',
    );
    expect(useButton().disabled).toBe(true);

    await type(fixture, 'kubernetes-label-eks', 'prod');
    expect(el(fixture, 'kubernetes-label-error')).toBeNull();
    expect(useButton().disabled).toBe(false);
    expect(bridge.kubernetesUse).not.toHaveBeenCalled();
  });

  it('shows the command an exec context runs before consent, and the server and auth of each', async () => {
    const fixture = await render(makeBridge(detected));

    expect(text(fixture, 'kubernetes-exec-eks')).toBe(
      'Runs aws eks get-token --cluster-name prod to get a token',
    );
    expect(el(fixture, 'kubernetes-exec-dev')).toBeNull();
    const card = text(fixture, 'card-kubernetes');
    expect(card).toContain('dev · https://dev.example.com · token');
    expect(card).toContain('eks · https://prod.example.com · command');
  });

  it('recommends a read-only user with copyable kubectl commands', async () => {
    const fixture = await render(makeBridge(detected));

    expect(text(fixture, 'kubernetes-read-only-note')).toBe(
      'Scout only reads from your clusters, but these credentials may allow changes. A read-only user is safer:',
    );
    expect(el(fixture, 'kubernetes-read-only-commands')?.textContent).toBe(
      [
        'kubectl create serviceaccount scout-reader -n default',
        'kubectl create clusterrolebinding scout-reader-view --clusterrole=view --serviceaccount=default:scout-reader',
        'kubectl create token scout-reader -n default --duration=8760h',
      ].join('\n'),
    );
  });

  it('says where it looked when there is no Kubernetes configuration', async () => {
    const fixture = await render(makeBridge({ path: null, contexts: [], error: null }));

    expect(text(fixture, 'kubernetes-not-found')).toBe(
      'No Kubernetes configuration found on this computer (looked in $KUBECONFIG and ~/.kube/config).',
    );
    expect(el(fixture, 'kubernetes-use')).toBeNull();
  });

  it('shows why a kubeconfig that cannot be read is not offered', async () => {
    const error = `The Kubernetes configuration at ${PATH} could not be read.`;
    const fixture = await render(makeBridge({ path: PATH, contexts: [], error }));

    expect(text(fixture, 'kubernetes-detect-error')).toBe(error);
    expect(el(fixture, 'kubernetes-use')).toBeNull();
  });

  it('when connected lists the labels and the file, never the context names or servers, and disconnect goes back to the question', async () => {
    const bridge = makeBridge(detected, {
      path: PATH,
      contexts: [
        { name: 'dev', label: 'qa' },
        { name: 'eks', label: 'prod' },
      ],
      connected: true,
    });
    const fixture = await render(bridge);

    expect(text(fixture, 'kubernetes-connected')).toBe(`Using qa, prod from ${PATH}`);
    expect(text(fixture, 'card-kubernetes')).not.toContain('eks');
    expect(text(fixture, 'card-kubernetes')).not.toContain('example.com');

    (el(fixture, 'kubernetes-disconnect') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.kubernetesDisconnect).toHaveBeenCalledOnce();
    expect(text(fixture, 'kubernetes-question')).toBe(
      'Use the Kubernetes configuration on this computer?',
    );
    expect(text(fixture, 'badge-kubernetes')).toBe('Not connected');
  });

  it('a connected Kubernetes card warns that nobody can use it from chat while no user is allowed', async () => {
    const fixture = await render(
      makeBridge(
        detected,
        { path: PATH, contexts: [{ name: 'dev', label: 'dev' }], connected: true },
        [],
      ),
    );

    expect(text(fixture, 'access-warning-kubernetes')).toBe(
      'Nobody can use this connection from chat until you add allowed users in Settings.',
    );
  });

  it('a connected Kubernetes card does not warn once a user is allowed, and a disconnected one never warns', async () => {
    const allowed = await render(
      makeBridge(
        detected,
        { path: PATH, contexts: [{ name: 'dev', label: 'dev' }], connected: true },
        ['111'],
      ),
    );
    expect(text(allowed, 'kubernetes-connected')).toBe(`Using dev from ${PATH}`);
    expect(el(allowed, 'access-warning-kubernetes')).toBeNull();

    TestBed.resetTestingModule();
    const disconnectedCard = await render(makeBridge(detected, disconnected, []));
    expect(text(disconnectedCard, 'badge-kubernetes')).toBe('Not connected');
    expect(el(disconnectedCard, 'access-warning-kubernetes')).toBeNull();
  });
});
