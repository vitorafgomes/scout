import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection } from '@angular/core';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  OllamaUnreachableError,
  RESTART_DEFERRED_LINE,
  type WorkerRestartResponse,
  type AdminKeyProvider,
  type OpenRouterKeyState,
  type OrgSpendState,
  type ProviderKeyValidateResponse,
  type UsageSnapshot,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LLM_CONFIG_STORAGE_KEY, LlmConfigStore } from '../core/llm-config.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { restartAsMain } from '../core/main-restart.testing';
import { ScoutBridgeService } from '../core/scout-bridge';
import { CLAUDE_ICON, SourcesComponent } from './sources.component';

const SHIELD_GENERIC = 'M12 2 4 5v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V5Z';
const CUSTOM_API_GENERIC = 'M8 8l-4 4 4 4M16 8l4 4-4 4';

function brandFile(name: string): string {
  return join(process.cwd(), 'src/assets/brands', name);
}

interface FakeBridge {
  llmListOllamaModels: ReturnType<typeof vi.fn>;
  llmListLmStudioModels: ReturnType<typeof vi.fn>;
  appOpenExternal: ReturnType<typeof vi.fn>;
  vaultStore: ReturnType<typeof vi.fn>;
  vaultHas: ReturnType<typeof vi.fn>;
  vaultDelete: ReturnType<typeof vi.fn>;
  validateProviderKey: ReturnType<typeof vi.fn>;
  vaultGetPublic: ReturnType<typeof vi.fn>;
  workerStatus: ReturnType<typeof vi.fn>;
  workerStop: ReturnType<typeof vi.fn>;
  workerStart: ReturnType<typeof vi.fn>;
  workerRestart: ReturnType<typeof vi.fn>;
  chatgptStatus: ReturnType<typeof vi.fn>;
  chatgptSignIn: ReturnType<typeof vi.fn>;
  chatgptSignOut: ReturnType<typeof vi.fn>;
  chatgptCancelSignIn: ReturnType<typeof vi.fn>;
  chatgptListModels: ReturnType<typeof vi.fn>;
  accessGet: ReturnType<typeof vi.fn>;
  usageGet: ReturnType<typeof vi.fn>;
  onUsageChanged: ReturnType<typeof vi.fn>;
  usageOpenRouterKey: ReturnType<typeof vi.fn>;
  usageOrgSpend: ReturnType<typeof vi.fn>;
  usageAdminKeySave: ReturnType<typeof vi.fn>;
  usageAdminKeyRemove: ReturnType<typeof vi.fn>;
  llmUnloadOllamaModels: ReturnType<typeof vi.fn>;
}

const PLAN_MODELS = [
  { slug: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol' },
  { slug: 'gpt-5.6-luna', displayName: 'GPT-5.6 Luna' },
];

interface FakeChatGptStatus {
  signedIn: boolean;
  email: string | null;
  planUsageGranted: boolean;
}

const CHATGPT_OUT: FakeChatGptStatus = { signedIn: false, email: null, planUsageGranted: false };
const CHATGPT_IN: FakeChatGptStatus = {
  signedIn: true,
  email: 'owner@example.com',
  planUsageGranted: true,
};

interface FakeModel {
  name: string;
  modifiedAt: string;
  sizeBytes: number;
}

interface FakeLmStudioModel {
  id: string;
}

function makeBridge(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
    vaultStoreError?: unknown;
    vaultHasError?: unknown;
    vaultHas?: boolean;
    vaultKeys?: readonly string[];
    vaultHasByKey?: Readonly<Record<string, boolean>>;
    validation?: ProviderKeyValidateResponse;
    publicValues?: Readonly<Record<string, string>>;
    vaultDeleteError?: unknown;
    workerState?: string;
    workerStartError?: unknown;
    chatgpt?: FakeChatGptStatus;
    chatgptSignInResult?: FakeChatGptStatus;
    chatgptSignInError?: unknown;
    revocationConfirmed?: boolean;
    allowedUserIds?: readonly string[];
    usage?: UsageSnapshot;
    openRouterKey?: OpenRouterKeyState | null;
    orgSpend?: Partial<Record<AdminKeyProvider, OrgSpendState | null>>;
    adminSave?: ProviderKeyValidateResponse;
    unload?: () => Promise<{ unloaded: readonly string[]; refused: readonly string[] }>;
  } = {},
): FakeBridge {
  const orgSpend = { ...opts.orgSpend };
  const bridge: FakeBridge = {
    workerRestart: vi.fn(() => restartAsMain(bridge)),
    llmListOllamaModels: vi.fn(async () => {
      if (opts.listError !== undefined) throw opts.listError;
      return { models: opts.models ?? [] };
    }),
    llmListLmStudioModels: vi.fn(async () => {
      if (opts.lmStudioError !== undefined) throw opts.lmStudioError;
      return { models: opts.lmStudioModels ?? [] };
    }),
    appOpenExternal: vi.fn(async () => undefined),
    vaultStore: vi.fn(async () => {
      if (opts.vaultStoreError !== undefined) throw opts.vaultStoreError;
    }),
    vaultHas: vi.fn(async (req: { key: string }) => {
      if (opts.vaultHasError !== undefined) throw opts.vaultHasError;
      return {
        exists:
          opts.vaultKeys !== undefined
            ? opts.vaultKeys.includes(req.key)
            : (opts.vaultHasByKey?.[req.key] ?? opts.vaultHas ?? false),
      };
    }),
    vaultDelete: vi.fn(async () => {
      if (opts.vaultDeleteError !== undefined) throw opts.vaultDeleteError;
      return { deleted: true };
    }),
    validateProviderKey: vi.fn(async () => opts.validation ?? { ok: true }),
    vaultGetPublic: vi.fn(async (req: { key: string }) => ({
      value: opts.publicValues?.[req.key] ?? null,
    })),
    workerStatus: vi.fn(async () => ({ state: opts.workerState ?? 'idle', restarts: 0 })),
    workerStop: vi.fn(async () => ({ exitCode: 0 })),
    workerStart: vi.fn(async () => {
      if (opts.workerStartError !== undefined) throw opts.workerStartError;
      return { pid: 42 };
    }),
    chatgptStatus: vi.fn(async () => opts.chatgpt ?? CHATGPT_OUT),
    chatgptSignIn: vi.fn(async () => {
      if (opts.chatgptSignInError !== undefined) throw opts.chatgptSignInError;
      return opts.chatgptSignInResult ?? CHATGPT_IN;
    }),
    chatgptSignOut: vi.fn(async () => ({ revocationConfirmed: opts.revocationConfirmed ?? true })),
    chatgptCancelSignIn: vi.fn(async () => undefined),
    chatgptListModels: vi.fn(async () => {
      return { models: PLAN_MODELS };
    }),
    accessGet: vi.fn(async () => ({ userIds: opts.allowedUserIds ?? [] })),
    usageGet: vi.fn(async () => opts.usage ?? {}),
    onUsageChanged: vi.fn(() => () => undefined),
    usageOpenRouterKey: vi.fn(async () => opts.openRouterKey ?? null),
    usageOrgSpend: vi.fn(
      async (req: { provider: AdminKeyProvider }) => orgSpend[req.provider] ?? null,
    ),
    usageAdminKeySave: vi.fn(async (req: { provider: AdminKeyProvider }) => {
      const result = opts.adminSave ?? { ok: true };
      if (result.ok) {
        orgSpend[req.provider] = {
          amount: 12.4,
          currency: 'usd',
          readAt: new Date(2026, 6, 14, 9, 5).toISOString(),
          error: null,
        };
      }
      return result;
    }),
    usageAdminKeyRemove: vi.fn(async (req: { provider: AdminKeyProvider }) => {
      orgSpend[req.provider] = null;
    }),
    llmUnloadOllamaModels: vi.fn(opts.unload ?? (async () => ({ unloaded: [], refused: [] }))),
  };
  return bridge;
}

/** jsdom lays nothing out, so the CDK focus trap would find no visible button; this checker answers as a browser would for an enabled one. */
const LAID_OUT: Pick<
  InteractivityChecker,
  'isDisabled' | 'isVisible' | 'isTabbable' | 'isFocusable'
> = {
  isDisabled: (el: HTMLElement) => el.hasAttribute('disabled'),
  isVisible: () => true,
  isTabbable: (el: HTMLElement) => focusable(el),
  isFocusable: (el: HTMLElement) => focusable(el),
};

function focusable(el: HTMLElement): boolean {
  if (el.hasAttribute('disabled')) return false;
  const tag = el.tagName.toLowerCase();
  if (['button', 'input', 'select', 'textarea'].includes(tag)) return true;
  if (tag === 'a' && el.hasAttribute('href')) return true;
  const tabindex = el.getAttribute('tabindex');
  return tabindex !== null && Number(tabindex) >= 0;
}

async function setup(
  opts: {
    models?: readonly FakeModel[];
    listError?: unknown;
    lmStudioModels?: readonly FakeLmStudioModel[];
    lmStudioError?: unknown;
    vaultStoreError?: unknown;
    vaultHasError?: unknown;
    vaultHas?: boolean;
    vaultKeys?: readonly string[];
    vaultHasByKey?: Readonly<Record<string, boolean>>;
    validation?: ProviderKeyValidateResponse;
    publicValues?: Readonly<Record<string, string>>;
    vaultDeleteError?: unknown;
    workerState?: string;
    workerStartError?: unknown;
    chatgpt?: FakeChatGptStatus;
    chatgptSignInResult?: FakeChatGptStatus;
    chatgptSignInError?: unknown;
    revocationConfirmed?: boolean;
    allowedUserIds?: readonly string[];
    usage?: UsageSnapshot;
    openRouterKey?: OpenRouterKeyState | null;
    orgSpend?: Partial<Record<AdminKeyProvider, OrgSpendState | null>>;
    adminSave?: ProviderKeyValidateResponse;
    unload?: () => Promise<{ unloaded: readonly string[]; refused: readonly string[] }>;
  } = {},
): Promise<{
  fixture: ComponentFixture<SourcesComponent>;
  bridge: FakeBridge;
}> {
  const bridge = makeBridge(opts);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
    ],
  });
  const fixture = TestBed.createComponent(SourcesComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  await Promise.resolve();
  await Promise.resolve();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, bridge };
}

/** The notices shown so far, read from the snack bar's text on the page. */
function noticesShown(): string[] {
  return Array.from(document.querySelectorAll('.sc-notice'), (n) =>
    (n.querySelector('[matsnackbarlabel]')?.textContent ?? '').trim(),
  );
}

async function answerConfirm(
  fixture: ComponentFixture<SourcesComponent>,
  answer: 'confirm-ok' | 'confirm-cancel',
): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${answer}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  for (let i = 0; i < 3; i += 1) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('SourcesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  });

  it('renders both Local and Cloud sections by default', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('renders all nine provider cards', async () => {
    const { fixture } = await setup();
    const expectedIds = [
      'ollama',
      'lm-studio',
      'chatgpt-plus',
      'openai-api',
      'google-gemini',
      'claude-api',
      'typesafe-jev',
      'openrouter',
      'custom-api',
    ];

    for (const id of expectedIds) {
      expect(
        fixture.nativeElement.querySelector(`[data-test="card-${id}"]`),
        `card ${id} should render`,
      ).not.toBeNull();
    }
  });

  const LOCAL_TITLES = ['Ollama', 'LM Studio'];
  const CLOUD_TITLES = [
    'ChatGPT plan',
    'OpenAI API',
    'Google Gemini',
    'Claude API',
    'TypeSafe (Jev)',
    'OpenRouter',
    'Custom API',
  ];

  it.each([
    ['All', 'filter-all', LOCAL_TITLES, CLOUD_TITLES],
    ['On this computer', 'filter-local', LOCAL_TITLES, []],
    ['Cloud', 'filter-cloud', [], CLOUD_TITLES],
  ] as const)(
    'remove the card: the %s filter lists exactly these cards, in order, and no Local Models card',
    async (_label, filter, local, cloud) => {
      const { fixture } = await setup();
      (fixture.nativeElement.querySelector(`[data-test="${filter}"]`) as HTMLButtonElement).click();
      fixture.detectChanges();
      const titlesIn = (section: string): string[] =>
        Array.from(
          fixture.nativeElement.querySelectorAll(
            `[data-test="section-${section}"] .card-title`,
          ) as NodeListOf<HTMLElement>,
          (h) => h.textContent?.trim() ?? '',
        );

      expect({ local: titlesIn('local'), cloud: titlesIn('cloud') }).toEqual({ local, cloud });
    },
  );

  it.each([
    [
      'Ollama connected',
      { models: [], lmStudioError: new Error('refused') },
      'Connected',
      'Not connected',
    ],
    [
      'LM Studio connected',
      { listError: new OllamaUnreachableError('down'), lmStudioModels: [] },
      'Not connected',
      'Detected',
    ],
    [
      'neither connected',
      { listError: new OllamaUnreachableError('down'), lmStudioError: new Error('refused') },
      'Not connected',
      'Not connected',
    ],
  ] as const)(
    'remove the card: with %s the screen never says "natively inside the app" nor offers "Manage downloaded models"',
    async (_state, opts, ollama, lmStudio) => {
      const { fixture } = await setup(opts);
      const page = fixture.nativeElement as HTMLElement;
      const badge = (id: string): string =>
        page.querySelector(`[data-test="badge-${id}"]`)?.textContent?.trim() ?? '';
      const buttons = Array.from(page.querySelectorAll('button'), (b) =>
        (b.textContent ?? '').replace(/\s+/g, ' ').trim(),
      );

      expect({
        ollama: badge('ollama'),
        lmStudio: badge('lm-studio'),
        native: (page.textContent ?? '').includes('natively inside the app'),
        manageDownloaded: buttons.filter((label) => /manage downloaded models/i.test(label)),
      }).toEqual({ ollama, lmStudio, native: false, manageDownloaded: [] });
    },
  );

  const PROVIDER_LOGOS: readonly (readonly [string, string, string, string])[] = [
    ['ollama', 'Ollama', 'ollama-white.svg', 'ollama-black.svg'],
    ['lm-studio', 'LM Studio', 'lm-studio-icon-color.svg', 'lm-studio-icon-color.svg'],
    ['chatgpt-plus', 'ChatGPT plan', 'openai-blossom-white.svg', 'openai-blossom-black.svg'],
    ['openai-api', 'OpenAI API', 'openai-blossom-white.svg', 'openai-blossom-black.svg'],
    ['google-gemini', 'Google Gemini', 'gemini-spark.svg', 'gemini-spark.svg'],
    ['openrouter', 'OpenRouter', 'openrouter-glyph-cloud.svg', 'openrouter-glyph-ink.svg'],
  ];

  it.each(PROVIDER_LOGOS)(
    'the %s card shows the official %s logo, a local file per theme, named for screen readers',
    async (id, provider, dark, light) => {
      const { fixture } = await setup();
      const icon = fixture.nativeElement.querySelector(`[data-test="icon-${id}"]`) as HTMLElement;
      const srcOf = (variant: string): string | null | undefined =>
        icon.querySelector(`img.mark--${variant}`)?.getAttribute('src');

      expect([icon.getAttribute('role'), icon.getAttribute('aria-label')]).toEqual([
        'img',
        `${provider} logo`,
      ]);
      expect([srcOf('dark'), srcOf('light')]).toEqual([
        `assets/brands/${dark}`,
        `assets/brands/${light}`,
      ]);
      expect(icon.querySelector('path')).toBeNull();
      for (const file of [dark, light]) {
        expect(existsSync(brandFile(file)), `${file} is bundled`).toBe(true);
      }
    },
  );

  it('the Claude logo is the official Claude spark from a local file, ready for the Claude API card', () => {
    expect(CLAUDE_ICON).toEqual({
      kind: 'asset',
      dark: 'assets/brands/claude-spark-clay.svg',
      light: 'assets/brands/claude-spark-clay.svg',
    });
    expect(existsSync(brandFile('claude-spark-clay.svg'))).toBe(true);
  });

  it('in the dark theme the dark variant of a logo is the visible one, in the light theme the light variant', async () => {
    const { fixture } = await setup();
    const icon = fixture.nativeElement.querySelector(
      '[data-test="icon-openai-api"]',
    ) as HTMLElement;
    const shown = (): string[] =>
      [...icon.querySelectorAll('img')]
        .filter((img) => getComputedStyle(img).display !== 'none')
        .map((img) => img.getAttribute('src') ?? '');

    document.documentElement.setAttribute('data-theme', 'dark');
    expect(shown()).toEqual(['assets/brands/openai-blossom-white.svg']);
    document.documentElement.setAttribute('data-theme', 'light');
    expect(shown()).toEqual(['assets/brands/openai-blossom-black.svg']);
  });

  it('the logo tile takes the theme surface and text colours, so a black variant sits on a light tile in the light theme', async () => {
    const { fixture } = await setup();
    const icon = fixture.nativeElement.querySelector(
      '[data-test="icon-openai-api"]',
    ) as HTMLElement;

    const tileRule = [...document.styleSheets]
      .flatMap((sheet) => [...sheet.cssRules])
      .find(
        (rule): rule is CSSStyleRule =>
          rule instanceof CSSStyleRule &&
          icon.matches(rule.selectorText) &&
          /^\.icon\[/.test(rule.selectorText),
      );

    expect(tileRule?.style.getPropertyValue('background')).toBe('var(--sc-surface-2)');
    expect(tileRule?.style.getPropertyValue('color')).toBe('var(--sc-fg)');
  });

  it('every brand file the cards load is a plain local SVG: no script, no event attribute, no external reference', () => {
    const files = readdirSync(brandFile('')).filter((name) => name.endsWith('.svg'));
    expect(files.length).toBeGreaterThan(0);
    for (const name of files) {
      const svg = readFileSync(brandFile(name), 'utf8');
      expect(svg, `${name} has no script`).not.toMatch(/<\s*script/i);
      expect(svg, `${name} has no event attribute`).not.toMatch(/\son[a-z]+\s*=/i);
      expect(svg, `${name} has no foreignObject`).not.toMatch(/<\s*foreignObject/i);
      const refs = [...svg.matchAll(/(?:href\s*=\s*"|url\(\s*)([^")]*)/g)].map((m) => m[1] ?? '');
      expect(
        refs.filter((ref) => !ref.startsWith('#') && !ref.startsWith('data:image/')),
        `${name} references nothing outside itself`,
      ).toEqual([]);
    }
  });

  it('TypeSafe and Custom API keep their generic icons with an accessible name', async () => {
    const { fixture } = await setup();
    const icon = (id: string): HTMLElement =>
      fixture.nativeElement.querySelector(`[data-test="icon-${id}"]`) as HTMLElement;

    expect(icon('typesafe-jev').getAttribute('aria-label')).toBe('TypeSafe (Jev) logo');
    expect(icon('typesafe-jev').querySelector('path')?.getAttribute('d')).toBe(SHIELD_GENERIC);
    expect(icon('custom-api').querySelector('path')?.getAttribute('d')).toBe(CUSTOM_API_GENERIC);
    expect(icon('claude-api').getAttribute('aria-label')).toBe('Claude API logo');
  });

  it('the Ollama card offers Manage local models only while Ollama answers, and it opens the Ollama models there', async () => {
    const off = await setup({ listError: new OllamaUnreachableError('down') });
    expect(
      off.fixture.nativeElement.querySelector('[data-test="manage-ollama-models"]'),
    ).toBeNull();
    expect(off.fixture.nativeElement.querySelector('[data-test="ollama-models"]')).toBeNull();
    TestBed.resetTestingModule();

    const { fixture, bridge } = await setup({ models: [] });
    Object.assign(bridge, {
      onLlmPullOllamaProgress: vi.fn(() => () => undefined),
      onWhisperDownloadProgress: vi.fn(() => () => undefined),
      whisperListInstalled: vi.fn(async () => ({ installed: [] })),
      onWorkerEvent: vi.fn(() => () => undefined),
    });
    const manage = fixture.nativeElement.querySelector(
      '[data-test="manage-ollama-models"]',
    ) as HTMLButtonElement;
    expect(manage.getAttribute('aria-haspopup')).toBe('dialog');
    manage.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const panel = document.querySelector('[data-test="ollama-models"]') as HTMLElement;
    expect(panel.closest('[role="dialog"]')).not.toBeNull();
    expect(panel.querySelector('[data-test="row-gemma-3-4b"]')).not.toBeNull();
    expect(panel.querySelector('[data-test="row-whisper-base"]')).toBeNull();
    expect(panel.querySelector('[data-test="crumb-back"]')).toBeNull();
    expect(panel.classList.contains('sc-dark-canvas')).toBe(false);
  });

  it('Local filter hides the Cloud section', async () => {
    const { fixture } = await setup();

    const button = fixture.nativeElement.querySelector(
      '[data-test="filter-local"]',
    ) as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).toBeNull();
  });

  it('Cloud filter hides the Local section', async () => {
    const { fixture } = await setup();

    const button = fixture.nativeElement.querySelector(
      '[data-test="filter-cloud"]',
    ) as HTMLButtonElement;
    button.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('initial Ollama scan calls the bridge with default endpoint', async () => {
    const { bridge } = await setup();
    expect(bridge.llmListOllamaModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:11434',
    });
  });

  it('successful scan marks Ollama as Connected (reachable counts, even with zero models)', async () => {
    const { fixture } = await setup({ models: [] });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;
    expect(badge.textContent?.trim()).toBe('Connected');
  });

  it('failed scan marks Ollama as Not connected', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not connected');
  });

  it('clicking Scan re-runs the bridge call', async () => {
    const { fixture, bridge } = await setup();
    bridge.llmListOllamaModels.mockClear();

    const action = fixture.nativeElement.querySelector(
      '[data-test="action-ollama"]',
    ) as HTMLButtonElement;
    action.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(bridge.llmListOllamaModels).toHaveBeenCalledTimes(1);
  });

  it('the Custom API card offers no action, only that it arrives later, and OpenRouter offers an enabled Connect', async () => {
    const { fixture } = await setup();

    const custom = fixture.nativeElement.querySelector(
      '[data-test="action-custom-api"]',
    ) as HTMLElement;
    const openRouter = fixture.nativeElement.querySelector(
      '[data-test="action-openrouter"]',
    ) as HTMLButtonElement;
    expect(custom.textContent?.trim()).toBe('Not available yet. It arrives in a later version.');
    expect(fixture.nativeElement.querySelector('[data-test="card-custom-api"] button')).toBeNull();
    expect(openRouter.disabled).toBe(false);
    expect(openRouter.getAttribute('aria-label')).toBe('Connect OpenRouter');
  });

  it('initial LM Studio scan calls the bridge with the default port', async () => {
    const { bridge } = await setup();
    expect(bridge.llmListLmStudioModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:1234',
    });
  });

  it('LM Studio scan failure marks it Not connected', async () => {
    const { fixture } = await setup({ lmStudioError: new Error('refused') });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-lm-studio"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not connected');
  });

  it('an LM Studio server that answers reads Detected, since Scout cannot answer with it yet', async () => {
    const { fixture } = await setup({ lmStudioModels: [] });

    expect(
      fixture.nativeElement.querySelector('[data-test="badge-lm-studio"]')?.textContent?.trim(),
    ).toBe('Detected');
    expect(
      fixture.nativeElement.querySelector('[data-test="lm-studio-note"]')?.textContent?.trim(),
    ).toBe('Scout cannot answer with LM Studio yet.');
  });

  it('LM Studio Scan button is enabled', async () => {
    const { fixture } = await setup();
    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-lm-studio"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
  });

  it('Download Ollama secondary opens the external URL', async () => {
    const { fixture, bridge } = await setup();
    const link = fixture.nativeElement.querySelector(
      '[data-test="secondary-ollama"]',
    ) as HTMLButtonElement;
    expect(link).not.toBeNull();
    link.click();
    await fixture.whenStable();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: 'https://ollama.com/download' });
  });

  it('Download LM Studio secondary opens the external URL', async () => {
    const { fixture, bridge } = await setup();
    const link = fixture.nativeElement.querySelector(
      '[data-test="secondary-lm-studio"]',
    ) as HTMLButtonElement;
    expect(link).not.toBeNull();
    link.click();
    await fixture.whenStable();

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: 'https://lmstudio.ai' });
  });
  function query<T extends Element>(fixture: ComponentFixture<SourcesComponent>, sel: string): T {
    return fixture.nativeElement.querySelector(sel) as T;
  }

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  async function settle(fixture: ComponentFixture<SourcesComponent>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function text(fixture: ComponentFixture<SourcesComponent>, sel: string): string {
    return query<HTMLElement>(fixture, sel)?.textContent ?? '';
  }

  function buttonsOf(fixture: ComponentFixture<SourcesComponent>, id: string): string[] {
    const card = query<HTMLElement>(fixture, `[data-test="card-${id}"]`);
    return [...card.querySelectorAll('.card-actions button')].map((b) =>
      (b.textContent ?? '').trim(),
    );
  }

  function storeKeysWritten(bridge: FakeBridge): string[] {
    return bridge.vaultStore.mock.calls.map((c) => (c[0] as { key: string }).key);
  }

  async function connectKey(
    fixture: ComponentFixture<SourcesComponent>,
    id: string,
    key: string,
  ): Promise<void> {
    query<HTMLButtonElement>(fixture, `[data-test="action-${id}"]`).click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, `[data-test="api-key-${id}"]`), key);
    query<HTMLButtonElement>(fixture, `[data-test="save-${id}"]`).click();
    await settle(fixture);
    await settle(fixture);
  }

  async function click(fixture: ComponentFixture<SourcesComponent>, sel: string): Promise<void> {
    query<HTMLButtonElement>(fixture, sel).click();
    await settle(fixture);
    await settle(fixture);
  }

  async function disconnectConfirmed(
    fixture: ComponentFixture<SourcesComponent>,
    sel: string,
  ): Promise<void> {
    query<HTMLButtonElement>(fixture, sel).click();
    await settle(fixture);
    await answerConfirm(fixture, 'confirm-ok');
  }

  function useGeminiWithBothKeys(): void {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'Gemini',
        model: 'gemini-2.5-pro',
        endpoint: 'http://localhost:11434',
      }),
    );
  }

  const BOTH_KEYS = { 'llm.openai.api-key': true, 'llm.gemini.api-key': true };
  const GEMINI_IN_VAULT = { 'llm.provider': 'Gemini', 'llm.cloud-model': 'gemini-2.5-pro' };

  it('the three states: a card with no key offers Connect; a stored key shows Connected with Disconnect, Change key and the Models link; the provider in use shows In use with its model, Disconnect, Change key and the Models link', async () => {
    const empty = await setup();
    expect(text(empty.fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
    expect(buttonsOf(empty.fixture, 'openai-api')).toEqual(['Connect']);
    expect(
      query<HTMLElement>(empty.fixture, '[data-test="action-openai-api"]').getAttribute(
        'aria-label',
      ),
    ).toBe('Connect OpenAI API');
    TestBed.resetTestingModule();

    useGeminiWithBothKeys();
    const { fixture } = await setup({ vaultHasByKey: BOTH_KEYS, publicValues: GEMINI_IN_VAULT });

    const openAiBadge = text(fixture, '[data-test="badge-openai-api"]');
    expect(openAiBadge.trim()).toBe('Connected');
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Change key', 'Disconnect']);
    expect(text(fixture, '[data-test="choose-model-openai-api"]').trim()).toBe(
      'Choose a model in Models, Analysis',
    );
    expect(
      query<HTMLElement>(fixture, '[data-test="disconnect-openai-api"]').getAttribute('aria-label'),
    ).toBe('Disconnect OpenAI API');

    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('In use');
    expect(text(fixture, '[data-test="card-google-gemini"]')).toContain('gemini-2.5-pro');
    expect(buttonsOf(fixture, 'google-gemini')).toEqual(['Change key', 'Disconnect']);
    expect(query(fixture, '[data-test="choose-model-google-gemini"]')).not.toBeNull();
    expect(query(fixture, '[data-test="choose-model-openai-api"]')).not.toBeNull();
  });

  it('AI Sources keeps connect and disconnect only: the OpenAI and Gemini cards have no Use, Change model or model field, and the link opens Models on the Analysis tab', async () => {
    useGeminiWithBothKeys();
    const { fixture } = await setup({ vaultHasByKey: BOTH_KEYS, publicValues: GEMINI_IN_VAULT });

    for (const id of ['openai-api', 'google-gemini']) {
      expect(query(fixture, `[data-test="use-${id}"]`)).toBeNull();
      expect(query(fixture, `[data-test="change-model-${id}"]`)).toBeNull();
      expect(query(fixture, `[data-test="model-${id}"]`)).toBeNull();
    }
    const link = query<HTMLAnchorElement>(fixture, '[data-test="choose-model-openai-api"]');
    expect(link.getAttribute('href')).toBe('/llm?tab=analysis');
  });

  it('Connect while another cloud provider is in use stores only the OpenAI key after the provider accepts it, does not switch and does not restart Scout', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: { 'llm.gemini.api-key': true },
      publicValues: { 'llm.provider': 'Gemini', 'llm.cloud-model': 'gemini-2.5-flash' },
    });
    const keyField = (): HTMLInputElement | null =>
      fixture.nativeElement.querySelector('[data-test="api-key-openai-api"]');

    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();
    expect(keyField()?.type).toBe('password');
    expect(keyField()?.getAttribute('autocomplete')).toBe('off');
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-typed-key');
    await click(fixture, '[data-test="save-openai-api"]');

    expect(bridge.validateProviderKey).toHaveBeenCalledWith({
      provider: 'OpenAI',
      apiKey: 'sk-typed-key',
    });
    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.openai.api-key', value: 'sk-typed-key' },
    ]);
    expect(keyField()).toBeNull();
    expect(text(fixture, '[data-test="badge-openai-api"]').trim()).toBe('Connected');
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Change key', 'Disconnect']);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Connected. Choose a model in Models → Analysis to switch Scout to OpenAI API.',
    );
    expect(fixture.nativeElement.textContent).not.toContain('sk-typed-key');
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('In use');
  });

  it('it answers with the provider that is connected: the first Connect makes Gemini the provider in use and restarts a running Scout', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      publicValues: { 'llm.provider': 'Ollama' },
    });

    await connectKey(fixture, 'google-gemini', 'gemini-typed-key');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.gemini.api-key', value: 'gemini-typed-key' },
      { key: 'llm.cloud-model', value: 'gemini-2.5-flash' },
      { key: 'llm.provider', value: 'Gemini' },
    ]);
    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('In use');
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(text(fixture, '[data-test="saved-google-gemini"]')).toContain(
      'Connected. Restarting Scout to use Google Gemini.',
    );
  });

  it('validate the key: a key the provider rejects shows the alert, stores nothing and clears the field', async () => {
    const { fixture, bridge } = await setup({
      validation: { ok: false, reason: 'invalid-key' },
    });

    await connectKey(fixture, 'openai-api', 'sk-rejected-key');

    expect(text(fixture, '[data-test="key-form-openai-api"] [role="alert"]')).toContain(
      'That key was rejected by OpenAI.',
    );
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]').value).toBe('');
    expect(fixture.nativeElement.textContent).not.toContain('sk-rejected-key');
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
  });

  it('a provider that cannot be reached shows the connection alert and stores nothing', async () => {
    const { fixture, bridge } = await setup({
      validation: { ok: false, reason: 'unreachable' },
    });

    await connectKey(fixture, 'google-gemini', 'gemini-typed-key');

    expect(text(fixture, '[data-test="key-form-google-gemini"] [role="alert"]')).toContain(
      'Gemini could not be reached; check the connection and try again.',
    );
    expect(bridge.vaultStore).not.toHaveBeenCalled();
  });

  it('saving with an empty API key shows an error and stores nothing', async () => {
    const { fixture, bridge } = await setup();

    await connectKey(fixture, 'openai-api', '   ');

    expect(bridge.validateProviderKey).not.toHaveBeenCalled();
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(text(fixture, '[data-test="key-form-openai-api"] [role="alert"]')).toContain(
      'Enter an API key.',
    );
  });

  it('a failed save shows the error text, keeps the form open and never shows the key', async () => {
    const { fixture } = await setup({ vaultStoreError: new Error('vault unavailable') });

    await connectKey(fixture, 'openai-api', 'sk-typed-key');

    expect(text(fixture, '[data-test="key-form-openai-api"] [role="alert"]')).toContain(
      'vault unavailable',
    );
    expect(fixture.nativeElement.textContent).not.toContain('sk-typed-key');
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
  });

  it('cancel closes the form and clears the typed key', async () => {
    const { fixture, bridge } = await setup();
    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-typed-key');

    query<HTMLButtonElement>(fixture, '[data-test="cancel-openai-api"]').click();
    fixture.detectChanges();
    expect(query(fixture, '[data-test="key-form-openai-api"]')).toBeNull();

    query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
    fixture.detectChanges();
    expect(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]').value).toBe('');
    expect(bridge.vaultStore).not.toHaveBeenCalled();
  });

  it('there is a way to disconnect: Disconnect on the provider in use deletes its key, switches Scout to the other connected provider and restarts it', async () => {
    useGeminiWithBothKeys();
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: BOTH_KEYS,
      publicValues: GEMINI_IN_VAULT,
    });

    await disconnectConfirmed(fixture, '[data-test="disconnect-google-gemini"]');

    expect(bridge.vaultDelete.mock.calls.map((c) => (c[0] as { key: string }).key)).toContain(
      'llm.gemini.api-key',
    );
    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-4o-mini' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(text(fixture, '[data-test="saved-google-gemini"]')).toContain(
      'Disconnected. Scout now answers with OpenAI API.',
    );
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('Not connected');
    expect(buttonsOf(fixture, 'google-gemini')).toEqual(['Connect']);
  });

  it('with no localStorage mirror the vault decides: OpenAI in the vault shows In use, and Disconnect of the last connected provider writes llm.provider=None', async () => {
    const { fixture, bridge } = await setup({
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });

    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    expect(text(fixture, '[data-test="card-openai-api"]')).toContain('gpt-4o');

    await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.provider', value: 'None' },
    ]);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Disconnected. No AI provider is connected; Scout will say so until you connect one.',
    );
  });

  it('a key that cannot be deleted while disconnecting the provider in use: Scout still moves off it and restarts, the error is shown, and the card shows Connected', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
      vaultDeleteError: new Error('keyring locked'),
    });

    await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.provider', value: 'None' },
    ]);
    expect(text(fixture, '[data-test="card-openai-api"] [role="alert"]')).toContain(
      'Disconnected from Scout, but the stored key could not be removed: keyring locked',
    );
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    const badge = text(fixture, '[data-test="badge-openai-api"]');
    expect(badge).toContain('Connected');
    expect(badge).not.toContain('In use');
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Change key', 'Disconnect']);
  });

  it('Disconnect on a provider that is not in use deletes only its key and does not restart a running Scout', async () => {
    useGeminiWithBothKeys();
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: BOTH_KEYS,
      publicValues: GEMINI_IN_VAULT,
    });

    await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultDelete.mock.calls.map((c) => c[0])).toEqual([{ key: 'llm.openai.api-key' }]);
    expect(storeKeysWritten(bridge)).not.toContain('llm.provider');
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(noticesShown()).toEqual([
      'OpenAI API disconnected. The key was deleted from this computer.',
    ]);
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('In use');
  });

  it('Ollama is an explicit choice: when its server answers, Use makes it the provider in use and restarts a running Scout', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });
    const use = query<HTMLButtonElement>(fixture, '[data-test="use-ollama"]');
    expect(use.getAttribute('aria-label')).toBe('Use Ollama');

    await click(fixture, '[data-test="use-ollama"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.provider', value: 'Ollama' },
    ]);
    expect(text(fixture, '[data-test="badge-ollama"]')).toContain('In use');
    expect(text(fixture, '[data-test="badge-openai-api"]')).not.toContain('In use');
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(text(fixture, '[data-test="saved-ollama"]')).toContain(
      'Restarting Scout to use Ollama.',
    );
    expect(query(fixture, '[data-test="use-ollama"]')).toBeNull();
  });

  const RESTART_ROWS: readonly (readonly [
    card: string,
    from: string,
    act: (fixture: ComponentFixture<SourcesComponent>) => Promise<void>,
    message: string,
  ])[] = [
    [
      'Claude API',
      'Ollama',
      (fixture) => connectKey(fixture, 'claude-api', 'sk-ant-typed-key'),
      'Connected. Restarting Scout to use Claude API.',
    ],
    [
      'OpenAI API',
      'Ollama',
      (fixture) => connectKey(fixture, 'openai-api', 'sk-typed-key'),
      'Connected. Restarting Scout to use OpenAI API.',
    ],
    [
      'Google Gemini',
      'Ollama',
      (fixture) => connectKey(fixture, 'google-gemini', 'gemini-typed-key'),
      'Connected. Restarting Scout to use Google Gemini.',
    ],
    [
      'ChatGPT plan',
      'None',
      (fixture) => click(fixture, '[data-test="chatgpt-sign-in"]'),
      'Restarting Scout to use ChatGPT plan.',
    ],
    [
      'Ollama',
      'OpenAI',
      (fixture) => click(fixture, '[data-test="use-ollama"]'),
      'Restarting Scout to use Ollama.',
    ],
  ];

  for (const [card, from, act, message] of RESTART_ROWS) {
    it(`the restart message names the card: ${card} reads "${message}"`, async () => {
      const id = card === 'ChatGPT plan' ? 'chatgpt-plus' : card.toLowerCase().replace(/ /g, '-');
      const { fixture, bridge } = await setup({
        workerState: 'running',
        vaultHasByKey: { 'llm.openai.api-key': from === 'OpenAI' },
        publicValues: { 'llm.provider': from },
      });

      await act(fixture);

      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
      expect(text(fixture, `[data-test="saved-${id}"]`).trim()).toBe(message);
    });
  }

  it('Ollama offers no Use while its server does not answer, and neither local card offers Disconnect', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });

    expect(query(fixture, '[data-test="use-ollama"]')).toBeNull();
    expect(query(fixture, '[data-test="use-lm-studio"]')).toBeNull();
    expect(query(fixture, '[data-test="disconnect-ollama"]')).toBeNull();
    expect(query(fixture, '[data-test="disconnect-lm-studio"]')).toBeNull();
  });

  it('Ollama shows In use when the vault says Ollama', async () => {
    const { fixture } = await setup({ publicValues: { 'llm.provider': 'Ollama' } });

    expect(text(fixture, '[data-test="badge-ollama"]')).toContain('In use');
  });

  it('Disconnect of the last connected provider in use when Scout is stopped writes None and says no AI provider is connected', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'idle',
      vaultHasByKey: { 'llm.gemini.api-key': true },
      publicValues: { 'llm.provider': 'Gemini', 'llm.cloud-model': 'gemini-2.5-flash' },
    });

    await disconnectConfirmed(fixture, '[data-test="disconnect-google-gemini"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.provider', value: 'None' },
    ]);
    expect(storeKeysWritten(bridge)).not.toContain('llm.model');
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(text(fixture, '[data-test="saved-google-gemini"]')).toContain(
      'Disconnected. No AI provider is connected; Scout will say so until you connect one.',
    );
    expect(text(fixture, '[data-test="badge-ollama"]')).not.toContain('In use');
  });

  async function chatGptInUse(
    opts: Parameters<typeof setup>[0],
  ): Promise<{ fixture: ComponentFixture<SourcesComponent>; bridge: FakeBridge }> {
    return setup({
      chatgpt: CHATGPT_IN,
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
      ...opts,
    });
  }

  it('the ChatGPT plan card, not connected, offers "Continue with ChatGPT" and describes plan usage', async () => {
    const { fixture } = await setup();
    const card = query<HTMLElement>(fixture, '[data-test="card-chatgpt-plus"]');
    const signIn = query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]');

    expect(signIn.disabled).toBe(false);
    expect(signIn.textContent?.trim()).toBe('Continue with ChatGPT');
    expect(query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent).toContain(
      'Not connected',
    );
    expect(card.textContent).toContain(
      "Sign in with your ChatGPT Plus or Pro plan. Usage counts against your plan's limits.",
    );
    expect(card.textContent).not.toContain('Not supported');
  });

  it('ChatGPT plan, as in the prototype: the card is titled "ChatGPT plan" and its logo is named so', async () => {
    const { fixture } = await setup();
    const card = query<HTMLElement>(fixture, '[data-test="card-chatgpt-plus"]');

    expect(card.querySelector('.card-title')?.textContent?.trim()).toBe('ChatGPT plan');
    expect(card.querySelector('[data-test="icon-chatgpt-plus"]')?.getAttribute('aria-label')).toBe(
      'ChatGPT plan logo',
    );
  });

  it('"Continue with ChatGPT" signs in through the main process and the card shows the account, the Models link and Sign out', async () => {
    const { fixture, bridge } = await setup({
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').click();
    await settle(fixture);

    expect(bridge.chatgptSignIn).toHaveBeenCalledTimes(1);
    expect(
      query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent?.trim(),
    ).toBe('Connected');
    expect(query<HTMLElement>(fixture, '[data-test="chatgpt-account"]').textContent).toContain(
      'owner@example.com',
    );
    expect(text(fixture, '[data-test="choose-model-chatgpt-plus"]').trim()).toBe('Choose a model');
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-out"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-in"]')).toBeNull();
  });

  it('a failed sign-in stays Not connected and shows the reason in an alert', async () => {
    const { fixture } = await setup({
      chatgptSignInError: new Error('OAuth flow cancelled by user'),
    });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').click();
    await settle(fixture);

    expect(query<HTMLElement>(fixture, '[data-test="chatgpt-error"]').textContent).toContain(
      'OAuth flow cancelled by user',
    );
    expect(query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').disabled).toBe(false);
  });

  it('signed in without plan usage: no Models link, and the card says Scout cannot answer through it', async () => {
    const { fixture } = await setup({ chatgpt: { ...CHATGPT_IN, planUsageGranted: false } });

    expect(
      fixture.nativeElement.querySelector('[data-test="choose-model-chatgpt-plus"]'),
    ).toBeNull();
    expect(
      query<HTMLElement>(fixture, '[data-test="chatgpt-no-plan-usage"]').textContent,
    ).toContain('OpenAI did not grant plan usage to Scout');
  });

  it('the ChatGPT card no longer has a Model select: in use, it shows the model, the Models link and Sign out, and never loads the model list', async () => {
    const { fixture, bridge } = await chatGptInUse({ workerState: 'idle' });

    expect(
      query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent?.trim(),
    ).toBe('In use');
    expect(query<HTMLElement>(fixture, '[data-test="chatgpt-model"]').textContent).toContain(
      'gpt-5.6-luna',
    );
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-model-select"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('select')).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="choose-model-chatgpt-plus"]'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-out"]')).not.toBeNull();
    expect(bridge.chatgptListModels).not.toHaveBeenCalled();
  });

  it('Sign out while ChatGPT is in use moves Scout to no provider (never Ollama) and restarts a running Scout', async () => {
    const { fixture, bridge } = await chatGptInUse({ workerState: 'running' });
    bridge.vaultStore.mockClear();
    bridge.workerStop.mockClear();
    bridge.workerStart.mockClear();

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.chatgptSignOut).toHaveBeenCalledTimes(1);
    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.provider', value: 'None' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(
      query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent?.trim(),
    ).toBe(
      'Signed out of ChatGPT. No AI provider is connected; Scout will say so until you connect one.',
    );
    expect(
      query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').textContent?.trim(),
    ).toBe('Continue with ChatGPT');
  });

  it('Sign out while ChatGPT is not in use changes no provider and does not restart Scout', async () => {
    const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN, workerState: 'running' });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.chatgptSignOut).toHaveBeenCalledTimes(1);
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(
      query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent?.trim(),
    ).toBe('Signed out of ChatGPT.');
  });

  describe('ChatGPT card and allowed users', () => {
    const NO_ALLOWED_USERS =
      'Nobody can use this connection from chat until you add allowed users in Settings.';

    async function inUse(allowedUserIds: readonly string[]) {
      window.localStorage.setItem(
        LLM_CONFIG_STORAGE_KEY,
        JSON.stringify({
          provider: 'ChatGptPlan',
          model: 'gpt-5.6-luna',
          endpoint: 'http://localhost:11434',
        }),
      );
      return setup({
        vaultHas: true,
        chatgpt: CHATGPT_IN,
        allowedUserIds,
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
      });
    }

    it('warns that nobody can use the plan from chat while ChatGPT is in use and no user is allowed', async () => {
      const { fixture } = await inUse([]);

      const warning = fixture.nativeElement.querySelector(
        '[data-test="access-warning-chatgpt"]',
      ) as HTMLElement | null;
      expect(warning?.textContent?.replace(/\s+/g, ' ').trim()).toBe(NO_ALLOWED_USERS);
    });

    it('does not warn once a user is allowed', async () => {
      const { fixture } = await inUse(['111']);

      expect(fixture.nativeElement.querySelector('[data-test="chatgpt-model"]')).not.toBeNull();
      expect(
        fixture.nativeElement.querySelector('[data-test="access-warning-chatgpt"]'),
      ).toBeNull();
    });

    it('does not warn while the plan is signed in but not in use, because the bot stays open', async () => {
      const { fixture } = await setup({ chatgpt: CHATGPT_IN, allowedUserIds: [] });

      expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-out"]')).not.toBeNull();
      expect(
        fixture.nativeElement.querySelector('[data-test="access-warning-chatgpt"]'),
      ).toBeNull();
    });
  });

  it('offers "Sign in again" when ChatGPT is still the chosen provider but the session has ended', async () => {
    window.localStorage.setItem(
      LLM_CONFIG_STORAGE_KEY,
      JSON.stringify({
        provider: 'ChatGptPlan',
        model: 'gpt-5.6-luna',
        endpoint: 'http://localhost:11434',
      }),
    );
    const { fixture } = await setup({
      vaultHas: true,
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    });

    expect(
      query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').textContent?.trim(),
    ).toBe('Sign in again');
  });

  it('Cancel while waiting for the browser aborts the sign-in and returns the card to Not connected without an error', async () => {
    const { fixture, bridge } = await setup();
    let rejectSignIn: (err: Error) => void = () => undefined;
    bridge.chatgptSignIn.mockImplementationOnce(
      () =>
        new Promise((_resolve, reject) => {
          rejectSignIn = reject;
        }),
    );
    bridge.chatgptCancelSignIn.mockImplementationOnce(async () => {
      rejectSignIn(new Error('OAuth flow aborted before the callback arrived'));
    });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').click();
    await settle(fixture);
    expect(query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').disabled).toBe(true);

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-cancel"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.chatgptCancelSignIn).toHaveBeenCalledTimes(1);
    const signIn = query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]');
    expect(signIn.disabled).toBe(false);
    expect(signIn.textContent?.trim()).toBe('Continue with ChatGPT');
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-cancel"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-error"]')).toBeNull();
    expect(query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent).toContain(
      'Not connected',
    );
  });

  it('Sign out whose remote revocation was not confirmed says so', async () => {
    const { fixture } = await setup({ chatgpt: CHATGPT_IN, revocationConfirmed: false });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]').click();
    await settle(fixture);

    expect(query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent).toContain(
      'OpenAI did not confirm the remote sign-out',
    );
    expect(query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent).toContain(
      'Not connected',
    );
  });

  it('it answers with the provider that is connected: signing in to ChatGPT while no cloud provider is in use makes it the provider in use and restarts a running Scout', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      publicValues: { 'llm.provider': 'None' },
    });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(
      query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent?.trim(),
    ).toBe('In use');
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('Sign out while ChatGPT is in use switches to another connected cloud provider and says so', async () => {
    const { fixture, bridge } = await setup({
      chatgpt: CHATGPT_IN,
      workerState: 'running',
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
    });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-4o-mini' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(
      query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent?.trim(),
    ).toBe('Signed out of ChatGPT. Scout now answers with OpenAI API.');
    expect(query<HTMLElement>(fixture, '[data-test="badge-openai-api"]').textContent).toContain(
      'In use',
    );
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('disconnecting the OpenAI key in use switches to the signed-in ChatGPT plan', async () => {
    const { fixture, bridge } = await setup({
      chatgpt: CHATGPT_IN,
      workerState: 'idle',
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });

    await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Disconnected. Scout now answers with ChatGPT plan.',
    );
    expect(
      query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent?.trim(),
    ).toBe('In use');
  });

  it('the TypeSafe (Jev) card stores the guard key without changing the LLM provider and shows Connected', async () => {
    const { fixture, bridge } = await setup();
    const card = query<HTMLElement>(fixture, '[data-test="card-typesafe-jev"]');
    expect(card.textContent).toContain('TypeSafe (Jev)');
    expect(card.textContent).toContain(
      'Read-only guard: checks each request with the recent conversation and refuses ones that ask Scout to change something.',
    );
    expect(query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]').textContent).toContain(
      'Not connected',
    );
    const action = query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]');
    expect(action.textContent).toContain('Add API key');

    action.click();
    fixture.detectChanges();

    const keyInput = query<HTMLInputElement>(fixture, '[data-test="api-key-typesafe-jev"]');
    expect(keyInput.type).toBe('password');
    expect(query(fixture, '[data-test="model-typesafe-jev"]')).toBeNull();
    type(keyInput, 'ts-typed-key');
    query<HTMLButtonElement>(fixture, '[data-test="save-typesafe-jev"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'guard.typesafe-api-key', value: 'ts-typed-key' },
    ]);
    expect(query(fixture, '[data-test="key-form-typesafe-jev"]')).toBeNull();
    const badge = query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]');
    expect(badge.textContent?.trim()).toBe('Connected');
    expect(query<HTMLElement>(fixture, '[data-test="saved-typesafe-jev"]').textContent).toContain(
      'Saved. Scout will use the read-only guard the next time it starts.',
    );
  });

  it('the TypeSafe (Jev) card with an empty key shows an error in an alert and stores nothing', async () => {
    const { fixture, bridge } = await setup();
    query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]').click();
    fixture.detectChanges();

    type(query<HTMLInputElement>(fixture, '[data-test="api-key-typesafe-jev"]'), '  ');
    query<HTMLButtonElement>(fixture, '[data-test="save-typesafe-jev"]').click();
    await settle(fixture);

    expect(bridge.vaultStore).not.toHaveBeenCalled();
    const alert = query<HTMLElement>(fixture, '[data-test="key-form-typesafe-jev"] [role="alert"]');
    expect(alert.textContent).toContain('Enter an API key.');
  });

  it('the TypeSafe (Jev) card shows Connected on load when the vault already holds the guard key', async () => {
    const { fixture } = await setup({ vaultKeys: ['guard.typesafe-api-key'] });

    const badge = query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]');
    expect(badge.textContent?.trim()).toBe('Connected');
  });

  it('the TypeSafe (Jev) card stays Not connected on load when only a cloud LLM key is stored', async () => {
    const { fixture } = await setup({ vaultKeys: ['llm.api-key'] });

    expect(query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]').textContent).toContain(
      'Not connected',
    );
  });

  it('saving the TypeSafe (Jev) key while Scout is running restarts it so the guard applies', async () => {
    const { fixture, bridge } = await setup({ workerState: 'running' });
    query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]').click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-typesafe-jev"]'), 'ts-typed-key');
    query<HTMLButtonElement>(fixture, '[data-test="save-typesafe-jev"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(query<HTMLElement>(fixture, '[data-test="saved-typesafe-jev"]').textContent).toContain(
      'Saved. Restarting Scout to use the read-only guard.',
    );
    expect(fixture.nativeElement.textContent).not.toContain('ts-typed-key');
  });

  const guardBadge = (fixture: ComponentFixture<SourcesComponent>): string =>
    query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]').textContent ?? '';

  async function disconnectGuard(fixture: ComponentFixture<SourcesComponent>): Promise<void> {
    query<HTMLButtonElement>(fixture, '[data-test="disconnect-typesafe-jev"]').click();
    await settle(fixture);
    await answerConfirm(fixture, 'confirm-ok');
  }

  it('the TypeSafe (Jev) card offers Disconnect only while Connected', async () => {
    const notConnected = await setup();
    expect(query(notConnected.fixture, '[data-test="disconnect-typesafe-jev"]')).toBeNull();
    TestBed.resetTestingModule();

    const { fixture } = await setup({ vaultKeys: ['guard.typesafe-api-key'] });
    const button = query<HTMLButtonElement>(fixture, '[data-test="disconnect-typesafe-jev"]');
    expect(button.getAttribute('aria-label')).toBe('Disconnect TypeSafe (Jev)');
    expect(button.textContent).toContain('Disconnect');
  });

  it('disconnecting TypeSafe (Jev) deletes only the guard key, turns the badge Not connected and says the guard is off', async () => {
    const { fixture, bridge } = await setup({
      vaultKeys: ['guard.typesafe-api-key', 'llm.api-key'],
    });

    await disconnectGuard(fixture);

    expect(bridge.vaultDelete.mock.calls.map((c) => c[0])).toEqual([
      { key: 'guard.typesafe-api-key' },
    ]);
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(guardBadge(fixture)).toContain('Not connected');
    expect(
      query<HTMLElement>(fixture, '[data-test="card-typesafe-jev"] [role="status"]').textContent,
    ).toContain('Disconnected. The read-only guard is off.');
    expect(query(fixture, '[data-test="disconnect-typesafe-jev"]')).toBeNull();
  });

  it('disconnecting TypeSafe (Jev) while Scout is running restarts it (stop, then start)', async () => {
    const { fixture, bridge } = await setup({
      vaultKeys: ['guard.typesafe-api-key'],
      workerState: 'running',
    });

    await disconnectGuard(fixture);

    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(fixture.componentInstance.savedMessage()).not.toContain('Saved.');
    expect(fixture.nativeElement.textContent).not.toContain('Saved.');
  });

  it('disconnecting TypeSafe (Jev) while Scout is stopped leaves it stopped', async () => {
    const { fixture, bridge } = await setup({
      vaultKeys: ['guard.typesafe-api-key'],
      workerState: 'idle',
    });

    await disconnectGuard(fixture);

    expect(bridge.vaultDelete).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(fixture.componentInstance.savedMessage()).not.toContain('Saved.');
    expect(fixture.nativeElement.textContent).not.toContain('Saved.');
  });

  it('a failed TypeSafe (Jev) key save shows the error in an alert and the badge stays Not connected', async () => {
    const { fixture } = await setup({ vaultStoreError: new Error('keychain locked') });
    query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]').click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-typesafe-jev"]'), 'ts-typed-key');

    query<HTMLButtonElement>(fixture, '[data-test="save-typesafe-jev"]').click();
    await settle(fixture);

    const alert = query<HTMLElement>(fixture, '[data-test="key-form-typesafe-jev"] [role="alert"]');
    expect(alert.textContent).toContain('keychain locked');
    expect(guardBadge(fixture)).toContain('Not connected');
    expect(fixture.nativeElement.textContent).not.toContain('ts-typed-key');
  });

  it('when the vault check fails on load the TypeSafe (Jev) card says so in an alert and still offers Disconnect', async () => {
    const { fixture } = await setup({ vaultHasError: new Error('vault unavailable') });

    expect(guardBadge(fixture)).toContain('Not connected');
    const alert = fixture.nativeElement.querySelector(
      '[data-test="card-typesafe-jev"] [role="alert"]',
    ) as HTMLElement | null;
    expect(alert?.textContent ?? '').toContain(
      'Could not check the TypeSafe key; the read-only guard may still be on.',
    );
    const disconnect = fixture.nativeElement.querySelector(
      '[data-test="disconnect-typesafe-jev"]',
    ) as HTMLButtonElement | null;
    expect(disconnect?.getAttribute('aria-label') ?? '').toBe('Disconnect TypeSafe (Jev)');
  });

  it('a failed TypeSafe (Jev) disconnect shows the error in an alert and the badge stays Connected', async () => {
    const { fixture, bridge } = await setup({
      vaultKeys: ['guard.typesafe-api-key'],
      vaultDeleteError: new Error('keychain locked'),
      workerState: 'running',
    });

    await disconnectGuard(fixture);

    expect(
      query<HTMLElement>(fixture, '[data-test="card-typesafe-jev"] [role="alert"]').textContent,
    ).toContain('keychain locked');
    expect(guardBadge(fixture).trim()).toBe('Connected');
    expect(bridge.workerStop).not.toHaveBeenCalled();
  });

  describe('the Claude API card', () => {
    const CLAUDE_HELP =
      'A Claude Pro or Max subscription cannot be used by other apps; Scout needs an Anthropic API key.';

    it('put the Claude API in AI Sources: with no key it offers Connect; Connect checks the key with Anthropic, stores it as the Anthropic key and, with nothing else in use, makes Claude the provider in use and restarts a running Scout', async () => {
      const { fixture, bridge } = await setup({
        workerState: 'running',
        publicValues: { 'llm.provider': 'Ollama' },
      });
      expect(text(fixture, '[data-test="badge-claude-api"]')).toContain('Not connected');
      expect(buttonsOf(fixture, 'claude-api')).toEqual(['Connect']);
      expect(
        query<HTMLElement>(fixture, '[data-test="action-claude-api"]').getAttribute('aria-label'),
      ).toBe('Connect Claude API');

      await connectKey(fixture, 'claude-api', 'sk-ant-typed-key');

      expect(bridge.validateProviderKey).toHaveBeenCalledWith({
        provider: 'Anthropic',
        apiKey: 'sk-ant-typed-key',
      });
      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
        { key: 'llm.anthropic.api-key', value: 'sk-ant-typed-key' },
        { key: 'llm.cloud-model', value: 'claude-opus-5-5' },
        { key: 'llm.provider', value: 'Anthropic' },
      ]);
      expect(text(fixture, '[data-test="badge-claude-api"]')).toContain('In use');
      expect(text(fixture, '[data-test="in-use-model-claude-api"]').trim()).toBe(
        'Answering with claude-opus-5-5',
      );
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
      expect(text(fixture, '[data-test="saved-claude-api"]')).toContain(
        'Connected. Restarting Scout to use Claude API.',
      );
      expect(fixture.nativeElement.textContent).not.toContain('sk-ant-typed-key');
    });

    it('put the Claude API in AI Sources: a stored key shows Connected with Disconnect, Change key and the Models link, and Disconnect asks first, then deletes only the Anthropic key', async () => {
      const { fixture, bridge } = await setup({
        vaultHasByKey: { 'llm.anthropic.api-key': true },
        publicValues: { 'llm.provider': 'Ollama' },
      });
      expect(text(fixture, '[data-test="badge-claude-api"]').trim()).toBe('Connected');
      expect(buttonsOf(fixture, 'claude-api')).toEqual(['Change key', 'Disconnect']);
      expect(
        query<HTMLAnchorElement>(fixture, '[data-test="choose-model-claude-api"]').getAttribute(
          'href',
        ),
      ).toBe('/llm?tab=analysis');

      query<HTMLButtonElement>(fixture, '[data-test="disconnect-claude-api"]').click();
      await settle(fixture);
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      await answerConfirm(fixture, 'confirm-ok');

      expect(title).toBe('Disconnect Claude API?');
      expect(bridge.vaultDelete.mock.calls.map((c) => (c[0] as { key: string }).key)).toEqual([
        'llm.anthropic.api-key',
      ]);
      expect(bridge.vaultStore).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="badge-claude-api"]')).toContain('Not connected');
    });

    it('Claude Plan: no Claude Plan card exists, nothing offers a Claude sign-in, and the Claude API card says a Pro or Max subscription cannot be used and an API key is needed', async () => {
      const { fixture } = await setup();
      const titles = [...fixture.nativeElement.querySelectorAll('.card-title')].map((el) =>
        ((el as HTMLElement).textContent ?? '').trim(),
      );
      const buttons = [...fixture.nativeElement.querySelectorAll('button')].map((el) =>
        ((el as HTMLElement).textContent ?? '').trim(),
      );
      const mentions = [...fixture.nativeElement.querySelectorAll('p')].filter((el) =>
        /Claude (Pro|Max|Plan)/.test((el as HTMLElement).textContent ?? ''),
      );

      expect(text(fixture, '[data-test="help-claude-api"]').trim()).toBe(CLAUDE_HELP);
      expect(titles.filter((title) => title.includes('Claude'))).toEqual(['Claude API']);
      expect(query(fixture, '[data-test="card-claude-plan"]')).toBeNull();
      expect(buttons.filter((label) => /claude|sign in/i.test(label))).toEqual([]);
      expect(mentions.map((el) => (el as HTMLElement).getAttribute('data-test'))).toEqual([
        'help-claude-api',
      ]);
    });
  });

  describe('the OpenRouter card', () => {
    const OR_BALANCE: OpenRouterKeyState = {
      info: {
        limit: 20,
        limitRemaining: 12.4,
        limitReset: 'monthly',
        usageDaily: 0.5,
        usageMonthly: 7.6,
      },
      readAt: new Date(2026, 6, 14, 9, 5).toISOString(),
      error: null,
    };

    const STATES = [
      {
        state: 'not connected',
        opts: { publicValues: { 'llm.provider': 'Ollama' } },
        badge: 'Not connected',
        buttons: ['Connect'],
        modelsLink: false,
        credits: null,
      },
      {
        state: 'connected',
        opts: {
          vaultHasByKey: { 'llm.openrouter.api-key': true },
          publicValues: { 'llm.provider': 'Ollama' },
          openRouterKey: OR_BALANCE,
        },
        badge: 'Connected',
        buttons: ['Change key', 'Disconnect'],
        modelsLink: true,
        credits: 'US$ 12.40 of US$ 20.00 left',
      },
      {
        state: 'in use',
        opts: {
          vaultHasByKey: { 'llm.openrouter.api-key': true },
          publicValues: { 'llm.provider': 'OpenRouter', 'llm.cloud-model': 'openai/gpt-4o-mini' },
          openRouterKey: OR_BALANCE,
        },
        badge: 'In use',
        buttons: ['Change key', 'Disconnect'],
        modelsLink: true,
        credits: 'US$ 12.40 of US$ 20.00 left',
      },
    ] as const;

    it.each(STATES)(
      'connect OpenRouter, $state: badge $badge, buttons $buttons, and the credit available when connected by API',
      async ({ opts, badge, buttons, modelsLink, credits }) => {
        const { fixture } = await setup(opts);

        expect(text(fixture, '[data-test="badge-openrouter"]').trim()).toBe(badge);
        expect(buttonsOf(fixture, 'openrouter')).toEqual([...buttons]);
        expect(query(fixture, '[data-test="choose-model-openrouter"]') !== null).toBe(modelsLink);
        expect(
          query(
            fixture,
            '[data-test="usage-balance-openrouter"] .usage-value',
          )?.textContent?.trim() ?? null,
        ).toBe(credits);
      },
    );

    it('connect OpenRouter: Connect checks the key with OpenRouter, stores it as the OpenRouter key, makes OpenRouter the provider in use with openai/gpt-4o-mini, restarts a running Scout and then shows the credit left', async () => {
      const { fixture, bridge } = await setup({
        workerState: 'running',
        publicValues: { 'llm.provider': 'Ollama' },
      });
      bridge.usageOpenRouterKey.mockResolvedValue(OR_BALANCE);

      await connectKey(fixture, 'openrouter', 'sk-or-v1-typed-key');

      expect(bridge.validateProviderKey).toHaveBeenCalledWith({
        provider: 'OpenRouter',
        apiKey: 'sk-or-v1-typed-key',
      });
      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
        { key: 'llm.openrouter.api-key', value: 'sk-or-v1-typed-key' },
        { key: 'llm.cloud-model', value: 'openai/gpt-4o-mini' },
        { key: 'llm.provider', value: 'OpenRouter' },
      ]);
      expect(text(fixture, '[data-test="badge-openrouter"]')).toContain('In use');
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
      expect(bridge.usageOpenRouterKey).toHaveBeenCalledTimes(2);
      expect(text(fixture, '[data-test="usage-balance-openrouter"]')).toContain(
        'US$ 12.40 of US$ 20.00 left',
      );
      expect(fixture.nativeElement.textContent).not.toContain('sk-or-v1-typed-key');
    });

    it('connect OpenRouter: a key OpenRouter rejects is not stored and the form says so', async () => {
      const { fixture, bridge } = await setup({
        validation: { ok: false, reason: 'invalid-key' },
      });

      await connectKey(fixture, 'openrouter', 'sk-or-v1-bad');

      expect(bridge.vaultStore).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="key-form-openrouter"]')).toContain(
        'That key was rejected by OpenRouter.',
      );
      expect(text(fixture, '[data-test="badge-openrouter"]')).toContain('Not connected');
    });

    it('connect OpenRouter: Disconnect asks "Disconnect OpenRouter?" first, then deletes only the OpenRouter key', async () => {
      const { fixture, bridge } = await setup({
        vaultHasByKey: { 'llm.openrouter.api-key': true },
        publicValues: { 'llm.provider': 'Ollama' },
        openRouterKey: OR_BALANCE,
      });

      query<HTMLButtonElement>(fixture, '[data-test="disconnect-openrouter"]').click();
      await settle(fixture);
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      await answerConfirm(fixture, 'confirm-ok');

      expect(title).toBe('Disconnect OpenRouter?');
      expect(bridge.vaultDelete.mock.calls.map((c) => (c[0] as { key: string }).key)).toEqual([
        'llm.openrouter.api-key',
      ]);
      expect(text(fixture, '[data-test="badge-openrouter"]')).toContain('Not connected');
      expect(query(fixture, '[data-test="usage-openrouter"]')).toBeNull();
    });
  });

  describe('Organization spend with an optional Admin key', () => {
    type AdminCard = 'openai-api' | 'claude-api';
    const PROVIDER: Readonly<Record<AdminCard, AdminKeyProvider>> = {
      'openai-api': 'OpenAI',
      'claude-api': 'Anthropic',
    };
    const API_KEY: Readonly<Record<AdminCard, string>> = {
      'openai-api': 'llm.openai.api-key',
      'claude-api': 'llm.anthropic.api-key',
    };
    const HELP =
      "Optional. An Admin key can read your organization's billing; Scout uses it only to show this month's spend and keeps it on this computer.";
    const READ_AT = new Date(2026, 6, 14, 9, 5).toISOString();
    const SPENDS: readonly {
      readonly name: string;
      readonly spend: OrgSpendState | null;
      readonly line: string | null;
      readonly asOf: string | null;
      readonly buttons: readonly string[];
    }[] = [
      { name: 'no Admin key', spend: null, line: null, asOf: null, buttons: ['Add Admin key'] },
      {
        name: 'read in US dollars',
        spend: { amount: 12.4, currency: 'usd', readAt: READ_AT, error: null },
        line: 'US$ 12.40 spent this month (organization)',
        asOf: null,
        buttons: ['Remove'],
      },
      {
        name: 'read in the currency the API returns',
        spend: { amount: 3, currency: 'EUR', readAt: READ_AT, error: null },
        line: 'EUR 3.00 spent this month (organization)',
        asOf: null,
        buttons: ['Remove'],
      },
      {
        name: 'rejected (401 or 403)',
        spend: { amount: 12.4, currency: 'USD', readAt: READ_AT, error: 'invalid-key' },
        line: 'The Admin key was rejected',
        asOf: 'Spend as of 09:05; the latest read failed.',
        buttons: ['Remove'],
      },
      {
        name: 'a failed read after a good one',
        spend: { amount: 12.4, currency: 'USD', readAt: READ_AT, error: 'unreachable' },
        line: 'US$ 12.40 spent this month (organization)',
        asOf: 'Spend as of 09:05; the latest read failed.',
        buttons: ['Remove'],
      },
      {
        name: 'never read successfully',
        spend: { amount: null, currency: null, readAt: null, error: 'unexpected' },
        line: 'Could not be read',
        asOf: null,
        buttons: ['Remove'],
      },
    ];
    const MATRIX = (['openai-api', 'claude-api'] as const).flatMap((card) =>
      SPENDS.map((row) => ({ card, ...row })),
    );

    function section(
      fixture: ComponentFixture<SourcesComponent>,
      card: string,
    ): HTMLDetailsElement {
      return query<HTMLDetailsElement>(fixture, `[data-test="org-spend-${card}"]`);
    }

    it.each(MATRIX)(
      "an Admin key shows the month's spend for OpenAI and Claude: $card with $name",
      async ({ card, spend, line, asOf, buttons }) => {
        const { fixture } = await setup({
          vaultHasByKey: { [API_KEY[card]]: true },
          orgSpend: { [PROVIDER[card]]: spend },
        });
        const details = section(fixture, card);

        expect(details.open).toBe(false);
        expect(details.querySelector('summary')?.textContent?.trim()).toBe('Organization spend');
        expect(text(fixture, `[data-test="org-spend-help-${card}"]`).trim()).toBe(HELP);
        expect(
          query(fixture, `[data-test="org-spend-value-${card}"]`)?.textContent?.trim() ?? null,
        ).toBe(line);
        expect(
          query(fixture, `[data-test="org-spend-as-of-${card}"]`)?.textContent?.trim() ?? null,
        ).toBe(asOf);
        expect(
          [...details.querySelectorAll('button')].map((b) => (b.textContent ?? '').trim()),
        ).toEqual([...buttons]);
      },
    );

    it('the section is only on a connected OpenAI API or Claude API card, never on Gemini or OpenRouter', async () => {
      const { fixture, bridge } = await setup({
        vaultHasByKey: {
          'llm.gemini.api-key': true,
          'llm.openrouter.api-key': true,
          'llm.anthropic.api-key': true,
        },
      });

      expect(query(fixture, '[data-test="org-spend-openai-api"]')).toBeNull();
      expect(query(fixture, '[data-test="org-spend-claude-api"]')).not.toBeNull();
      expect(query(fixture, '[data-test="org-spend-google-gemini"]')).toBeNull();
      expect(query(fixture, '[data-test="org-spend-openrouter"]')).toBeNull();
      expect(bridge.usageOrgSpend.mock.calls.map((c) => c[0])).toEqual([
        { provider: 'OpenAI' },
        { provider: 'Anthropic' },
      ]);
    });

    it("Add Admin key sends the key to main once, clears the field, and shows this month's spend without the key anywhere on the page", async () => {
      const { fixture, bridge } = await setup({ vaultHasByKey: { 'llm.openai.api-key': true } });

      await click(fixture, '[data-test="admin-key-add-openai-api"]');
      const input = query<HTMLInputElement>(fixture, '[data-test="admin-key-openai-api"]');
      type(input, 'sk-admin-typed-key');
      await click(fixture, '[data-test="admin-key-save-openai-api"]');

      expect(bridge.usageAdminKeySave).toHaveBeenCalledWith({
        provider: 'OpenAI',
        adminKey: 'sk-admin-typed-key',
      });
      expect(bridge.vaultStore).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="org-spend-value-openai-api"]').trim()).toBe(
        'US$ 12.40 spent this month (organization)',
      );
      expect(query(fixture, '[data-test="admin-key-openai-api"]')).toBeNull();
      expect(input.value).toBe('');
      expect(fixture.nativeElement.textContent).not.toContain('sk-admin-typed-key');
    });

    it('an Admin key the provider rejects is not saved and the form says "The Admin key was rejected"', async () => {
      const { fixture, bridge } = await setup({
        vaultHasByKey: { 'llm.anthropic.api-key': true },
        adminSave: { ok: false, reason: 'invalid-key' },
      });

      await click(fixture, '[data-test="admin-key-add-claude-api"]');
      type(
        query<HTMLInputElement>(fixture, '[data-test="admin-key-claude-api"]'),
        'sk-ant-admin01-bad',
      );
      await click(fixture, '[data-test="admin-key-save-claude-api"]');

      expect(text(fixture, '[data-test="admin-key-form-claude-api"]')).toContain(
        'The Admin key was rejected',
      );
      expect(query(fixture, '[data-test="org-spend-value-claude-api"]')).toBeNull();
      expect(bridge.usageOrgSpend).toHaveBeenCalledTimes(2);
    });

    it('a stored Admin key stays removable after its API card is disconnected, so the broad key is never stranded', async () => {
      const spend: OrgSpendState = { amount: 12.4, currency: 'usd', readAt: READ_AT, error: null };
      const { fixture } = await setup({
        vaultHasByKey: { 'llm.openai.api-key': false },
        orgSpend: { OpenAI: spend },
      });

      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
      expect(text(fixture, '[data-test="org-spend-value-openai-api"]').trim()).toBe(
        'US$ 12.40 spent this month (organization)',
      );
      expect(query(fixture, '[data-test="admin-key-remove-openai-api"]')).not.toBeNull();
    });

    it('an Admin key rejected on one card is reported on that card only', async () => {
      const spend: OrgSpendState = { amount: 12.4, currency: 'usd', readAt: READ_AT, error: null };
      const { fixture } = await setup({
        vaultHasByKey: { 'llm.openai.api-key': true, 'llm.anthropic.api-key': true },
        orgSpend: { OpenAI: spend },
        adminSave: { ok: false, reason: 'invalid-key' },
      });

      await click(fixture, '[data-test="admin-key-add-claude-api"]');
      type(
        query<HTMLInputElement>(fixture, '[data-test="admin-key-claude-api"]'),
        'sk-ant-admin01-bad',
      );
      await click(fixture, '[data-test="admin-key-save-claude-api"]');

      expect(text(fixture, '[data-test="admin-key-form-claude-api"] [role="alert"]').trim()).toBe(
        'The Admin key was rejected',
      );
      expect(section(fixture, 'openai-api').querySelector('[role="alert"]')).toBeNull();
      expect(text(fixture, '[data-test="org-spend-value-openai-api"]').trim()).toBe(
        'US$ 12.40 spent this month (organization)',
      );
    });

    it("Remove asks first, then removes only that provider's Admin key and offers Add Admin key again; Cancel keeps it", async () => {
      const spend: OrgSpendState = { amount: 12.4, currency: 'USD', readAt: READ_AT, error: null };
      const { fixture, bridge } = await setup({
        vaultHasByKey: { 'llm.anthropic.api-key': true },
        orgSpend: { Anthropic: spend },
      });

      query<HTMLButtonElement>(fixture, '[data-test="admin-key-remove-claude-api"]').click();
      await settle(fixture);
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      await answerConfirm(fixture, 'confirm-cancel');
      expect(title).toBe('Remove the Admin key?');
      expect(bridge.usageAdminKeyRemove).not.toHaveBeenCalled();

      query<HTMLButtonElement>(fixture, '[data-test="admin-key-remove-claude-api"]').click();
      await settle(fixture);
      await settle(fixture);
      await answerConfirm(fixture, 'confirm-ok');

      expect(bridge.usageAdminKeyRemove.mock.calls).toEqual([[{ provider: 'Anthropic' }]]);
      expect(bridge.vaultDelete).not.toHaveBeenCalled();
      expect(query(fixture, '[data-test="org-spend-value-claude-api"]')).toBeNull();
      expect(query(fixture, '[data-test="admin-key-add-claude-api"]')).not.toBeNull();
    });
  });

  describe('usage on the cards', () => {
    type Card =
      'openai-api' | 'google-gemini' | 'claude-api' | 'chatgpt-plus' | 'ollama' | 'openrouter';
    type Usage = 'none' | 'reported' | 'unreported' | 'limit';

    const SOURCE: Readonly<Record<Card, string>> = {
      'openai-api': 'OpenAI',
      'google-gemini': 'Gemini',
      'claude-api': 'Anthropic',
      'chatgpt-plus': 'ChatGptPlan',
      ollama: 'Ollama',
      openrouter: 'OpenRouter',
    };
    const LIMIT_AT = new Date(2026, 6, 14, 14, 30).toISOString();
    const KEY_STATE: OpenRouterKeyState = {
      info: {
        limit: 20,
        limitRemaining: 12.4,
        limitReset: 'monthly',
        usageDaily: 0.5,
        usageMonthly: 7.6,
      },
      readAt: new Date(2026, 6, 14, 9, 5).toISOString(),
      error: null,
    };

    function counted(card: Card, usage: Usage): UsageSnapshot {
      const cost = card === 'openrouter' ? 0.12 : null;
      switch (usage) {
        case 'none':
          return {};
        case 'reported':
          return {
            [SOURCE[card]]: {
              today: { tokens: 1500, answers: 3, cost },
              month: { tokens: 1_200_000, answers: 90, cost: cost === null ? null : 3.4 },
              lastModel: 'm',
              lastReported: true,
              limitAt: null,
            },
          };
        case 'unreported':
          return {
            [SOURCE[card]]: {
              today: { tokens: 0, answers: 2, cost: null },
              month: { tokens: 0, answers: 2, cost: null },
              lastModel: 'm',
              lastReported: false,
              limitAt: null,
            },
          };
        case 'limit':
          return {
            [SOURCE[card]]: {
              today: { tokens: 1500, answers: 3, cost: null },
              month: { tokens: 1_200_000, answers: 90, cost: null },
              lastModel: 'm',
              lastReported: true,
              limitAt: LIMIT_AT,
            },
          };
      }
    }

    function connection(card: Card, connected: boolean): Parameters<typeof setup>[0] {
      switch (card) {
        case 'openai-api':
          return { vaultHasByKey: { 'llm.openai.api-key': connected } };
        case 'google-gemini':
          return { vaultHasByKey: { 'llm.gemini.api-key': connected } };
        case 'claude-api':
          return { vaultHasByKey: { 'llm.anthropic.api-key': connected } };
        case 'chatgpt-plus':
          return { chatgpt: connected ? CHATGPT_IN : CHATGPT_OUT };
        case 'ollama':
          return connected ? { models: [] } : { listError: new OllamaUnreachableError('down') };
        case 'openrouter':
          return {
            vaultHasByKey: { 'llm.openrouter.api-key': connected },
            openRouterKey: connected ? KEY_STATE : null,
          };
      }
    }

    const BALANCE: Readonly<Record<Card, readonly string[]>> = {
      'openai-api': ['Balance', 'Not reported by the provider'],
      'google-gemini': ['Balance', 'Not reported by the provider'],
      'claude-api': ['Balance', 'Not reported by the provider'],
      'chatgpt-plus': ['Plan usage', 'Not reported by the provider'],
      ollama: ['Cost', 'Local, free', 'Models installed', '0 · 0.0 GB'],
      openrouter: [
        'Credits',
        'US$ 12.40 of US$ 20.00 left',
        'Resets',
        'Monthly',
        'OpenRouter this month',
        'US$ 7.60',
      ],
    };
    const PAGE: Readonly<Record<Card, string | null>> = {
      'openai-api': 'https://platform.openai.com/usage',
      'google-gemini': 'https://aistudio.google.com/usage',
      'claude-api': 'https://platform.claude.com/usage',
      'chatgpt-plus': 'https://chatgpt.com/#settings',
      ollama: null,
      openrouter: null,
    };

    interface Row {
      readonly card: Card;
      readonly connected: boolean;
      readonly usage: Usage;
      readonly expected: readonly string[] | null;
      readonly note: string | null;
    }

    function expectedRows(card: Card, usage: Usage): readonly string[] {
      const scout = (today: string, month: string): string[] => [
        'Scout today',
        today,
        'Scout this month',
        month,
      ];
      if (usage === 'limit') {
        return [
          'Plan usage',
          'Usage limit reached at 14:30',
          ...scout('1.5K tokens', '1.2M tokens'),
        ];
      }
      if (usage === 'reported') {
        return card === 'openrouter'
          ? [...BALANCE[card], ...scout('1.5K tokens · US$ 0.12', '1.2M tokens · US$ 3.40')]
          : [...BALANCE[card], ...scout('1.5K tokens', '1.2M tokens')];
      }
      return [...BALANCE[card], ...scout('0 tokens', '0 tokens')];
    }

    const CARDS: readonly Card[] = [
      'openai-api',
      'google-gemini',
      'claude-api',
      'chatgpt-plus',
      'ollama',
      'openrouter',
    ];
    const MATRIX: readonly Row[] = CARDS.flatMap((card) =>
      [false, true].flatMap((connected) =>
        (card === 'chatgpt-plus'
          ? (['none', 'reported', 'unreported', 'limit'] as const)
          : (['none', 'reported', 'unreported'] as const)
        ).map((usage) => ({
          card,
          connected,
          usage,
          expected: connected ? expectedRows(card, usage) : null,
          note: connected && usage === 'unreported' ? 'Usage not reported for this model' : null,
        })),
      ),
    );

    function usageText(fixture: ComponentFixture<SourcesComponent>, card: Card): string[] | null {
      const block = query<HTMLElement>(fixture, `[data-test="usage-${card}"]`);
      if (block === null) return null;
      return [...block.querySelectorAll('.usage-row span')].map((el) =>
        (el.textContent ?? '').trim(),
      );
    }

    it.each(MATRIX)(
      '$card connected=$connected usage=$usage',
      async ({ card, connected, usage, expected, note }) => {
        const { fixture, bridge } = await setup({
          ...connection(card, connected),
          usage: counted(card, usage),
        });

        expect(usageText(fixture, card)).toEqual(expected);
        expect(text(fixture, `[data-test="usage-unreported-${card}"]`).trim()).toBe(note ?? '');
        const page = query<HTMLButtonElement>(fixture, `[data-test="usage-page-${card}"]`);
        page?.click();
        expect(bridge.appOpenExternal.mock.calls).toEqual(
          connected && PAGE[card] !== null ? [[{ url: PAGE[card] }]] : [],
        );
      },
    );

    it('the link on a card opens that provider usage page through the main process', async () => {
      const { fixture, bridge } = await setup({ vaultHasByKey: { 'llm.gemini.api-key': true } });

      query<HTMLButtonElement>(fixture, '[data-test="usage-page-google-gemini"]').click();

      expect(bridge.appOpenExternal).toHaveBeenCalledWith({
        url: 'https://aistudio.google.com/usage',
      });
    });

    it('a key with no limit reads "No limit on this key" and shows no reset', async () => {
      const { fixture } = await setup({
        vaultHasByKey: { 'llm.openrouter.api-key': true },
        openRouterKey: {
          info: { ...KEY_STATE.info!, limit: null, limitRemaining: null },
          readAt: KEY_STATE.readAt,
          error: null,
        },
      });

      expect(text(fixture, '[data-test="usage-balance-openrouter"]')).toContain(
        'No limit on this key',
      );
      expect(query(fixture, '[data-test="usage-reset-openrouter"]')).toBeNull();
    });

    it('after a failed OpenRouter read the card keeps the last balance and says when it was read', async () => {
      const { fixture } = await setup({
        vaultHasByKey: { 'llm.openrouter.api-key': true },
        openRouterKey: { ...KEY_STATE, error: 'unreachable' },
      });

      expect(text(fixture, '[data-test="usage-balance-openrouter"]')).toContain(
        'US$ 12.40 of US$ 20.00 left',
      );
      expect(text(fixture, '[data-test="usage-as-of-openrouter"]').trim()).toBe(
        'Balance as of 09:05; the latest read failed.',
      );
    });

    it('the totals main pushes after an answer replace the ones on the card', async () => {
      const { fixture, bridge } = await setup({ vaultHasByKey: { 'llm.openai.api-key': true } });
      const push = bridge.onUsageChanged.mock.calls[0]?.[0] as (usage: UsageSnapshot) => void;

      push(counted('openai-api', 'reported'));
      await settle(fixture);

      expect(text(fixture, '[data-test="usage-today-openai-api"]')).toContain('1.5K tokens');
    });
  });

  describe('while the screen is loading', () => {
    function pending<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
      let resolve!: (value: T) => void;
      const promise = new Promise<T>((r) => (resolve = r));
      return { promise, resolve };
    }

    it('every card that is still being checked says Checking..., never Not connected, until its answer arrives', async () => {
      const bridge = makeBridge();
      const ollama = pending<{ models: [] }>();
      const lmStudio = pending<{ models: [] }>();
      const chatgpt = pending<FakeChatGptStatus>();
      const vault = pending<{ exists: boolean }>();
      bridge.llmListOllamaModels.mockImplementation(() => ollama.promise);
      bridge.llmListLmStudioModels.mockImplementation(() => lmStudio.promise);
      bridge.chatgptStatus.mockImplementation(() => chatgpt.promise);
      bridge.vaultHas.mockImplementation(() => vault.promise);
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: ScoutBridgeService, useValue: bridge },
        ],
      });
      const fixture = TestBed.createComponent(SourcesComponent);
      fixture.detectChanges();
      const ids = [
        'ollama',
        'lm-studio',
        'chatgpt-plus',
        'openai-api',
        'google-gemini',
        'typesafe-jev',
      ];
      const badges = (): string[] =>
        ids.map((id) => text(fixture, `[data-test="badge-${id}"]`).trim());

      const loading = badges();
      ollama.resolve({ models: [] });
      lmStudio.resolve({ models: [] });
      chatgpt.resolve(CHATGPT_OUT);
      vault.resolve({ exists: false });
      for (let i = 0; i < 6; i += 1) await settle(fixture);

      expect(loading).toEqual(Array(ids.length).fill('Checking...'));
      expect(badges()).toEqual([
        'Connected',
        'Detected',
        'Not connected',
        'Not connected',
        'Not connected',
        'Not connected',
      ]);
    });
  });

  describe('each card state on the shared status chip', () => {
    const CHECK = 'M20 6L9 17l-5-5';
    const ARC = 'M12 3a9 9 0 1 0 9 9';
    const DOT = 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z';

    function chip(fixture: ComponentFixture<SourcesComponent>, id: string) {
      const node = query<HTMLElement>(fixture, `[data-test="badge-${id}"]`);
      return {
        chip: node.tagName.toLowerCase(),
        text: node.textContent?.trim(),
        tone: node.getAttribute('data-tone'),
        leadingGlyph: node.firstElementChild?.querySelector('path')?.getAttribute('d'),
      };
    }

    it('a card still being checked reads "Checking..." after the arc on the info container', async () => {
      const bridge = makeBridge();
      const never = new Promise<never>(() => undefined);
      bridge.llmListOllamaModels.mockImplementation(() => never);
      bridge.llmListLmStudioModels.mockImplementation(() => never);
      bridge.chatgptStatus.mockImplementation(() => never);
      bridge.vaultHas.mockImplementation(() => never);
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: ScoutBridgeService, useValue: bridge },
        ],
      });
      const fixture = TestBed.createComponent(SourcesComponent);
      fixture.detectChanges();

      const checking = {
        chip: 'sc-status-chip',
        text: 'Checking...',
        tone: 'info',
        leadingGlyph: ARC,
      };
      expect(
        ['ollama', 'lm-studio', 'chatgpt-plus', 'openai-api', 'typesafe-jev'].map((id) =>
          chip(fixture, id),
        ),
      ).toEqual(Array(5).fill(checking));
    });

    it('Connected, Detected and In use read in sentence case after the check on the ok container; Not connected after the dot on the neutral one', async () => {
      useGeminiWithBothKeys();
      const { fixture } = await setup({
        models: [],
        vaultHasByKey: { 'llm.openai.api-key': true, 'llm.gemini.api-key': true },
        publicValues: { 'llm.provider': 'Gemini', 'llm.cloud-model': 'gemini-2.5-pro' },
      });

      const ok = (text: string) => ({
        chip: 'sc-status-chip',
        text,
        tone: 'ok',
        leadingGlyph: CHECK,
      });
      expect({
        ollama: chip(fixture, 'ollama'),
        lmStudio: chip(fixture, 'lm-studio'),
        openAi: chip(fixture, 'openai-api'),
        gemini: chip(fixture, 'google-gemini'),
        chatgpt: chip(fixture, 'chatgpt-plus'),
        guard: chip(fixture, 'typesafe-jev'),
      }).toEqual({
        ollama: ok('Connected'),
        lmStudio: ok('Detected'),
        openAi: ok('Connected'),
        gemini: ok('In use'),
        chatgpt: {
          chip: 'sc-status-chip',
          text: 'Not connected',
          tone: 'neutral',
          leadingGlyph: DOT,
        },
        guard: {
          chip: 'sc-status-chip',
          text: 'Not connected',
          tone: 'neutral',
          leadingGlyph: DOT,
        },
      });
    });

    it('ChatGPT signed in reads "Connected" after the check on the ok container', async () => {
      const { fixture } = await setup({ chatgpt: CHATGPT_IN });

      expect(chip(fixture, 'chatgpt-plus')).toEqual({
        chip: 'sc-status-chip',
        text: 'Connected',
        tone: 'ok',
        leadingGlyph: CHECK,
      });
    });

    it('Ollama in use while its check has not answered reads "In use" after the dot on the neutral container', async () => {
      const bridge = makeBridge({ publicValues: { 'llm.provider': 'Ollama' } });
      bridge.llmListOllamaModels.mockImplementation(() => new Promise<never>(() => undefined));
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: ScoutBridgeService, useValue: bridge },
        ],
      });
      const fixture = TestBed.createComponent(SourcesComponent);
      fixture.detectChanges();
      await expect
        .poll(() => {
          fixture.detectChanges();
          return chip(fixture, 'ollama').text;
        })
        .toBe('In use');

      expect(chip(fixture, 'ollama')).toEqual({
        chip: 'sc-status-chip',
        text: 'In use',
        tone: 'neutral',
        leadingGlyph: DOT,
      });
    });
  });

  describe('primary and secondary actions', () => {
    function declared(el: Element, property: string): string {
      let value = '';
      for (const sheet of Array.from(document.styleSheets)) {
        for (const rule of Array.from(sheet.cssRules)) {
          if (rule instanceof CSSStyleRule && el.matches(rule.selectorText)) {
            value = rule.style.getPropertyValue(property) || value;
          }
        }
      }
      return value;
    }

    it('Save is the one filled primary button in the key form and Cancel is an outlined secondary one', async () => {
      const { fixture } = await setup();
      await click(fixture, '[data-test="action-openai-api"]');
      const save = query<HTMLButtonElement>(fixture, '[data-test="save-openai-api"]');
      const cancel = query<HTMLButtonElement>(fixture, '[data-test="cancel-openai-api"]');

      expect([save.getAttribute('matbutton'), cancel.getAttribute('matbutton')]).toEqual([
        'filled',
        'outlined',
      ]);
      expect(declared(save, '--mat-button-filled-container-color')).toBe('var(--sc-accent-strong)');
      expect(declared(save, '--mat-button-filled-label-text-color')).toBe('var(--sc-on-accent)');
    });
  });

  describe('Disconnect asks first', () => {
    it('Disconnect opens a confirmation naming the provider with Cancel focused, and Cancel deletes nothing', async () => {
      const { fixture, bridge } = await setup({ vaultHasByKey: { 'llm.openai.api-key': true } });

      query<HTMLButtonElement>(fixture, '[data-test="disconnect-openai-api"]').click();
      await settle(fixture);
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
      await answerConfirm(fixture, 'confirm-cancel');

      expect(title).toBe('Disconnect OpenAI API?');
      expect(focused).toBe('confirm-cancel');
      expect(bridge.vaultDelete).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Connected');
    });

    it('Disconnect on TypeSafe (Jev) asks first with Cancel focused, and Cancel keeps the guard on', async () => {
      const { fixture, bridge } = await setup({ vaultKeys: ['guard.typesafe-api-key'] });

      query<HTMLButtonElement>(fixture, '[data-test="disconnect-typesafe-jev"]').click();
      await settle(fixture);
      await settle(fixture);
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
      await answerConfirm(fixture, 'confirm-cancel');

      expect(title).toBe('Disconnect TypeSafe (Jev)?');
      expect(focused).toBe('confirm-cancel');
      expect(bridge.vaultDelete).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="badge-typesafe-jev"]').trim()).toBe('Connected');
    });
  });

  describe('provider actions, keys and card states', () => {
    function pending<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
      let resolve!: (value: T) => void;
      const promise = new Promise<T>((r) => (resolve = r));
      return { promise, resolve };
    }

    async function mount(bridge: FakeBridge): Promise<ComponentFixture<SourcesComponent>> {
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: ScoutBridgeService, useValue: bridge },
          { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
          { provide: InteractivityChecker, useValue: LAID_OUT },
        ],
      });
      const fixture = TestBed.createComponent(SourcesComponent);
      fixture.detectChanges();
      for (let i = 0; i < 3; i += 1) await settle(fixture);
      return fixture;
    }

    const OPENAI_IN_USE = {
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    };

    it('disconnecting the OpenAI key in use never moves Scout to a ChatGPT plan whose session ended after the screen opened', async () => {
      const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN, ...OPENAI_IN_USE });
      bridge.chatgptStatus.mockResolvedValue(CHATGPT_OUT);

      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
        { key: 'llm.provider', value: 'None' },
      ]);
    });

    it('when disconnecting the OpenAI key in use finds the ChatGPT session ended, the ChatGPT card stops showing it signed in', async () => {
      const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN, ...OPENAI_IN_USE });
      expect(text(fixture, '[data-test="badge-chatgpt-plus"]').trim()).toBe('Connected');
      bridge.chatgptStatus.mockResolvedValue(CHATGPT_OUT);

      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

      expect(text(fixture, '[data-test="badge-chatgpt-plus"]').trim()).toBe('Not connected');
      expect(query(fixture, '[data-test="chatgpt-sign-out"]')).toBeNull();
      expect(query(fixture, '[data-test="chatgpt-sign-in"]')).not.toBeNull();
    });

    it('a ChatGPT status that cannot be read after disconnecting the OpenAI key in use leaves Scout on no provider and the ChatGPT card as it was', async () => {
      const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN, ...OPENAI_IN_USE });
      bridge.chatgptStatus.mockRejectedValue(
        new Error('[scout:ipc/unavailable] The main process did not answer.'),
      );

      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
        { key: 'llm.provider', value: 'None' },
      ]);
      expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
        'Disconnected. No AI provider is connected; Scout will say so until you connect one.',
      );
      expect(text(fixture, '[data-test="badge-chatgpt-plus"]').trim()).toBe('Connected');
    });

    it('Connect makes OpenAI the provider in use and restarts Scout when the ChatGPT plan still chosen has no session', async () => {
      const { fixture, bridge } = await setup({
        workerState: 'running',
        chatgpt: CHATGPT_OUT,
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
      });

      await connectKey(fixture, 'openai-api', 'sk-typed-key');

      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toContainEqual({
        key: 'llm.provider',
        value: 'OpenAI',
      });
      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    });

    it('Change key on the provider in use checks the new key, stores it and restarts a running Scout so the key reaches it', async () => {
      const { fixture, bridge } = await setup({ workerState: 'running', ...OPENAI_IN_USE });
      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');

      await click(fixture, '[data-test="change-key-openai-api"]');
      type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-rotated-key');
      await click(fixture, '[data-test="save-openai-api"]');

      expect(bridge.validateProviderKey).toHaveBeenCalledWith({
        provider: 'OpenAI',
        apiKey: 'sk-rotated-key',
      });
      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
        { key: 'llm.openai.api-key', value: 'sk-rotated-key' },
      ]);
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
      expect(text(fixture, '[data-test="saved-openai-api"]').trim()).toBe(
        'Key changed. Restarting Scout to use the new OpenAI API key.',
      );
      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    });

    it('Change key on the provider in use while Scout is stopped stores the key and says it applies at the next start', async () => {
      const { fixture, bridge } = await setup({ workerState: 'idle', ...OPENAI_IN_USE });

      await click(fixture, '[data-test="change-key-openai-api"]');
      type(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]'), 'sk-rotated-key');
      await click(fixture, '[data-test="save-openai-api"]');

      expect(bridge.workerStop).not.toHaveBeenCalled();
      expect(text(fixture, '[data-test="saved-openai-api"]').trim()).toBe(
        'Key changed. Scout will use the new OpenAI API key the next time it starts.',
      );
    });

    it('signing out of ChatGPT closes a key form left open, so a card that becomes in use never keeps a stale form', async () => {
      const { fixture } = await setup({
        workerState: 'running',
        chatgpt: CHATGPT_IN,
        vaultHasByKey: { 'llm.openai.api-key': true },
        publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
      });
      await click(fixture, '[data-test="change-key-openai-api"]');
      expect(query(fixture, '[data-test="key-form-openai-api"]')).not.toBeNull();

      await click(fixture, '[data-test="chatgpt-sign-out"]');

      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
      expect(query(fixture, '[data-test="key-form-openai-api"]')).toBeNull();
    });

    it('a connect whose switch fails keeps the key and says it is connected but not switched', async () => {
      const { fixture, bridge } = await setup({ publicValues: { 'llm.provider': 'None' } });
      bridge.vaultStore.mockImplementation(async (req: { key: string }) => {
        if (req.key === 'llm.provider') throw new Error('keyring locked');
      });

      query<HTMLButtonElement>(fixture, '[data-test="action-openai-api"]').click();
      fixture.detectChanges();
      const keyField = query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]');
      type(keyField, 'sk-typed-key');
      await click(fixture, '[data-test="save-openai-api"]');

      expect(storeKeysWritten(bridge)).toContain('llm.openai.api-key');
      expect(text(fixture, '[data-test="saved-openai-api"]').trim()).toBe(
        'Connected, not switched: Scout still answers as before, not with OpenAI API.',
      );
      expect(keyField.value).toBe('');
      expect(text(fixture, '[data-test="card-openai-api"] [role="alert"]')).toContain(
        'keyring locked',
      );
      expect(text(fixture, '[data-test="badge-openai-api"]').trim()).toBe('Connected');
    });

    it('when the keychain cannot be read, the OpenAI card says the check failed in an alert', async () => {
      const { fixture } = await setup({ vaultHasError: new Error('keychain locked') });

      expect(query(fixture, '[data-test="card-openai-api"] [role="alert"]')).not.toBeNull();
    });

    it('a second provider action while a restart is still running starts no second stop of Scout', async () => {
      const { fixture, bridge } = await setup({
        workerState: 'running',
        models: [],
        ...OPENAI_IN_USE,
      });
      const stop = pending<{ exitCode: number }>();
      bridge.workerStop.mockImplementation(() => stop.promise);
      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);

      await click(fixture, '[data-test="use-ollama"]');
      const stopsWhileTheFirstRestartRuns = bridge.workerStop.mock.calls.length;
      stop.resolve({ exitCode: 0 });
      await settle(fixture);

      expect(stopsWhileTheFirstRestartRuns).toBe(1);
    });

    it('a double click on ChatGPT Sign out signs out once', async () => {
      const { fixture, bridge } = await chatGptInUse({ workerState: 'running' });
      const signOut = query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]');

      signOut.click();
      fixture.detectChanges();
      signOut.click();
      await settle(fixture);
      await settle(fixture);

      expect(bridge.chatgptSignOut).toHaveBeenCalledTimes(1);
    });

    it('while both local scans run at load, the Ollama Scan button is disabled', async () => {
      const bridge = makeBridge();
      bridge.llmListOllamaModels.mockImplementation(() => new Promise(() => undefined));
      bridge.llmListLmStudioModels.mockImplementation(() => new Promise(() => undefined));
      const fixture = await mount(bridge);

      expect(query<HTMLButtonElement>(fixture, '[data-test="action-lm-studio"]').disabled).toBe(
        true,
      );
      expect(query<HTMLButtonElement>(fixture, '[data-test="action-ollama"]').disabled).toBe(true);
    });

    it('signing in to ChatGPT while another AI Sources action is saving still makes it the provider in use', async () => {
      const { fixture, bridge } = await setup({
        vaultHasByKey: { 'llm.gemini.api-key': true },
        publicValues: { 'llm.provider': 'None' },
      });
      const removal = pending<{ deleted: boolean }>();
      bridge.vaultDelete.mockImplementation(() => removal.promise);
      await disconnectConfirmed(fixture, '[data-test="disconnect-google-gemini"]');
      expect(fixture.componentInstance.saving()).toBe(true);

      await click(fixture, '[data-test="chatgpt-sign-in"]');
      removal.resolve({ deleted: true });
      for (let i = 0; i < 3; i += 1) await settle(fixture);

      expect(bridge.chatgptSignIn).toHaveBeenCalledTimes(1);
      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toContainEqual({
        key: 'llm.provider',
        value: 'ChatGptPlan',
      });
    });

    it('a ChatGPT sign-in without plan usage is never shown In use, even while ChatGPT is still the chosen provider', async () => {
      const { fixture } = await chatGptInUse({
        chatgpt: { ...CHATGPT_IN, planUsageGranted: false },
      });

      expect(query(fixture, '[data-test="chatgpt-no-plan-usage"]')).not.toBeNull();
      expect(text(fixture, '[data-test="badge-chatgpt-plus"]').trim()).not.toBe('In use');
    });

    it('an OpenRouter key with a limit but no reported remaining credit never shows an invented US$ 0.00', async () => {
      const { fixture } = await setup({
        vaultHasByKey: { 'llm.openrouter.api-key': true },
        openRouterKey: {
          info: {
            limit: 10,
            limitRemaining: null,
            limitReset: 'monthly',
            usageDaily: null,
            usageMonthly: null,
          },
          readAt: new Date(2026, 6, 14, 9, 5).toISOString(),
          error: null,
        },
      });
      const credits = text(fixture, '[data-test="usage-balance-openrouter"]');

      expect(credits).toContain('US$ 10.00');
      expect(credits).not.toContain('US$ 0.00');
    });

    it('when the organization spend read fails on a disconnected OpenAI card, a stored Admin key can still be removed', async () => {
      const bridge = makeBridge();
      bridge.usageOrgSpend.mockRejectedValue(new Error('vault unavailable'));
      const fixture = await mount(bridge);

      expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not connected');
      expect(query(fixture, '[data-test="admin-key-remove-openai-api"]')).not.toBeNull();
    });

    it('a TypeSafe Disconnect whose restart fails says to restart Scout from the status screen', async () => {
      const { fixture } = await setup({
        vaultKeys: ['guard.typesafe-api-key'],
        workerState: 'running',
        workerStartError: new Error('spawn failed'),
      });

      await disconnectConfirmed(fixture, '[data-test="disconnect-typesafe-jev"]');

      expect(text(fixture, '[data-test="card-typesafe-jev"]')).toContain(
        'Restart Scout from the status screen',
      );
    });

    it.each(['unreachable', 'unexpected'] as const)(
      'an API key the provider could not check (%s) stays in the field',
      async (reason) => {
        const { fixture } = await setup({ validation: { ok: false, reason } });

        await connectKey(fixture, 'openai-api', 'sk-typed');

        expect(query<HTMLInputElement>(fixture, '[data-test="api-key-openai-api"]').value).toBe(
          'sk-typed',
        );
      },
    );

    it.each(['unreachable', 'unexpected'] as const)(
      'an Admin key the provider could not check (%s) stays in the field',
      async (reason) => {
        const { fixture } = await setup({
          vaultHasByKey: { 'llm.openai.api-key': true },
          adminSave: { ok: false, reason },
        });

        await click(fixture, '[data-test="admin-key-add-openai-api"]');
        type(query<HTMLInputElement>(fixture, '[data-test="admin-key-openai-api"]'), 'admin-typed');
        await click(fixture, '[data-test="admin-key-save-openai-api"]');

        expect(query<HTMLInputElement>(fixture, '[data-test="admin-key-openai-api"]').value).toBe(
          'admin-typed',
        );
      },
    );

    it('a provider switch while a live listen is open waits for the listen to end, and the card says so', async () => {
      const { fixture, bridge } = await setup({ workerState: 'running', ...OPENAI_IN_USE });
      Object.assign(bridge, {
        localListening: vi.fn(async () => ({ listening: true })),
        onLocalListening: vi.fn(() => () => undefined),
      });

      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

      expect(bridge.workerStop).not.toHaveBeenCalled();
      const saved = text(fixture, '[data-test="saved-openai-api"]');
      expect(saved).toMatch(/wait/i);
      expect(saved).toMatch(/listen/i);
    });

    it('a provider switch while an answer is running waits for the answer to end, and the card says so', async () => {
      const { fixture, bridge } = await setup({ workerState: 'running', ...OPENAI_IN_USE });
      const running = [
        { id: 'turn-1', question: 'is prod green?', reply: '', citations: [], state: 'running' },
      ];
      Object.assign(bridge, {
        localGetTurns: vi.fn(async () => running),
        onLocalTurns: vi.fn(() => () => undefined),
      });

      await disconnectConfirmed(fixture, '[data-test="disconnect-openai-api"]');

      expect(bridge.workerStop).not.toHaveBeenCalled();
      const saved = text(fixture, '[data-test="saved-openai-api"]');
      expect(saved).toMatch(/wait/i);
      expect(saved).toMatch(/answer/i);
    });

    it('while main restarts Scout the card says it is restarting, then shows what main did', async () => {
      const { fixture, bridge } = await setup({
        workerState: 'running',
        publicValues: { 'llm.provider': 'Ollama' },
      });
      let answer: (value: WorkerRestartResponse) => void = () => undefined;
      bridge.workerRestart.mockImplementationOnce(
        () => new Promise((resolve) => (answer = resolve)),
      );

      await connectKey(fixture, 'google-gemini', 'gemini-typed-key');
      const whileRestarting = text(fixture, '[data-test="saved-google-gemini"]').trim();
      answer({ outcome: 'deferred' });
      await settle(fixture);

      expect({
        whileRestarting,
        after: text(fixture, '[data-test="saved-google-gemini"]').trim(),
      }).toEqual({
        whileRestarting: 'Connected. Restarting Scout to use Google Gemini.',
        after: RESTART_DEFERRED_LINE,
      });
    });
  });

  describe('the Ollama models panel follows the provider in use', () => {
    const OPENAI_IN_USE = {
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    };

    async function openOllamaModels(
      opts: Parameters<typeof setup>[0],
    ): Promise<{ fixture: ComponentFixture<SourcesComponent>; bridge: FakeBridge }> {
      const { fixture, bridge } = await setup({
        models: ['qwen2.5:0.5b', 'gemma3:4b'].map((name) => ({
          name,
          modifiedAt: '',
          sizeBytes: 0,
        })),
        ...opts,
      });
      Object.assign(bridge, {
        onLlmPullOllamaProgress: vi.fn(() => () => undefined),
        onWhisperDownloadProgress: vi.fn(() => () => undefined),
        whisperListInstalled: vi.fn(async () => ({ installed: [] })),
        onWorkerEvent: vi.fn(() => () => undefined),
      });
      const store = TestBed.inject(LocalModelsStore);
      store.markDownloaded('qwen-2.5-0.5b-instruct');
      store.markDownloaded('gemma-3-4b');
      bridge.vaultStore.mockClear();
      await click(fixture, '[data-test="manage-ollama-models"]');
      return { fixture, bridge };
    }

    async function clickInDialog(
      fixture: ComponentFixture<SourcesComponent>,
      sel: string,
    ): Promise<void> {
      document.querySelector<HTMLButtonElement>(sel)!.click();
      await settle(fixture);
      await settle(fixture);
    }

    it('Use this on an Ollama model in the AI Sources Ollama panel makes Ollama the provider in use and restarts a running Scout', async () => {
      const { fixture, bridge } = await openOllamaModels({
        workerState: 'running',
        ...OPENAI_IN_USE,
      });

      await clickInDialog(fixture, '[data-test="ollama-models"] [data-test="select-gemma-3-4b"]');

      expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toContainEqual({
        key: 'llm.provider',
        value: 'Ollama',
      });
      expect(bridge.workerStop).toHaveBeenCalledTimes(1);
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    });

    it('The AI Sources Ollama panel marks no model In use while another provider is in use', async () => {
      await openOllamaModels(OPENAI_IN_USE);
      const panel = document.querySelector('[data-test="ollama-models"]') as HTMLElement;

      expect(panel.querySelector('[data-test="row-qwen-2.5-0.5b-instruct"]')).not.toBeNull();
      expect(panel.querySelector('[data-test="check-qwen-2.5-0.5b-instruct"]')).toBeNull();
    });

    it('Use this on the Ollama panel whose llm.model cannot be saved shows why on the row, and neither switches the provider nor restarts Scout', async () => {
      const { fixture, bridge } = await openOllamaModels({
        workerState: 'running',
        ...OPENAI_IN_USE,
      });
      bridge.vaultStore.mockImplementation(async (req: { key: string; value: string }) => {
        if (req.key === 'llm.model')
          throw new Error('[scout:vault/unavailable] the keyring is locked');
      });

      await clickInDialog(fixture, '[data-test="ollama-models"] [data-test="select-gemma-3-4b"]');

      expect({
        stored: bridge.vaultStore.mock.calls.map((c) => (c[0] as { key: string }).key),
        stops: bridge.workerStop.mock.calls.length,
        starts: bridge.workerStart.mock.calls.length,
        error: document.querySelector('[data-test="ollama-models"] [data-test="error-gemma-3-4b"]')
          ?.textContent,
      }).toEqual({
        stored: ['llm.model'],
        stops: 0,
        starts: 0,
        error: expect.stringContaining('the keyring is locked'),
      });
    });

    it('Use this on the Ollama panel while Scout runs on Ollama saves the model before Scout restarts, and says it restarts', async () => {
      const { fixture, bridge } = await openOllamaModels({
        workerState: 'running',
        publicValues: { 'llm.provider': 'Ollama' },
      });
      const order: string[] = [];
      bridge.vaultStore.mockImplementation(async (req: { key: string; value: string }) => {
        order.push(`store ${req.key}=${req.value}`);
      });
      bridge.workerStop.mockImplementation(async () => {
        order.push('stop');
        return { exitCode: 0 };
      });

      await clickInDialog(fixture, '[data-test="ollama-models"] [data-test="select-gemma-3-4b"]');

      expect(order.indexOf('store llm.model=gemma3:4b')).toBeGreaterThanOrEqual(0);
      expect(order.indexOf('store llm.model=gemma3:4b')).toBeLessThan(order.indexOf('stop'));
      expect(bridge.workerStart).toHaveBeenCalledTimes(1);
      expect(text(fixture, '[data-test="saved-ollama"]')).toContain(
        'Restarting Scout to use Ollama.',
      );
    });
  });
});

describe('AI Sources follows the theme too', () => {
  it('the Ollama models panel is no longer pinned to the dark canvas: it paints on the theme background', () => {
    const source = readFileSync('src/app/sources/ollama-models-dialog.component.ts', 'utf8');
    const panel = /<section\s+id="ollama-models"\s+class="([^"]*)"/.exec(source)?.[1];
    const rule = /\n {6}\.ollama-models \{([^}]*)\}/.exec(source)?.[1] ?? '';

    expect(panel).toBe('ollama-models');
    expect(rule).toContain('background: var(--sc-surface);');
    expect(rule).toContain('color: var(--sc-fg);');
  });

  it('its styles paint only with the theme tokens, no hex, rgb() or hsl() colour left', () => {
    const source = readFileSync('src/app/sources/sources.component.ts', 'utf8');
    const start = source.indexOf('  styles:');
    const css = source.slice(start, source.indexOf('\n})\n', start));

    expect(start).toBeGreaterThanOrEqual(0);
    expect(css).toContain('var(--sc-');
    expect(
      Array.from(
        css.matchAll(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g),
        (match) => match[0],
      ),
    ).toEqual([]);
  });
});

describe('Ollama card: scan result x what it shows', () => {
  const NOT_ANSWERING =
    'Ollama is not answering. Open the Ollama app or run ollama serve, then scan again.';
  const GONE = [
    'start-ollama',
    'autostart-ollama',
    'ollama-stopped',
    'ollama-worker-down',
    'ollama-server-error',
  ];

  type Scan = 'answers' | 'does not answer' | 'scan again running after no answer';

  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  async function open(scan: Scan) {
    const { fixture, bridge } = await setup(
      scan === 'answers' ? { models: [] } : { listError: new OllamaUnreachableError('down') },
    );
    if (scan === 'scan again running after no answer') {
      bridge.llmListOllamaModels.mockImplementation(() => new Promise(() => undefined));
      void fixture.componentInstance.onScan('ollama');
    }
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, bridge };
  }

  function seen(fixture: ComponentFixture<SourcesComponent>) {
    const card = fixture.nativeElement.querySelector('[data-test="card-ollama"]') as HTMLElement;
    const text = (test: string) =>
      card.querySelector(`[data-test="${test}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
    return {
      badge: text('badge-ollama'),
      actions: Array.from(card.querySelectorAll('.card-actions .row button'), (b) =>
        b.textContent?.trim(),
      ),
      notAnswering: text('ollama-not-answering'),
      present: GONE.filter((test) => card.querySelector(`[data-test="${test}"]`) !== null),
    };
  }

  const SEEN: Readonly<Record<Scan, ReturnType<typeof seen>>> = {
    answers: {
      badge: 'Connected',
      actions: ['Manage local models', 'Use', 'Scan again'],
      notAnswering: null,
      present: [],
    },
    'does not answer': {
      badge: 'Not connected',
      actions: ['Connect'],
      notAnswering: NOT_ANSWERING,
      present: [],
    },
    'scan again running after no answer': {
      badge: 'Not connected',
      actions: ['Scanning for Ollama...'],
      notAnswering: null,
      present: [],
    },
  };

  for (const scan of Object.keys(SEEN) as Scan[]) {
    it(`Ollama ${scan}: the badge, the actions and the not-answering line, with no Start or Autostart`, async () => {
      const { fixture } = await open(scan);

      expect(seen(fixture)).toEqual(SEEN[scan]);
    });
  }

  it('an Ollama that does not answer says so on its card and offers only Connect', async () => {
    const { fixture, bridge } = await open('does not answer');
    (
      fixture.nativeElement.querySelector('[data-test="action-ollama"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();
    const card = fixture.nativeElement.querySelector('[data-test="card-ollama"]') as HTMLElement;

    expect({
      line: seen(fixture).notAnswering,
      actions: seen(fixture).actions,
      switches: card.querySelectorAll('[role="switch"]').length,
      scans: bridge.llmListOllamaModels.mock.calls.length,
    }).toEqual({
      line: NOT_ANSWERING,
      actions: ['Connect'],
      switches: 0,
      scans: 2,
    });
  });

  it('Ollama in use but not answering is never shown with the green In use badge', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('down'),
      publicValues: { 'llm.provider': 'Ollama' },
    });
    await fixture.componentInstance.onScan('ollama');
    await fixture.whenStable();
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;

    expect({
      line: seen(fixture).notAnswering,
      tone: badge.getAttribute('data-tone'),
    }).toEqual({ line: NOT_ANSWERING, tone: 'neutral' });
  });
});

describe('AI Sources as Sources.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  });

  async function mount(
    opts: Parameters<typeof makeBridge>[0] = {},
    tweak: (bridge: FakeBridge) => void = () => undefined,
  ): Promise<{ fixture: ComponentFixture<SourcesComponent>; bridge: FakeBridge }> {
    const bridge = makeBridge(opts);
    tweak(bridge);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: ScoutBridgeService, useValue: bridge },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: InteractivityChecker, useValue: LAID_OUT },
      ],
    });
    const fixture = TestBed.createComponent(SourcesComponent);
    await settle(fixture);
    return { fixture, bridge };
  }

  async function settle(fixture: ComponentFixture<SourcesComponent>): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      fixture.detectChanges();
      await fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    fixture.detectChanges();
  }

  function lookOf(button: Element): string | null {
    if (button.classList.contains('mat-mdc-unelevated-button')) return 'filled';
    if (button.classList.contains('mat-mdc-outlined-button')) return 'outlined';
    if (button.classList.contains('mat-mdc-button')) return 'text';
    return null;
  }

  function el(fixture: ComponentFixture<SourcesComponent>, test: string): HTMLElement {
    return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement;
  }

  function words(node: Element | null | undefined): string {
    return (node?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function glyphOf(node: Element): string | null {
    return node.querySelector('svg path')?.getAttribute('d') ?? null;
  }

  function never<T>(): Promise<T> {
    return new Promise<T>(() => undefined);
  }

  const OPENAI_KEY = { vaultHasByKey: { 'llm.openai.api-key': true } };
  const CHATGPT_IN_USE = {
    chatgpt: CHATGPT_IN,
    publicValues: { 'llm.provider': 'ChatGptPlan', 'llm.cloud-model': 'gpt-5.6-luna' },
  };
  const GLYPH = {
    plug: 'M9 2v6M15 2v6M7 8h10v4a5 5 0 0 1-10 0zM12 17v5',
    unplug: 'M9 2v6M15 2v6M7 8h10v4a5 5 0 0 1-7.5 4.3M12 17v5M3 3l18 18',
    key: 'M14 7a4 4 0 1 1-3.5 6L4 19.5V21h3v-2h2v-2h2l1.4-1.4',
    server: 'M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01',
    busy: 'M12 3a9 9 0 1 0 9 9',
    search: 'M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM20 20l-3.5-3.5',
    info: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 11v5M12 8v.5',
    arrowOut: 'M7 17L17 7M8 7h9v9',
    signOut: 'M15 4h4v16h-4M10 17l5-5-5-5M15 12H3',
    cube: 'M12 2l9 5v10l-9 5-9-5V7zM12 12l9-5M12 12v10M12 12L3 7',
  };

  it('one h1, "AI Sources", 20 px at 600 over the artboard lede; the page starts at the left edge, at most 1200 px wide, 24 by 32 px in, its parts 16 px apart, its text at 14 px', async () => {
    const { fixture } = await mount();
    const page = fixture.nativeElement.querySelector('main.page') as HTMLElement;
    const h1 = fixture.nativeElement.querySelector('h1') as HTMLElement;

    expect({
      headings: Array.from(fixture.nativeElement.querySelectorAll('h1'), (h) =>
        words(h as Element),
      ),
      h1: [declaredIn(h1, 'font-size'), declaredIn(h1, 'font-weight')],
      lede: words(fixture.nativeElement.querySelector('.lede')),
      page: [
        declaredIn(page, 'padding'),
        declaredIn(page, 'max-width'),
        declaredIn(page, 'margin'),
        declaredIn(page, 'gap'),
        declaredIn(page, 'font-size'),
      ],
    }).toEqual({
      headings: ['AI Sources'],
      h1: ['20px', '600'],
      lede: 'Connect a model on this computer or a cloud provider. Scout answers only with the sources you connect.',
      page: ['24px 32px 40px', '1200px', null, '16px', '14px'],
    });
  });

  it('the filter is the "Show sources" group of three pressed-state pills, All, On this computer and Cloud, 30 px tall, the pressed one on --sc-pressed', async () => {
    const { fixture } = await mount();
    const group = fixture.nativeElement.querySelector('.filter') as HTMLElement;
    const pills = (): HTMLButtonElement[] => Array.from(group.querySelectorAll('button'));
    const before = pills().map((b) => [
      words(b),
      b.getAttribute('aria-pressed'),
      b.getAttribute('role'),
    ]);

    el(fixture, 'filter-local').click();
    await settle(fixture);
    const pressed = pills().find((b) => b.getAttribute('aria-pressed') === 'true')!;

    expect({
      group: [group.getAttribute('role'), group.getAttribute('aria-label')],
      before,
      after: words(pressed),
      pill: [
        declaredIn(pressed, 'height'),
        declaredIn(pressed, 'padding'),
        declaredIn(pressed, 'border-radius'),
        declaredIn(pressed, 'background'),
        declaredIn(pressed, 'font-weight'),
      ],
      frame: [declaredIn(group, 'padding'), declaredIn(group, 'border-radius')],
    }).toEqual({
      group: ['group', 'Show sources'],
      before: [
        ['All', 'true', null],
        ['On this computer', 'false', null],
        ['Cloud', 'false', null],
      ],
      after: 'On this computer',
      pill: ['30px', '0 14px', '16px', 'var(--sc-pressed)', '600'],
      frame: ['3px', '20px'],
    });
  });

  it('the sections are labelled by the uppercase eyebrows "On this computer" and "Cloud", 13 px at 600, with no leading rule; their cards wrap 300 px and up, 16 px apart', async () => {
    const { fixture } = await mount();
    const sections = Array.from(
      fixture.nativeElement.querySelectorAll('section.section') as NodeListOf<HTMLElement>,
    );
    const h2 = sections[0]!.querySelector('h2') as HTMLElement;
    const grid = sections[0]!.querySelector('.grid') as HTMLElement;
    const card = el(fixture, 'card-ollama');

    expect({
      eyebrows: sections.map((s) =>
        document.getElementById(s.getAttribute('aria-labelledby')!)?.textContent?.trim(),
      ),
      eyebrow: [
        declaredIn(h2, 'text-transform'),
        declaredIn(h2, 'font-size'),
        declaredIn(h2, 'font-weight'),
        declaredIn(h2, 'letter-spacing'),
      ],
      rule: fixture.nativeElement.querySelector('.section-rule'),
      grid: [declaredIn(grid, 'display'), declaredIn(grid, 'flex-wrap'), declaredIn(grid, 'gap')],
      card: declaredIn(card, 'flex'),
    }).toEqual({
      eyebrows: ['On this computer', 'Cloud'],
      eyebrow: ['uppercase', '13px', '600', '0.04em'],
      rule: null,
      grid: ['flex', 'wrap', '16px'],
      card: '1 1 300px',
    });
  });

  const CARDS: readonly (readonly [string, string, string])[] = [
    [
      'ollama',
      'Ollama',
      'Runs open models on this computer. Private, and offline once a model is downloaded.',
    ],
    [
      'lm-studio',
      'LM Studio',
      "Runs local models through LM Studio's server. Start the server in LM Studio, then scan.",
    ],
    [
      'chatgpt-plus',
      'ChatGPT plan',
      "Sign in with your ChatGPT Plus or Pro plan. Usage counts against your plan's limits.",
    ],
    [
      'openai-api',
      'OpenAI API',
      'Use your OpenAI developer API key. Billing goes to your OpenAI account.',
    ],
    [
      'google-gemini',
      'Google Gemini',
      "Google's Gemini models, such as Gemini 2.5 Pro and Flash, with your Gemini API key.",
    ],
    ['claude-api', 'Claude API', "Anthropic's Claude models, with your own Anthropic API key."],
    [
      'typesafe-jev',
      'TypeSafe (Jev)',
      'Read-only guard: checks each request with the recent conversation and refuses ones that ask Scout to change something.',
    ],
    [
      'openrouter',
      'OpenRouter',
      'A unified interface to access hundreds of AI models through a single API connection.',
    ],
    [
      'custom-api',
      'Custom API',
      'Any OpenAI-compatible endpoint, such as a model server you host yourself.',
    ],
  ];

  it.each(CARDS)(
    'the %s card is the artboard card: labelled by its 15 px title in one row with the 36 px logo tile and the chip, then its description in --sc-fg-2',
    async (id, title, description) => {
      const { fixture } = await mount();
      const card = el(fixture, `card-${id}`);
      const head = card.querySelector('.card-head') as HTMLElement;
      const h3 = head.querySelector('h3') as HTMLElement;
      const tile = head.querySelector('.icon') as HTMLElement;
      const desc = card.querySelector('.card-desc') as HTMLElement;

      expect({
        labelledBy: document
          .getElementById(card.getAttribute('aria-labelledby')!)
          ?.textContent?.trim(),
        head: Array.from(head.children, (c) => c.tagName.toLowerCase()),
        title: [declaredIn(h3, 'font-size'), declaredIn(h3, 'font-weight')],
        tile: [
          declaredIn(tile, 'width'),
          declaredIn(tile, 'border-radius'),
          declaredIn(tile, 'border'),
          declaredIn(tile, 'background'),
        ],
        box: [
          declaredIn(card, 'padding'),
          declaredIn(card, 'gap'),
          declaredIn(card, 'border-radius'),
          declaredIn(card, 'min-height'),
        ],
        description: words(desc),
        descStyle: [declaredIn(desc, 'font-size'), declaredIn(desc, 'color')],
      }).toEqual({
        labelledBy: title,
        head: ['span', 'h3', 'sc-status-chip'],
        title: ['15px', '600'],
        tile: ['36px', '10px', null, 'var(--sc-surface-2)'],
        box: ['16px', '10px', 'var(--sc-radius)', null],
        description,
        descStyle: ['13px', 'var(--sc-fg-2)'],
      });
    },
  );

  it.each([
    ['ChatGPT plan in use', CHATGPT_IN_USE, 'chatgpt-plus', ['openai-api']],
    [
      'OpenAI API in use',
      { ...OPENAI_KEY, publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' } },
      'openai-api',
      ['chatgpt-plus', 'google-gemini'],
    ],
  ] as const)(
    '%s: only the card in use has the 2 px accent border and 15 px padding',
    async (_, opts, inUse, others) => {
      const { fixture } = await mount(opts);
      const card = el(fixture, `card-${inUse}`);

      expect({
        inUse: card.classList.contains('card--in-use'),
        border: declaredIn(card, 'border'),
        padding: declaredIn(card, 'padding'),
        others: others.map((id) => el(fixture, `card-${id}`).classList.contains('card--in-use')),
      }).toEqual({
        inUse: true,
        border: '2px solid var(--sc-accent)',
        padding: '15px',
        others: others.map(() => false),
      });
    },
  );

  it.each([
    ['not connected', {}, [['Connect', 'filled', GLYPH.plug, null]]],
    [
      'connected',
      OPENAI_KEY,
      [
        ['Change key', 'outlined', GLYPH.key, null],
        ['Disconnect', 'outlined', GLYPH.unplug, 'var(--sc-error)'],
      ],
    ],
  ] as const)(
    'the OpenAI API card %s: its buttons are the artboard pills, content-wide, with the artboard glyphs and no chevron, in one row',
    async (_, opts, expected) => {
      const { fixture } = await mount(opts);
      const row = el(fixture, 'card-openai-api').querySelector('.card-actions .row') as HTMLElement;
      const buttons = Array.from(row.querySelectorAll('button'));

      expect({
        buttons: buttons.map((b) => [
          words(b),
          lookOf(b),
          glyphOf(b),
          declaredIn(b, '--mat-button-outlined-label-text-color'),
        ]),
        chevron: el(fixture, 'card-openai-api').querySelector('.chevron'),
        content: buttons.map((b) => declaredIn(b, 'align-self')),
      }).toEqual({
        buttons: expected.map((e) => [...e]),
        chevron: null,
        content: expected.map(() => 'flex-start'),
      });
    },
  );

  it('the filled pill is white on --sc-accent-strong, 16 px in; an outlined pill takes the theme outline', async () => {
    const { fixture } = await mount();
    const connect = el(fixture, 'action-openai-api');

    expect([
      declaredIn(connect, '--mat-button-filled-container-color'),
      declaredIn(connect, '--mat-button-filled-label-text-color'),
      declaredIn(connect, '--mat-button-filled-horizontal-padding'),
    ]).toEqual(['var(--sc-accent-strong)', 'var(--sc-on-accent)', '16px']);
  });

  it("the usage block is the artboard panel: on --sc-bg with 10 px corners, 13 px rows, Scout's own count line and the provider page as a 32 px outlined pill", async () => {
    const { fixture } = await mount(OPENAI_KEY);
    const usage = el(fixture, 'usage-openai-api');
    const row = usage.querySelector('.usage-row') as HTMLElement;
    const page = el(fixture, 'usage-page-openai-api');

    expect({
      role: [usage.getAttribute('role'), usage.getAttribute('aria-label')],
      box: [
        declaredIn(usage, 'background'),
        declaredIn(usage, 'border-radius'),
        declaredIn(usage, 'padding'),
      ],
      row: declaredIn(row, 'font-size'),
      own: words(el(fixture, 'usage-own-openai-api')),
      page: [
        words(page),
        lookOf(page),
        glyphOf(page),
        declaredIn(page, '--mat-button-outlined-container-height'),
      ],
    }).toEqual({
      role: ['group', 'Usage'],
      box: ['var(--sc-bg)', '10px', '10px 12px'],
      row: '13px',
      own: "Scout's own count, from the usage each answer reports.",
      page: [
        "Open the provider's usage page",
        'outlined',
        'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
        '32px',
      ],
    });
  });

  it('Plan limit reached: the ChatGPT plan row reads the limit time in the warn colour', async () => {
    const limitAt = new Date(2026, 6, 14, 13, 42).toISOString();
    const { fixture } = await mount({
      ...CHATGPT_IN_USE,
      usage: {
        ChatGptPlan: {
          today: { tokens: 96_400, answers: 3, cost: null },
          month: { tokens: 2_800_000, answers: 90, cost: null },
          lastModel: 'gpt-5.6-luna',
          lastReported: true,
          limitAt,
        },
      },
    });
    const value = el(fixture, 'usage-limit-chatgpt-plus').querySelector(
      '.usage-value',
    ) as HTMLElement;

    expect([words(value), declaredIn(value, 'color')]).toEqual([
      'Usage limit reached at 13:42',
      'var(--sc-on-warn-container)',
    ]);
  });

  it('OpenRouter read failed: the note keeps the last balance time in the warn colour after the info glyph', async () => {
    const { fixture } = await mount({
      vaultHasByKey: { 'llm.openrouter.api-key': true },
      openRouterKey: {
        info: {
          limit: 20,
          limitRemaining: 7.42,
          limitReset: 'monthly',
          usageDaily: 0.3,
          usageMonthly: 12.58,
        },
        readAt: new Date(2026, 6, 14, 14, 2).toISOString(),
        error: 'unreachable',
      },
    });
    const note = el(fixture, 'usage-as-of-openrouter');

    expect([words(note), glyphOf(note), declaredIn(note, 'color')]).toEqual([
      'Balance as of 14:02; the latest read failed.',
      GLYPH.info,
      'var(--sc-on-warn-container)',
    ]);
  });

  it('Usage not reported: the note reads after the info glyph in the muted colour', async () => {
    const { fixture } = await mount({
      ...OPENAI_KEY,
      usage: {
        OpenAI: {
          today: { tokens: 0, answers: 2, cost: null },
          month: { tokens: 1_200_000, answers: 2, cost: null },
          lastModel: 'm',
          lastReported: false,
          limitAt: null,
        },
      },
    });
    const note = el(fixture, 'usage-unreported-openai-api');

    expect([words(note), glyphOf(note), declaredIn(note, 'color')]).toEqual([
      'Usage not reported for this model',
      GLYPH.info,
      'var(--sc-muted)',
    ]);
  });

  it('Ollama connected: Cost, then "Models installed" from the scan, the count and the size on disk', async () => {
    const GB = 1024 * 1024 * 1024;
    const { fixture } = await mount({
      models: [
        { name: 'gemma3:4b', modifiedAt: '2026-07-01T00:00:00Z', sizeBytes: 3.3 * GB },
        { name: 'qwen2.5:1.5b', modifiedAt: '2026-07-01T00:00:00Z', sizeBytes: 1.1 * GB },
      ],
    });

    expect(
      Array.from(el(fixture, 'usage-ollama').querySelectorAll('.usage-row'), (r) =>
        Array.from(r.children, (c) => words(c)),
      ).slice(0, 2),
    ).toEqual([
      ['Cost', 'Local, free'],
      ['Models installed', '2 · 4.4 GB'],
    ]);
  });

  it.each([
    ['while it scans', 'scanning', 'Scanning for Ollama...', 'outlined', GLYPH.busy, true],
    ['not answering', 'down', 'Connect', 'filled', GLYPH.plug, false],
    ['answering', 'up', 'Scan again', 'outlined', GLYPH.search, false],
  ] as const)(
    'Ollama %s (%s): the scan button reads the artboard label for that state',
    async (_, state, label, look, glyph, busy) => {
      const { fixture } = await mount(
        state === 'down' ? { listError: new OllamaUnreachableError('down') } : { models: [] },
        (bridge) => {
          if (state === 'scanning') bridge.llmListOllamaModels.mockImplementation(never);
        },
      );
      const scan = el(fixture, 'action-ollama') as HTMLButtonElement;

      expect({
        label: words(scan),
        look: lookOf(scan),
        glyph: glyphOf(scan),
        busy: scan.getAttribute('aria-busy') === 'true',
        disabled: scan.disabled,
      }).toEqual({ label, look, glyph, busy, disabled: busy });
    },
  );

  it('Ollama answering: Manage local models is the filled pill with the server glyph, and opens a dialog', async () => {
    const { fixture } = await mount({ models: [] });
    const manage = el(fixture, 'manage-ollama-models');

    expect([
      words(manage),
      lookOf(manage),
      glyphOf(manage),
      manage.getAttribute('aria-haspopup'),
    ]).toEqual(['Manage local models', 'filled', GLYPH.server, 'dialog']);
  });

  it('LM Studio: "Scan for LM Studio" with the search glyph, "Scanning for LM Studio..." while it scans', async () => {
    const idle = await mount({ lmStudioError: new Error('down') });
    const before = [
      words(el(idle.fixture, 'action-lm-studio')),
      glyphOf(el(idle.fixture, 'action-lm-studio')),
    ];
    TestBed.resetTestingModule();
    const busy = await mount({}, (bridge) =>
      bridge.llmListLmStudioModels.mockImplementation(never),
    );

    expect([before, words(el(busy.fixture, 'action-lm-studio'))]).toEqual([
      ['Scan for LM Studio', GLYPH.search],
      'Scanning for LM Studio...',
    ]);
  });

  it('Download Ollama is the artboard link: 13 px at 500 in the accent, with the arrow out', async () => {
    const { fixture } = await mount();
    const link = el(fixture, 'secondary-ollama');

    expect([
      words(link),
      glyphOf(link),
      declaredIn(link, 'color'),
      declaredIn(link, 'font-size'),
      declaredIn(link, 'font-weight'),
    ]).toEqual(['Download Ollama', GLYPH.arrowOut, 'var(--sc-accent)', '13px', '500']);
  });

  it('Checking: the ChatGPT plan card shows the skeleton and "Checking your ChatGPT sign-in..." as a status, and no sign-in yet', async () => {
    const { fixture } = await mount({}, (bridge) => bridge.chatgptStatus.mockImplementation(never));
    const checking = el(fixture, 'chatgpt-checking');

    expect({
      role: [checking?.getAttribute('role'), checking?.getAttribute('aria-label')],
      bars: checking?.querySelectorAll('.skeleton-bar').length,
      text: words(checking),
      signIn: el(fixture, 'chatgpt-sign-in'),
    }).toEqual({
      role: ['status', 'Checking your ChatGPT sign-in'],
      bars: 2,
      text: 'Checking your ChatGPT sign-in...',
      signIn: null,
    });
  });

  it('Checking: a cloud card whose key is not read yet offers no Connect', async () => {
    const { fixture } = await mount({}, (bridge) => bridge.vaultHas.mockImplementation(never));

    expect({
      chip: words(el(fixture, 'badge-openai-api')),
      connect: ['openai-api', 'google-gemini', 'claude-api', 'openrouter', 'typesafe-jev'].map(
        (id) => el(fixture, `action-${id}`),
      ),
    }).toEqual({ chip: 'Checking...', connect: [null, null, null, null, null] });
  });

  it('ChatGPT plan in use: one account line, the email then "answering with" the model in bold; Choose a model as an outlined pill and Sign out as an accent text button', async () => {
    const { fixture } = await mount(CHATGPT_IN_USE);
    const account = el(fixture, 'chatgpt-account');
    const choose = el(fixture, 'choose-model-chatgpt-plus');
    const signOut = el(fixture, 'chatgpt-sign-out');

    expect({
      account: words(account),
      bold: words(account.querySelector('strong')),
      choose: [words(choose), choose.tagName.toLowerCase(), lookOf(choose), glyphOf(choose)],
      signOut: [
        words(signOut),
        lookOf(signOut),
        glyphOf(signOut),
        declaredIn(signOut, '--mat-button-text-label-text-color'),
      ],
    }).toEqual({
      account: 'owner@example.com · answering with gpt-5.6-luna',
      bold: 'gpt-5.6-luna',
      choose: ['Choose a model', 'a', 'outlined', GLYPH.cube],
      signOut: ['Sign out', 'text', GLYPH.signOut, 'var(--sc-accent)'],
    });
  });

  it('the key form is the artboard form: a labelled 40 px password field with its help line; Connect gives way to it and the focus moves in, Cancel brings Connect and the focus back', async () => {
    const { fixture } = await mount();
    el(fixture, 'action-google-gemini').click();
    await settle(fixture);
    const input = el(fixture, 'api-key-google-gemini') as HTMLInputElement;
    const opened = {
      label: words(document.querySelector(`label[for="${input.id}"]`)),
      type: input.type,
      autocomplete: input.getAttribute('autocomplete'),
      help: words(document.getElementById(input.getAttribute('aria-describedby')!)),
      height: declaredIn(input, 'height'),
      connect: el(fixture, 'action-google-gemini'),
      focused: document.activeElement === input,
    };

    el(fixture, 'cancel-google-gemini').click();
    await settle(fixture);

    expect({
      opened,
      closed: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
    }).toEqual({
      opened: {
        label: 'API key',
        type: 'password',
        autocomplete: 'off',
        help: 'Stored on this computer only.',
        height: '40px',
        connect: null,
        focused: true,
      },
      closed: 'action-google-gemini',
    });
  });

  it('Custom API is the dashed "Coming soon" card: the chip on the info pair, the muted outline mark, and no button', async () => {
    const { fixture } = await mount();
    const card = el(fixture, 'card-custom-api');
    const chip = el(fixture, 'badge-custom-api');

    expect({
      dashed: declaredIn(card, 'border-style'),
      chip: [words(chip), chip.getAttribute('data-tone')],
      mark: [
        glyphOf(el(fixture, 'icon-custom-api')),
        el(fixture, 'icon-custom-api').classList.contains('icon--muted'),
      ],
      buttons: card.querySelectorAll('button').length,
      note: words(el(fixture, 'action-custom-api')),
    }).toEqual({
      dashed: 'dashed',
      chip: ['Coming soon', 'info'],
      mark: ['M8 8l-4 4 4 4M16 8l4 4-4 4', true],
      buttons: 0,
      note: 'Not available yet. It arrives in a later version.',
    });
  });

  it.each([
    [
      'OpenAI API, not in use',
      OPENAI_KEY,
      'openai-api',
      'Disconnect OpenAI API?',
      'Scout deletes the stored API key from this computer.',
    ],
    [
      'OpenAI API, in use',
      { ...OPENAI_KEY, publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' } },
      'openai-api',
      'Disconnect OpenAI API?',
      'Scout deletes the stored API key from this computer. Answers stop using OpenAI until you connect it again with a key.',
    ],
    [
      'OpenRouter',
      { vaultHasByKey: { 'llm.openrouter.api-key': true } },
      'openrouter',
      'Disconnect OpenRouter?',
      'Scout deletes the stored OpenRouter key from this computer and stops reading its balance. Your credits stay on your OpenRouter account.',
    ],
  ] as const)(
    'Disconnect confirm, %s: the shared dialog card, 440 px wide, over the scrim, with the artboard sentence',
    async (_, opts, id, title, body) => {
      const { fixture } = await mount(opts);
      el(fixture, `disconnect-${id}`).click();
      await settle(fixture);
      const pane = document.querySelector('.cdk-overlay-pane.sc-dialog') as HTMLElement | null;

      expect({
        scrim: document
          .querySelector('.cdk-overlay-backdrop')
          ?.classList.contains('sc-dialog-backdrop'),
        pane: [pane?.style.width, pane?.style.marginTop],
        title: words(pane?.querySelector('h2')),
        body: words(pane?.querySelector('.body')),
        focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      }).toEqual({
        scrim: true,
        pane: ['440px', '120px'],
        title,
        body,
        focused: 'confirm-cancel',
      });
      (document.querySelector('[data-test="confirm-cancel"]') as HTMLButtonElement).click();
      await settle(fixture);
    },
  );

  it('a confirmed Disconnect says so in the notice, "<card> disconnected. The key was deleted from this computer.", with Dismiss', async () => {
    const { fixture } = await mount(OPENAI_KEY);
    el(fixture, 'disconnect-openai-api').click();
    await settle(fixture);
    (document.querySelector('[data-test="confirm-ok"]') as HTMLButtonElement).click();
    await settle(fixture);
    const notice = document.querySelector('.sc-notice');

    expect([noticesShown(), words(notice?.querySelector('button'))]).toEqual([
      ['OpenAI API disconnected. The key was deleted from this computer.'],
      'Dismiss',
    ]);
  });

  it('a Disconnect of the key in use whose key could not be deleted moves Scout off it but never says the key was deleted', async () => {
    const { fixture } = await mount({
      ...OPENAI_KEY,
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
      vaultDeleteError: new Error('the keyring is locked'),
    });
    el(fixture, 'disconnect-openai-api').click();
    await settle(fixture);
    (document.querySelector('[data-test="confirm-ok"]') as HTMLButtonElement).click();
    await settle(fixture);

    expect([noticesShown(), words(el(fixture, 'card-openai-api'))]).toEqual([
      [],
      expect.stringContaining('the keyring is locked'),
    ]);
  });

  it('Admin key rejected: the line reads in the error colour, Remove is a 32 px outlined pill in the error colour', async () => {
    const { fixture } = await mount({
      ...OPENAI_KEY,
      orgSpend: { OpenAI: { amount: null, currency: null, readAt: null, error: 'invalid-key' } },
    });
    const value = el(fixture, 'org-spend-value-openai-api');
    const remove = el(fixture, 'admin-key-remove-openai-api');

    expect({
      value: [words(value), declaredIn(value, 'color')],
      remove: [
        words(remove),
        lookOf(remove),
        declaredIn(remove, '--mat-button-outlined-container-height'),
        declaredIn(remove, '--mat-button-outlined-label-text-color'),
      ],
    }).toEqual({
      value: ['The Admin key was rejected', 'var(--sc-error)'],
      remove: ['Remove', 'outlined', '32px', 'var(--sc-error)'],
    });
  });

  it('Admin key added: the spend line in the text colour; with no key, Add Admin key is a 32 px outlined pill', async () => {
    const { fixture } = await mount({
      vaultHasByKey: { 'llm.openai.api-key': true, 'llm.anthropic.api-key': true },
      orgSpend: {
        OpenAI: { amount: 12.4, currency: 'usd', readAt: new Date().toISOString(), error: null },
      },
    });
    const value = el(fixture, 'org-spend-value-openai-api');
    const add = el(fixture, 'admin-key-add-claude-api');

    expect([
      words(value),
      declaredIn(value, 'color'),
      words(add),
      declaredIn(add, '--mat-button-outlined-container-height'),
    ]).toEqual([
      'US$ 12.40 spent this month (organization)',
      'var(--sc-fg)',
      'Add Admin key',
      '32px',
    ]);
  });

  it('Manage local models opens "Local models in Ollama" as the artboard dialog card, 560 px wide and 160 px from the top, with Close, its lede and the Skill matching, Installed, Pull by name and Recommended sections', async () => {
    const { fixture, bridge } = await mount({
      models: [{ name: 'gemma3:4b', modifiedAt: '', sizeBytes: 0 }],
    });
    Object.assign(bridge, {
      onLlmPullOllamaProgress: vi.fn(() => () => undefined),
      onWhisperDownloadProgress: vi.fn(() => () => undefined),
      whisperListInstalled: vi.fn(async () => ({ installed: [] })),
      onWorkerEvent: vi.fn(() => () => undefined),
    });
    TestBed.inject(LocalModelsStore).markDownloaded('gemma-3-4b');
    el(fixture, 'manage-ollama-models').click();
    await settle(fixture);
    const pane = document.querySelector('.cdk-overlay-pane.sc-dialog') as HTMLElement;
    const dialog = pane.querySelector('[data-test="ollama-models"]') as HTMLElement;

    expect({
      pane: [pane.style.width, pane.style.marginTop],
      title: words(document.getElementById(dialog.getAttribute('aria-labelledby')!)),
      lede: words(document.getElementById(dialog.getAttribute('aria-describedby')!)),
      close: dialog.querySelector('[data-test="ollama-models-close"]')?.getAttribute('aria-label'),
      lists: Array.from(dialog.querySelectorAll('.list-title'), (h) => words(h)).slice(1),
      installed: Array.from(dialog.querySelectorAll('[data-test="installed-models"] > li'), (li) =>
        li.getAttribute('data-test'),
      ),
    }).toEqual({
      pane: ['560px', '160px'],
      title: 'Local models in Ollama',
      lede: 'The models installed in Ollama on this computer. They appear in Models, Analysis, under Ollama.',
      close: 'Close',
      lists: ['Skill matching', 'Installed', 'Pull by name', 'Recommended'],
      installed: ['row-gemma-3-4b'],
    });
  });

  it('no credential reaches the page: every key field is a password field with autocomplete off, and no attribute carries the email or a typed key', async () => {
    const { fixture } = await mount({
      ...CHATGPT_IN_USE,
      validation: { ok: false, reason: 'invalid-key' },
    });
    const fields: (readonly [string, string | null])[] = [];
    for (const id of ['google-gemini', 'claude-api', 'openrouter', 'typesafe-jev', 'openai-api']) {
      el(fixture, `action-${id}`).click();
      await settle(fixture);
      const input = el(fixture, `api-key-${id}`) as HTMLInputElement;
      fields.push([input.type, input.getAttribute('autocomplete')]);
    }
    const key = el(fixture, 'api-key-openai-api') as HTMLInputElement;
    key.value = 'sk-rejected-secret';
    el(fixture, 'save-openai-api').click();
    await settle(fixture);
    const attributes = Array.from(document.querySelectorAll('*')).flatMap((node) =>
      Array.from(node.attributes, (a) => a.value),
    );

    expect({
      fields,
      emailInAttribute: attributes.some((v) => v.includes('owner@example.com')),
      keyAnywhere: document.body.innerHTML.includes('sk-rejected-secret') || key.value !== '',
    }).toEqual({
      fields: Array.from({ length: 5 }, () => ['password', 'off']),
      emailInAttribute: false,
      keyAnywhere: false,
    });
  });

  it('the local default needs no paid credential: with no key and no sign-in, Ollama offers Connect, and once it answers, Use', async () => {
    const down = await mount({ listError: new OllamaUnreachableError('down') });
    const connect = el(down.fixture, 'action-ollama') as HTMLButtonElement;
    const before = [words(connect), connect.disabled];
    TestBed.resetTestingModule();
    const up = await mount({ models: [] });
    const use = el(up.fixture, 'use-ollama') as HTMLButtonElement;

    expect([before, [words(use), use.disabled]]).toEqual([
      ['Connect', false],
      ['Use', false],
    ]);
  });
});

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declaredIn(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s{])${property}:\\s*([^;]+)`);
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

describe('Ollama card: what Scout asked Ollama when the provider moved away x the line it shows', () => {
  const FREED = 'Ollama freed its models from memory. It keeps running for other programs.';
  const NOT_FREED =
    'Scout could not ask Ollama to free its memory. Ollama frees it by itself after a few minutes without use.';

  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  const rows: ReadonlyArray<{
    readonly name: string;
    readonly switched: boolean;
    readonly usedOllama?: false;
    readonly unload: () => Promise<{ unloaded: readonly string[]; refused: readonly string[] }>;
    readonly freed: string | null;
    readonly notFreed: string | null;
  }> = [
    {
      name: "Ollama unloaded Scout's models",
      switched: true,
      unload: async () => ({ unloaded: ['fixture-chat:7b'], refused: [] }),
      freed: FREED,
      notFreed: null,
    },
    {
      name: 'the call failed',
      switched: true,
      unload: async () => {
        throw new OllamaUnreachableError('http://localhost:11434');
      },
      freed: null,
      notFreed: NOT_FREED,
    },
    {
      name: 'Ollama refused one model',
      switched: true,
      unload: async () => ({ unloaded: ['nomic-embed-text:latest'], refused: ['fixture-chat:7b'] }),
      freed: null,
      notFreed: NOT_FREED,
    },
    {
      name: "none of Scout's models was loaded",
      switched: true,
      unload: async () => ({ unloaded: [], refused: [] }),
      freed: null,
      notFreed: null,
    },
    {
      name: 'an owner who never used Ollama connects a cloud source while Ollama cannot be reached',
      switched: true,
      usedOllama: false,
      unload: async () => {
        throw new OllamaUnreachableError('http://localhost:11434');
      },
      freed: null,
      notFreed: null,
    },
    {
      name: 'the provider never moved away',
      switched: false,
      unload: async () => ({ unloaded: ['fixture-chat:7b'], refused: [] }),
      freed: null,
      notFreed: null,
    },
  ];

  it.each(rows)(
    '$name: the Ollama card shows the line for it, never freed when the call failed',
    async (row) => {
      const { fixture } = await setup({ models: [], unload: row.unload });
      const store = TestBed.inject(LlmConfigStore);
      if (row.usedOllama !== false) await store.useOllama();
      if (row.switched) await store.use('OpenAI', 'gpt-4o-mini');
      await new Promise((resolve) => setTimeout(resolve, 0));
      fixture.detectChanges();
      const card = fixture.nativeElement.querySelector('[data-test="card-ollama"]') as HTMLElement;
      const text = (test: string) =>
        card.querySelector(`[data-test="${test}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ??
        null;

      expect({ freed: text('ollama-freed'), notFreed: text('ollama-not-freed') }).toEqual({
        freed: row.freed,
        notFreed: row.notFreed,
      });
    },
  );
});
