import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { readFileSync } from 'node:fs';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  RESTART_DEFERRED_LINE,
  ScoutError,
  serializeError,
  type FeatureDto,
  FeatureId,
  type GitHubBotStatus,
  type GitHubStatus,
  type KubernetesStatus,
  type YouTubeStatus,
} from '@scout/shared';

import { FeaturesStore } from '../core/features.store';
import { LAID_OUT } from '../core/laid-out.testing';
import { NoticeService } from '../core/notice.service';
import { MAIN_WHISPER_TABLE } from '../core/whisper-table.testing';
import { ScoutBridgeService } from '../core/scout-bridge';

import { CATALOG } from './feature-catalog.testing';
import { FeaturePanelComponent } from './feature-panel.component';
import { FeaturesComponent } from './features.component';
import { WriteConfirmationDialogComponent } from './write-confirmation-dialog.component';

function catalog(
  id: 'github' | 'kubernetes' | 'youtube' | 'desktop',
  displayName: string,
  granted = false,
): FeatureDto {
  const entry = CATALOG[id];
  return {
    id,
    displayName,
    description: entry.description,
    requiresCredentials: false,
    hasCredentials: true,
    enabled: true,
    status: 'enabled',
    lastError: null,
    credentialFields: [],
    settingsFields: [],
    settings: {},
    scopeMatrix: entry.rows,
    setupSteps: null,
    writeClass: entry.writeClass,
    granted,
    grantedAt: null,
  };
}

const KUBERNETES_READS = [
  ['Namespaces', 'List the namespaces of each context you chose'],
  ['Pods', 'Read pod status and restarts; never logs or secrets'],
  ['Deployments', 'Read replicas and rollout status'],
  ['Events', 'Read recent events'],
] as const;

const NOT_CONNECTED: GitHubStatus = {
  configured: true,
  connected: false,
  method: null,
  login: null,
  warning: null,
  installed: false,
  repositories: null,
  installUrl: null,
  pending: null,
  error: null,
};

const CONNECTED: GitHubStatus = {
  ...NOT_CONNECTED,
  connected: true,
  method: 'browser',
  login: 'acme-owner',
  installed: true,
  repositories: 6,
};

const BOT_NONE: GitHubBotStatus = {
  state: 'none',
  login: null,
  name: null,
  nameError: null,
  installUrl: null,
  settingsUrl: null,
  repositories: [],
  writes: [],
  error: null,
};

const BOT_INSTALLED: GitHubBotStatus = {
  ...BOT_NONE,
  state: 'installed',
  login: 'scout-acme[bot]',
  name: 'scout-acme',
  installUrl: 'https://github.com/apps/scout-acme/installations/new',
  settingsUrl: 'https://github.com/settings/apps/scout-acme',
  repositories: ['acme/shop', 'acme/api'],
  writes: ['review', 'verdict'],
};

const KUBERNETES_CONNECTED: KubernetesStatus = {
  path: '/home/owner/.kube/config',
  contexts: [{ name: 'shop-prod', label: 'shop-prod' }],
  connected: true,
} as unknown as KubernetesStatus;

const YOUTUBE_CONNECTED: YouTubeStatus = {
  configured: true,
  connected: true,
  method: 'oauth',
  channelId: 'UCacme',
  channelTitle: 'Acme Tech Explained',
  pending: false,
  error: null,
  clientConfigPath: '/user-data/google-oauth-client.json',
  source: 'file',
  scopes: [],
};

interface Options {
  readonly github?: GitHubStatus;
  readonly bot?: GitHubBotStatus;
  readonly kubernetes?: KubernetesStatus;
  readonly youtube?: YouTubeStatus;
  readonly list?: () => Promise<{ features: FeatureDto[] }>;
  readonly extra?: Readonly<Record<string, unknown>>;
}

function bridgeWith(o: Options = {}) {
  return {
    onFeaturesChanged: vi.fn(() => () => undefined),
    featuresList: vi.fn(
      o.list ??
        (async () => ({
          features: [
            catalog('github', 'GitHub'),
            catalog('kubernetes', 'Kubernetes'),
            catalog('youtube', 'YouTube'),
            catalog('desktop', 'Desktop'),
          ],
        })),
    ),
    featureEnable: vi.fn(),
    featureDisable: vi.fn(),
    featureSetCredentials: vi.fn(),
    featureSetup: vi.fn(),
    featureDeleteCredentials: vi.fn(),
    featureWriteGrant: vi.fn(),
    noticeRecord: vi.fn(async () => undefined),
    accessGet: vi.fn(async () => ({ userIds: ['111'] })),
    appOpenExternal: vi.fn(async () => undefined),
    githubStatus: vi.fn(async () => o.github ?? NOT_CONNECTED),
    githubSignIn: vi.fn(async () => undefined),
    githubBotStatus: vi.fn(async () => o.bot ?? BOT_NONE),
    kubernetesStatus: vi.fn(
      async () => o.kubernetes ?? { path: null, contexts: [], connected: false },
    ),
    kubernetesDetect: vi.fn(async () => ({ path: null, contexts: [], error: null })),
    youtubeStatus: vi.fn(
      async () =>
        o.youtube ?? {
          ...YOUTUBE_CONNECTED,
          connected: false,
          channelId: null,
          channelTitle: null,
        },
    ),
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
    vaultStore: vi.fn(async () => undefined),
    workerRestart: vi.fn(async () => ({ outcome: 'now' })),
    onDockerPullProgress: vi.fn(() => () => undefined),
    ...o.extra,
  };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 6; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  await TestBed.inject(ApplicationRef).whenStable();
  fixture.detectChanges();
}

async function screen(o: Options = {}): Promise<ComponentFixture<FeaturesComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: ScoutBridgeService, useValue: bridgeWith(o) as unknown as ScoutBridgeService },
      FeaturesStore,
    ],
  });
  const fixture = TestBed.createComponent(FeaturesComponent);
  await settle(fixture);
  return fixture;
}

function q(fixture: ComponentFixture<unknown>, test: string): HTMLElement {
  const el = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
    `[data-test="${test}"]`,
  );
  if (el === null) throw new Error(`${test} is not on the screen`);
  return el;
}

async function open(
  fixture: ComponentFixture<FeaturesComponent>,
  id: string,
): Promise<HTMLElement> {
  q(fixture, `details-${id}`).click();
  await settle(fixture);
  return q(fixture, `panel-${id}`);
}

function words(el: Element | null): string | null {
  return el === null ? null : (el.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function actionLines(row: Element): (string | null)[] {
  return Array.from(row.querySelectorAll('.what > li'), (li) => words(li));
}

function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

function own(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    if (!selector!.includes('[_ngcontent-') && !selector!.includes('[_nghost-')) continue;
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    if (selector!.split(',').some((one) => node.matches(one.trim()))) value = found;
  }
  return value;
}

function effective(el: Element, property: string): string | null {
  const value = own(el, property);
  const hook = /^var\((--[\w-]+),\s*(.+)\)$/.exec(value ?? '');
  if (hook === null) return value;
  return own(el.closest('sc-collapsible-list')!, hook[1]!) ?? hook[2]!;
}

function paths(el: Element): string[] {
  return Array.from(el.querySelectorAll('svg path'), (p) => p.getAttribute('d') ?? '');
}

async function compileOnce(): Promise<void> {
  const fixture = await screen({ github: CONNECTED, bot: BOT_INSTALLED });
  fixture.destroy();
  TestBed.resetTestingModule();
  document.body.innerHTML = '';
}

await compileOnce();

describe('Features as Features.dc.html draws it: the page', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('Loaded: the column starts at the left edge, at most 1200 px wide, with the lede at full width', async () => {
    const fixture = await screen();
    const page = (fixture.nativeElement as HTMLElement).querySelector('main.page')!;

    expect({
      margin: declared(page, 'margin'),
      maxWidth: declared(page, 'max-width'),
      padding: declared(page, 'padding'),
      ledeMax: declared(q(fixture, 'features-lede'), 'max-width'),
    }).toEqual({ margin: null, maxWidth: '1200px', padding: '24px 32px', ledeMax: null });
  });

  it("every button on the screen is the artboard's: 36 px, a 14 px label at 400, outlined 14 px in, filled white on the strong accent and grey while off", async () => {
    const fixture = await screen();
    const host = fixture.nativeElement as HTMLElement;
    const token = (name: string) => declared(host, `--mat-button-${name}`);

    expect({
      outlined: [
        token('outlined-container-height'),
        token('outlined-horizontal-padding'),
        token('outlined-label-text-size'),
        token('outlined-label-text-weight'),
      ],
      filled: [
        token('filled-container-height'),
        token('filled-horizontal-padding'),
        token('filled-label-text-weight'),
        token('filled-container-color'),
        token('filled-label-text-color'),
      ],
      off: [
        token('filled-disabled-container-color'),
        token('filled-disabled-label-text-color'),
        token('outlined-disabled-label-text-color'),
      ],
      refreshIconGap: declared(
        q(fixture, 'features-refresh').querySelector('sc-icon')!,
        'margin-right',
      ),
    }).toEqual({
      outlined: ['36px', '14px', '14px', '400'],
      filled: ['36px', '16px', '400', 'var(--sc-accent-strong)', 'var(--sc-on-accent)'],
      off: ['var(--sc-surface-2)', 'var(--sc-muted)', 'var(--sc-muted)'],
      refreshIconGap: '8px',
    });
  });

  it('Control plane down: the alert has 14 px corners, the circled exclamation mark in the error colour, and Try again on the surface', async () => {
    const fixture = await screen({
      list: async () => {
        throw new Error(
          `Error invoking remote method 'scout:features.list': Error: ${encodeIpcErrorMessage(serializeError(new ControlPlaneUnavailableError('socket did not appear')))}`,
        );
      },
    });
    const alert = q(fixture, 'backend-unavailable').parentElement!;
    const glyph = alert.querySelector('svg')!;
    const retry = q(fixture, 'features-retry');

    expect({
      radius: declared(alert, 'border-radius'),
      glyph: [glyph.querySelector('circle')?.getAttribute('r'), paths(glyph)],
      glyphColour: declared(glyph, 'color'),
      retryBackground: own(retry, 'background'),
      words: words(retry),
    }).toEqual({
      radius: 'var(--sc-radius)',
      glyph: ['9', ['M12 7v6M12 16.5v.5']],
      glyphColour: 'var(--sc-error)',
      retryBackground: 'var(--sc-surface)',
      words: 'Try again',
    });
  });

  it('Loading: a 4 px bar with 2 px ends, the accent over the second surface, above five skeleton cards', async () => {
    const fixture = await screen({ list: () => new Promise(() => undefined) });
    const loading = q(fixture, 'features-loading');

    expect({
      height: declared(loading, '--mat-progress-bar-track-height'),
      indicator: declared(loading, '--mat-progress-bar-active-indicator-height'),
      ends: declared(loading, '--mat-progress-bar-track-shape'),
      track: declared(loading, '--mat-progress-bar-track-color'),
      bar: declared(loading, '--mat-progress-bar-active-indicator-color'),
      skeletons: loading.querySelectorAll('[data-test="features-skeleton"]').length,
    }).toEqual({
      height: '4px',
      indicator: '4px',
      ends: '2px',
      track: 'var(--sc-surface-2)',
      bar: 'var(--sc-accent)',
      skeletons: 5,
    });
  });
});

describe('Features as Features.dc.html draws it: the cards', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const MARKS: readonly [string, string][] = [
    ['github', 'M9 19c-4 1.5-4-2-6-2.5M15 21v-3.5'],
    ['docker', 'M21 8l-9-5-9 5 9 5zM3 8v8l9 5 9-5V8M12 13v8'],
    ['kubernetes', 'M12 2l8.5 4.5v9L12 20l-8.5-4.5v-9zM12 7v5l4.3 2.5M12 12l-4.3 2.5'],
    ['youtube', 'M3 6h18v12H3zM10 9l5 3-5 3z'],
    ['desktop', 'M3 4h18v12H3zM8 20h8M12 16v4'],
  ];

  it.each(MARKS)('the %s tile draws its stroke mark at 20 px', async (id, start) => {
    const fixture = await screen();
    const logo = q(fixture, `card-${id}`).querySelector(`[data-test="feature-logo-${id}"]`)!;
    const svg = logo.querySelector('svg')!;

    expect({
      mark: paths(svg)[0]!.startsWith(start),
      stroke: svg.getAttribute('stroke-width'),
      size: [declared(svg, 'width'), declared(svg, 'height')],
    }).toEqual({ mark: true, stroke: '1.8', size: ['20px', '20px'] });
  });

  const CHIPS: readonly {
    readonly state: string;
    readonly o: Options;
    readonly id: string;
    readonly chip: string;
    readonly glyph: string;
  }[] = [
    {
      state: 'Connected',
      o: { github: CONNECTED },
      id: 'github',
      chip: 'Connected',
      glyph: 'M20 6L9 17l-5-5',
    },
    { state: 'Not connected', o: {}, id: 'github', chip: 'Not connected', glyph: 'M6 12h12' },
    { state: 'On', o: {}, id: 'desktop', chip: 'On', glyph: 'M20 6L9 17l-5-5' },
    {
      state: 'Docker running',
      o: {},
      id: 'docker',
      chip: 'Running · Docker 28.5.1',
      glyph: 'M20 6L9 17l-5-5',
    },
  ];

  it.each(CHIPS.map((row) => [row.state, row] as const))(
    "Each state, %s: the chip leads with the artboard's glyph, a check for on, a dash for off",
    async (_state, row) => {
      const fixture = await screen(row.o);
      const chip = q(fixture, `badge-${row.id}`);

      expect([words(chip), paths(chip), chip.querySelector('i')]).toEqual([
        row.chip,
        [row.glyph],
        null,
      ]);
    },
  );

  it("a card and a loading skeleton have the theme's card corners, not a radius of their own", async () => {
    const fixture = await screen();
    const card = q(fixture, 'card-github');

    expect(own(card, 'border-radius')).toBe('var(--sc-radius)');
  });

  it("the card buttons are the artboard's, 12 px in at the sides, the Details icon 6 px from its label", async () => {
    const fixture = await screen();
    const details = q(fixture, 'details-github');
    const act = q(fixture, 'act-github');

    expect({
      details: declared(details, '--mat-button-outlined-horizontal-padding'),
      act: [
        declared(act, '--mat-button-outlined-horizontal-padding'),
        declared(act, '--mat-button-filled-horizontal-padding'),
      ],
      gap: declared(details.querySelector('svg')!, 'margin-right'),
    }).toEqual({ details: '12px', act: ['12px', '12px'], gap: '6px' });
  });
});

describe('Features as Features.dc.html draws it: the side panel', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('Panel: GitHub: the close button is 36 px round with a line border and the X glyph; the chip leads with its glyph; the sheet casts its shadow to the left', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'github');
    const close = q(fixture, 'feature-panel-close');
    const panel = q(fixture, 'feature-panel');

    expect({
      size: declared(close, '--mat-icon-button-state-layer-size'),
      border: own(close, 'border'),
      glyph: paths(close),
      chip: [words(q(fixture, 'feature-panel-chip')), paths(q(fixture, 'feature-panel-chip'))],
      shadow: declared(panel, 'box-shadow'),
    }).toEqual({
      size: '36px',
      border: '1px solid var(--sc-line)',
      glyph: ['M6 6l12 12M18 6L6 18'],
      chip: ['Connected', ['M20 6L9 17l-5-5']],
      shadow: '-16px 0 40px var(--sc-shadow)',
    });
  });

  it('a card keeps the fact its panel reads, with the panel never opened, and after it opens and closes', async () => {
    const fixture = await screen({ github: CONNECTED });
    const before = words(q(fixture, 'fact-github'));
    await open(fixture, 'github');
    q(fixture, 'feature-panel-close').click();
    await settle(fixture);

    expect([before, words(q(fixture, 'fact-github'))]).toEqual([
      'Connected as acme-owner · Writes not allowed',
      'Connected as acme-owner · Writes not allowed',
    ]);
  });
});

@Component({
  standalone: true,
  imports: [FeaturePanelComponent],
  template: `
    <div data-test="pane" style="overflow-y: auto; height: 200px">
      <div style="overflow-y: scroll">
        <sc-feature-panel [open]="open()" logo="github" name="GitHub" description="d">
          <button type="button">Inside</button>
        </sc-feature-panel>
      </div>
    </div>
  `,
})
class ScrollingHost {
  readonly open = signal(false);
}

describe('the side panel holds the page still while it is open', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('opening stops every box around it from scrolling; closing gives each its own overflow back', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(ScrollingHost);
    await settle(fixture);
    const pane = q(fixture, 'pane');
    const inner = pane.firstElementChild as HTMLElement;
    const closed = [pane.style.overflowY, inner.style.overflowY];

    fixture.componentInstance.open.set(true);
    await settle(fixture);
    const opened = [pane.style.overflowY, inner.style.overflowY];
    fixture.componentInstance.open.set(false);
    await settle(fixture);

    expect([closed, opened, [pane.style.overflowY, inner.style.overflowY]]).toEqual([
      ['auto', 'scroll'],
      ['hidden', 'hidden'],
      ['auto', 'scroll'],
    ]);
  });

  it('a panel removed while open gives the page its scroll back', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(ScrollingHost);
    fixture.componentInstance.open.set(true);
    await settle(fixture);
    const pane = q(fixture, 'pane');
    const locked = pane.style.overflowY;

    fixture.destroy();

    expect([locked, pane.style.overflowY]).toEqual(['hidden', 'auto']);
  });
});

describe('Features as Features.dc.html draws it: Reads, Permissions and the grant box', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('GitHub connected, writes not allowed: the grant box on the page colour with a line border, the grey chip, Allow writes filled and its reason on its own line', async () => {
    const fixture = await screen({ github: CONNECTED });
    const panel = await open(fixture, 'github');
    const chip = q(fixture, 'grant-github');
    const box = chip.parentElement!;
    const allow = panel.querySelector('[data-test="allow-writes-github"]')!;

    expect({
      box: [declared(box, 'background'), declared(box, 'border'), declared(box, 'border-radius')],
      chip: [
        words(chip),
        declared(chip, 'background'),
        declared(chip, 'color'),
        declared(chip, 'border-radius'),
      ],
      allow: [allow.classList.contains('mat-mdc-unelevated-button'), words(allow)],
      reason: [
        words(q(fixture, 'grant-reason-github')),
        declared(q(fixture, 'grant-reason-github'), 'flex-basis'),
      ],
      headings: [
        words(q(fixture, 'reads-summary-github')),
        words(q(fixture, 'permissions-summary-github')),
      ],
    }).toEqual({
      box: ['var(--sc-bg)', '1px solid var(--sc-line)', '10px'],
      chip: ['Write permission: not granted', 'var(--sc-surface-2)', 'var(--sc-fg-2)', '12px'],
      allow: [true, 'Allow writes'],
      reason: ['You review the list before anything is allowed.', '100%'],
      headings: ['Reads 5', 'Permissions 8'],
    });
  });

  it('GitHub connected, writes allowed: the chip on the ok pair and Revoke writes outlined', async () => {
    const fixture = await screen({
      github: CONNECTED,
      list: async () => ({ features: [catalog('github', 'GitHub', true)] }),
    });
    const panel = await open(fixture, 'github');
    const chip = q(fixture, 'grant-github');
    const revoke = panel.querySelector('[data-test="revoke-writes-github"]')!;

    expect({
      chip: [words(chip), declared(chip, 'background'), declared(chip, 'color')],
      revoke: [revoke.classList.contains('mat-mdc-outlined-button'), words(revoke)],
      reason: panel.querySelector('[data-test="grant-reason-github"]'),
    }).toEqual({
      chip: ['Write permission: granted', 'var(--sc-ok-container)', 'var(--sc-on-ok-container)'],
      revoke: [true, 'Revoke writes'],
      reason: null,
    });
  });
});

describe("Features as Features.dc.html draws it: each panel's description, Reads and Permissions in the artboard's words", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const PANELS = [
    {
      id: 'github',
      chips: ['Read 5', 'Write 3', 'Cannot be undone 5'],
      description:
        'Query repositories, issues and pull requests from chat; review a pull request and audit a whole project. With your write permission it also posts reviews and comments, opens issues and gives verdicts, each one only after you approve it.',
      reads: [
        ['Metadata', 'List the repositories the Scout GitHub App is installed on'],
        [
          'Contents',
          'Read files, directories and languages; list and search every file to audit a project',
        ],
        [
          'Pull requests',
          'List and read pull requests, their diffs, comments, checks and reviews, and the account Scout signs in as',
        ],
        ['Issues', 'List open issues and read one issue'],
        [
          'Notifications',
          "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name",
        ],
      ],
      permissions: [
        [
          "Scout's data folder",
          'Write',
          'Keep conventions profiles, repository snapshots and audit jobs on this computer',
        ],
        [
          'Pull request reviews',
          'Cannot be undone',
          'Post a review with comments; your click on Post on a review card is the approval, and one asked in chat waits for you in Approvals',
        ],
        [
          'Pull request verdicts',
          'Cannot be undone',
          'Approve a pull request or request changes; your choice on a review card is the approval, and one asked in chat waits for you in Approvals',
        ],
        [
          'Pull request comments',
          'Cannot be undone',
          'Comment on a pull request or reply to a review comment; each one waits for you in Approvals',
        ],
        [
          'New issues',
          'Cannot be undone',
          'Open an issue; your click on Open issues on a findings card is the approval, and one asked in chat waits for you in Approvals',
        ],
        [
          'Issue comments',
          'Cannot be undone',
          'Comment on an existing issue; each one waits for you in Approvals',
        ],
        [
          'Local container',
          'Write',
          "Run a pull request's tests in a disposable container on this computer",
        ],
        [
          'Scout bot on GitHub (optional)',
          'Write',
          "Sign what you switched to the bot with its token, on the repositories you installed it on; you create the bot on GitHub's page and its key stays on this computer",
        ],
      ],
    },
    {
      id: 'kubernetes',
      chips: ['Read 4'],
      description:
        'Read pods, deployments and events from your clusters. Uses the Kubernetes configuration on this computer, with your consent; read-only.',
      reads: KUBERNETES_READS.map(([label, what]) => [label, what]),
      permissions: null,
    },
    {
      id: 'youtube',
      chips: ['Read 8', 'Write 2', 'Cannot be undone 6'],
      description:
        "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after you sign in. With your write permission it also produces videos under Scout's folder and publishes what you approve.",
      reads: [
        ['Channel overview', 'Title, handle, subscribers, total views and video count'],
        ['Latest uploads', 'The latest uploads of a channel'],
        ['Video statistics', 'Title, duration, views, likes and comment count'],
        ['Search', 'Search YouTube for videos on a topic'],
        ['Comments', 'The comments on your videos'],
        ['Analytics', 'Views, watch time, retention and subscribers gained'],
        ['Reach reports', 'Thumbnail impressions and click-through rate'],
        [
          'Free-licence pictures',
          'Find a CC0, public domain or CC BY picture for a scene on Openverse and download it',
        ],
      ],
      permissions: [
        [
          "Scout's data folder",
          'Write',
          'Save scripts, narration, images, the scene list, the video and thumbnails, and the recording you attach with its transcript',
        ],
        ['Reach reporting job', 'Write', "Create the channel's reach reporting job once"],
        ['Upload a video', 'Cannot be undone', 'Public, private, unlisted or scheduled'],
        ['Video details', 'Cannot be undone', "Change a video's title during a title test"],
        ['Thumbnail', 'Cannot be undone', "Set a video's thumbnail"],
        ['Add to a playlist', 'Cannot be undone', 'Add a video to a playlist'],
        ['Reply to a comment', 'Cannot be undone', 'Publish a reply to a comment'],
        ['Moderate a comment', 'Cannot be undone', 'Hold a comment for your review on YouTube'],
      ],
    },
    {
      id: 'desktop',
      chips: ['Read 3'],
      description:
        'What Scout can see and hear on this computer when you ask from the desktop. Nothing is captured unless you click.',
      reads: [
        ['Screen', 'One screenshot you attach to a question, and the text read from it'],
        ['Microphone', 'Your voice while you hold to talk, listen or record a meeting'],
        ['System audio', "The computer's sound while you keep System audio on or record a meeting"],
      ],
      permissions: null,
    },
  ] as const;

  it.each(PANELS.map((panel) => [panel.id, panel] as const))(
    "%s: the card's chips, then in its panel the description, Reads n with each row's label and one sentence, and Permissions n with each row's label, chip and one sentence",
    async (id, expected) => {
      const fixture = await screen();
      const chips = Array.from(q(fixture, `perms-${id}`).querySelectorAll('li'), (li) => words(li));
      const panel = await open(fixture, id);
      const rows = (test: string, parts: readonly string[]) => {
        const section = panel.querySelector(`[data-test="${test}"]`);
        return section === null
          ? null
          : Array.from(section.querySelectorAll('li'), (li) =>
              parts.map((part) => words(li.querySelector(part))),
            );
      };

      expect({
        chips,
        description: words(q(fixture, 'feature-panel-desc')),
        readsHeading: words(q(fixture, `reads-summary-${id}`)),
        reads: rows(`reads-${id}`, ['.label', '.what']),
        permissionsHeading: words(panel.querySelector(`[data-test="permissions-summary-${id}"]`)),
        permissions: rows(`permissions-${id}`, ['.label', '.access', '.what']),
      }).toEqual({
        chips: expected.chips,
        description: expected.description,
        readsHeading: `Reads ${expected.reads.length}`,
        reads: expected.reads,
        permissionsHeading:
          expected.permissions === null ? null : `Permissions ${expected.permissions.length}`,
        permissions: expected.permissions,
      });
    },
  );

  it('Allow writes on YouTube still lists every write with every one of its actions in full, never the short sentence', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(WriteConfirmationDialogComponent);
    fixture.componentInstance.feature = catalog('youtube', 'YouTube');
    await settle(fixture);
    const box = q(fixture, 'write-confirmation-dialog');

    expect(
      Array.from(box.querySelectorAll('[data-test^="write-row-"]'), (row) => [
        words(row.querySelector('.label')),
        words(row.querySelector('.access')),
        ...actionLines(row),
      ]),
    ).toEqual([
      [
        "Scout's data folder",
        'Write',
        'Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript',
      ],
      [
        'Reach reporting job',
        'Write',
        "Create the channel's reach reporting job once, so YouTube prepares the reach reports",
      ],
      [
        'Upload a video',
        'Cannot be undone',
        'Upload a video as public, private or unlisted, or as private with a time YouTube publishes it',
      ],
      [
        'Video details',
        'Cannot be undone',
        "Change a video's title during a title test: to each title the test tries, and then to the title kept when it ends",
      ],
      ['Thumbnail', 'Cannot be undone', "Set a video's thumbnail"],
      ['Add to a playlist', 'Cannot be undone', 'Add a video to a playlist'],
      ['Reply to a comment', 'Cannot be undone', 'Publish a reply to a comment'],
      [
        'Moderate a comment',
        'Cannot be undone',
        'Hold a comment for your review on YouTube; Scout never publishes or rejects a comment',
      ],
    ]);
  });
});

describe('Features as Features.dc.html draws it: the Allow writes confirmation', () => {
  beforeEach(() => TestBed.resetTestingModule());

  async function dialog() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(WriteConfirmationDialogComponent);
    fixture.componentInstance.feature = catalog('github', 'GitHub');
    let allowed = 0;
    let cancelled = 0;
    fixture.componentInstance.allow.subscribe(() => (allowed += 1));
    fixture.componentInstance.cancel.subscribe(() => (cancelled += 1));
    await settle(fixture);
    return { fixture, counts: () => ({ allowed, cancelled }) };
  }

  it('a 520 px card 64 px from the top with 14 px corners, never taller than the window: a 17 px title, the writes as rows with what each does, Cancel and Allow these writes at the end', async () => {
    const { fixture } = await dialog();
    const box = q(fixture, 'write-confirmation-dialog');
    const scrim = box.parentElement!;
    const rows = Array.from(box.querySelectorAll('[data-test^="write-row-"]'), (row) => [
      words(row.querySelector('.label')),
      words(row.querySelector('.access')),
      actionLines(row),
    ]);

    expect({
      shadow: declared(box, 'box-shadow'),
      box: [
        declared(box, 'width'),
        declared(box, 'max-height'),
        declared(box, 'border-radius'),
        declared(scrim, 'padding'),
      ],
      title: [words(box.querySelector('h2')), declared(box.querySelector('h2')!, 'font-size')],
      rows,
      described: document.getElementById(box.getAttribute('aria-describedby') ?? '')?.textContent,
      buttons: Array.from(box.querySelectorAll('footer button'), (b) => words(b)),
      table: box.querySelector('table'),
    }).toEqual({
      box: ['520px', 'min(760px, 100%)', 'var(--sc-radius)', '64px 16px 16px'],
      shadow: '0 20px 48px var(--sc-shadow)',
      title: ['Allow GitHub to write?', '17px'],
      rows: CATALOG.github.rows
        .filter((row) => row.access !== 'read')
        .map((row) => [
          row.scope,
          row.access === 'irreversible' ? 'Cannot be undone' : 'Write',
          [...row.actions],
        ]),
      described:
        'Scout will be allowed to perform these writes on your behalf. Actions marked irreversible will still wait for your approval, one by one, in Approvals.',
      buttons: ['Cancel', 'Allow these writes'],
      table: null,
    });
  });

  it('a write with two actions shows each action on its own line, in order: GitHub Pull request verdicts and New issues', async () => {
    const { fixture } = await dialog();
    const row = (scope: string) => q(fixture, `write-row-${scope}`);

    expect({
      verdicts: actionLines(row('Pull request verdicts')),
      issues: actionLines(row('New issues')),
    }).toEqual({
      verdicts: [
        "Approve a pull request or request changes on it when you choose that verdict on the review card; a verdict stays in the pull request's history, so each one runs only after you approve that action in Scout Desktop",
        'Approve a pull request or request changes on it when you ask for it in chat; each verdict waits for you to approve it in Scout Desktop',
      ],
      issues: [
        "Open an issue on a repository when you ask for it in chat; an issue notifies the repository's watchers and stays in its history, so each one waits for you to approve that issue in Scout Desktop",
        'Open the issues a findings card lists when you click Open issues on it; that click is your approval of exactly those issues',
      ],
    });
  });

  it("a write with one action shows one line, and the lines keep the sentence's look: muted, no bullet, 4 px apart", async () => {
    const { fixture } = await dialog();
    const row = q(fixture, 'write-row-Issue comments');
    const list = row.querySelector('.what')!;

    expect({
      lines: actionLines(row),
      list: [
        list.tagName,
        declared(list, 'color'),
        declared(list, 'list-style'),
        declared(list, 'padding'),
        declared(list, 'gap'),
      ],
    }).toEqual({
      lines: [
        'Comment on an existing issue when you ask for it in chat; a comment notifies everyone following the issue, so each one waits for you to approve that comment in Scout Desktop',
      ],
      list: ['UL', 'var(--sc-muted)', 'none', '0', '4px'],
    });
  });

  it('Esc cancels, as Cancel does, and never allows', async () => {
    const { fixture, counts } = await dialog();

    q(fixture, 'write-confirmation-allow').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );

    expect(counts()).toEqual({ allowed: 0, cancelled: 1 });
  });

  it('holds the focus: tabbing past Allow these writes comes back to its first stop, the list of writes', async () => {
    const { fixture } = await dialog();
    const box = q(fixture, 'write-confirmation-dialog');
    const anchors = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '.cdk-focus-trap-anchor',
      ),
    );
    const after = anchors.find(
      (a) => box.compareDocumentPosition(a) & Node.DOCUMENT_POSITION_FOLLOWING,
    );
    q(fixture, 'write-confirmation-allow').focus();

    after?.focus();

    expect([anchors.length, document.activeElement?.getAttribute('data-test')]).toEqual([
      2,
      'write-confirmation-list',
    ]);
  });
});

describe('Features as Features.dc.html draws it: the GitHub panel', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it("the bot's repositories list keeps the shared list's look: 22 px indent, 8 px in its body, rows 4 px apart, scrolling inside 256 px past eight rows", async () => {
    const repositories = Array.from({ length: 10 }, (_, i) => `acme/repo-${i}`);
    const fixture = await screen({ github: CONNECTED, bot: { ...BOT_INSTALLED, repositories } });
    await open(fixture, 'github');
    q(fixture, 'list-toggle-github-bot-repos').click();
    await settle(fixture);
    const body = q(fixture, 'list-body-github-bot-repos');
    const rows = body.querySelector('.rows')!;

    expect({
      padding: effective(body, 'padding'),
      gap: effective(body, 'gap'),
      rows: effective(rows, 'gap'),
      cap: [effective(rows, 'max-height'), own(rows, 'overflow-y')],
    }).toEqual({
      padding: '4px 0 0 22px',
      gap: '8px',
      rows: '4px',
      cap: ['256px', 'auto'],
    });
  });

  it("kept: the bot's repositories list still starts folded behind its count when the GitHub panel opens", async () => {
    const fixture = await screen({ github: CONNECTED, bot: BOT_INSTALLED });
    await open(fixture, 'github');

    expect({
      expanded: q(fixture, 'list-toggle-github-bot-repos').getAttribute('aria-expanded'),
      body: (fixture.nativeElement as HTMLElement).querySelector(
        '[data-test="list-body-github-bot-repos"]',
      ),
    }).toEqual({ expanded: 'false', body: null });
  });

  it('Panel: GitHub, not connected: Connect with GitHub and Use a token side by side, the one-time-code line under them', async () => {
    const fixture = await screen();
    await open(fixture, 'github');
    const connect = q(fixture, 'github-connect');
    const token = q(fixture, 'github-use-token');

    expect({
      sameRow: connect.parentElement === token.parentElement,
      token: [words(token), token.querySelector('circle')?.getAttribute('r')],
      help: words(q(fixture, 'github-connect-help')),
    }).toEqual({
      sameRow: true,
      token: ['Use a token', '4'],
      help: 'Scout shows a one-time code; you enter it on GitHub and approve the Scout GitHub App there.',
    });
  });

  it('GitHub connected: "Connected as" with the login in bold, "installed on 6 repositories" after it, and Disconnect on the same row, in the error colour with the unplugged glyph', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'github');
    const who = q(fixture, 'github-connected');
    const disconnect = q(fixture, 'github-disconnect');

    expect({
      who: [words(who), words(who.querySelector('strong'))],
      sameRow: who.parentElement === disconnect.parentElement,
      glyph: paths(disconnect),
      colour: declared(disconnect, '--mat-button-outlined-label-text-color'),
    }).toEqual({
      who: ['Connected as acme-owner · installed on 6 repositories', 'acme-owner'],
      sameRow: true,
      glyph: ['M9 7V3M15 7V3M7 7h10v4a5 5 0 0 1-10 0zM12 16v5M3 3l18 18'],
      colour: 'var(--sc-error)',
    });
  });

  it('the bot, none yet: the lede at 13 px in the second text colour, Create the bot as an outlined button with the robot glyph', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'github');
    const lede = q(fixture, 'github-bot-help');
    const create = q(fixture, 'github-bot-create');

    expect({
      lede: [declared(lede, 'font-size'), declared(lede, 'color'), declared(lede, 'line-height')],
      create: [
        create.classList.contains('mat-mdc-outlined-button'),
        create.querySelector('rect')?.getAttribute('rx'),
      ],
    }).toEqual({
      lede: ['13px', 'var(--sc-fg-2)', '1.5'],
      create: [true, '3'],
    });
  });

  it('the bot installed: the lede stays, and each action it signs is a switch at the right of its label, in a list named for what the bot signs', async () => {
    const fixture = await screen({ github: CONNECTED, bot: BOT_INSTALLED });
    const panel = await open(fixture, 'github');
    const list = panel.querySelector('ul[aria-label="What the bot signs"]')!;
    const review = q(fixture, 'github-bot-write-review') as HTMLInputElement;
    const label = review.parentElement!;

    expect({
      lede: words(panel.querySelector('[data-test="github-bot-help"]')),
      rows: list.querySelectorAll('li').length,
      switch: [review.getAttribute('role'), review.checked, label.lastElementChild === review],
      track: [
        declared(review, 'width'),
        declared(review, 'height'),
        declared(review, 'border-radius'),
      ],
      on: declared(review, 'background'),
    }).toEqual({
      lede: 'Reviews, verdicts, comments and issues Scout posts show as the bot instead of you. GitHub creates an App that you own; Scout keeps its key on this computer.',
      rows: 4,
      switch: ['switch', true, true],
      track: ['40px', '24px', '12px'],
      on: 'var(--sc-accent-strong)',
    });
  });
});

describe('Features as Features.dc.html draws it: Docker, Kubernetes and YouTube', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it("Panel: Docker: the switch sits at the right of its label and help, named by the label, on the artboard's 40 by 24 track", async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'docker');
    const toggle = q(fixture, 'docker-switch');
    const row = toggle.parentElement!;
    const button = toggle.querySelector('button[role="switch"]')!;

    expect({
      last: row.lastElementChild === toggle,
      named: document
        .getElementById(button.getAttribute('aria-labelledby') ?? '')
        ?.textContent?.trim(),
      track: [
        declared(toggle, '--mat-slide-toggle-track-width'),
        declared(toggle, '--mat-slide-toggle-track-height'),
      ],
      on: declared(toggle, '--mat-slide-toggle-selected-track-color'),
    }).toEqual({
      last: true,
      named: "Run a project's tests in isolated containers",
      track: ['40px', '24px'],
      on: 'var(--sc-accent-strong)',
    });
  });

  it("Panel: Docker: an image chip leads with a check when downloaded and a dot when not; a stack's Download is 32 px", async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'docker');
    if (q(fixture, 'docker-toggle-images').getAttribute('aria-expanded') !== 'true') {
      q(fixture, 'docker-toggle-images').click();
      await settle(fixture);
    }
    const chip = (ref: string) => q(fixture, `docker-image-${ref}`).querySelector('.chip')!;
    const download = q(fixture, 'docker-download-Node');

    expect({
      present: [
        words(chip('mcr.microsoft.com/dotnet/sdk:10.0')),
        paths(chip('mcr.microsoft.com/dotnet/sdk:10.0')),
      ],
      missing: [words(chip('node:22')), paths(chip('node:22'))],
      height: declared(download, '--mat-button-outlined-container-height'),
    }).toEqual({
      present: ['Downloaded', ['M20 6L9 17l-5-5']],
      missing: ['Not downloaded', ['M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z']],
      height: '32px',
    });
  });

  it('Kubernetes connected: "Using" the labels in bold and Disconnect on the same row with the unplugged glyph', async () => {
    const fixture = await screen({ kubernetes: KUBERNETES_CONNECTED });
    await open(fixture, 'kubernetes');
    const who = q(fixture, 'kubernetes-connected');
    const disconnect = q(fixture, 'kubernetes-disconnect');

    expect({
      who: [words(who), words(who.querySelector('strong'))],
      sameRow: who.parentElement === disconnect.parentElement,
      glyph: paths(disconnect),
    }).toEqual({
      who: ['Using shop-prod from /home/owner/.kube/config', 'shop-prod'],
      sameRow: true,
      glyph: ['M9 7V3M15 7V3M7 7h10v4a5 5 0 0 1-10 0zM12 16v5M3 3l18 18'],
    });
  });

  it('Kubernetes panel, connected: Reads 4 with the four rows as the artboard draws them, after the connection and with no grant box', async () => {
    const fixture = await screen({ kubernetes: KUBERNETES_CONNECTED });
    const panel = await open(fixture, 'kubernetes');
    const reads = q(fixture, 'reads-kubernetes');
    const row = reads.querySelector('li')!;

    expect({
      heading: words(q(fixture, 'reads-summary-kubernetes')),
      rows: Array.from(reads.querySelectorAll('li'), (li) => [
        words(li.querySelector('.label')),
        words(li.querySelector('.what')),
      ]),
      after:
        q(fixture, 'kubernetes-connect-section').compareDocumentPosition(reads) &
        Node.DOCUMENT_POSITION_FOLLOWING,
      look: [
        own(row, 'gap'),
        own(row, 'font-size'),
        own(row.querySelector('.label')!, 'font-weight'),
        own(row.querySelector('.what')!, 'color'),
      ],
      grant: panel.querySelector('[data-test="permissions-kubernetes"]'),
    }).toEqual({
      heading: 'Reads 4',
      rows: KUBERNETES_READS.map(([label, what]) => [label, what]),
      after: Node.DOCUMENT_POSITION_FOLLOWING,
      look: ['2px', '13px', '500', 'var(--sc-muted)'],
      grant: null,
    });
  });

  it('YouTube connected: the channel in bold over "Signed in with Google", Disconnect with the unplugged glyph', async () => {
    const fixture = await screen({ youtube: YOUTUBE_CONNECTED });
    await open(fixture, 'youtube');
    const who = q(fixture, 'youtube-connected');

    expect({
      who: [words(who), words(who.querySelector('strong'))],
      method: who.nextElementSibling?.getAttribute('data-test'),
      glyph: paths(q(fixture, 'youtube-disconnect')),
    }).toEqual({
      who: ['Connected to Acme Tech Explained', 'Acme Tech Explained'],
      method: 'youtube-method',
      glyph: ['M9 7V3M15 7V3M7 7h10v4a5 5 0 0 1-10 0zM12 16v5M3 3l18 18'],
    });
  });

  it('YouTube not connected: Sign in with Google and Add an API key side by side', async () => {
    const fixture = await screen({
      youtube: { ...YOUTUBE_CONNECTED, connected: false, channelId: null, channelTitle: null },
    });
    await open(fixture, 'youtube');
    const signIn = q(fixture, 'youtube-sign-in');
    const key = q(fixture, 'youtube-use-key');

    expect([signIn.parentElement === key.parentElement, words(key)]).toEqual([
      true,
      'Add an API key',
    ]);
  });
});

function repo(name: string, over: Partial<Record<string, unknown>> = {}) {
  return {
    repository: name,
    commit: 'abc',
    stack: 'Node',
    image: 'node:22',
    runs: 'Node 22.1.0',
    declared: [{ source: '.nvmrc', version: 'Node 22' }],
    refusal: null,
    detectedAt: '2026-11-07T10:00:00Z',
    ...over,
  };
}

async function openSection(fixture: ComponentFixture<FeaturesComponent>, id: string) {
  if (q(fixture, `docker-toggle-${id}`).getAttribute('aria-expanded') !== 'true') {
    q(fixture, `docker-toggle-${id}`).click();
    await settle(fixture);
  }
}

function toasts(): string[] {
  return Array.from(
    document.querySelectorAll<HTMLElement>('.cdk-overlay-container .sc-notice'),
    (el) => (el.textContent ?? '').trim(),
  );
}

describe('Features as Features.dc.html draws it, round 1: Docker repositories', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('Panel: Docker: a repository row is the name over its detail line, the image chip, and Download for the image named for the repository', async () => {
    const fixture = await screen({
      github: CONNECTED,
      extra: {
        dockerRepositories: vi.fn(async () => ({
          repositories: [
            repo('acme/web'),
            repo('acme/legacy', {
              image: null,
              runs: null,
              refusal: 'This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.',
            }),
          ],
        })),
      },
    });
    await open(fixture, 'docker');
    await openSection(fixture, 'repositories');
    const web = q(fixture, 'docker-repository-acme/web');
    const legacy = q(fixture, 'docker-repository-acme/legacy');
    const download = web.querySelector('[data-test="docker-repository-download"]')!;

    expect({
      name: [
        words(web.querySelector('.repo-name')),
        own(web.querySelector('.repo-name')!, 'font-weight'),
      ],
      detail: words(web.querySelector('.repo-detail')),
      chip: [
        words(web.querySelector('[data-test="docker-repository-image"]')),
        paths(web.querySelector('[data-test="docker-repository-image"]')!),
      ],
      download: [
        download.getAttribute('aria-label'),
        declared(download, '--mat-button-outlined-container-height'),
      ],
      refused: [
        words(legacy.querySelector('[data-test="docker-repository-unsupported"]')),
        paths(legacy.querySelector('[data-test="docker-repository-unsupported"]')!),
        own(legacy.querySelector('.repo-detail')!, 'color'),
      ],
      help: words(q(fixture, 'docker-body-repositories').querySelector('.help')),
    }).toEqual({
      name: ['acme/web', '600'],
      detail: 'Node · Runs on Node 22.1.0. .nvmrc asks for Node 22.',
      chip: ['Image not downloaded', ['M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z']],
      download: ['Download the image for acme/web', '32px'],
      refused: ['Not supported', ['M12 3l9 16H3zM12 10v4M12 17v.5'], 'var(--sc-on-warn-container)'],
      help: 'The stack and versions Scout detected when it read each repository.',
    });
  });

  it('Panel: Docker, long repository list: every row is drawn, the list is not cut, and the panel body is what scrolls', async () => {
    const many = Array.from({ length: 28 }, (_, i) => repo(`acme/repo-${i}`));
    const fixture = await screen({
      github: CONNECTED,
      extra: { dockerRepositories: vi.fn(async () => ({ repositories: many })) },
    });
    await open(fixture, 'docker');
    await openSection(fixture, 'repositories');
    const list = q(fixture, 'docker-repositories');
    const body = q(fixture, 'feature-panel').querySelector('.body')!;

    expect({
      rows: list.querySelectorAll('li.repo').length,
      listBound: [own(list, 'max-height'), own(list, 'overflow'), own(list, 'overflow-y')],
      body: [own(body, 'overflow-y'), own(body, 'min-height'), own(body, 'flex')],
    }).toEqual({
      rows: 28,
      listBound: [null, null, null],
      body: ['auto', '0', '1'],
    });
  });

  it('none read yet: the dashed box with the folder mark and the sentence', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'docker');
    await openSection(fixture, 'repositories');
    const empty = q(fixture, 'docker-repositories-empty');

    expect([words(empty), paths(empty), own(empty, 'border')]).toEqual([
      "Scout lists a repository's stack the first time it reads it.",
      ['M4 4h6l2 2h8v14H4zM4 10h16'],
      '1px dashed var(--sc-line)',
    ]);
  });
});

describe('Features as Features.dc.html draws it, round 1: Kubernetes before it is connected', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  function detected(count: number) {
    const names = ['dev', ...Array.from({ length: count - 1 }, (_, i) => `staging-${i + 1}`)];
    return {
      kubernetesDetect: vi.fn(async () => ({
        path: '/home/owner/.kube/config',
        contexts: names.map((name) => ({
          name,
          cluster: name,
          server: `https://${name}.example.com`,
          auth: 'token',
        })),
        error: null,
      })),
    };
  }

  async function connectForm(contexts = 1): Promise<ComponentFixture<FeaturesComponent>> {
    const fixture = await screen({ extra: detected(contexts) });
    await open(fixture, 'kubernetes');
    q(fixture, 'kubernetes-context-dev').click();
    await settle(fixture);
    return fixture;
  }

  it('Kubernetes panel, not connected: Reads 4 under the connect form, with no grant box', async () => {
    const fixture = await connectForm();
    const reads = q(fixture, 'reads-kubernetes');

    expect({
      heading: words(q(fixture, 'reads-summary-kubernetes')),
      rows: Array.from(reads.querySelectorAll('li'), (li) => [
        words(li.querySelector('.label')),
        words(li.querySelector('.what')),
      ]),
      under:
        q(fixture, 'kubernetes-copy-commands').compareDocumentPosition(reads) &
        Node.DOCUMENT_POSITION_FOLLOWING,
      grant: q(fixture, 'panel-kubernetes').querySelector('[data-test="permissions-kubernetes"]'),
    }).toEqual({
      heading: 'Reads 4',
      rows: KUBERNETES_READS.map(([label, what]) => [label, what]),
      under: Node.DOCUMENT_POSITION_FOLLOWING,
      grant: null,
    });
  });

  it('the connect form keeps 10 px between its parts', async () => {
    const fixture = await connectForm();

    expect(own(q(fixture, 'kubernetes-connect-section'), 'gap')).toBe('10px');
  });

  it("a context's own lines sit 6 px apart: the context, its command line and its Label field", async () => {
    const fixture = await connectForm();
    const lines = q(fixture, 'list-row-kubernetes-contexts').querySelector('.context-lines')!;

    expect([own(lines, 'display'), own(lines, 'flex-direction'), own(lines, 'gap')]).toEqual([
      'flex',
      'column',
      '6px',
    ]);
  });

  it("a context's Label field is 10 px in at the sides", async () => {
    const fixture = await connectForm();

    expect(own(q(fixture, 'kubernetes-label-dev'), 'padding')).toBe('0 10px');
  });

  it('the read-only user commands are in the second text colour', async () => {
    const fixture = await connectForm();

    expect(own(q(fixture, 'kubernetes-read-only-commands'), 'color')).toBe('var(--sc-fg-2)');
  });

  it('the contexts list has no indent: its body starts 10 px under the header, flush with it', async () => {
    const fixture = await connectForm();

    expect(effective(q(fixture, 'list-body-kubernetes-contexts'), 'padding')).toBe('10px 0 0');
  });

  it('the contexts list keeps 10 px between the filter and the contexts', async () => {
    const fixture = await connectForm();

    expect(effective(q(fixture, 'list-body-kubernetes-contexts'), 'gap')).toBe('10px');
  });

  it('the contexts sit 6 px apart', async () => {
    const fixture = await connectForm();
    const rows = q(fixture, 'list-body-kubernetes-contexts').querySelector('.rows')!;

    expect(effective(rows, 'gap')).toBe('6px');
  });

  it('a long list of 30 contexts has no height cap of its own: every row is drawn and the panel body is what scrolls', async () => {
    const fixture = await connectForm(30);
    const rows = q(fixture, 'list-body-kubernetes-contexts').querySelector('.rows')!;
    const body = q(fixture, 'feature-panel').querySelector('.body')!;

    expect({
      rows: rows.querySelectorAll('li').length,
      cap: effective(rows, 'max-height'),
      body: [own(body, 'overflow-y'), own(body, 'min-height'), own(body, 'flex')],
    }).toEqual({ rows: 30, cap: 'none', body: ['auto', '0', '1'] });
  });

  it('the path in a code pill, then "Contexts: n" open when the panel opens (the owner: the Kubernetes detail shows the contexts it found), the filter always shown, each context a row on the page colour', async () => {
    const fixture = await screen({
      extra: {
        kubernetesDetect: vi.fn(async () => ({
          path: '/home/owner/.kube/config',
          contexts: [
            { name: 'dev', cluster: 'dev', server: 'https://dev.example.com', auth: 'token' },
          ],
          error: null,
        })),
      },
    });
    await open(fixture, 'kubernetes');
    const path = q(fixture, 'kubernetes-path');
    const row = q(fixture, 'list-row-kubernetes-contexts');

    expect({
      path: [own(path, 'background'), own(path, 'padding'), own(path, 'border-radius')],
      expanded: q(fixture, 'list-toggle-kubernetes-contexts').getAttribute('aria-expanded'),
      count: words(q(fixture, 'list-summary-kubernetes-contexts')),
      filter: (q(fixture, 'list-filter-kubernetes-contexts') as HTMLInputElement).type,
      row: [
        declared(row, '--sc-list-row-bg') ?? own(row, 'background'),
        own(row, 'padding'),
        own(row, 'border-radius'),
      ],
      hooks: [
        own(row.closest('sc-collapsible-list')!, '--sc-list-row-bg'),
        own(row.closest('sc-collapsible-list')!, '--sc-list-row-padding'),
      ],
    }).toEqual({
      path: ['var(--sc-code-bg)', '6px 10px', '8px'],
      expanded: 'true',
      count: 'Contexts: 1 (tick the ones Scout may read)',
      filter: 'search',
      row: ['var(--sc-list-row-bg, var(--sc-bg))', 'var(--sc-list-row-padding, 6px 10px)', '8px'],
      hooks: ['var(--sc-bg)', '8px 10px'],
    });
  });
});

describe('Features as Features.dc.html draws it, round 1: the credential dialog', () => {
  beforeEach(() => TestBed.resetTestingModule());

  const OTHER: FeatureDto = {
    ...catalog('github', 'Other'),
    id: 'other' as FeatureId,
    requiresCredentials: true,
    hasCredentials: false,
    credentialFields: [
      {
        key: 'token',
        kind: 'password',
        label: 'Token',
        helperText: 'From the vendor console.',
        createLinkUrl: null,
        createLinkLabel: null,
      },
    ],
    scopeMatrix: [
      {
        scope: 'videos.insert',
        summary: 'Upload a video',
        actions: ['Upload a video'],
        access: 'irreversible',
      },
    ],
    setupSteps: ['Open the console.'],
  };

  async function dialog() {
    const fixture = await screen({ list: async () => ({ features: [OTHER] }) });
    q(fixture, 'act-other').click();
    await settle(fixture);
    return fixture;
  }

  it("Credential dialog: a 560 px card 48 px from the top with 14 px corners, the lede, Required permissions folded with the scope's words, Cancel focused and Save filled", async () => {
    const fixture = await dialog();
    const box = q(fixture, 'credential-dialog');
    const card = box.querySelector('.dialog')!;
    const table = box.querySelector('[data-test="scope-matrix"]')!;

    expect({
      shadow: own(card, 'box-shadow'),
      card: [own(card, 'width'), own(card, 'border-radius'), own(box, 'padding')],
      lede: words(q(fixture, 'dialog-lede')),
      label: words(box.querySelector('.label')),
      permissions: [
        table.tagName,
        words(table.querySelector('summary')),
        words(table.querySelector('th')),
        words(table.querySelector('td')),
      ],
      focused: document.activeElement?.getAttribute('data-test'),
      save: [
        q(fixture, 'credential-submit').classList.contains('mat-mdc-unelevated-button'),
        words(q(fixture, 'credential-submit')),
      ],
    }).toEqual({
      card: ['560px', 'var(--sc-radius)', '48px 16px'],
      shadow: '0 20px 48px var(--sc-shadow)',
      lede: "Fields marked * are required. Values stay in this computer's protected storage.",
      label: 'Token *',
      permissions: ['DETAILS', 'Required permissions', 'Permission', 'Upload a video'],
      focused: 'credential-cancel',
      save: [true, 'Save'],
    });
  });

  it('a typed secret stays hidden: the field is still a password, and the value is in no text, title or label on the screen', async () => {
    const fixture = await dialog();
    const input = q(fixture, 'input-token') as HTMLInputElement;
    input.value = 'sk-secret-123';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const attrs = Array.from(root.querySelectorAll('*')).flatMap((el) =>
      ['title', 'aria-label', 'aria-description', 'placeholder'].map(
        (a) => el.getAttribute(a) ?? '',
      ),
    );

    expect({
      type: input.type,
      text: root.textContent?.includes('sk-secret-123'),
      attributes: attrs.some((a) => a.includes('sk-secret-123')),
    }).toEqual({ type: 'password', text: false, attributes: false });
  });

  it('Esc cancels the dialog and saves nothing', async () => {
    const fixture = await dialog();
    q(fixture, 'input-token').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    await settle(fixture);

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-test="credential-dialog"]'),
    ).toBeNull();
  });
});

describe('Features as Features.dc.html draws it: the GitHub token dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const SPINNER = 'M21 12a9 9 0 1 1-9-9';
  const SAVE_GLYPH = 'M5 3h11l3 3v15H5zM8 3v6h8V3M8 21v-7h8v7';
  const ALERT_GLYPH = 'M12 7v6M12 16.5v.5';

  async function tokenDialog(saveToken?: () => Promise<GitHubStatus>) {
    const fixture = await screen({
      extra: saveToken === undefined ? {} : { githubSaveToken: vi.fn(saveToken) },
    });
    await open(fixture, 'github');
    q(fixture, 'github-use-token').click();
    await settle(fixture);
    return fixture;
  }

  function typeToken(fixture: ComponentFixture<FeaturesComponent>): void {
    const input = q(fixture, 'github-token-input') as HTMLInputElement;
    input.value = 'typed-token';
    input.dispatchEvent(new Event('input'));
  }

  it('Credential dialog: the shared 560 px card 48 px from the top, Connect GitHub, the lede, one required masked token field with its help and the link with its arrow, Required permissions folded, the four steps, Cancel focused and Save filled', async () => {
    const fixture = await tokenDialog();
    const box = q(fixture, 'github-token-dialog');
    const card = box.querySelector('.dialog')!;
    const permissions = q(fixture, 'github-token-permissions') as HTMLDetailsElement;
    const link = q(fixture, 'github-create-token');

    expect({
      card: [own(card, 'width'), own(card, 'border-radius'), own(box, 'padding')],
      title: words(q(fixture, 'github-token-title')),
      lede: words(q(fixture, 'github-token-lede')),
      fields: Array.from(box.querySelectorAll('input'), (i) => i.type),
      label: words(box.querySelector('label[for="github-token-input"]')),
      help: words(q(fixture, 'github-token-help')),
      link: [link.tagName, words(link), paths(link)],
      permissions: [
        permissions.tagName,
        permissions.open,
        words(permissions.querySelector('summary')),
        Array.from(permissions.querySelectorAll('tbody td.perm'), (td) => words(td)),
      ],
      steps: Array.from(q(fixture, 'github-token-steps').querySelectorAll('li'), (li) => words(li)),
      focused: document.activeElement?.getAttribute('data-test'),
      save: [
        q(fixture, 'github-token-save').classList.contains('mat-mdc-unelevated-button'),
        words(q(fixture, 'github-token-save')),
        paths(q(fixture, 'github-token-save')),
      ],
    }).toEqual({
      card: ['560px', 'var(--sc-radius)', '48px 16px'],
      title: 'Connect GitHub',
      lede: "Fields marked * are required. Values stay in this computer's protected storage.",
      fields: ['password'],
      label: 'Personal access token *',
      help: 'Read access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues, on the repositories Scout should see.',
      link: ['A', 'Create a fine-grained token on GitHub', ['M7 17L17 7M8 7h9v9']],
      permissions: [
        'DETAILS',
        false,
        'Required permissions',
        ['Metadata', 'Contents', 'Pull requests', 'Issues'],
      ],
      steps: [
        'Click Create a fine-grained token on GitHub and sign in there.',
        'Name the token, choose when it expires and pick the repositories Scout should see.',
        'Under Repository permissions, give read-only access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues.',
        'Click Generate token, copy the token, paste it here and click Save.',
      ],
      focused: 'github-token-cancel',
      save: [true, 'Save', [SAVE_GLYPH]],
    });
  });

  it('Credential dialog: Save with the token empty: the circled exclamation mark and the sentence in the error colour, the field marked and its help in the error colour', async () => {
    const fixture = await tokenDialog();
    q(fixture, 'github-token-save').click();
    await settle(fixture);
    const alert = q(fixture, 'github-token-alert');
    const help = q(fixture, 'github-token-help');

    expect({
      alert: [alert.getAttribute('role'), words(alert), paths(alert), declared(alert, 'color')],
      invalid: [
        q(fixture, 'github-token-input').getAttribute('aria-invalid'),
        declared(q(fixture, 'github-token-input'), 'border-color'),
      ],
      help: [words(help), declared(help, 'color')],
    }).toEqual({
      alert: [
        'alert',
        'One required field is empty. Nothing was saved.',
        [ALERT_GLYPH],
        'var(--sc-error)',
      ],
      invalid: ['true', 'var(--sc-error)'],
      help: ['Enter a GitHub token.', 'var(--sc-error)'],
    });
  });

  it('Credential dialog: saving: Saving… with the open ring in place of the save glyph, Cancel and Save both off', async () => {
    const fixture = await tokenDialog(() => new Promise<GitHubStatus>(() => undefined));
    typeToken(fixture);
    q(fixture, 'github-token-save').click();
    await settle(fixture);
    const save = q(fixture, 'github-token-save') as HTMLButtonElement;

    expect({
      save: [words(save), paths(save), save.disabled],
      cancel: (q(fixture, 'github-token-cancel') as HTMLButtonElement).disabled,
    }).toEqual({ save: ['Saving…', [SPINNER], true], cancel: true });
  });

  it("Credential dialog: GitHub refused the token: main's sentence in the alert, the dialog open, the field emptied and focused, Save back on", async () => {
    const fixture = await tokenDialog(async () => {
      throw new Error(
        `Error invoking remote method 'scout:github.saveToken': Error: ${encodeIpcErrorMessage(serializeError(new ScoutError('github/token-rejected', 'GitHub rejected this token.')))}`,
      );
    });
    typeToken(fixture);
    q(fixture, 'github-token-save').click();
    await settle(fixture);
    const alert = q(fixture, 'github-token-alert');
    const input = q(fixture, 'github-token-input') as HTMLInputElement;

    expect({
      alert: [words(alert), paths(alert)],
      field: [input.value, input.getAttribute('aria-invalid')],
      focused: document.activeElement?.getAttribute('data-test'),
      save: [
        words(q(fixture, 'github-token-save')),
        (q(fixture, 'github-token-save') as HTMLButtonElement).disabled,
      ],
    }).toEqual({
      alert: ['GitHub rejected this token.', [ALERT_GLYPH]],
      field: ['', null],
      focused: 'github-token-input',
      save: ['Save', false],
    });
  });

  it.each([
    { answer: 'refused', fails: true },
    { answer: 'saved', fails: false },
  ])(
    'a GitHub token stays hidden, typed and then $answer: the field is a password, and the value is in no text, attribute, data-test value or title',
    async ({ fails }) => {
      const secret = 'github_pat_11SECRETVALUE0hidden';
      let status: GitHubStatus = NOT_CONNECTED;
      const fixture = await screen({
        extra: {
          githubStatus: vi.fn(async () => status),
          githubSaveToken: vi.fn(async () => {
            if (fails)
              throw new Error(
                `Error invoking remote method 'scout:github.saveToken': Error: ${encodeIpcErrorMessage(serializeError(new ScoutError('github/token-rejected', 'GitHub rejected this token.')))}`,
              );
            status = { ...CONNECTED, method: 'token' };
            return status;
          }),
        },
      });
      await open(fixture, 'github');
      q(fixture, 'github-use-token').click();
      await settle(fixture);
      const input = q(fixture, 'github-token-input') as HTMLInputElement;
      const type = input.type;
      input.value = secret;
      input.dispatchEvent(new Event('input'));
      await settle(fixture);
      const leaks = (): string[] => [
        ...(document.body.textContent?.includes(secret) ? ['text'] : []),
        ...(document.title.includes(secret) ? ['title'] : []),
        ...Array.from(document.querySelectorAll('*')).flatMap((el) =>
          Array.from(el.attributes)
            .filter((a) => a.value.includes(secret))
            .map((a) => a.name),
        ),
      ];
      const typed = leaks();
      q(fixture, 'github-token-save').click();
      await settle(fixture);
      await settle(fixture);

      expect({ type, typed, answered: leaks() }).toEqual({
        type: 'password',
        typed: [],
        answered: [],
      });
    },
  );
});

describe('Features as Features.dc.html draws it, round 1: the toasts', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  async function confirmOk(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    await settle(fixture);
    document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
    await settle(fixture);
    await settle(fixture);
  }

  it('GitHub signs in: "GitHub connected as" the login, announced politely, the focus left where it was', async () => {
    let status: GitHubStatus = NOT_CONNECTED;
    const fixture = await screen({
      extra: {
        githubStatus: vi.fn(async () => status),
        githubSignIn: vi.fn(async () => {
          status = CONNECTED;
          return null;
        }),
      },
    });
    await open(fixture, 'github');
    const focused = document.activeElement;
    q(fixture, 'github-connect').click();
    await settle(fixture);
    await settle(fixture);

    expect({
      toasts: toasts(),
      polite: document
        .querySelector('.cdk-overlay-container [aria-live]')
        ?.getAttribute('aria-live'),
      focus: document.activeElement === focused,
    }).toEqual({ toasts: ['GitHub connected as acme-owner'], polite: 'polite', focus: true });
  });

  it('GitHub already connected when the screen opens: no toast', async () => {
    await screen({ github: CONNECTED });

    expect(toasts()).toEqual([]);
  });

  const DISCONNECTS = [
    { outcome: 'done', fails: false, toasts: ['GitHub disconnected'], error: null },
    { outcome: 'refused', fails: true, toasts: [], error: 'Scout could not reach GitHub.' },
  ] as const;

  it.each(DISCONNECTS.map((row) => [row.outcome, row] as const))(
    'GitHub Disconnect %s: the toast only when it disconnected, the error where it was otherwise',
    async (_name, row) => {
      let status: GitHubStatus = CONNECTED;
      const fixture = await screen({
        extra: {
          githubStatus: vi.fn(async () => status),
          githubSignOut: vi.fn(async () => {
            if (row.fails) throw new Error('Scout could not reach GitHub.');
            status = NOT_CONNECTED;
            return status;
          }),
        },
      });
      await open(fixture, 'github');
      q(fixture, 'github-disconnect').click();
      await confirmOk(fixture);

      expect({
        toasts: toasts(),
        error: words(
          (fixture.nativeElement as HTMLElement).querySelector('[data-test="github-action-error"]'),
        ),
      }).toEqual({ toasts: [...row.toasts], error: row.error });
    },
  );

  const PULLS = [
    {
      outcome: 'started',
      progress: true,
      error: null,
      toasts: ['Downloading the Node images in Docker'],
    },
    {
      outcome: 'refused before it began',
      progress: false,
      error: 'Docker Hub is limiting downloads; try again later.',
      toasts: [],
    },
  ] as const;

  it.each(PULLS.map((row) => [row.outcome, row] as const))(
    "a stack's Download %s: the toast once Docker reports progress, never over a refusal",
    async (_name, row) => {
      let report: ((p: unknown) => void) | null = null;
      const fixture = await screen({
        github: CONNECTED,
        extra: {
          onDockerPullProgress: vi.fn((cb: (p: unknown) => void) => {
            report = cb;
            return () => undefined;
          }),
          dockerPull: vi.fn(async () => {
            if (row.progress)
              report?.({ reference: 'node:22', index: 1, total: 1, state: 'pulling' });
            return { error: row.error };
          }),
        },
      });
      await open(fixture, 'docker');
      await openSection(fixture, 'images');
      q(fixture, 'docker-download-Node').click();
      await settle(fixture);
      await settle(fixture);

      expect({
        toasts: toasts(),
        error: words(
          (fixture.nativeElement as HTMLElement).querySelector('[data-test="docker-pull-error"]'),
        ),
      }).toEqual({ toasts: [...row.toasts], error: row.error });
    },
  );

  const SAVES = [
    { outcome: 'saved', fails: false, toasts: ['Other credentials saved'] },
    { outcome: 'refused', fails: true, toasts: [] },
  ] as const;

  it.each(SAVES.map((row) => [row.outcome, row] as const))(
    'a credential dialog %s: "<feature> credentials saved" only when the control plane kept them',
    async (_name, row) => {
      const other: FeatureDto = {
        ...catalog('github', 'Other'),
        id: 'other' as FeatureId,
        requiresCredentials: true,
        hasCredentials: false,
        credentialFields: [
          {
            key: 'token',
            kind: 'password',
            label: 'Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      };
      const fixture = await screen({
        list: async () => ({ features: [other] }),
        extra: {
          featureSetup: vi.fn(async () => {
            if (row.fails) throw new Error('The control plane refused the credentials.');
            return { ...other, hasCredentials: true };
          }),
        },
      });
      q(fixture, 'act-other').click();
      await settle(fixture);
      const input = q(fixture, 'input-token') as HTMLInputElement;
      input.value = 'sk-1';
      input.dispatchEvent(new Event('input'));
      await settle(fixture);
      q(fixture, 'credential-submit').click();
      await settle(fixture);
      await settle(fixture);

      expect(toasts()).toEqual([...row.toasts]);
    },
  );

  const KUBE = [
    { outcome: 'connected', fails: false, toasts: ['Kubernetes connected: dev'] },
    { outcome: 'refused', fails: true, toasts: [] },
  ] as const;

  it.each(KUBE.map((row) => [row.outcome, row] as const))(
    'Use selected contexts %s: "Kubernetes connected:" the labels only when main kept them',
    async (_name, row) => {
      let connected = false;
      const fixture = await screen({
        extra: {
          kubernetesDetect: vi.fn(async () => ({
            path: '/home/owner/.kube/config',
            contexts: [
              { name: 'dev', cluster: 'dev', server: 'https://dev.example.com', auth: 'token' },
            ],
            error: null,
          })),
          kubernetesStatus: vi.fn(async () =>
            connected
              ? {
                  path: '/home/owner/.kube/config',
                  contexts: [{ name: 'dev', label: 'dev' }],
                  connected: true,
                }
              : { path: null, contexts: [], connected: false },
          ),
          kubernetesUse: vi.fn(async () => {
            if (row.fails) throw new Error('Scout could not read that context.');
            connected = true;
            return {
              path: '/home/owner/.kube/config',
              contexts: [{ name: 'dev', label: 'dev' }],
              connected: true,
            };
          }),
        },
      });
      await open(fixture, 'kubernetes');
      q(fixture, 'kubernetes-context-dev').click();
      await settle(fixture);
      q(fixture, 'kubernetes-use').click();
      await settle(fixture);
      await settle(fixture);

      expect(toasts()).toEqual([...row.toasts]);
    },
  );

  it('YouTube signs in: "YouTube connected as" the channel', async () => {
    let status: YouTubeStatus = {
      ...YOUTUBE_CONNECTED,
      connected: false,
      channelId: null,
      channelTitle: null,
    };
    const fixture = await screen({
      extra: {
        youtubeStatus: vi.fn(async () => status),
        youtubeSignIn: vi.fn(async () => {
          status = YOUTUBE_CONNECTED;
          return status;
        }),
      },
    });
    await open(fixture, 'youtube');
    q(fixture, 'youtube-sign-in').click();
    await settle(fixture);
    await settle(fixture);

    expect(toasts()).toEqual(['YouTube connected as Acme Tech Explained']);
  });

  const CLEARS = [
    { outcome: 'cleared', refused: null, toasts: ['Work folder cleared'] },
    { outcome: 'refused', refused: 'A test run is using the folder.', toasts: [] },
  ] as const;

  it.each(CLEARS.map((row) => [row.outcome, row] as const))(
    'Clear the work folder %s: "Work folder cleared" only when main cleared it',
    async (_name, row) => {
      const fixture = await screen({
        github: CONNECTED,
        extra: {
          dockerWorkFolder: vi.fn(async () => ({ path: '/w', bytes: 2048, runs: 12 })),
          dockerClear: vi.fn(async () => ({
            folder: {
              path: '/w',
              bytes: row.refused === null ? 0 : 2048,
              runs: row.refused === null ? 0 : 12,
            },
            refused: row.refused,
          })),
        },
      });
      await open(fixture, 'docker');
      await openSection(fixture, 'folder');
      q(fixture, 'docker-clear').click();
      await confirmOk(fixture);

      expect(toasts()).toEqual([...row.toasts]);
    },
  );
});

describe('Features as Features.dc.html draws it, round 1: the voice model dialog', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it("Download a voice model?: 160 px from the top, the theme's dialog card (sc-dialog) with its own 17 px title, Download with its glyph", async () => {
    const fixture = await screen({
      extra: { whisperModelTable: vi.fn(async () => MAIN_WHISPER_TABLE) },
    });
    q(fixture, 'act-desktop').click();
    await settle(fixture);
    const pane = document.querySelector<HTMLElement>('.cdk-overlay-pane.sc-voice-dialog');
    const scss = readFileSync('src/styles.scss', 'utf8');
    const block = (cls: string) =>
      scss.slice(scss.indexOf(`${cls} {`), scss.indexOf('}\n', scss.indexOf(`${cls} {`)));

    expect({
      pane: [pane?.classList.contains('sc-dialog'), pane?.classList.contains('sc-voice-dialog')],
      scrim: document
        .querySelector('.cdk-overlay-backdrop')
        ?.classList.contains('sc-dialog-backdrop'),
      top: pane?.style.marginTop,
      download: paths(document.querySelector('[data-test="voice-download"]')!),
      card: ['container-shape: 14px', 'container-elevation-shadow'].map((t) =>
        block('.sc-dialog').includes(t),
      ),
      title: ['subhead-size: 17px', 'subhead-weight: 600'].map((t) =>
        block('.sc-voice-dialog').includes(t),
      ),
      ownCard: block('.sc-voice-dialog').includes('container-shape'),
    }).toEqual({
      pane: [true, true],
      scrim: true,
      top: '160px',
      download: ['M12 3v12M7 10l5 5 5-5M4 21h16'],
      card: [true, true],
      title: [true, true],
      ownCard: false,
    });
  });
});

describe('Allow writes: the owner is shown every write before Allow these writes turns on', () => {
  beforeEach(() => TestBed.resetTestingModule());

  const MANY: FeatureDto = {
    ...catalog('github', 'GitHub'),
    scopeMatrix: Array.from({ length: 12 }, (_, i) => ({
      scope: `Write ${i}`,
      summary: `Write ${i}`,
      actions: [`Write number ${i}`],
      access: i % 2 === 0 ? ('write' as const) : ('irreversible' as const),
    })),
  };

  const geo = { scrollHeight: 0, clientHeight: 0, scrollTop: 0 };
  const LIST = 'write-confirmation-list';
  const saved: Record<string, PropertyDescriptor | undefined> = {};

  beforeAll(() => {
    for (const key of ['scrollHeight', 'clientHeight', 'scrollTop'] as const) {
      const original = Object.getOwnPropertyDescriptor(Element.prototype, key);
      saved[key] = original;
      Object.defineProperty(HTMLElement.prototype, key, {
        configurable: true,
        get(this: HTMLElement) {
          return this.getAttribute('data-test') === LIST
            ? geo[key]
            : (original?.get?.call(this) ?? 0);
        },
        set(this: HTMLElement, v: number) {
          if (this.getAttribute('data-test') === LIST) geo[key] = v;
          else original?.set?.call(this, v);
        },
      });
    }
  });

  afterAll(() => {
    for (const key of ['scrollHeight', 'clientHeight', 'scrollTop'] as const)
      delete (HTMLElement.prototype as unknown as Record<string, unknown>)[key];
  });

  function layout(scrollHeight: number, clientHeight: number, scrollTop = 0): void {
    Object.assign(geo, { scrollHeight, clientHeight, scrollTop });
  }

  async function open(feature: FeatureDto) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(WriteConfirmationDialogComponent);
    fixture.componentRef.setInput('feature', feature);
    let allowed = 0;
    let cancelled = 0;
    fixture.componentInstance.allow.subscribe(() => (allowed += 1));
    fixture.componentInstance.cancel.subscribe(() => (cancelled += 1));
    await settle(fixture);
    const list = q(fixture, 'write-confirmation-list');
    const state = () => ({
      disabled: (q(fixture, 'write-confirmation-allow') as HTMLButtonElement).disabled,
      hint: words(
        (fixture.nativeElement as HTMLElement).querySelector(
          '[data-test="write-confirmation-scroll-hint"]',
        ),
      ),
    });
    return { fixture, list, state, counts: () => ({ allowed, cancelled }) };
  }

  async function scrolled(fixture: ComponentFixture<unknown>, list: HTMLElement): Promise<void> {
    list.dispatchEvent(new Event('scroll'));
    await settle(fixture);
  }

  it('a list that fits: Allow these writes is on at once, with no line', async () => {
    layout(200, 300, 0);
    const { state } = await open(catalog('github', 'GitHub'));

    expect(state()).toEqual({ disabled: false, hint: null });
  });

  it('a list that overflows: off with the line until the list has been scrolled to its end, then on', async () => {
    layout(900, 300, 0);
    const { fixture, list, state } = await open(MANY);
    const top = state();
    layout(900, 300, 400);
    await scrolled(fixture, list);
    const middle = state();
    layout(900, 300, 600);
    await scrolled(fixture, list);

    expect([top, middle, state()]).toEqual([
      { disabled: true, hint: 'Scroll to the end of the list to allow these writes.' },
      { disabled: true, hint: 'Scroll to the end of the list to allow these writes.' },
      { disabled: false, hint: null },
    ]);
  });

  it('the list is a focusable, labelled region, so the keyboard can scroll it with Tab and the arrow keys', async () => {
    const { list } = await open(MANY);

    expect([
      list.getAttribute('tabindex'),
      list.getAttribute('role'),
      list.getAttribute('aria-label'),
    ]).toEqual(['0', 'region', 'The writes to allow']);
  });

  it('a row added while it is open turns Allow these writes off again until the end is seen', async () => {
    layout(900, 300, 600);
    const { fixture, list, state } = await open(MANY);
    const before = state();
    fixture.componentRef.setInput('feature', {
      ...MANY,
      scopeMatrix: [
        ...MANY.scopeMatrix!,
        {
          scope: 'Write 12',
          summary: 'One more',
          actions: ['One more'],
          access: 'irreversible' as const,
        },
      ],
    });
    layout(960, 300, 600);
    await settle(fixture);
    await scrolled(fixture, list);

    expect([before, state()]).toEqual([
      { disabled: false, hint: null },
      { disabled: true, hint: 'Scroll to the end of the list to allow these writes.' },
    ]);
  });

  it('Esc and Cancel still cancel while Allow these writes is off, and nothing is allowed', async () => {
    layout(900, 300, 0);
    const { fixture, list, counts } = await open(MANY);
    q(fixture, 'write-confirmation-allow').click();
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    q(fixture, 'write-confirmation-cancel').click();

    expect(counts()).toEqual({ allowed: 0, cancelled: 2 });
  });
});

describe('the toasts, review: disconnects and the needed images', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  async function confirmOk(fixture: ComponentFixture<FeaturesComponent>): Promise<void> {
    await settle(fixture);
    document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
    await settle(fixture);
    await settle(fixture);
  }

  const OUTCOMES = [
    { outcome: 'done', fails: false },
    { outcome: 'refused', fails: true },
  ] as const;

  it.each(OUTCOMES.map((row) => [row.outcome, row] as const))(
    'Kubernetes Disconnect %s: "Kubernetes disconnected" only when it disconnected, the error where it was otherwise',
    async (_name, row) => {
      let status: KubernetesStatus = KUBERNETES_CONNECTED;
      const fixture = await screen({
        extra: {
          kubernetesStatus: vi.fn(async () => status),
          kubernetesDisconnect: vi.fn(async () => {
            if (row.fails) throw new Error('Scout could not forget the contexts.');
            status = { path: null, contexts: [], connected: false } as unknown as KubernetesStatus;
            return status;
          }),
        },
      });
      await open(fixture, 'kubernetes');
      q(fixture, 'kubernetes-disconnect').click();
      await confirmOk(fixture);

      expect({
        toasts: toasts(),
        error: words(
          (fixture.nativeElement as HTMLElement).querySelector(
            '[data-test="kubernetes-action-error"]',
          ),
        ),
      }).toEqual(
        row.fails
          ? { toasts: [], error: 'Scout could not forget the contexts.' }
          : { toasts: ['Kubernetes disconnected'], error: null },
      );
    },
  );

  it.each(OUTCOMES.map((row) => [row.outcome, row] as const))(
    'YouTube Disconnect %s: "YouTube disconnected" only when it disconnected, the error where it was otherwise',
    async (_name, row) => {
      let status: YouTubeStatus = YOUTUBE_CONNECTED;
      const fixture = await screen({
        youtube: YOUTUBE_CONNECTED,
        extra: {
          youtubeStatus: vi.fn(async () => status),
          youtubeSignOut: vi.fn(async () => {
            if (row.fails) throw new Error('Google did not answer.');
            status = {
              ...YOUTUBE_CONNECTED,
              connected: false,
              channelId: null,
              channelTitle: null,
            };
            return status;
          }),
        },
      });
      await open(fixture, 'youtube');
      q(fixture, 'youtube-disconnect').click();
      await confirmOk(fixture);

      expect({
        toasts: toasts(),
        error: words(
          (fixture.nativeElement as HTMLElement).querySelector(
            '[data-test="youtube-action-error"]',
          ),
        ),
      }).toEqual(
        row.fails
          ? { toasts: [], error: 'Google did not answer.' }
          : { toasts: ['YouTube disconnected'], error: null },
      );
    },
  );

  const NEEDED = [
    {
      outcome: 'progress reported',
      progress: true,
      error: null,
      toasts: ['Downloading 1 image in Docker'],
    },
    {
      outcome: 'refused before progress',
      progress: false,
      error: 'Docker Hub is limiting downloads; try again later.',
      toasts: [],
    },
  ] as const;

  it.each(NEEDED.map((row) => [row.outcome, row] as const))(
    'Download images, %s: "Downloading 1 image in Docker" once Docker reports progress, never over a refusal',
    async (_name, row) => {
      let report: ((p: unknown) => void) | null = null;
      const fixture = await screen({
        github: CONNECTED,
        extra: {
          dockerRepositories: vi.fn(async () => ({ repositories: [repo('acme/web')] })),
          onDockerPullProgress: vi.fn((cb: (p: unknown) => void) => {
            report = cb;
            return () => undefined;
          }),
          dockerPullImage: vi.fn(async () => {
            if (row.progress)
              report?.({ reference: 'node:22', index: 1, total: 1, state: 'pulling' });
            return { error: row.error };
          }),
        },
      });
      await open(fixture, 'docker');
      await openSection(fixture, 'images');
      q(fixture, 'docker-download').click();
      await settle(fixture);
      await settle(fixture);

      expect({
        toasts: toasts(),
        error: words(
          (fixture.nativeElement as HTMLElement).querySelector('[data-test="docker-pull-error"]'),
        ),
      }).toEqual({ toasts: [...row.toasts], error: row.error });
    },
  );
});

describe('Features as Features.dc.html draws it: the YouTube panel and the Connect YouTube dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  const NOT_CONNECTED_YT: YouTubeStatus = {
    ...YOUTUBE_CONNECTED,
    connected: false,
    method: null,
    channelId: null,
    channelTitle: null,
  };
  const CLIENT_PATH = '/home/owner/.config/Scout/google-oauth-client.json';
  const STUDIO = 'button "Open YouTube Studio"';
  const CHANNEL = 'button "Open channel"';
  const OPERATOR = 'button "Open YouTube operator"';
  const DISCONNECT = 'button "Disconnect"';
  const SIGNED_IN = ['text "Connected to Acme Tech Explained"', 'text "Signed in with Google"'];
  const SIGN_IN = 'button "Sign in with Google"';
  const ADD_KEY = 'button "Add an API key"';

  function drawn(fixture: ComponentFixture<FeaturesComponent>): string[] {
    const host = (fixture.nativeElement as HTMLElement).querySelector('sc-youtube-connect');
    if (host === null) throw new Error('the YouTube panel is not on the screen');
    return Array.from(host.querySelectorAll<HTMLElement>('[data-test]'))
      .filter((node) => node.getAttribute('data-test') !== 'youtube-connect-section')
      .filter((node) => node.closest('[data-test="youtube-key-dialog"]') === null)
      .filter(
        (node) => node.parentElement?.closest('[data-test^="youtube-"]:not(section)') === null,
      )
      .map((node) => {
        const kind = node.tagName === 'BUTTON' ? 'button' : 'text';
        const off = node instanceof HTMLButtonElement && node.disabled ? ' off' : '';
        return `${kind} "${words(node)}"${off}`;
      });
  }

  const PANEL_STATES: readonly {
    preview: string;
    status: YouTubeStatus;
    fail?: { readonly call: string; readonly sentence: string };
    drawn: readonly string[];
  }[] = [
    {
      preview: 'Preview: YouTube connected, on',
      status: YOUTUBE_CONNECTED,
      drawn: [...SIGNED_IN, STUDIO, CHANNEL, OPERATOR, DISCONNECT],
    },
    {
      preview: 'Preview: YouTube connected, off',
      status: NOT_CONNECTED_YT,
      drawn: [SIGN_IN, ADD_KEY],
    },
    {
      preview: 'YouTube panel: Google access',
      status: {
        ...YOUTUBE_CONNECTED,
        scopes: [
          'https://www.googleapis.com/auth/youtube.readonly',
          'https://www.googleapis.com/auth/yt-analytics.readonly',
        ],
      },
      drawn: [
        ...SIGNED_IN,
        'text "Google access: youtube.readonly, yt-analytics.readonly"',
        STUDIO,
        CHANNEL,
        OPERATOR,
        DISCONNECT,
      ],
    },
    {
      preview: 'YouTube panel: connected with an API key',
      status: { ...NOT_CONNECTED_YT, connected: true, method: 'apiKey' },
      drawn: [
        'text "Connected with an API key"',
        'text "Public data only."',
        STUDIO,
        OPERATOR,
        DISCONNECT,
      ],
    },
    {
      preview: 'YouTube panel: waiting for the browser',
      status: { ...NOT_CONNECTED_YT, pending: true },
      drawn: ['text "Waiting for the browser..."', 'button "Cancel"'],
    },
    {
      preview: 'YouTube panel: waiting for the browser while connected',
      status: { ...YOUTUBE_CONNECTED, pending: true },
      drawn: [
        ...SIGNED_IN,
        'text "Waiting for the browser..."',
        'button "Cancel"',
        STUDIO,
        CHANNEL,
        OPERATOR,
        DISCONNECT,
      ],
    },
    {
      preview: 'YouTube panel: OAuth client missing',
      status: {
        ...NOT_CONNECTED_YT,
        configured: false,
        source: null,
        clientConfigPath: CLIENT_PATH,
      },
      drawn: [
        `text "Sign in needs a Google OAuth client: enable the YouTube Data API, create an OAuth client of type Desktop app, then download its JSON as it is to ${CLIENT_PATH} (or write {"client_id": ..., "client_secret": ...} there). Or use an API key below."`,
        'button "Enable the YouTube Data API"',
        'button "Open Google Cloud Console credentials"',
        `${SIGN_IN} off`,
        ADD_KEY,
      ],
    },
    {
      preview: 'YouTube panel: sign-in failed',
      status: {
        ...NOT_CONNECTED_YT,
        error: 'Google could not finish the sign-in (HTTP 503). Sign in again in a moment.',
      },
      drawn: [
        'text "Google could not finish the sign-in (HTTP 503). Sign in again in a moment."',
        SIGN_IN,
        ADD_KEY,
      ],
    },
    {
      preview: 'YouTube panel: Disconnect failed',
      status: YOUTUBE_CONNECTED,
      fail: {
        call: 'youtubeSignOut',
        sentence:
          'Could not remove the YouTube token file, so Scout is still connected. Try Disconnect again.',
      },
      drawn: [
        ...SIGNED_IN,
        STUDIO,
        CHANNEL,
        OPERATOR,
        DISCONNECT,
        'text "Could not remove the YouTube token file, so Scout is still connected. Try Disconnect again."',
      ],
    },
  ];

  it.each(PANEL_STATES.map((row) => [row.preview, row] as const))(
    '%s: the panel draws its lines and buttons in this order, each button named by its label',
    async (_preview, row) => {
      const fixture = await screen({
        youtube: row.status,
        extra:
          row.fail === undefined
            ? {}
            : {
                [row.fail.call]: vi.fn(async () => {
                  throw new Error(
                    `Error invoking remote method 'scout:youtube.signOut': Error: ${encodeIpcErrorMessage(serializeError(new ScoutError('youtube/token-file-not-removed', row.fail!.sentence)))}`,
                  );
                }),
              },
      });
      await open(fixture, 'youtube');
      if (row.fail !== undefined) {
        q(fixture, 'youtube-disconnect').click();
        await settle(fixture);
        document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
        await settle(fixture);
        await settle(fixture);
      }

      expect(drawn(fixture)).toEqual([...row.drawn]);
    },
  );

  it("the panel keeps the artboard's 10 px between its parts", async () => {
    const fixture = await screen({ youtube: YOUTUBE_CONNECTED });
    await open(fixture, 'youtube');

    expect(own(q(fixture, 'youtube-connect-section'), 'gap')).toBe('10px');
  });

  it('Open YouTube operator sits in the connected row between Open channel and Disconnect, and is not drawn while YouTube is not connected', async () => {
    const connected = await screen({ youtube: YOUTUBE_CONNECTED });
    await open(connected, 'youtube');
    const operator = q(connected, 'youtube-open-operator');
    const place = [
      operator.previousElementSibling?.getAttribute('data-test'),
      operator.nextElementSibling?.getAttribute('data-test'),
      operator.parentElement === q(connected, 'youtube-open-studio').parentElement,
    ];
    TestBed.resetTestingModule();
    const notConnected = await screen({ youtube: NOT_CONNECTED_YT });
    await open(notConnected, 'youtube');

    expect({
      place,
      absent: (notConnected.nativeElement as HTMLElement).querySelector(
        '[data-test="youtube-open-operator"]',
      ),
    }).toEqual({ place: ['youtube-open-channel', 'youtube-disconnect', true], absent: null });
  });

  const KEY_HELP =
    'Create it in the Google Cloud Console (APIs & Services, Credentials) for a project with the YouTube Data API v3 enabled. It reads public data only.';
  const KEY_STEPS = [
    'Create a project in the Google Cloud Console.',
    'Enable the YouTube Data API v3 for that project.',
    'Create an API key, or sign in with Google from Scout to read your own channel.',
  ];
  const SPINNER = 'M21 12a9 9 0 1 1-9-9';
  const SAVE_GLYPH = 'M5 3h11l3 3v15H5zM8 3v6h8V3M8 21v-7h8v7';
  const ALERT_GLYPH = 'M12 7v6M12 16.5v.5';
  const REFUSED =
    'Google does not recognise this API key. Copy it again from Google Cloud Console credentials.';

  async function keyDialog(save?: () => Promise<YouTubeStatus>) {
    const fixture = await screen({
      youtube: NOT_CONNECTED_YT,
      extra: save === undefined ? {} : { youtubeSaveApiKey: vi.fn(save) },
    });
    await open(fixture, 'youtube');
    q(fixture, 'youtube-use-key').click();
    await settle(fixture);
    return fixture;
  }

  function dialogText(fixture: ComponentFixture<FeaturesComponent>): (string | null)[] {
    const box = q(fixture, 'youtube-key-dialog');
    return [
      words(q(fixture, 'youtube-key-title')),
      words(q(fixture, 'youtube-key-lede')),
      words(box.querySelector('label[for="youtube-key-input"]')),
      words(q(fixture, 'youtube-key-help')),
      words(q(fixture, 'youtube-key-permissions').querySelector('summary')),
      ...Array.from(
        q(fixture, 'youtube-key-permissions').querySelectorAll<HTMLTableRowElement>('tbody tr'),
        (tr) => Array.from(tr.cells, (cell) => words(cell)).join(' / '),
      ),
      words(box.querySelector('.setup-steps h3')),
      ...Array.from(q(fixture, 'youtube-key-steps').querySelectorAll('li'), (li) => words(li)),
      words(box.querySelector('[data-test="youtube-key-alert"]')),
      words(q(fixture, 'youtube-key-cancel')),
      words(q(fixture, 'youtube-key-save')),
    ];
  }

  const opened = (help: string, alert: string | null, save: string) => [
    'Connect YouTube',
    "Fields marked * are required. Values stay in this computer's protected storage.",
    'YouTube Data API key *',
    help,
    'Required permissions',
    "Channel overview / Read a channel's title, handle and counts",
    'Comments / Read the comments on your videos',
    'How to get this credential',
    ...KEY_STEPS,
    alert,
    'Cancel',
    save,
  ];

  it('Connect YouTube dialog: Add an API key opens the shared 560 px card with the title, the lede, one required masked key field named by its label and described by its help, Required permissions folded, the three steps, no link, Cancel focused and Save filled', async () => {
    const fixture = await keyDialog();
    const box = q(fixture, 'youtube-key-dialog');
    const input = q(fixture, 'youtube-key-input') as HTMLInputElement;

    expect({
      card: [own(box.querySelector('.dialog')!, 'width'), own(box, 'padding')],
      dialog: [
        box.getAttribute('role'),
        box.getAttribute('aria-modal'),
        box.getAttribute('aria-labelledby'),
      ],
      text: dialogText(fixture),
      fields: Array.from(box.querySelectorAll('input'), (i) => [
        i.type,
        i.id,
        i.getAttribute('aria-describedby'),
      ]),
      links: box.querySelectorAll('a').length,
      folded: (q(fixture, 'youtube-key-permissions') as HTMLDetailsElement).open,
      value: input.value,
      focused: document.activeElement?.getAttribute('data-test'),
      save: [
        q(fixture, 'youtube-key-save').classList.contains('mat-mdc-unelevated-button'),
        paths(q(fixture, 'youtube-key-save')),
      ],
    }).toEqual({
      card: ['560px', '48px 16px'],
      dialog: ['dialog', 'true', 'youtube-key-title'],
      text: opened(KEY_HELP, null, 'Save'),
      fields: [['password', 'youtube-key-input', 'youtube-key-help']],
      links: 0,
      folded: false,
      value: '',
      focused: 'youtube-key-cancel',
      save: [true, [SAVE_GLYPH]],
    });
  });

  it('Connect YouTube: Save with the key empty: the sentence with the circled exclamation mark in the error colour, the field marked and its help saying Enter the API key., nothing sent', async () => {
    const save = vi.fn(async () => NOT_CONNECTED_YT);
    const fixture = await keyDialog(save);
    q(fixture, 'youtube-key-save').click();
    await settle(fixture);
    const alert = q(fixture, 'youtube-key-alert');

    expect({
      text: dialogText(fixture),
      alert: [alert.getAttribute('role'), paths(alert), declared(alert, 'color')],
      invalid: q(fixture, 'youtube-key-input').getAttribute('aria-invalid'),
      help: declared(q(fixture, 'youtube-key-help'), 'color'),
      sent: save.mock.calls.length,
    }).toEqual({
      text: opened('Enter the API key.', 'One required field is empty. Nothing was saved.', 'Save'),
      alert: ['alert', [ALERT_GLYPH], 'var(--sc-error)'],
      invalid: 'true',
      help: 'var(--sc-error)',
      sent: 0,
    });
  });

  it('Connect YouTube: saving: Saving… with the open ring in place of the save glyph, Cancel and Save both off', async () => {
    const fixture = await keyDialog(() => new Promise<YouTubeStatus>(() => undefined));
    const input = q(fixture, 'youtube-key-input') as HTMLInputElement;
    input.value = 'AIzaSyInventedKey0001';
    input.dispatchEvent(new Event('input'));
    q(fixture, 'youtube-key-save').click();
    await settle(fixture);
    const save = q(fixture, 'youtube-key-save') as HTMLButtonElement;

    expect({
      text: dialogText(fixture),
      save: [paths(save), save.disabled],
      cancel: (q(fixture, 'youtube-key-cancel') as HTMLButtonElement).disabled,
    }).toEqual({
      text: opened(KEY_HELP, null, 'Saving…'),
      save: [[SPINNER], true],
      cancel: true,
    });
  });

  it("Connect YouTube: Google refused the key: main's sentence in the alert, the dialog open, the field emptied and focused, Save back on", async () => {
    const fixture = await keyDialog(async () => {
      throw new Error(
        `Error invoking remote method 'scout:youtube.saveApiKey': Error: ${encodeIpcErrorMessage(serializeError(new ScoutError('youtube/key-rejected', REFUSED)))}`,
      );
    });
    const input = q(fixture, 'youtube-key-input') as HTMLInputElement;
    input.value = 'AIzaSyInventedKey0001';
    input.dispatchEvent(new Event('input'));
    q(fixture, 'youtube-key-save').click();
    await settle(fixture);

    expect({
      text: dialogText(fixture),
      alert: paths(q(fixture, 'youtube-key-alert')),
      field: [input.value, input.getAttribute('aria-invalid')],
      focused: document.activeElement?.getAttribute('data-test'),
      save: (q(fixture, 'youtube-key-save') as HTMLButtonElement).disabled,
    }).toEqual({
      text: opened(KEY_HELP, REFUSED, 'Save'),
      alert: [ALERT_GLYPH],
      field: ['', null],
      focused: 'youtube-key-input',
      save: false,
    });
  });

  it.each(['Cancel', 'Esc'] as const)(
    'Connect YouTube + %s: the dialog closes, the panel stays open, the focus goes back to Add an API key and nothing is sent',
    async (action) => {
      const save = vi.fn(async () => NOT_CONNECTED_YT);
      const fixture = await keyDialog(save);
      if (action === 'Cancel') q(fixture, 'youtube-key-cancel').click();
      else
        q(fixture, 'youtube-key-input').dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
        );
      await settle(fixture);

      expect({
        dialog: (fixture.nativeElement as HTMLElement).querySelector(
          '[data-test="youtube-key-dialog"]',
        ),
        panel: q(fixture, 'panel-youtube').hidden,
        focused: document.activeElement?.getAttribute('data-test'),
        sent: save.mock.calls.length,
      }).toEqual({ dialog: null, panel: false, focused: 'youtube-use-key', sent: 0 });
    },
  );
});

describe('Features as Features.dc.html draws it: the Docker toasts, look and Preview states', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  const VAULT_REFUSED =
    'Scout found no keyring service such as GNOME Keyring or KWallet on this computer, so it saved nothing and does not use the credentials saved before; start or install one, then restart Scout.';
  const NEVER = () => new Promise<never>(() => undefined);
  const NOT_RUNNING = { state: 'notRunning' };
  const RESTART_FAILED =
    'Saved, but Scout could not restart to apply it: worker exited before SCOUT_READY (code=1)';
  const FAILED_RUN = {
    repository: 'acme/shop',
    pullRequest: 5,
    stack: '.NET',
    image: 'mcr.microsoft.com/dotnet/sdk:10.0',
    command: 'dotnet test',
    result: 'failed',
    reason: 'The restore exited with code 1; the tests did not run.',
    exitCode: 1,
    durationSeconds: 41,
    startedAt: null,
    finishedAt: '2026-11-07T10:05:00Z',
  };

  function switchButton(fixture: ComponentFixture<FeaturesComponent>): HTMLButtonElement {
    return q(fixture, 'docker-switch').querySelector<HTMLButtonElement>('button[role="switch"]')!;
  }

  function shown(fixture: ComponentFixture<FeaturesComponent>, test: string): string | null {
    return words((fixture.nativeElement as HTMLElement).querySelector(`[data-test="${test}"]`));
  }

  const TURN_ONS = [
    {
      outcome: 'done',
      store: 'saves',
      restart: { outcome: 'now' },
      toasts: ["Scout may run a project's tests in isolated containers"],
      error: null,
      note: null,
    },
    {
      outcome: 'saved, the restart waiting for the answer',
      store: 'saves',
      restart: { outcome: 'deferred' },
      toasts: ["Scout may run a project's tests in isolated containers"],
      error: null,
      note: RESTART_DEFERRED_LINE,
    },
    {
      outcome: 'refused',
      store: 'refuses',
      restart: { outcome: 'now' },
      toasts: [],
      error: VAULT_REFUSED,
      note: null,
    },
    {
      outcome: 'the call fails',
      store: 'saves',
      restart: { outcome: 'failed', reason: 'worker exited before SCOUT_READY (code=1)' },
      toasts: [],
      error: RESTART_FAILED,
      note: null,
    },
  ] as const;

  it.each(TURN_ONS.map((row) => [row.outcome, row] as const))(
    "Docker running, the card's Turn on %s: \"Scout may run a project's tests in isolated containers\" once, only when the switch was saved and Scout's restart did not fail",
    async (_name, row) => {
      const fixture = await screen({
        github: CONNECTED,
        extra: {
          vaultStore: vi.fn(async () => {
            if (row.store === 'refuses') throw new Error(VAULT_REFUSED);
          }),
          workerRestart: vi.fn(async () => row.restart),
        },
      });
      const label = words(q(fixture, 'act-docker'));
      const told = vi.spyOn(TestBed.inject(NoticeService), 'show');

      q(fixture, 'act-docker').click();
      await settle(fixture);
      await settle(fixture);

      expect({
        label,
        told: told.mock.calls.map(([message]) => message),
        toasts: toasts(),
        error: shown(fixture, 'docker-switch-error'),
        note: shown(fixture, 'docker-switch-note'),
      }).toEqual({
        label: 'Turn on',
        told: [...row.toasts],
        toasts: [...row.toasts],
        error: row.error,
        note: row.note,
      });
    },
  );

  const CARD_REFRESHES = [
    {
      outcome: 'Docker still not running',
      answer: 'notRunning',
      toasts: ['Docker is still not running'],
    },
    { outcome: 'Docker started meanwhile', answer: 'running', toasts: ['Docker is running'] },
    { outcome: 'the call fails', answer: 'fails', toasts: [] },
  ] as const;

  it.each(CARD_REFRESHES.map((row) => [row.outcome, row] as const))(
    "Docker not running, the card's Refresh, %s: one toast saying what Docker answered, none when the call fails",
    async (_name, row) => {
      let asked = 0;
      const fixture = await screen({
        github: CONNECTED,
        extra: {
          dockerState: vi.fn(async () => {
            asked += 1;
            if (asked === 1) return NOT_RUNNING;
            if (row.answer === 'fails') throw new Error('bridge down');
            return row.answer === 'running' ? { state: 'running', version: '28.5.1' } : NOT_RUNNING;
          }),
        },
      });
      const label = words(q(fixture, 'act-docker'));
      const told = vi.spyOn(TestBed.inject(NoticeService), 'show');

      q(fixture, 'act-docker').click();
      await settle(fixture);
      await settle(fixture);

      expect({
        label,
        asked,
        told: told.mock.calls.map(([message]) => message),
        toasts: toasts(),
      }).toEqual({
        label: 'Refresh',
        asked: 2,
        told: [...row.toasts],
        toasts: [...row.toasts],
      });
    },
  );

  it("Panel: Docker, last run: Passed in the theme's ok text colour, #7ed6a6 in the dark theme and #1d6b46 in the light one", async () => {
    const fixture = await screen({
      github: CONNECTED,
      extra: {
        dockerLastRun: vi.fn(async () => ({
          run: { ...FAILED_RUN, result: 'passed', reason: null, exitCode: 0 },
        })),
      },
    });
    await open(fixture, 'docker');
    await openSection(fixture, 'last');
    const scss = readFileSync('src/styles.scss', 'utf8');
    const scheme = (name: string) => scss.slice(scss.indexOf(`$_${name}: (`));

    expect({
      result: shown(fixture, 'docker-last-result'),
      color: own(q(fixture, 'docker-last-result'), 'color'),
      dark: /on-ok-container: (#[0-9a-f]{6})/.exec(scheme('dark'))?.[1],
      light: /on-ok-container: (#[0-9a-f]{6})/.exec(scheme('light'))?.[1],
    }).toEqual({
      result: 'Passed',
      color: 'var(--sc-on-ok-container)',
      dark: '#7ed6a6',
      light: '#1d6b46',
    });
  });

  it('Panel: Docker, pinned images: the image rows 2 px apart, 12 px in, with no padding of their own', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'docker');
    await openSection(fixture, 'images');
    const list = q(fixture, 'docker-group-Node').querySelector('ul.images')!;
    const row = list.querySelector('li')!;

    expect([own(list, 'gap'), own(list, 'padding-left'), own(row, 'padding')]).toEqual([
      '2px',
      '12px',
      null,
    ]);
  });

  it('Panel: Docker, what Scout uses Docker for: the uses 4 px apart at a line height of 1.45', async () => {
    const fixture = await screen({ github: CONNECTED });
    await open(fixture, 'docker');
    await openSection(fixture, 'uses');
    const list = q(fixture, 'docker-body-uses').querySelector('ul.uses')!;

    expect([own(list, 'gap'), own(list, 'line-height')]).toEqual(['4px', '1.45']);
  });

  const PREVIEWS: readonly {
    preview: string;
    extra?: Readonly<Record<string, unknown>>;
    sections?: readonly string[];
    act?: (fixture: ComponentFixture<FeaturesComponent>) => Promise<void>;
    shown: Readonly<Record<string, string | null>>;
  }[] = [
    {
      preview: 'Panel: Docker',
      shown: {
        'docker-status-line': 'Docker Engine 28.5.1 answers on this computer.',
        'docker-install-guide': null,
        'docker-refresh': 'Refresh',
        'badge-docker': 'Running · Docker 28.5.1',
      },
    },
    {
      preview: 'Docker: Not installed',
      extra: { dockerState: vi.fn(async () => ({ state: 'notInstalled' })) },
      shown: {
        'docker-status-line': 'Docker is not installed on this computer.',
        'docker-install-guide': 'How to install Docker',
        'badge-docker': 'Not installed',
        'docker-toggle-images': 'Images: Docker is not running',
      },
    },
    {
      preview: 'Docker: Not running',
      extra: { dockerState: vi.fn(async () => NOT_RUNNING) },
      shown: {
        'docker-status-line':
          'Docker is installed but not running. Start Docker Desktop or the Docker service.',
        'docker-install-guide': null,
        'badge-docker': 'Installed, not running',
        'docker-toggle-images': 'Images: Docker is not running',
      },
    },
    {
      preview: 'Docker: Running',
      shown: {
        'docker-status-line': 'Docker Engine 28.5.1 answers on this computer.',
        'badge-docker': 'Running · Docker 28.5.1',
      },
    },
    {
      preview: 'Docker panel: checking',
      extra: { dockerState: vi.fn(NEVER) },
      shown: {
        'docker-status-line': 'Scout is checking Docker on this computer.',
        'badge-docker': 'Checking...',
        'docker-toggle-images': 'Images: checking',
      },
    },
    {
      preview: 'Docker panel: Docker not read',
      extra: {
        dockerState: vi.fn(async () => {
          throw new Error('bridge down');
        }),
      },
      shown: {
        'docker-status-line': 'Scout could not ask Docker for its state. Try Refresh.',
        'badge-docker': 'Unknown',
        'act-docker': 'Refresh',
      },
    },
    {
      preview: 'Docker panel: a stack downloading',
      extra: {
        onDockerPullProgress: vi.fn((cb: (p: unknown) => void) => {
          queueMicrotask(() => cb({ reference: 'node:22', index: 1, total: 1, state: 'pulling' }));
          return () => undefined;
        }),
        dockerPull: vi.fn(NEVER),
      },
      sections: ['images'],
      act: async (fixture) => {
        q(fixture, 'docker-download-Node').click();
        await settle(fixture);
      },
      shown: {
        'docker-progress': 'Downloading 1 of 1: node:22',
        'docker-toggle-images': 'Pinned images per stack',
      },
    },
    {
      preview: 'Docker panel: Download images downloading',
      extra: {
        dockerRepositories: vi.fn(async () => ({ repositories: [repo('acme/web')] })),
        dockerPullImage: vi.fn(NEVER),
      },
      sections: ['images'],
      act: async (fixture) => {
        q(fixture, 'docker-download').click();
        await settle(fixture);
      },
      shown: { 'docker-needed-progress': 'Downloading 1 of 1: node:22' },
    },
    {
      preview: 'Docker panel: the switch not saved',
      extra: {
        vaultStore: vi.fn(async () => {
          throw new Error(VAULT_REFUSED);
        }),
      },
      act: async (fixture) => {
        switchButton(fixture).click();
        await settle(fixture);
      },
      shown: { 'docker-switch-error': VAULT_REFUSED, 'docker-switch-note': null },
    },
    {
      preview: 'Docker panel: saved, Scout not restarted',
      extra: {
        workerRestart: vi.fn(async () => ({
          outcome: 'failed',
          reason: 'worker exited before SCOUT_READY (code=1)',
        })),
      },
      act: async (fixture) => {
        switchButton(fixture).click();
        await settle(fixture);
      },
      shown: { 'docker-switch-error': RESTART_FAILED, 'docker-switch-note': null },
    },
    {
      preview: 'Docker panel: restart waits for the answer',
      extra: { workerRestart: vi.fn(async () => ({ outcome: 'deferred' })) },
      act: async (fixture) => {
        switchButton(fixture).click();
        await settle(fixture);
      },
      shown: { 'docker-switch-error': null, 'docker-switch-note': RESTART_DEFERRED_LINE },
    },
    {
      preview: 'Docker panel: Clear refused',
      extra: {
        dockerWorkFolder: vi.fn(async () => ({ path: '/w', bytes: 2048, runs: 12 })),
        dockerClear: vi.fn(async () => ({
          folder: { path: '/w', bytes: 2048, runs: 12 },
          refused: 'A test run is in progress in the work folder; clear it after the run ends.',
        })),
      },
      sections: ['folder'],
      act: async (fixture) => {
        q(fixture, 'docker-clear').click();
        await settle(fixture);
        document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
        await settle(fixture);
        await settle(fixture);
      },
      shown: {
        'docker-clear-refused':
          'A test run is in progress in the work folder; clear it after the run ends.',
        'docker-folder-size':
          "2.0 KB used · the logs of 12 runs; each run's copy of the code is deleted after it",
      },
    },
    {
      preview: 'Docker panel: no run yet',
      sections: ['last'],
      shown: { 'docker-last-empty': 'No run yet.', 'docker-last-details': null },
    },
    {
      preview: 'Docker panel: last run failed',
      extra: { dockerLastRun: vi.fn(async () => ({ run: FAILED_RUN })) },
      sections: ['last'],
      shown: {
        'docker-last-result': 'Failed (exit 1)',
        'docker-last-reason': 'The restore exited with code 1; the tests did not run.',
        'docker-last-stack': '.NET (dotnet test)',
      },
    },
    {
      preview: 'Docker panel: repository detail as the app writes it',
      extra: {
        dockerRepositories: vi.fn(async () => ({
          repositories: [
            repo('acme/web', {
              runs: 'Node 20.20.2',
              declared: [{ source: '.nvmrc', version: 'Node 20' }],
            }),
          ],
        })),
      },
      sections: ['repositories'],
      shown: {
        'docker-repository-stack': 'Node',
        'docker-repository-versions': 'Runs on Node 20.20.2. .nvmrc asks for Node 20.',
      },
    },
    {
      preview: 'Docker panel: repositories while Docker is not running',
      extra: {
        dockerState: vi.fn(async () => NOT_RUNNING),
        dockerRepositories: vi.fn(async () => ({ repositories: [repo('acme/web')] })),
      },
      sections: ['repositories'],
      shown: {
        'docker-repository-image': 'Docker is not running',
        'docker-repository-download': 'Download',
      },
    },
  ];

  it.each(PREVIEWS.map((row) => [row.preview, row] as const))(
    '%s: the panel shows what the Preview state draws',
    async (_preview, row) => {
      const fixture = await screen({ github: CONNECTED, extra: row.extra ?? {} });
      await open(fixture, 'docker');
      for (const id of row.sections ?? []) await openSection(fixture, id);
      await row.act?.(fixture);

      expect(
        Object.fromEntries(Object.keys(row.shown).map((id) => [id, shown(fixture, id)])),
      ).toEqual(row.shown);
    },
  );

  it('Docker panel: repositories while Docker is not running: Download is drawn disabled, named for its repository and described by the reason', async () => {
    const fixture = await screen({
      github: CONNECTED,
      extra: {
        dockerState: vi.fn(async () => NOT_RUNNING),
        dockerRepositories: vi.fn(async () => ({ repositories: [repo('acme/web')] })),
      },
    });
    await open(fixture, 'docker');
    await openSection(fixture, 'repositories');
    const download = q(fixture, 'docker-repository-download') as HTMLButtonElement;

    expect({
      disabled: download.disabled,
      name: download.getAttribute('aria-label'),
      reason: words(document.getElementById(download.getAttribute('aria-describedby') ?? '')),
    }).toEqual({
      disabled: true,
      name: 'Download the image for acme/web',
      reason: 'Docker is not running',
    });
  });
});

describe("Features as Features.dc.html draws it: the GitHub panel's Preview states", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  const REPOSITORIES = [
    'shop',
    'api',
    'web',
    'mobile',
    'payments',
    'search',
    'billing',
    'infra',
    'docs',
    'design-system',
    'analytics',
    'auth',
    'notifications',
    'status-page',
  ].map((name) => `acme/${name}`);
  const INSTALLED: GitHubBotStatus = {
    ...BOT_INSTALLED,
    repositories: REPOSITORIES,
    writes: ['review', 'verdict', 'comment', 'issue'],
  };
  const CREATED: GitHubBotStatus = { ...INSTALLED, state: 'created', repositories: [] };
  const SETTINGS = 'https://github.com/settings/apps/scout-acme';

  const CONNECTED_ROW = [
    'text "Connected as acme-owner · installed on 6 repositories"',
    'button "Disconnect"',
  ];
  const HINT = [
    'text "Turn on Docker to let Scout run a pull request\'s tests in an isolated container before it gives a verdict."',
    'button "Set up Docker"',
  ];
  const BOT_HEAD = [
    'heading "Use a Scout bot on GitHub (optional)"',
    'text "Reviews, verdicts, comments and issues Scout posts show as the bot instead of you. GitHub creates an App that you own; Scout keeps its key on this computer."',
  ];
  const NONE_ON = [
    'select "Create the bot on" "My account"',
    'button "Create the bot on GitHub"',
    'text "GitHub opens in your browser to confirm the App."',
  ];
  const NONE_OFF = [
    'select "Create the bot on" "My account"',
    'button "Create the bot on GitHub" off',
    'text "Connect GitHub first to create the bot."',
  ];
  const CONNECT_ROW = [
    'button "Connect with GitHub"',
    'button "Use a token"',
    'text "Scout shows a one-time code; you enter it on GitHub and approve the Scout GitHub App there."',
  ];
  const exists = (name: string, nameError: readonly string[] = []) => [
    `text "Name: ${name}"`,
    'text "Handle: @scout-acme"',
    ...nameError,
    'text "Ask for a review with @scout-acme"',
    'button "Copy @scout-acme"',
    'text "Mention it in a pull request\'s description or a comment; the bell tells you who asked."',
    'button "Add a picture on GitHub"',
    'button "Change the name on GitHub"',
    'button "Refresh"',
    'text "GitHub renames an App only on its settings page; Scout reads the new name when this card opens again or when you click Refresh."',
    'text "GitHub shows this picture on everything the bot signs. Actions you sign yourself keep your own picture."',
    `text "${SETTINGS}"`,
  ];
  const CREATED_ROWS = [
    'text "scout-acme[bot] exists. Install it on the repositories it may act on; until then Scout acts as you."',
    'button "Install the bot"',
    'button "Remove the bot"',
  ];
  const INSTALLED_ROWS = [
    'button "Repositories: 14 (scout-acme[bot] can post in these)"',
    'text "Signed by the bot; an action switched off is signed as you."',
    'switch "Post reviews" checked',
    'switch "Give verdicts (approve or request changes)" checked',
    'switch "Comment on pull requests and issues" checked',
    'switch "Open issues" checked',
    'button "Change repositories"',
    'button "Remove the bot"',
  ];
  const NAME_NOT_REFRESHED =
    "Scout could not refresh the bot's name from GitHub; this is the last name it read.";
  const REMOVED_LINE = [
    'text "Scout no longer uses the bot. The App stays on GitHub until you delete it there."',
    'button "Open the App\'s settings on GitHub"',
  ];

  const ipcError = (channel: string, code: string, sentence: string): Error =>
    new Error(
      `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(serializeError(new ScoutError(code, sentence)))}`,
    );

  function labelOf(control: Element): string {
    const label = control.closest('label');
    if (label === null) return '';
    return Array.from(label.childNodes)
      .filter((n) => n.nodeType === Node.TEXT_NODE)
      .map((n) => n.textContent ?? '')
      .join(' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  function drawn(fixture: ComponentFixture<FeaturesComponent>): string[] {
    const host = (fixture.nativeElement as HTMLElement).querySelector('sc-github-connect');
    if (host === null) throw new Error('the GitHub panel is not on the screen');
    return Array.from(
      host.querySelectorAll<HTMLElement>('h3, p, span, code, button, select, input'),
    )
      .filter((node) => node.closest('sc-github-token-dialog') === null)
      .filter((node) =>
        ['BUTTON', 'SELECT', 'INPUT'].includes(node.tagName)
          ? node.parentElement?.closest('button') === null
          : node.parentElement?.closest('h3, p, span, code, button, label') === null,
      )
      .map((node) => {
        const off = (node as HTMLButtonElement).disabled ? ' off' : '';
        if (node instanceof HTMLSelectElement)
          return `select "${labelOf(node)}" "${words(node.selectedOptions[0] ?? null)}"${off}`;
        if (node instanceof HTMLInputElement && node.getAttribute('role') === 'switch')
          return `switch "${words(node.closest('label'))}" ${node.checked ? 'checked' : 'unchecked'}${off}`;
        if (node instanceof HTMLInputElement)
          return `field "${labelOf(node)}" "${node.value}"${off}`;
        if (node.tagName === 'BUTTON')
          return `button "${node.getAttribute('aria-label') ?? words(node)}"${off}`;
        if (node.tagName === 'H3') return `heading "${words(node)}"`;
        return `text "${words(node)}"`;
      });
  }

  async function confirmed(fixture: ComponentFixture<FeaturesComponent>, button: string) {
    q(fixture, button).click();
    await settle(fixture);
    document.querySelector<HTMLElement>('[data-test="confirm-ok"]')!.click();
    await settle(fixture);
    await settle(fixture);
  }

  const PREVIEWS: readonly {
    preview: string;
    github: GitHubStatus;
    bot?: GitHubBotStatus;
    extra?: () => Readonly<Record<string, unknown>>;
    act?: (fixture: ComponentFixture<FeaturesComponent>) => Promise<void>;
    drawn: readonly string[];
  }[] = [
    {
      preview: 'Panel: GitHub',
      github: CONNECTED,
      bot: INSTALLED,
      drawn: [...CONNECTED_ROW, ...HINT, ...BOT_HEAD, ...INSTALLED_ROWS, ...exists('scout-acme')],
    },
    {
      preview: 'GitHub panel: bot created',
      github: CONNECTED,
      bot: CREATED,
      drawn: [...CONNECTED_ROW, ...HINT, ...BOT_HEAD, ...CREATED_ROWS, ...exists('scout-acme')],
    },
    {
      preview: 'GitHub panel: bot name not refreshed',
      github: CONNECTED,
      bot: { ...INSTALLED, nameError: NAME_NOT_REFRESHED },
      drawn: [
        ...CONNECTED_ROW,
        ...HINT,
        ...BOT_HEAD,
        ...INSTALLED_ROWS,
        ...exists('scout-acme', [`text "${NAME_NOT_REFRESHED}"`]),
      ],
    },
    {
      preview: 'GitHub panel: code shown',
      github: {
        ...NOT_CONNECTED,
        pending: { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device' },
      },
      drawn: [
        'text "Enter this code on GitHub:"',
        'text "WDJB-MJHT"',
        'button "Open GitHub"',
        'button "Copy code"',
        'button "Cancel"',
        ...BOT_HEAD,
        ...NONE_OFF,
      ],
    },
    {
      preview: 'GitHub panel: bot being created',
      github: CONNECTED,
      bot: { ...BOT_NONE, state: 'creating' },
      drawn: [
        ...CONNECTED_ROW,
        ...HINT,
        ...BOT_HEAD,
        'text "Finish creating the App on GitHub in your browser."',
        'button "Cancel"',
      ],
    },
    {
      preview: 'GitHub panel: installed on 1 repository',
      github: { ...CONNECTED, repositories: 1 },
      drawn: [
        'text "Connected as acme-owner · installed on 1 repository"',
        'button "Disconnect"',
        ...HINT,
        ...BOT_HEAD,
        ...NONE_ON,
      ],
    },
    {
      preview: 'GitHub panel: App not installed yet',
      github: {
        ...CONNECTED,
        installed: false,
        repositories: 0,
        installUrl: 'https://github.com/apps/scout-chatops/installations/new',
      },
      drawn: [
        'text "Connected as acme-owner"',
        'button "Install Scout on your GitHub account"',
        'button "Disconnect"',
        ...HINT,
        ...BOT_HEAD,
        ...NONE_ON,
      ],
    },
    {
      preview: 'GitHub panel: a token that can write',
      github: {
        ...CONNECTED,
        method: 'token',
        installed: false,
        repositories: null,
        warning:
          'This token can write to your repositories. A read-only fine-grained token is safer.',
      },
      drawn: [
        'text "Connected as acme-owner"',
        'button "Disconnect"',
        'text "This token can write to your repositories. A read-only fine-grained token is safer."',
        ...HINT,
        ...BOT_HEAD,
        ...NONE_ON,
      ],
    },
    {
      preview: 'GitHub panel: App not configured',
      github: { ...NOT_CONNECTED, configured: false },
      drawn: [
        'text "The Scout GitHub App is not configured yet."',
        'button "Connect with GitHub" off',
        ...CONNECT_ROW.slice(1),
        ...BOT_HEAD,
        ...NONE_OFF,
      ],
    },
    {
      preview: 'GitHub panel: sign-in failed',
      github: {
        ...NOT_CONNECTED,
        error: {
          code: 'github/expired-token',
          message: 'The code expired. Click Connect with GitHub to get a new one.',
        },
      },
      drawn: [
        'text "The code expired. Click Connect with GitHub to get a new one."',
        ...CONNECT_ROW,
        ...BOT_HEAD,
        ...NONE_OFF,
      ],
    },
    {
      preview: 'GitHub panel: bot creation failed',
      github: CONNECTED,
      bot: {
        ...BOT_NONE,
        error: {
          code: 'github-bot/timeout',
          message:
            'GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again.',
        },
      },
      drawn: [
        ...CONNECTED_ROW,
        ...HINT,
        ...BOT_HEAD,
        ...NONE_ON,
        'text "GitHub did not send the new App back within 5 minutes, so Scout saved nothing. Try again."',
      ],
    },
    {
      preview: 'GitHub panel: bot not read',
      github: CONNECTED,
      extra: () => ({
        githubBotStatus: vi.fn(async () => {
          throw ipcError(
            'scout:github.botStatus',
            'github-bot/network-error',
            'Could not reach GitHub.',
          );
        }),
      }),
      drawn: [
        ...CONNECTED_ROW,
        ...HINT,
        'heading "Use a Scout bot on GitHub (optional)"',
        'text "Could not reach GitHub."',
      ],
    },
    {
      preview: 'GitHub panel: Disconnect failed',
      github: CONNECTED,
      extra: () => ({
        githubSignOut: vi.fn(async () => {
          throw ipcError(
            'scout:github.signOut',
            'github/token-file-not-removed',
            'Could not remove the GitHub token file, so Scout is still connected. Try Disconnect again.',
          );
        }),
      }),
      act: (fixture) => confirmed(fixture, 'github-disconnect'),
      drawn: [
        ...CONNECTED_ROW,
        ...HINT,
        ...BOT_HEAD,
        ...NONE_ON,
        'text "Could not remove the GitHub token file, so Scout is still connected. Try Disconnect again."',
      ],
    },
    {
      preview: 'GitHub panel: bot name not read yet',
      github: CONNECTED,
      bot: { ...CREATED, name: null },
      drawn: [...CONNECTED_ROW, ...HINT, ...BOT_HEAD, ...CREATED_ROWS, ...exists('not read yet')],
    },
    {
      preview: 'GitHub panel: bot removed',
      github: CONNECTED,
      extra: () => {
        let bot: GitHubBotStatus = INSTALLED;
        return {
          githubBotStatus: vi.fn(async () => bot),
          githubBotRemove: vi.fn(async () => {
            bot = BOT_NONE;
            return bot;
          }),
        };
      },
      act: (fixture) => confirmed(fixture, 'github-bot-remove'),
      drawn: [...CONNECTED_ROW, ...HINT, ...BOT_HEAD, ...NONE_ON, ...REMOVED_LINE],
    },
  ];

  it.each(PREVIEWS.map((row) => [row.preview, row] as const))(
    '%s: the panel draws its lines and controls in this order, each named as the artboard names it, off where it is drawn off',
    async (_preview, row) => {
      const fixture = await screen({
        github: row.github,
        ...(row.bot === undefined ? {} : { bot: row.bot }),
        ...(row.extra === undefined ? {} : { extra: row.extra() }),
      });
      await open(fixture, 'github');
      await row.act?.(fixture);

      expect(drawn(fixture)).toEqual([...row.drawn]);
    },
  );

  it('GitHub panel: Remove the bot asks first: the shared confirmation titled Remove the Scout bot?, its sentence, Cancel focused and Remove on the danger colour with the trash glyph, the bot still installed under it', async () => {
    const fixture = await screen({ github: CONNECTED, bot: INSTALLED });
    await open(fixture, 'github');

    q(fixture, 'github-bot-remove').click();
    await settle(fixture);
    const dialog = document.querySelector<HTMLElement>('sc-confirm-dialog')!;
    const remove = dialog.querySelector<HTMLElement>('[data-test="confirm-ok"]')!;

    expect({
      role: dialog.closest('[role="alertdialog"]') !== null,
      title: words(dialog.querySelector('h2')),
      body: words(dialog.querySelector('.body')),
      buttons: Array.from(dialog.querySelectorAll('button'), (b) => words(b)),
      focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      danger: remove.classList.contains('danger'),
      icon: remove.querySelector('sc-icon svg') !== null,
      panel: drawn(fixture).slice(-INSTALLED_ROWS.length - exists('scout-acme').length),
    }).toEqual({
      role: true,
      title: 'Remove the Scout bot?',
      body: 'Scout stops signing as the bot and acts as you again. The App stays on GitHub until you delete it there.',
      buttons: ['Cancel', 'Remove'],
      focused: 'confirm-cancel',
      danger: true,
      icon: true,
      panel: [...INSTALLED_ROWS, ...exists('scout-acme')],
    });
  });

  it('the toasts the artboard draws: "scout-acme[bot] was created on GitHub" when GitHub sends the App back, "scout-acme[bot] is installed on 14 repositories" when it is installed, "Scout no longer uses the bot" after Remove', async () => {
    const shown = vi.spyOn(NoticeService.prototype, 'show');
    let bot: GitHubBotStatus = { ...BOT_NONE, state: 'creating' };
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const fixture = await screen({
      github: CONNECTED,
      extra: {
        githubBotStatus: vi.fn(async () => bot),
        githubBotRemove: vi.fn(async () => {
          bot = BOT_NONE;
          return bot;
        }),
      },
    });
    await open(fixture, 'github');

    bot = CREATED;
    await vi.advanceTimersByTimeAsync(3000);
    bot = INSTALLED;
    await vi.advanceTimersByTimeAsync(3000);
    vi.useRealTimers();
    await settle(fixture);
    await confirmed(fixture, 'github-bot-remove');

    expect(shown.mock.calls.map(([message, kind]) => [message, kind])).toEqual([
      ['scout-acme[bot] was created on GitHub', 'success'],
      ['scout-acme[bot] is installed on 14 repositories', 'success'],
      ['Scout no longer uses the bot', 'success'],
    ]);
    expect(toasts()).toEqual(['Scout no longer uses the bot']);
  });

  it.each([
    ['on', null, true],
    ['off', 'true', false],
  ] as const)(
    'Preview: GitHub hint %s: the hint and Set up Docker are drawn under the connected row only while Docker runs no tests',
    async (_switch, stored, hint) => {
      const fixture = await screen({
        github: CONNECTED,
        extra: { vaultGetPublic: vi.fn(async () => ({ value: stored })) },
      });
      await open(fixture, 'github');

      expect(drawn(fixture).slice(0, hint ? 4 : 2)).toEqual(
        hint ? [...CONNECTED_ROW, ...HINT] : CONNECTED_ROW,
      );
      expect(drawn(fixture).includes(HINT[1]!)).toBe(hint);
    },
  );

  it('Features unreadable: the alert over the grid says the features could not be read and why, with Try again, and not that the control plane is down', async () => {
    const fixture = await screen({
      list: async () => {
        throw ipcError(
          'scout:features.list',
          'control-plane/permission-denied',
          "the Scout control plane refused this window's session.",
        );
      },
    });
    const alert = q(fixture, 'features-error').closest('[role="alert"]')!;

    expect({
      text: words(q(fixture, 'features-error')),
      buttons: Array.from(alert.querySelectorAll('button'), (b) => words(b)),
      glyph: paths(alert.querySelector('svg')!),
      backendDown: (fixture.nativeElement as HTMLElement).querySelector(
        '[data-test="backend-unavailable"]',
      ),
    }).toEqual({
      text: "The features could not be read: the Scout control plane refused this window's session.",
      buttons: ['Try again'],
      glyph: ['M12 7v6M12 16.5v.5'],
      backendDown: null,
    });
  });
});
