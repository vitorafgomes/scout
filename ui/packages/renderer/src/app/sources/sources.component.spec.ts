import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { OllamaUnreachableError, type ProviderKeyValidateResponse } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LLM_CONFIG_STORAGE_KEY } from '../core/llm-config.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { SourcesComponent } from './sources.component';

const OPENAI_MARK =
  'M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z';
const GEMINI_MARK =
  'M11.04 19.32Q12 21.51 12 24q0-2.49.93-4.68.96-2.19 2.58-3.81t3.81-2.55Q21.51 12 24 12q-2.49 0-4.68-.93a12.3 12.3 0 0 1-3.81-2.58 12.3 12.3 0 0 1-2.58-3.81Q12 2.49 12 0q0 2.49-.96 4.68-.93 2.19-2.55 3.81a12.3 12.3 0 0 1-3.81 2.58Q2.49 12 0 12q2.49 0 4.68.96 2.19.93 3.81 2.55t2.55 3.81';
const OPENROUTER_MARK =
  'M16.778 1.844v1.919q-.569-.026-1.138-.032-.708-.008-1.415.037c-1.93.126-4.023.728-6.149 2.237-2.911 2.066-2.731 1.95-4.14 2.75-.396.223-1.342.574-2.185.798-.841.225-1.753.333-1.751.333v4.229s.768.108 1.61.333c.842.224 1.789.575 2.185.799 1.41.798 1.228.683 4.14 2.75 2.126 1.509 4.22 2.11 6.148 2.236.88.058 1.716.041 2.555.005v1.918l7.222-4.168-7.222-4.17v2.176c-.86.038-1.611.065-2.278.021-1.364-.09-2.417-.357-3.979-1.465-2.244-1.593-2.866-2.027-3.68-2.508.889-.518 1.449-.906 3.822-2.59 1.56-1.109 2.614-1.377 3.978-1.466.667-.044 1.418-.017 2.278.02v2.176L24 6.014Z';
const OLLAMA_MARK =
  'M16.361 10.26a.894.894 0 0 0-.558.47l-.072.148.001.207c0 .193.004.217.059.353.076.193.152.312.291.448.24.238.51.3.872.205a.86.86 0 0 0 .517-.436.752.752 0 0 0 .08-.498c-.064-.453-.33-.782-.724-.897a1.06 1.06 0 0 0-.466 0zm-9.203.005c-.305.096-.533.32-.65.639a1.187 1.187 0 0 0-.06.52c.057.309.31.59.598.667.362.095.632.033.872-.205.14-.136.215-.255.291-.448.055-.136.059-.16.059-.353l.001-.207-.072-.148a.894.894 0 0 0-.565-.472 1.02 1.02 0 0 0-.474.007Zm4.184 2c-.131.071-.223.25-.195.383.031.143.157.288.353.407.105.063.112.072.117.136.004.038-.01.146-.029.243-.02.094-.036.194-.036.222.002.074.07.195.143.253.064.052.076.054.255.059.164.005.198.001.264-.03.169-.082.212-.234.15-.525-.052-.243-.042-.28.087-.355.137-.08.281-.219.324-.314a.365.365 0 0 0-.175-.48.394.394 0 0 0-.181-.033c-.126 0-.207.03-.355.124l-.085.053-.053-.032c-.219-.13-.259-.145-.391-.143a.396.396 0 0 0-.193.032zm.39-2.195c-.373.036-.475.05-.654.086-.291.06-.68.195-.951.328-.94.46-1.589 1.226-1.787 2.114-.04.176-.045.234-.045.53 0 .294.005.357.043.524.264 1.16 1.332 2.017 2.714 2.173.3.033 1.596.033 1.896 0 1.11-.125 2.064-.727 2.493-1.571.114-.226.169-.372.22-.602.039-.167.044-.23.044-.523 0-.297-.005-.355-.045-.531-.288-1.29-1.539-2.304-3.072-2.497a6.873 6.873 0 0 0-.855-.031zm.645.937a3.283 3.283 0 0 1 1.44.514c.223.148.537.458.671.662.166.251.26.508.303.82.02.143.01.251-.043.482-.08.345-.332.705-.672.957a3.115 3.115 0 0 1-.689.348c-.382.122-.632.144-1.525.138-.582-.006-.686-.01-.853-.042-.57-.107-1.022-.334-1.35-.68-.264-.28-.385-.535-.45-.946-.03-.192.025-.509.137-.776.136-.326.488-.73.836-.963.403-.269.934-.46 1.422-.512.187-.02.586-.02.773-.002zm-5.503-11a1.653 1.653 0 0 0-.683.298C5.617.74 5.173 1.666 4.985 2.819c-.07.436-.119 1.04-.119 1.503 0 .544.064 1.24.155 1.721.02.107.031.202.023.208a8.12 8.12 0 0 1-.187.152 5.324 5.324 0 0 0-.949 1.02 5.49 5.49 0 0 0-.94 2.339 6.625 6.625 0 0 0-.023 1.357c.091.78.325 1.438.727 2.04l.13.195-.037.064c-.269.452-.498 1.105-.605 1.732-.084.496-.095.629-.095 1.294 0 .67.009.803.088 1.266.095.555.288 1.143.503 1.534.071.128.243.393.264.407.007.003-.014.067-.046.141a7.405 7.405 0 0 0-.548 1.873c-.062.417-.071.552-.071.991 0 .56.031.832.148 1.279L3.42 24h1.478l-.05-.091c-.297-.552-.325-1.575-.068-2.597.117-.472.25-.819.498-1.296l.148-.29v-.177c0-.165-.003-.184-.057-.293a.915.915 0 0 0-.194-.25 1.74 1.74 0 0 1-.385-.543c-.424-.92-.506-2.286-.208-3.451.124-.486.329-.918.544-1.154a.787.787 0 0 0 .223-.531c0-.195-.07-.355-.224-.522a3.136 3.136 0 0 1-.817-1.729c-.14-.96.114-2.005.69-2.834.563-.814 1.353-1.336 2.237-1.475.199-.033.57-.028.776.01.226.04.367.028.512-.041.179-.085.268-.19.374-.431.093-.215.165-.333.36-.576.234-.29.46-.489.822-.729.413-.27.884-.467 1.352-.561.17-.035.25-.04.569-.04.319 0 .398.005.569.04a4.07 4.07 0 0 1 1.914.997c.117.109.398.457.488.602.034.057.095.177.132.267.105.241.195.346.374.43.14.068.286.082.503.045.343-.058.607-.053.943.016 1.144.23 2.14 1.173 2.581 2.437.385 1.108.276 2.267-.296 3.153-.097.15-.193.27-.333.419-.301.322-.301.722-.001 1.053.493.539.801 1.866.708 3.036-.062.772-.26 1.463-.533 1.854a2.096 2.096 0 0 1-.224.258.916.916 0 0 0-.194.25c-.054.109-.057.128-.057.293v.178l.148.29c.248.476.38.823.498 1.295.253 1.008.231 2.01-.059 2.581a.845.845 0 0 0-.044.098c0 .006.329.009.732.009h.73l.02-.074.036-.134c.019-.076.057-.3.088-.516.029-.217.029-1.016 0-1.258-.11-.875-.295-1.57-.597-2.226-.032-.074-.053-.138-.046-.141.008-.005.057-.074.108-.152.376-.569.607-1.284.724-2.228.031-.26.031-1.378 0-1.628-.083-.645-.182-1.082-.348-1.525a6.083 6.083 0 0 0-.329-.7l-.038-.064.131-.194c.402-.604.636-1.262.727-2.04a6.625 6.625 0 0 0-.024-1.358 5.512 5.512 0 0 0-.939-2.339 5.325 5.325 0 0 0-.95-1.02 8.097 8.097 0 0 1-.186-.152.692.692 0 0 1 .023-.208c.208-1.087.201-2.443-.017-3.503-.19-.924-.535-1.658-.98-2.082-.354-.338-.716-.482-1.15-.455-.996.059-1.8 1.205-2.116 3.01a6.805 6.805 0 0 0-.097.726c0 .036-.007.066-.015.066a.96.96 0 0 1-.149-.078A4.857 4.857 0 0 0 12 3.03c-.832 0-1.687.243-2.456.698a.958.958 0 0 1-.148.078c-.008 0-.015-.03-.015-.066a6.71 6.71 0 0 0-.097-.725C8.997 1.392 8.337.319 7.46.048a2.096 2.096 0 0 0-.585-.041Zm.293 1.402c.248.197.523.759.682 1.388.03.113.06.244.069.292.007.047.026.152.041.233.067.365.098.76.102 1.24l.002.475-.12.175-.118.178h-.278c-.324 0-.646.041-.954.124l-.238.06c-.033.007-.038-.003-.057-.144a8.438 8.438 0 0 1 .016-2.323c.124-.788.413-1.501.696-1.711.067-.05.079-.049.157.013zm9.825-.012c.17.126.358.46.498.888.28.854.36 2.028.212 3.145-.019.14-.024.151-.057.144l-.238-.06a3.693 3.693 0 0 0-.954-.124h-.278l-.119-.178-.119-.175.002-.474c.004-.669.066-1.19.214-1.772.157-.623.434-1.185.68-1.382.078-.062.09-.063.159-.012z';
const LM_STUDIO_GENERIC = 'M4 4h3v16H4Zm5 6h3v10H9Zm5-4h3v14h-3Zm5 8h3v6h-3Z';
const CUSTOM_API_GENERIC =
  'M4 7h10v2H4Zm0 4h6v2H4Zm0 4h10v2H4ZM20 6l-1.5 1.5L20 9h-3v2h3l-1.5 1.5L20 14l4-4Z';

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
  chatgptStatus: ReturnType<typeof vi.fn>;
  chatgptSignIn: ReturnType<typeof vi.fn>;
  chatgptSignOut: ReturnType<typeof vi.fn>;
  chatgptCancelSignIn: ReturnType<typeof vi.fn>;
  chatgptListModels: ReturnType<typeof vi.fn>;
  accessGet: ReturnType<typeof vi.fn>;
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
    modelsError?: unknown;
    allowedUserIds?: readonly string[];
  } = {},
): FakeBridge {
  return {
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
      if (opts.modelsError !== undefined) throw opts.modelsError;
      return { models: PLAN_MODELS };
    }),
    accessGet: vi.fn(async () => ({ userIds: opts.allowedUserIds ?? [] })),
  };
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
    modelsError?: unknown;
    allowedUserIds?: readonly string[];
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

describe('SourcesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
    window.localStorage.removeItem(LLM_CONFIG_STORAGE_KEY);
  });

  it('renders both Local and Cloud sections by default', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="section-local"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="section-cloud"]')).not.toBeNull();
  });

  it('renders all nine provider cards', async () => {
    const { fixture } = await setup();
    const expectedIds = [
      'local-models',
      'ollama',
      'lm-studio',
      'chatgpt-plus',
      'openai-api',
      'google-gemini',
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

  it('each provider card shows its brand mark', async () => {
    const { fixture } = await setup();
    const iconOf = (id: string): string | null =>
      fixture.nativeElement
        .querySelector(`[data-test="card-${id}"] .icon path`)
        ?.getAttribute('d') ?? null;

    expect(iconOf('lm-studio')).toBe(LM_STUDIO_GENERIC);
    expect(iconOf('custom-api')).toBe(CUSTOM_API_GENERIC);
    expect(iconOf('openai-api')).toBe(OPENAI_MARK);
    expect(iconOf('chatgpt-plus')).toBe(OPENAI_MARK);
    expect(iconOf('google-gemini')).toBe(GEMINI_MARK);
    expect(iconOf('openrouter')).toBe(OPENROUTER_MARK);
    expect(iconOf('ollama')).toBe(OLLAMA_MARK);
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
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('Local Models stays Not Connected when runtime is reachable but no local model selected', async () => {
    const { fixture } = await setup();

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('Local Models flips to Connected once a model is selected via the store', async () => {
    const { fixture } = await setup();
    const store = TestBed.inject(LocalModelsStore);
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('Local Models stays Not Connected when both Ollama and LM Studio fail', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
      lmStudioError: new Error('refused'),
    });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-local-models"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
  });

  it('Manage Downloaded Models button is enabled when a local runtime is reachable', async () => {
    const { fixture } = await setup();

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
  });

  it('Manage Downloaded Models button is disabled when neither runtime is reachable', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
      lmStudioError: new Error('refused'),
    });

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  it('clicking Manage Downloaded Models navigates to /sources/local-models', async () => {
    const { fixture } = await setup();
    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigateByUrl');

    const btn = fixture.nativeElement.querySelector(
      '[data-test="action-local-models"]',
    ) as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();

    expect(navSpy).toHaveBeenCalledWith('/sources/local-models');
  });

  it('failed scan marks Ollama as Not Connected', async () => {
    const { fixture } = await setup({
      listError: new OllamaUnreachableError('http://localhost:11434'),
    });

    const badge = fixture.nativeElement.querySelector('[data-test="badge-ollama"]') as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
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

  it('OpenRouter and Custom API cards keep disabled primary actions', async () => {
    const { fixture } = await setup();
    const ids = ['openrouter', 'custom-api'];

    for (const id of ids) {
      const btn = fixture.nativeElement.querySelector(
        `[data-test="action-${id}"]`,
      ) as HTMLButtonElement;
      expect(btn.disabled, `${id} action should be disabled`).toBe(true);
    }
  });

  it('initial LM Studio scan calls the bridge with the default port', async () => {
    const { bridge } = await setup();
    expect(bridge.llmListLmStudioModels).toHaveBeenCalledWith({
      endpoint: 'http://localhost:1234',
    });
  });

  it('successful LM Studio scan marks it Connected (reachable counts)', async () => {
    const { fixture } = await setup({ lmStudioModels: [] });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-lm-studio"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('LM Studio scan failure marks it Not Connected', async () => {
    const { fixture } = await setup({ lmStudioError: new Error('refused') });

    const badge = fixture.nativeElement.querySelector(
      '[data-test="badge-lm-studio"]',
    ) as HTMLElement;
    expect(badge.textContent ?? '').toContain('Not Connected');
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

  it('the three states: a card with no key offers Connect; a stored key shows Connected with Use, Disconnect and Change key; the provider in use shows In use with its model, Change model and Disconnect', async () => {
    const empty = await setup();
    expect(text(empty.fixture, '[data-test="badge-openai-api"]')).toContain('Not Connected');
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
    expect(openAiBadge).toContain('Connected');
    expect(openAiBadge).not.toContain('Not Connected');
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Use', 'Disconnect', 'Change key']);
    expect(
      query<HTMLElement>(fixture, '[data-test="disconnect-openai-api"]').getAttribute('aria-label'),
    ).toBe('Disconnect OpenAI API');

    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('In use');
    expect(text(fixture, '[data-test="card-google-gemini"]')).toContain('gemini-2.5-pro');
    expect(buttonsOf(fixture, 'google-gemini')).toEqual(['Change model', 'Disconnect']);
    expect(
      query<HTMLElement>(fixture, '[data-test="change-model-google-gemini"]').getAttribute(
        'aria-label',
      ),
    ).toBe('Change Google Gemini model');
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
    expect(text(fixture, '[data-test="badge-openai-api"]')).not.toContain('Not Connected');
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Use', 'Disconnect', 'Change key']);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain('Connected.');
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
      'Connected. Restarting Scout to use Gemini.',
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
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not Connected');
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
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not Connected');
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

  it('Use while Scout is running switches the provider and restarts Scout (stop, then start)', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: { 'llm.openai.api-key': true },
    });

    await click(fixture, '[data-test="use-openai-api"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-4o-mini' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Restarting Scout to use OpenAI.',
    );
  });

  it('Use while Scout is not running leaves it stopped and says the provider applies on next start', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'idle',
      vaultHasByKey: { 'llm.openai.api-key': true },
    });

    await click(fixture, '[data-test="use-openai-api"]');

    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Scout will use OpenAI the next time it starts.',
    );
  });

  it('a failed restart after Use shows the error in an alert and keeps the provider in use', async () => {
    const { fixture } = await setup({
      workerState: 'running',
      workerStartError: new Error('Worker binary not found'),
      vaultHasByKey: { 'llm.openai.api-key': true },
    });

    await click(fixture, '[data-test="use-openai-api"]');

    expect(text(fixture, '[data-test="card-openai-api"] [role="alert"]')).toContain(
      'Worker binary not found',
    );
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Restart Scout from the status screen to use OpenAI.',
    );
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
  });

  it('Change model does not show the previous message while the worker status is pending', async () => {
    const { fixture, bridge } = await setup({
      workerState: 'idle',
      vaultHasByKey: { 'llm.openai.api-key': true },
    });
    await click(fixture, '[data-test="use-openai-api"]');
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain('the next time it starts');

    let resolveStatus: (value: { state: string; restarts: number }) => void = () => undefined;
    bridge.workerStatus.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveStatus = resolve;
        }),
    );
    query<HTMLButtonElement>(fixture, '[data-test="change-model-openai-api"]').click();
    fixture.detectChanges();
    const modelField = query<HTMLInputElement>(fixture, '[data-test="model-openai-api"]');
    expect(modelField.value).toBe('gpt-4o-mini');
    type(modelField, 'gpt-4o');
    await click(fixture, '[data-test="save-model-openai-api"]');

    expect(bridge.vaultStore.mock.calls.at(-2)?.[0]).toEqual({
      key: 'llm.cloud-model',
      value: 'gpt-4o',
    });
    expect(text(fixture, '[data-test="saved-openai-api"]')).not.toContain(
      'the next time it starts',
    );

    resolveStatus({ state: 'running', restarts: 0 });
    await settle(fixture);
    await settle(fixture);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Restarting Scout to use OpenAI.',
    );
    expect(text(fixture, '[data-test="card-openai-api"]')).toContain('gpt-4o');
  });

  it('there is a way to disconnect: Disconnect on the provider in use deletes its key, switches Scout to the other connected provider and restarts it', async () => {
    useGeminiWithBothKeys();
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: BOTH_KEYS,
      publicValues: GEMINI_IN_VAULT,
    });

    await click(fixture, '[data-test="disconnect-google-gemini"]');

    expect(bridge.vaultDelete.mock.calls.map((c) => (c[0] as { key: string }).key)).toContain(
      'llm.gemini.api-key',
    );
    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-4o-mini' },
      { key: 'llm.provider', value: 'OpenAI' },
    ]);
    expect(text(fixture, '[data-test="saved-google-gemini"]')).toContain(
      'Disconnected. Scout now answers with OpenAI.',
    );
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(text(fixture, '[data-test="badge-google-gemini"]')).toContain('Not Connected');
    expect(buttonsOf(fixture, 'google-gemini')).toEqual(['Connect']);
  });

  it('with no localStorage mirror the vault decides: OpenAI in the vault shows In use, and Disconnect of the last connected provider writes llm.provider=None', async () => {
    const { fixture, bridge } = await setup({
      vaultHasByKey: { 'llm.openai.api-key': true },
      publicValues: { 'llm.provider': 'OpenAI', 'llm.cloud-model': 'gpt-4o' },
    });

    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('In use');
    expect(text(fixture, '[data-test="card-openai-api"]')).toContain('gpt-4o');

    await click(fixture, '[data-test="disconnect-openai-api"]');

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

    await click(fixture, '[data-test="disconnect-openai-api"]');

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
    expect(buttonsOf(fixture, 'openai-api')).toEqual(['Use', 'Disconnect', 'Change key']);
  });

  it('Disconnect on a provider that is not in use deletes only its key and does not restart a running Scout', async () => {
    useGeminiWithBothKeys();
    const { fixture, bridge } = await setup({
      workerState: 'running',
      vaultHasByKey: BOTH_KEYS,
      publicValues: GEMINI_IN_VAULT,
    });

    await click(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultDelete.mock.calls.map((c) => c[0])).toEqual([{ key: 'llm.openai.api-key' }]);
    expect(storeKeysWritten(bridge)).not.toContain('llm.provider');
    expect(bridge.workerStop).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain('Disconnected.');
    expect(text(fixture, '[data-test="badge-openai-api"]')).toContain('Not Connected');
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

    await click(fixture, '[data-test="disconnect-google-gemini"]');

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

  async function useChatGptCard(
    opts: Parameters<typeof setup>[0],
  ): Promise<{ fixture: ComponentFixture<SourcesComponent>; bridge: FakeBridge }> {
    const result = await setup({ chatgpt: CHATGPT_IN, ...opts });
    query<HTMLButtonElement>(result.fixture, '[data-test="chatgpt-use"]').click();
    await settle(result.fixture);
    await settle(result.fixture);
    return result;
  }

  it('the ChatGPT Plus card, not connected, offers "Continue with ChatGPT" and describes plan usage', async () => {
    const { fixture } = await setup();
    const card = query<HTMLElement>(fixture, '[data-test="card-chatgpt-plus"]');
    const signIn = query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-in"]');

    expect(signIn.disabled).toBe(false);
    expect(signIn.textContent?.trim()).toBe('Continue with ChatGPT');
    expect(query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent).toContain(
      'Not Connected',
    );
    expect(card.textContent).toContain(
      "Sign in with your ChatGPT Plus or Pro plan. Usage counts against your plan's limits.",
    );
    expect(card.textContent).not.toContain('Not supported');
  });

  it('"Continue with ChatGPT" signs in through the main process and the card shows the account, Use and Sign out', async () => {
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
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-use"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-out"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-in"]')).toBeNull();
  });

  it('a failed sign-in stays Not Connected and shows the reason in an alert', async () => {
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

  it('signed in without plan usage: no Use button, and the card says Scout cannot answer through it', async () => {
    const { fixture } = await setup({ chatgpt: { ...CHATGPT_IN, planUsageGranted: false } });

    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-use"]')).toBeNull();
    expect(
      query<HTMLElement>(fixture, '[data-test="chatgpt-no-plan-usage"]').textContent,
    ).toContain('OpenAI did not grant plan usage to Scout');
  });

  it('Use writes the ChatGptPlan provider and the default model, then restarts a running Scout', async () => {
    const { fixture, bridge } = await useChatGptCard({ workerState: 'running' });

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent).toContain(
      'Restarting Scout',
    );
  });

  it('in use, the card shows the model and Sign out, and no Use button', async () => {
    const { fixture } = await useChatGptCard({ workerState: 'idle' });

    expect(
      query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent?.trim(),
    ).toBe('In use');
    expect(query<HTMLElement>(fixture, '[data-test="chatgpt-model"]').textContent).toContain(
      'gpt-5.6-luna',
    );
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-use"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="chatgpt-sign-out"]')).not.toBeNull();
  });

  it('Sign out while ChatGPT is in use moves Scout to no provider (never Ollama) and restarts a running Scout', async () => {
    const { fixture, bridge } = await useChatGptCard({ workerState: 'running' });
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

  it('Cancel while waiting for the browser aborts the sign-in and returns the card to Not Connected without an error', async () => {
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
      'Not Connected',
    );
  });

  function modelSelect(fixture: ComponentFixture<SourcesComponent>): HTMLSelectElement {
    return query<HTMLSelectElement>(fixture, '[data-test="chatgpt-model-select"]');
  }

  async function pickModel(
    fixture: ComponentFixture<SourcesComponent>,
    slug: string,
  ): Promise<void> {
    const select = modelSelect(fixture);
    select.value = slug;
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    await settle(fixture);
  }

  it('signed in, the Model list shows the plan models by display name with the default preselected', async () => {
    const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN });
    await settle(fixture);

    expect(bridge.chatgptListModels).toHaveBeenCalledTimes(1);
    const options = Array.from(modelSelect(fixture).options).map((o) => [
      o.value,
      o.textContent?.trim(),
    ]);
    expect(options).toEqual([
      ['gpt-6.1-sol', 'GPT-6.1 Sol'],
      ['gpt-5.6-luna', 'GPT-5.6 Luna'],
    ]);
    expect(modelSelect(fixture).value).toBe('gpt-5.6-luna');
  });

  it('Use applies the model picked in the list', async () => {
    const { fixture, bridge } = await setup({ chatgpt: CHATGPT_IN });
    await settle(fixture);
    await pickModel(fixture, 'gpt-6.1-sol');

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-use"]').click();
    await settle(fixture);
    await settle(fixture);

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-6.1-sol' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
  });

  it('choosing another model while in use writes llm.cloud-model and restarts a running Scout', async () => {
    const { fixture, bridge } = await useChatGptCard({ workerState: 'running' });
    bridge.vaultStore.mockClear();
    bridge.workerStop.mockClear();
    bridge.workerStart.mockClear();

    await pickModel(fixture, 'gpt-6.1-sol');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toContainEqual({
      key: 'llm.cloud-model',
      value: 'gpt-6.1-sol',
    });
    expect(bridge.workerStop).toHaveBeenCalledTimes(1);
    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(bridge.workerStop.mock.invocationCallOrder[0]).toBeLessThan(
      bridge.workerStart.mock.invocationCallOrder[0] ?? 0,
    );
    expect(query<HTMLElement>(fixture, '[data-test="chatgpt-model"]').textContent).toContain(
      'gpt-6.1-sol',
    );
  });

  it('when the model list fails, the card says so in an alert and keeps the current model', async () => {
    const { fixture } = await setup({
      chatgpt: CHATGPT_IN,
      modelsError: new Error('Could not reach OpenAI to list ChatGPT models.'),
    });
    await settle(fixture);

    expect(
      query<HTMLElement>(fixture, '[data-test="chatgpt-models-error"]').textContent?.trim(),
    ).toBe('Could not load the ChatGPT model list.');
    expect(
      query<HTMLElement>(fixture, '[data-test="chatgpt-models-error"]').getAttribute('role'),
    ).toBe('alert');
    expect(modelSelect(fixture).value).toBe('gpt-5.6-luna');
  });

  it('Sign out whose remote revocation was not confirmed says so', async () => {
    const { fixture } = await setup({ chatgpt: CHATGPT_IN, revocationConfirmed: false });

    query<HTMLButtonElement>(fixture, '[data-test="chatgpt-sign-out"]').click();
    await settle(fixture);

    expect(query<HTMLElement>(fixture, '[data-test="saved-chatgpt-plus"]').textContent).toContain(
      'OpenAI did not confirm the remote sign-out',
    );
    expect(query<HTMLElement>(fixture, '[data-test="badge-chatgpt-plus"]').textContent).toContain(
      'Not Connected',
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
    ).toBe('Signed out of ChatGPT. Scout now answers with OpenAI.');
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

    await click(fixture, '[data-test="disconnect-openai-api"]');

    expect(bridge.vaultStore.mock.calls.map((c) => c[0])).toEqual([
      { key: 'llm.cloud-model', value: 'gpt-5.6-luna' },
      { key: 'llm.provider', value: 'ChatGptPlan' },
    ]);
    expect(text(fixture, '[data-test="saved-openai-api"]')).toContain(
      'Disconnected. Scout now answers with ChatGPT.',
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
      "Read-only guard: TypeSafe's Jev model checks each request, with the recent conversation, and refuses ones that ask Scout to change something.",
    );
    expect(query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]').textContent).toContain(
      'Not Connected',
    );
    const action = query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]');
    expect(action.textContent).toContain('Add API Key');

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
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
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
    expect(badge.textContent ?? '').toContain('Connected');
    expect(badge.textContent ?? '').not.toContain('Not Connected');
  });

  it('the TypeSafe (Jev) card stays Not Connected on load when only a cloud LLM key is stored', async () => {
    const { fixture } = await setup({ vaultKeys: ['llm.api-key'] });

    expect(query<HTMLElement>(fixture, '[data-test="badge-typesafe-jev"]').textContent).toContain(
      'Not Connected',
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
    await settle(fixture);
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

  it('disconnecting TypeSafe (Jev) deletes only the guard key, turns the badge Not Connected and says the guard is off', async () => {
    const { fixture, bridge } = await setup({
      vaultKeys: ['guard.typesafe-api-key', 'llm.api-key'],
    });

    await disconnectGuard(fixture);

    expect(bridge.vaultDelete.mock.calls.map((c) => c[0])).toEqual([
      { key: 'guard.typesafe-api-key' },
    ]);
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(guardBadge(fixture)).toContain('Not Connected');
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

  it('a failed TypeSafe (Jev) key save shows the error in an alert and the badge stays Not Connected', async () => {
    const { fixture } = await setup({ vaultStoreError: new Error('keychain locked') });
    query<HTMLButtonElement>(fixture, '[data-test="action-typesafe-jev"]').click();
    fixture.detectChanges();
    type(query<HTMLInputElement>(fixture, '[data-test="api-key-typesafe-jev"]'), 'ts-typed-key');

    query<HTMLButtonElement>(fixture, '[data-test="save-typesafe-jev"]').click();
    await settle(fixture);

    const alert = query<HTMLElement>(fixture, '[data-test="key-form-typesafe-jev"] [role="alert"]');
    expect(alert.textContent).toContain('keychain locked');
    expect(guardBadge(fixture)).toContain('Not Connected');
    expect(fixture.nativeElement.textContent).not.toContain('ts-typed-key');
  });

  it('when the vault check fails on load the TypeSafe (Jev) card says so in an alert and still offers Disconnect', async () => {
    const { fixture } = await setup({ vaultHasError: new Error('vault unavailable') });

    expect(guardBadge(fixture)).toContain('Not Connected');
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
    expect(guardBadge(fixture)).not.toContain('Not Connected');
    expect(guardBadge(fixture)).toContain('Connected');
    expect(bridge.workerStop).not.toHaveBeenCalled();
  });
});
