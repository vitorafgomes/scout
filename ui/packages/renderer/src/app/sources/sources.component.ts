import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';

import {
  getLlmProviderDescriptor,
  type LlmProviderDescriptor,
  type LlmProviderId,
} from '@scout/shared';

import { LlmConfigStore } from '../core/llm-config.store';
import { LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';

import {
  LlmProviderDialogComponent,
  type LlmProviderDialogSubmit,
} from './llm-provider-dialog.component';

type FilterKind = 'all' | 'local' | 'cloud';
type CardKind = 'local' | 'cloud';
type ConnectionState = 'connected' | 'not-connected' | 'unknown';

interface CardModel {
  readonly id: string;
  readonly kind: CardKind;
  readonly title: string;
  readonly description: string;
  readonly iconPath: string;
  readonly primaryAction: string;
  readonly secondaryLabel: string | null;
  readonly secondaryUrl: string | null;
  readonly primaryIsScan: boolean;
  readonly disabled: boolean;
}

const DEFAULT_OLLAMA_ENDPOINT = 'http://localhost:11434';
const DEFAULT_LM_STUDIO_ENDPOINT = 'http://localhost:1234';

const ICONS = Object.freeze({
  localModels:
    'M12 3c-2.5 0-4 1.8-4 4v1H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7c0-2.2-1.5-4-4-4Zm-2 5V7a2 2 0 1 1 4 0v1Zm-1 5a1 1 0 1 1 2 0 1 1 0 0 1-2 0Zm4 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0Z',
  ollama:
    'M12 2c-3 0-5 2-5 5v3c-2 0-3 1-3 3v7h4v-4h8v4h4v-7c0-2-1-3-3-3V7c0-3-2-5-5-5Zm-3 5c0-2 1-3 3-3s3 1 3 3v3H9V7Z',
  lmStudio: 'M4 4h3v16H4Zm5 6h3v10H9Zm5-4h3v14h-3Zm5 8h3v6h-3Z',
  openai: 'M12 2 13.5 9 21 11 13.5 13 12 20 10.5 13 3 11 10.5 9Z',
  gemini: 'M12 2 14 10 22 12 14 14 12 22 10 14 2 12 10 10Z',
  openRouter: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 4 4 4-4 4-4-4Zm0 4 4 4-4 4-4-4Z',
  azureOpenai: 'M5 20 10 4h4l5 16h-3.2l-1-3.4H9.2L8.2 20Zm4.9-6h4.2L12 7.5Z',
  customApi: 'M4 7h10v2H4Zm0 4h6v2H4Zm0 4h10v2H4ZM20 6l-1.5 1.5L20 9h-3v2h3l-1.5 1.5L20 14l4-4Z',
});

@Component({
  selector: 'sc-sources',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LlmProviderDialogComponent],
  template: `
    <main class="page">
      @if (showRestartBanner()) {
        <div class="banner banner--info" role="status" data-test="llm-restart-banner">
          <span>Restart the worker to apply the new LLM provider.</span>
          <button
            type="button"
            class="banner-action"
            data-test="llm-restart-now"
            (click)="restartWorker()"
          >
            Restart now
          </button>
        </div>
      }

      <header class="header">
        <div class="header-left">
          <h1>AI Sources</h1>
          <p class="lede">
            Connect your favorite cloud APIs or run models privately on your device. Configure
            authentication and local servers below.
          </p>
        </div>
        <div class="filter" role="tablist" aria-label="Filter providers">
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'all'"
            [class.filter-chip--active]="filter() === 'all'"
            class="filter-chip"
            data-test="filter-all"
            (click)="setFilter('all')"
          >
            All
          </button>
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'local'"
            [class.filter-chip--active]="filter() === 'local'"
            class="filter-chip"
            data-test="filter-local"
            (click)="setFilter('local')"
          >
            Local
          </button>
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'cloud'"
            [class.filter-chip--active]="filter() === 'cloud'"
            class="filter-chip"
            data-test="filter-cloud"
            (click)="setFilter('cloud')"
          >
            Cloud
          </button>
        </div>
      </header>

      @if (showLocal()) {
        <section data-test="section-local">
          <h2 class="section-title"><span class="section-rule"></span>Local &amp; On-Device</h2>
          <div class="grid">
            @for (card of localCards; track card.id) {
              <article class="card" [attr.data-test]="'card-' + card.id">
                <div class="card-head">
                  <div class="icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="22" height="22">
                      <path [attr.d]="card.iconPath" fill="currentColor" />
                    </svg>
                  </div>
                  <span
                    class="badge"
                    [class.badge--ok]="stateOf(card.id) === 'connected'"
                    [class.badge--off]="stateOf(card.id) !== 'connected'"
                    [attr.data-test]="'badge-' + card.id"
                  >
                    <span class="badge-dot" aria-hidden="true"></span>
                    {{ stateOf(card.id) === 'connected' ? 'Connected' : 'Not Connected' }}
                  </span>
                </div>
                <h3 class="card-title">{{ card.title }}</h3>
                <p class="card-desc">{{ card.description }}</p>
                <div class="card-actions">
                  @if (card.primaryIsScan) {
                    <button
                      type="button"
                      class="action action--scan"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="isCardDisabled(card) || scanning() === card.id"
                      (click)="onPrimaryAction(card)"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M10 4a6 6 0 1 0 3.74 10.68l4.29 4.3 1.41-1.42-4.29-4.29A6 6 0 0 0 10 4Zm0 2a4 4 0 1 1 0 8 4 4 0 0 1 0-8Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                    </button>
                  } @else {
                    <button
                      type="button"
                      class="action action--primary"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="isCardDisabled(card)"
                      [title]="
                        isCardDisabled(card) && card.id === 'local-models'
                          ? 'Install Ollama or LM Studio to manage downloaded models'
                          : ''
                      "
                      (click)="onPrimaryAction(card)"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M12 3a1 1 0 0 1 1 1v9.59l3.3-3.3 1.4 1.42L12 17.42 6.3 11.7l1.4-1.41L11 13.6V4a1 1 0 0 1 1-1Zm-7 16h14v2H5Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                    </button>
                  }
                  @if (card.secondaryLabel && card.secondaryUrl) {
                    <button
                      type="button"
                      class="secondary"
                      [attr.data-test]="'secondary-' + card.id"
                      (click)="openExternal(card.secondaryUrl)"
                    >
                      {{ card.secondaryLabel }}
                      <svg viewBox="0 0 24 24" width="11" height="11" aria-hidden="true">
                        <path
                          d="M14 3h7v7h-2V6.41l-9.29 9.3-1.42-1.42L17.59 5H14Zm-9 4h6v2H7v10h10v-6h2v8H5Z"
                          fill="currentColor"
                        />
                      </svg>
                    </button>
                  }
                </div>
              </article>
            }
          </div>
        </section>
      }
      @if (showCloud()) {
        <section data-test="section-cloud">
          <h2 class="section-title"><span class="section-rule"></span>Cloud APIs</h2>
          <div class="grid">
            @for (card of cloudCards; track card.id) {
              <article class="card" [attr.data-test]="'card-' + card.id">
                <div class="card-head">
                  <div class="icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="22" height="22">
                      <path [attr.d]="card.iconPath" fill="currentColor" />
                    </svg>
                  </div>
                  <span
                    class="badge"
                    [class.badge--ok]="cloudBadgeKind(card.id) === 'active'"
                    [class.badge--configured]="cloudBadgeKind(card.id) === 'configured'"
                    [class.badge--off]="cloudBadgeKind(card.id) === 'not-connected'"
                    [attr.data-test]="'badge-' + card.id"
                  >
                    <span class="badge-dot" aria-hidden="true"></span>
                    {{ cloudBadgeLabel(card.id) }}
                  </span>
                </div>
                <h3 class="card-title">{{ card.title }}</h3>
                <p class="card-desc">{{ card.description }}</p>
                <div class="card-actions">
                  <button
                    type="button"
                    class="action action--primary action--wide"
                    [attr.data-test]="'action-' + card.id"
                    [disabled]="card.disabled"
                    [title]="card.disabled ? 'Coming soon' : ''"
                    (click)="onCloudPrimaryClick(card)"
                  >
                    <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                      <path
                        d="M7 14a5 5 0 1 1 4.9-6h7.6l2 2-3 3-2-2-2 2-2-2-1.6 1.6A5 5 0 0 1 7 14Zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"
                        fill="currentColor"
                      />
                    </svg>
                    <span>{{ card.primaryAction }}</span>
                    <svg
                      class="chevron"
                      viewBox="0 0 24 24"
                      width="14"
                      height="14"
                      aria-hidden="true"
                    >
                      <path d="m9 6 6 6-6 6-1.4-1.4L12.2 12 7.6 7.4Z" fill="currentColor" />
                    </svg>
                  </button>
                  @if (cloudShowSetActive(card.id)) {
                    <button
                      type="button"
                      class="secondary"
                      [attr.data-test]="'set-active-' + card.id"
                      (click)="setActiveProvider(card.id)"
                    >
                      Set active
                    </button>
                  }
                </div>
              </article>
            }
          </div>
        </section>
      }

      @if (activeDialog(); as dlg) {
        <sc-llm-provider-dialog
          [descriptor]="dlg"
          [existingValues]="llmConfig.configById()[dlg.id] ?? {}"
          [secretPresent]="llmConfig.secretPresentById()[dlg.id] === true"
          (submit)="onLlmDialogSubmit($event)"
          (cancel)="closeLlmDialog()"
        />
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: var(--sc-text);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .page {
        padding: 32px 40px 48px;
        max-width: 1400px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 28px;
      }
      .header {
        display: flex;
        justify-content: space-between;
        gap: 24px;
        align-items: flex-start;
      }
      .header-left {
        display: flex;
        flex-direction: column;
        gap: 8px;
        max-width: 720px;
      }
      h1 {
        font-size: 26px;
        margin: 0;
        font-weight: 700;
      }
      .lede {
        margin: 0;
        color: var(--sc-text-muted);
        font-size: 13px;
        line-height: 1.5;
      }
      .filter {
        background: var(--sc-surface);
        border: 1px solid var(--sc-border);
        border-radius: 8px;
        padding: 4px;
        display: inline-flex;
        gap: 2px;
      }
      .filter-chip {
        background: transparent;
        color: var(--sc-text-muted);
        border: none;
        border-radius: 6px;
        padding: 6px 16px;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
        font-family: inherit;
      }
      .filter-chip:hover {
        color: var(--sc-text);
      }
      .filter-chip--active {
        background: var(--sc-border-strong);
        color: var(--sc-text);
      }
      .section-title {
        font-size: 12px;
        font-weight: 500;
        color: var(--sc-text-muted);
        text-transform: none;
        letter-spacing: 0.02em;
        margin: 0 0 14px;
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .section-rule {
        display: inline-block;
        width: 22px;
        height: 1px;
        background: var(--sc-disabled-bg);
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
        gap: 16px;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-border);
        border-radius: 12px;
        padding: 18px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        min-height: 220px;
      }
      .card-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
      }
      .icon {
        width: 38px;
        height: 38px;
        border-radius: 8px;
        background: var(--sc-surface-3);
        display: flex;
        align-items: center;
        justify-content: center;
        color: var(--sc-text);
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 11px;
        font-weight: 600;
        padding: 3px 8px;
        border-radius: 999px;
        border: 1px solid transparent;
      }
      .badge-dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: currentColor;
      }
      .badge--ok {
        color: var(--sc-success-text);
        background: color-mix(in srgb, var(--sc-success-text) 8%, transparent);
        border-color: color-mix(in srgb, var(--sc-success-text) 25%, transparent);
      }
      .badge--off {
        color: var(--sc-text-muted);
        background: color-mix(in srgb, var(--sc-text-muted) 8%, transparent);
        border-color: color-mix(in srgb, var(--sc-text-muted) 20%, transparent);
      }
      .badge--configured {
        color: #d4a24a;
        background: rgba(212, 162, 74, 0.08);
        border-color: rgba(212, 162, 74, 0.25);
      }
      .card-title {
        font-size: 16px;
        font-weight: 700;
        margin: 0;
      }
      .card-desc {
        font-size: 12px;
        line-height: 1.5;
        color: var(--sc-text-muted);
        margin: 0;
        flex: 1;
      }
      .card-actions {
        display: flex;
        flex-direction: column;
        gap: 8px;
        align-items: stretch;
      }
      .action {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        justify-content: center;
        font-family: inherit;
        font-size: 13px;
        font-weight: 500;
        border-radius: 8px;
        cursor: pointer;
        padding: 9px 12px;
        border: 1px solid transparent;
      }
      .action--scan {
        background: var(--sc-surface-3);
        color: var(--sc-text);
        border-color: var(--sc-border-strong);
        justify-content: flex-start;
      }
      .action--scan:hover:enabled {
        background: var(--sc-border);
      }
      .action--primary {
        background: var(--sc-surface-3);
        color: var(--sc-text);
        border-color: var(--sc-border-strong);
      }
      .action--wide {
        justify-content: space-between;
      }
      .action--wide .chevron {
        margin-left: auto;
      }
      .action:disabled {
        opacity: 0.55;
        cursor: not-allowed;
      }
      .secondary {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        font-size: 12px;
        color: var(--sc-info);
        align-self: center;
        background: transparent;
        border: none;
        padding: 4px 0;
        cursor: pointer;
        font-family: inherit;
      }
      .secondary:hover {
        text-decoration: underline;
      }
      .banner {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        padding: 10px 14px;
        border-radius: 6px;
        font-size: 13px;
      }
      .banner--info {
        background: #1e2e45;
        color: #e4e8f0;
        border: 1px solid #5a8fd4;
      }
      .banner-action {
        background: #5a8fd4;
        color: #0f1117;
        border: none;
        border-radius: 6px;
        padding: 6px 12px;
        font-size: 13px;
        font-weight: 600;
        cursor: pointer;
      }
    `,
  ],
})
export class SourcesComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly localModelsStore = inject(LocalModelsStore);
  readonly llmConfig = inject(LlmConfigStore);

  readonly filter = signal<FilterKind>('all');
  readonly scanning = signal<string | null>(null);
  readonly activeDialog = signal<LlmProviderDescriptor | null>(null);
  readonly showRestartBanner = signal(false);
  private readonly states = signal<Readonly<Record<string, ConnectionState>>>({
    ollama: 'unknown',
    'lm-studio': 'not-connected',
  });

  // Cloud card id -> catalog provider id. Cards without an entry here (e.g.
  // chatgpt-plus, custom-api) do not open the generic provider dialog.
  private readonly cardToProviderId: Readonly<Partial<Record<string, LlmProviderId>>> =
    Object.freeze({
      'openai-api': 'openai',
      'google-gemini': 'gemini',
      openrouter: 'openrouter',
      'azure-openai': 'azure-openai',
    });

  readonly runtimeReachable = computed<boolean>(
    () => this.stateOf('ollama') === 'connected' || this.stateOf('lm-studio') === 'connected',
  );

  readonly hasSelectedLocalModel = computed<boolean>(() => {
    const sel = this.localModelsStore.selected();
    return sel.transcription !== null || sel.analysis !== null;
  });

  readonly localModelsConnected = computed<boolean>(
    () => this.runtimeReachable() && this.hasSelectedLocalModel(),
  );

  readonly showLocal = computed<boolean>(() => this.filter() !== 'cloud');
  readonly showCloud = computed<boolean>(() => this.filter() !== 'local');

  readonly localCards: readonly CardModel[] = Object.freeze([
    {
      id: 'local-models',
      kind: 'local',
      title: 'Local Models',
      description:
        'Download and run AI models natively inside the app. Private, completely offline, and absolutely no API keys required.',
      iconPath: ICONS.localModels,
      primaryAction: 'Manage Downloaded Models',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
    {
      id: 'ollama',
      kind: 'local',
      title: 'Ollama',
      description:
        'Run powerful AI models locally on your machine. Private, fast, and completely offline with no API keys required.',
      iconPath: ICONS.ollama,
      primaryAction: 'Scan for Ollama Server',
      secondaryLabel: 'Download Ollama',
      secondaryUrl: 'https://ollama.com/download',
      primaryIsScan: true,
      disabled: false,
    },
    {
      id: 'lm-studio',
      kind: 'local',
      title: 'LM Studio',
      description:
        'Discover, download, and run any compatible local LLM. Start the local server in LM Studio to connect automatically.',
      iconPath: ICONS.lmStudio,
      primaryAction: 'Scan for LM Studio',
      secondaryLabel: 'Download LM Studio',
      secondaryUrl: 'https://lmstudio.ai',
      primaryIsScan: true,
      disabled: false,
    },
  ]);

  readonly cloudCards: readonly CardModel[] = Object.freeze([
    {
      id: 'chatgpt-plus',
      kind: 'cloud',
      title: 'ChatGPT Plus',
      description:
        'Sign in with your ChatGPT Plus or Pro subscription to access advanced OpenAI models without API keys.',
      iconPath: ICONS.openai,
      primaryAction: 'Sign in with ChatGPT',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
    {
      id: 'openai-api',
      kind: 'cloud',
      title: 'OpenAI API',
      description:
        'Provide your developer API key to build with GPT-4o, GPT-4 Turbo, and DALL·E models directly.',
      iconPath: ICONS.openai,
      primaryAction: 'Configure API Key',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'google-gemini',
      kind: 'cloud',
      title: 'Google Gemini',
      description: 'Fast, multimodal AI models from Google including Gemini 2.5 Pro and Flash.',
      iconPath: ICONS.gemini,
      primaryAction: 'Add API Key',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'openrouter',
      kind: 'cloud',
      title: 'OpenRouter',
      description:
        'A unified interface to access hundreds of AI models through a single API connection.',
      iconPath: ICONS.openRouter,
      primaryAction: 'Configure OpenRouter',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'azure-openai',
      kind: 'cloud',
      title: 'Azure OpenAI',
      description:
        'Use your Azure OpenAI resource deployments for chat and embeddings under your own tenant.',
      iconPath: ICONS.azureOpenai,
      primaryAction: 'Configure Azure OpenAI',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'custom-api',
      kind: 'cloud',
      title: 'Custom API',
      description:
        'Connect to any OpenAI-compatible API endpoint. Works with self-hosted models, vLLM, LocalAI, and more.',
      iconPath: ICONS.customApi,
      primaryAction: 'Configure Endpoint',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
  ]);

  ngOnInit(): void {
    void this.onScan('ollama');
    void this.onScan('lm-studio');
    void this.llmConfig.loadFromVault();
  }

  cloudProviderId(cardId: string): LlmProviderId | null {
    return this.cardToProviderId[cardId] ?? null;
  }

  cloudBadgeKind(cardId: string): 'active' | 'configured' | 'not-connected' {
    const id = this.cloudProviderId(cardId);
    if (id === null) return 'not-connected';
    if (!this.llmConfig.isProviderConfigured(id)) return 'not-connected';
    return this.llmConfig.activeProviderId() === id ? 'active' : 'configured';
  }

  cloudBadgeLabel(cardId: string): string {
    switch (this.cloudBadgeKind(cardId)) {
      case 'active':
        return 'Active';
      case 'configured':
        return 'Configured';
      case 'not-connected':
        return 'Not Connected';
    }
  }

  cloudShowSetActive(cardId: string): boolean {
    return this.cloudBadgeKind(cardId) === 'configured';
  }

  onCloudPrimaryClick(card: CardModel): void {
    if (card.disabled) return;
    const id = this.cloudProviderId(card.id);
    if (id === null) return;
    this.activeDialog.set(getLlmProviderDescriptor(id));
  }

  closeLlmDialog(): void {
    this.activeDialog.set(null);
  }

  async onLlmDialogSubmit(payload: LlmProviderDialogSubmit): Promise<void> {
    await this.llmConfig.saveProvider(payload.id, payload.values);
    await this.llmConfig.setActive(payload.id);
    this.closeLlmDialog();
    this.showRestartBanner.set(true);
  }

  async setActiveProvider(cardId: string): Promise<void> {
    const id = this.cloudProviderId(cardId);
    if (id === null) return;
    await this.llmConfig.setActive(id);
    this.showRestartBanner.set(true);
  }

  async restartWorker(): Promise<void> {
    try {
      await this.bridge.workerStop();
    } catch {
      // Restart is best-effort; supervisor events drive UI state elsewhere.
    }
    try {
      await this.bridge.workerStart();
    } catch {
      // Same as above.
    }
    this.showRestartBanner.set(false);
  }

  setFilter(kind: FilterKind): void {
    this.filter.set(kind);
  }

  stateOf(id: string): ConnectionState {
    if (id === 'local-models') {
      return this.localModelsConnected() ? 'connected' : 'not-connected';
    }
    return this.states()[id] ?? 'unknown';
  }

  isCardDisabled(card: CardModel): boolean {
    if (card.id === 'local-models') {
      return !this.runtimeReachable();
    }
    return card.disabled;
  }

  onPrimaryAction(card: CardModel): void {
    if (card.id === 'local-models') {
      if (this.runtimeReachable()) {
        void this.router.navigateByUrl('/sources/local-models');
      }
      return;
    }
    if (card.primaryIsScan) {
      void this.onScan(card.id);
    }
  }

  async onScan(id: string): Promise<void> {
    if (id !== 'ollama' && id !== 'lm-studio') return;
    this.scanning.set(id);
    try {
      if (id === 'ollama') {
        await this.bridge.llmListOllamaModels({ endpoint: DEFAULT_OLLAMA_ENDPOINT });
      } else {
        await this.bridge.llmListLmStudioModels({ endpoint: DEFAULT_LM_STUDIO_ENDPOINT });
      }
      // Connected = endpoint reachable. A reachable server with zero
      // pulled models still counts as installed and connected.
      this.patchState(id, 'connected');
    } catch {
      this.patchState(id, 'not-connected');
    } finally {
      this.scanning.set(null);
    }
  }

  openExternal(url: string): void {
    void this.bridge.appOpenExternal({ url }).catch(() => {
      // Silent: the renderer cannot do better than hand off to the main process.
    });
  }

  private patchState(id: string, state: ConnectionState): void {
    this.states.update((prev) => ({ ...prev, [id]: state }));
  }
}
