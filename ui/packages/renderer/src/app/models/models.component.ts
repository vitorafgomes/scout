import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import type { LlmModel } from '@scout/shared';

import { type ActiveCloudProvider, LlmConfigStore } from '../core/llm-config.store';
import { LogStore } from '../core/log.store';
import { type LocalModelCatalogEntry, ollamaTagForCatalogId } from '../core/local-models.catalog';
import { LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';

type TaskTab = 'transcription' | 'analysis' | 'translation';

interface CloudBlock {
  readonly provider: ActiveCloudProvider;
  readonly key: string;
  readonly title: string;
  readonly label: string;
}

type ModelList =
  | { readonly state: 'loading' }
  | { readonly state: 'failed' }
  | { readonly state: 'ready'; readonly models: readonly LlmModel[] };

const CLOUD_BLOCKS: readonly CloudBlock[] = Object.freeze([
  { provider: 'ChatGptPlan', key: 'chatgpt', title: 'ChatGPT Plus', label: 'ChatGPT' },
  { provider: 'OpenAI', key: 'openai', title: 'OpenAI API', label: 'OpenAI' },
  { provider: 'Gemini', key: 'gemini', title: 'Google Gemini', label: 'Gemini' },
]);

const OPTION_ICON =
  'M9 4a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0Zm-3 7a6 6 0 0 0 12 0h-2a4 4 0 0 1-8 0Zm5 8h2v3h-2Z';
const CHECK_ICON =
  'M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm-1 14-5-5 1.4-1.4L11 13.2l6.6-6.6L19 8Z';

@Component({
  selector: 'sc-models',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <nav class="tabs" role="tablist" aria-label="Model task">
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="tab() === 'transcription'"
          [class.tab--active]="tab() === 'transcription'"
          class="tab"
          data-test="tab-transcription"
          (click)="setTab('transcription')"
        >
          Transcription
        </button>
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="tab() === 'analysis'"
          [class.tab--active]="tab() === 'analysis'"
          class="tab"
          data-test="tab-analysis"
          (click)="setTab('analysis')"
        >
          Analysis
        </button>
        <button
          type="button"
          role="tab"
          class="tab tab--soon"
          data-test="tab-translation"
          [disabled]="true"
          title="Coming soon"
        >
          <span>Translation</span>
          <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
            <path
              d="M9 21v-2h6v2Zm2-4-5-9a6 6 0 1 1 11 0l-5 9Zm-1-4h6V8a3 3 0 1 0-6 0Z"
              fill="currentColor"
            />
          </svg>
        </button>
      </nav>

      @if (tab() === 'transcription') {
        <section class="panel" data-test="panel-transcription">
          <header class="panel-head">
            <h1>Transcription</h1>
            <p class="lede">Choose which model handles speech-to-text</p>
          </header>

          @if (downloaded().length === 0) {
            <div class="empty" data-test="empty-state">
              <p>No models downloaded yet.</p>
              <a routerLink="/sources/local-models" class="empty-link" data-test="empty-cta">
                Go to AI Sources → Local Models to download one.
              </a>
            </div>
          } @else {
            <article class="provider">
              <header class="provider-head">
                <div class="icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24" width="22" height="22">
                    <path
                      d="M12 3c-2.5 0-4 1.8-4 4v1H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7c0-2.2-1.5-4-4-4Z"
                      fill="currentColor"
                    />
                  </svg>
                </div>
                <div class="provider-body">
                  <div class="provider-title">
                    <h2>Local Whisper</h2>
                    <span class="badge">On Device</span>
                  </div>
                  <p class="provider-desc">Runs locally without a cloud provider.</p>
                </div>
              </header>

              @if (selectedTitle() !== null) {
                <div class="selected-pill" data-test="selected-pill">
                  <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
                    <path
                      d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm-1 14-5-5 1.4-1.4L11 13.2l6.6-6.6L19 8Z"
                      fill="currentColor"
                    />
                  </svg>
                  <span>Selected: {{ selectedTitle() }}</span>
                </div>
              }

              <div class="options">
                @for (m of downloaded(); track m.id) {
                  <button
                    type="button"
                    class="option"
                    [class.option--selected]="selectedId() === m.id"
                    [disabled]="locked()"
                    [attr.data-test]="'option-' + m.id"
                    (click)="onPick(m.id)"
                  >
                    <div class="option-icon" aria-hidden="true">
                      <svg viewBox="0 0 24 24" width="20" height="20">
                        <path
                          d="M9 4a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0Zm-3 7a6 6 0 0 0 12 0h-2a4 4 0 0 1-8 0Zm5 8h2v3h-2Z"
                          fill="currentColor"
                        />
                      </svg>
                    </div>
                    <div class="option-body">
                      <div class="option-title">{{ m.title }}</div>
                      <div class="option-desc">{{ m.description }}</div>
                    </div>
                    @if (selectedId() === m.id) {
                      <svg
                        class="option-check"
                        viewBox="0 0 24 24"
                        width="16"
                        height="16"
                        aria-hidden="true"
                      >
                        <path
                          d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm-1 14-5-5 1.4-1.4L11 13.2l6.6-6.6L19 8Z"
                          fill="currentColor"
                        />
                      </svg>
                    }
                  </button>
                }
              </div>
            </article>
          }
        </section>
      }
      @if (tab() === 'analysis') {
        <section class="panel" data-test="panel-analysis">
          <header class="panel-head">
            <h1>Analysis</h1>
            <p class="lede">Choose which model handles AI analysis and chat</p>
            <p class="lede lede--note">
              Each provider you connect in AI Sources lists its models here. When both ChatGPT
              Sign-In and an OpenAI API key are connected, the provider group you choose from
              decides which credential is used.
            </p>
          </header>

          @if (!anySelected()) {
            <p class="nudge" data-test="no-active-model-nudge">
              No model in use for this task. Pick one below.
            </p>
          }

          @if (localAnalysis().length === 0) {
            <div class="empty" data-test="empty-state">
              <p>No models downloaded yet.</p>
              <a routerLink="/sources/local-models" class="empty-link" data-test="empty-cta">
                Go to AI Sources → Local Models to download one.
              </a>
            </div>
          } @else {
            <article class="provider" data-test="provider-ollama">
              <header class="provider-head">
                <div class="icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24" width="22" height="22">
                    <path
                      d="M12 3c-2.5 0-4 1.8-4 4v1H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7c0-2.2-1.5-4-4-4Z"
                      fill="currentColor"
                    />
                  </svg>
                </div>
                <div class="provider-body">
                  <div class="provider-title">
                    <h2>Local LLM</h2>
                    <span class="badge">On Device</span>
                  </div>
                  <p class="provider-desc">On-device models for private analysis and chat.</p>
                </div>
              </header>
              @if (localSelectedTitle(); as title) {
                <div class="selected-pill" data-test="selected-pill">
                  <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
                    <path [attr.d]="checkIcon" fill="currentColor" />
                  </svg>
                  <span>Selected: {{ title }}</span>
                </div>
              }
              <div class="options">
                @for (m of localAnalysis(); track m.id) {
                  <button
                    type="button"
                    class="option"
                    [class.option--selected]="isLocalSelected(m.id)"
                    [disabled]="saving()"
                    [attr.data-test]="'option-' + m.id"
                    (click)="chooseLocal(m.id)"
                  >
                    <div class="option-icon" aria-hidden="true">
                      <svg viewBox="0 0 24 24" width="20" height="20">
                        <path [attr.d]="optionIcon" fill="currentColor" />
                      </svg>
                    </div>
                    <div class="option-body">
                      <div class="option-title">{{ m.title }}</div>
                      <div class="option-desc">{{ m.description }}</div>
                    </div>
                    @if (isLocalSelected(m.id)) {
                      <svg
                        class="option-check"
                        viewBox="0 0 24 24"
                        width="16"
                        height="16"
                        aria-hidden="true"
                      >
                        <path [attr.d]="checkIcon" fill="currentColor" />
                      </svg>
                    }
                  </button>
                }
              </div>
            </article>
          }

          @for (block of cloudBlocks(); track block.provider) {
            <article class="provider" [attr.data-test]="'provider-' + block.key">
              <header class="provider-head">
                <div class="provider-body">
                  <div class="provider-title">
                    <h2>{{ block.title }}</h2>
                    <span class="badge">Cloud</span>
                  </div>
                </div>
              </header>
              @if (cloudSelectedModel(block.provider); as model) {
                <div class="selected-pill" [attr.data-test]="'selected-pill-' + block.key">
                  <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
                    <path [attr.d]="checkIcon" fill="currentColor" />
                  </svg>
                  <span>Selected: {{ model }}</span>
                </div>
              }
              @switch (listOf(block.provider).state) {
                @case ('failed') {
                  <p class="form-error" role="alert" [attr.data-test]="'models-error-' + block.key">
                    Could not load the {{ block.label }} model list.
                  </p>
                }
                @case ('ready') {
                  <div class="options">
                    @for (m of modelsOf(block.provider); track m.id) {
                      <button
                        type="button"
                        class="option"
                        [class.option--selected]="cloudSelectedModel(block.provider) === m.id"
                        [disabled]="saving()"
                        [attr.data-test]="'model-' + block.key + '-' + m.id"
                        (click)="chooseCloud(block, m.id)"
                      >
                        <div class="option-body">
                          <div class="option-title">{{ m.displayName }}</div>
                          @if (m.displayName !== m.id) {
                            <div class="option-desc">{{ m.id }}</div>
                          }
                        </div>
                        @if (cloudSelectedModel(block.provider) === m.id) {
                          <svg
                            class="option-check"
                            viewBox="0 0 24 24"
                            width="16"
                            height="16"
                            aria-hidden="true"
                          >
                            <path [attr.d]="checkIcon" fill="currentColor" />
                          </svg>
                        }
                      </button>
                    }
                  </div>
                }
                @default {
                  <p class="provider-desc" [attr.data-test]="'models-loading-' + block.key">
                    Loading models...
                  </p>
                }
              }
            </article>
          }

          @if (statusMessage(); as message) {
            <p class="status" role="status" data-test="analysis-status">{{ message }}</p>
          }
          @if (actionError(); as error) {
            <p class="form-error" role="alert" data-test="analysis-error">{{ error }}</p>
          }
        </section>
      }
      @if (tab() === 'translation') {
        <section class="panel panel--soon" data-test="panel-translation">
          <div class="soon-card">
            <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
              <path
                d="M9 21v-2h6v2Zm2-4-5-9a6 6 0 1 1 11 0l-5 9Zm-1-4h6V8a3 3 0 1 0-6 0Z"
                fill="currentColor"
              />
            </svg>
            <h2>Translation</h2>
            <p>Coming soon. Translation models will land in a future release.</p>
          </div>
        </section>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .page {
        padding: 28px 40px 48px;
        max-width: 980px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 20px;
      }
      .tabs {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 12px;
        padding: 4px;
        display: inline-flex;
        gap: 2px;
        width: fit-content;
      }
      .tab {
        background: transparent;
        color: #7b8299;
        border: none;
        border-radius: 8px;
        padding: 8px 16px;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
        font-family: inherit;
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .tab:hover:not(:disabled) {
        color: #e4e8f0;
      }
      .tab--active {
        background: #2a3245;
        color: #e4e8f0;
      }
      .tab--soon {
        color: #c08a3e;
      }
      .tab:disabled {
        cursor: not-allowed;
      }
      .panel {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .panel-head h1 {
        font-size: 22px;
        font-weight: 700;
        margin: 0 0 4px;
      }
      .panel-head .lede {
        margin: 0;
        color: #7b8299;
        font-size: 13px;
      }
      .lede--note {
        margin-top: 2px;
      }
      .empty {
        background: #141822;
        border: 1px dashed #252b3b;
        border-radius: 14px;
        padding: 32px;
        text-align: center;
        color: #7b8299;
        font-size: 13px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        align-items: center;
      }
      .empty p {
        margin: 0;
      }
      .empty-link {
        color: #6b89c4;
        text-decoration: none;
        font-weight: 600;
      }
      .empty-link:hover {
        text-decoration: underline;
      }
      .provider {
        background: #141822;
        border: 1px solid #252b3b;
        border-radius: 14px;
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .provider-head {
        display: flex;
        gap: 14px;
        align-items: flex-start;
      }
      .icon {
        width: 44px;
        height: 44px;
        border-radius: 10px;
        background: #1e2333;
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .provider-body {
        display: flex;
        flex-direction: column;
        gap: 4px;
        flex: 1;
      }
      .provider-title {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .provider-title h2 {
        font-size: 16px;
        font-weight: 700;
        margin: 0;
      }
      .badge {
        font-size: 11px;
        font-weight: 600;
        color: #7b8299;
        background: rgba(123, 130, 153, 0.12);
        border: 1px solid rgba(123, 130, 153, 0.25);
        border-radius: 999px;
        padding: 2px 8px;
      }
      .provider-desc {
        margin: 0;
        color: #7b8299;
        font-size: 13px;
      }
      .selected-pill {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        font-weight: 600;
        color: #6b89c4;
        background: rgba(107, 137, 196, 0.1);
        border: 1px solid rgba(107, 137, 196, 0.25);
        border-radius: 999px;
        padding: 4px 10px;
        align-self: flex-start;
      }
      .nudge {
        margin: 0;
        color: #c08a3e;
        font-size: 12px;
        font-weight: 500;
        background: rgba(192, 138, 62, 0.08);
        border: 1px solid rgba(192, 138, 62, 0.25);
        border-radius: 8px;
        padding: 8px 12px;
      }
      .options {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .option {
        display: flex;
        gap: 12px;
        align-items: center;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 12px;
        padding: 14px;
        cursor: pointer;
        text-align: left;
        font-family: inherit;
        color: #e4e8f0;
      }
      .option:hover:not(:disabled) {
        background: #1a1f2c;
      }
      .option:disabled {
        cursor: not-allowed;
        opacity: 0.7;
      }
      .option--selected {
        background: rgba(58, 95, 156, 0.15);
        border-color: #4267ad;
      }
      .option-icon {
        width: 32px;
        height: 32px;
        border-radius: 8px;
        background: #1e2333;
        color: #7b8299;
        display: flex;
        align-items: center;
        justify-content: center;
        flex-shrink: 0;
      }
      .option-body {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .option-title {
        font-size: 14px;
        font-weight: 600;
      }
      .option-desc {
        font-size: 12px;
        color: #7b8299;
      }
      .form-error {
        margin: 0;
        color: #e06c75;
        font-size: 12px;
      }
      .status {
        margin: 0;
        color: #7b8299;
        font-size: 12px;
      }
      .option-check {
        color: #6b89c4;
        margin-left: auto;
      }
      .panel--soon {
        align-items: center;
        padding-top: 40px;
      }
      .soon-card {
        background: #141822;
        border: 1px solid #252b3b;
        border-radius: 14px;
        padding: 28px 32px;
        max-width: 420px;
        text-align: center;
        color: #7b8299;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 8px;
      }
      .soon-card h2 {
        margin: 0;
        font-size: 18px;
        color: #e4e8f0;
        font-weight: 700;
      }
      .soon-card p {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
      }
    `,
  ],
})
export class ModelsComponent implements OnInit {
  readonly store = inject(LocalModelsStore);
  private readonly logStore = inject(LogStore);
  private readonly llmConfig = inject(LlmConfigStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly route = inject(ActivatedRoute);

  readonly optionIcon = OPTION_ICON;
  readonly checkIcon = CHECK_ICON;

  readonly tab = signal<TaskTab>(
    this.route.snapshot.queryParamMap.get('tab') === 'analysis' ? 'analysis' : 'transcription',
  );
  readonly saving = signal<boolean>(false);
  readonly statusMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  private readonly lists = signal<Partial<Record<ActiveCloudProvider, ModelList>>>({});

  readonly locked = computed<boolean>(
    () => this.tab() === 'transcription' && this.logStore.status().state === 'running',
  );

  readonly downloaded = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.downloadedFor('transcription'),
  );

  readonly selectedId = computed<string | null>(() => this.store.selectedFor('transcription'));

  readonly selectedTitle = computed<string | null>(() => {
    const id = this.selectedId();
    if (id === null) return null;
    return this.downloaded().find((m) => m.id === id)?.title ?? null;
  });

  readonly localAnalysis = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.downloadedFor('analysis'),
  );

  private readonly ollamaInUse = computed<boolean>(() => {
    const provider = this.llmConfig.provider();
    return provider === null || provider === 'Ollama';
  });

  readonly localSelectedTitle = computed<string | null>(() => {
    if (!this.ollamaInUse()) return null;
    const id = this.store.selectedFor('analysis');
    return this.localAnalysis().find((m) => m.id === id)?.title ?? null;
  });

  readonly cloudBlocks = computed<readonly CloudBlock[]>(() =>
    CLOUD_BLOCKS.filter((block) => this.llmConfig.connectedProviders().has(block.provider)),
  );

  readonly anySelected = computed<boolean>(
    () =>
      this.localSelectedTitle() !== null ||
      this.cloudBlocks().some((block) => this.cloudSelectedModel(block.provider) !== null),
  );

  async ngOnInit(): Promise<void> {
    await this.llmConfig.loadFromVault();
    const chatgpt = await this.bridge.chatgptStatus().catch(() => null);
    this.llmConfig.setChatGptConnected(
      chatgpt !== null && chatgpt.signedIn && chatgpt.planUsageGranted,
    );
    await Promise.all(this.cloudBlocks().map((block) => this.loadModels(block.provider)));
  }

  setTab(tab: TaskTab): void {
    this.tab.set(tab);
  }

  onPick(id: string): void {
    if (this.locked()) return;
    this.store.select('transcription', id);
  }

  isLocalSelected(id: string): boolean {
    return this.ollamaInUse() && this.store.selectedFor('analysis') === id;
  }

  cloudSelectedModel(provider: ActiveCloudProvider): string | null {
    return this.llmConfig.provider() === provider ? this.llmConfig.model() : null;
  }

  listOf(provider: ActiveCloudProvider): ModelList {
    return this.lists()[provider] ?? { state: 'loading' };
  }

  modelsOf(provider: ActiveCloudProvider): readonly LlmModel[] {
    const list = this.listOf(provider);
    return list.state === 'ready' ? list.models : [];
  }

  async chooseLocal(id: string): Promise<void> {
    const tag = ollamaTagForCatalogId(id);
    if (tag === null || this.saving() || this.isLocalSelected(id)) return;
    this.beginAction();
    try {
      this.store.select('analysis', id);
      await this.llmConfig.useOllama();
      await this.bridge.vaultStore({ key: 'llm.model', value: tag });
    } catch (err: unknown) {
      this.actionError.set(err instanceof Error ? err.message : 'Could not switch to Ollama.');
      return;
    } finally {
      this.saving.set(false);
    }
    await this.restartIfRunning('Ollama');
  }

  async chooseCloud(block: CloudBlock, model: string): Promise<void> {
    if (this.saving() || this.cloudSelectedModel(block.provider) === model) return;
    this.beginAction();
    try {
      await this.llmConfig.use(block.provider, model);
    } catch (err: unknown) {
      this.actionError.set(
        err instanceof Error ? err.message : `Could not switch to ${block.label}.`,
      );
      return;
    } finally {
      this.saving.set(false);
    }
    await this.restartIfRunning(block.label);
  }

  private async loadModels(provider: ActiveCloudProvider): Promise<void> {
    this.lists.update((lists) => ({ ...lists, [provider]: { state: 'loading' } }));
    let list: ModelList;
    try {
      const models =
        provider === 'ChatGptPlan'
          ? (await this.bridge.chatgptListModels()).models.map((m) => ({
              id: m.slug,
              displayName: m.displayName,
            }))
          : (await this.bridge.llmListModels({ provider })).models;
      list = { state: 'ready', models };
    } catch {
      list = { state: 'failed' };
    }
    this.lists.update((lists) => ({ ...lists, [provider]: list }));
  }

  private beginAction(): void {
    this.saving.set(true);
    this.statusMessage.set(null);
    this.actionError.set(null);
  }

  private async restartIfRunning(label: string): Promise<void> {
    try {
      const { state } = await this.bridge.workerStatus();
      if (state !== 'running' && state !== 'starting') {
        this.statusMessage.set(`Scout will use ${label} the next time it starts.`);
        return;
      }
      this.statusMessage.set(`Restarting Scout to use ${label}.`);
      await this.bridge.workerStop();
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.statusMessage.set(`Restart Scout from the status screen to use ${label}.`);
      this.actionError.set(err instanceof Error ? err.message : 'Could not restart Scout.');
    }
  }
}
