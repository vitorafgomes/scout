import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ScoutError, type OllamaModelInfo } from '@scout/shared';

import { LlmConfigStore, type LlmProvider } from '../../core/llm-config.store';
import { ScoutBridgeService } from '../../core/scout-bridge';

function isHttpUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

@Component({
  selector: 'sc-llm',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="llm">
      <h1>LLM</h1>
      <p class="lede">Choose the model Scout uses to compose answers.</p>

      @if (saved()) {
        <div class="banner banner--info" role="status" data-test="restart-banner">
          <span>Restart the worker to apply the new model.</span>
          <button
            type="button"
            class="banner-action"
            data-test="restart-now"
            (click)="restartWorker()"
          >
            Restart now
          </button>
        </div>
      }

      <section class="card">
        <h2>Provider</h2>
        <div class="radio-group" role="radiogroup" aria-label="LLM provider">
          <label class="radio">
            <input
              type="radio"
              name="provider"
              data-test="provider-ollama"
              [checked]="selectedProvider() === 'Ollama'"
              (change)="onProviderChange('Ollama')"
            />
            <span>Ollama</span>
          </label>
          <label class="radio radio--disabled">
            <input type="radio" name="provider" disabled />
            <span>OpenAI <em>(coming soon)</em></span>
          </label>
          <label class="radio radio--disabled">
            <input type="radio" name="provider" disabled />
            <span>Azure OpenAI <em>(coming soon)</em></span>
          </label>
        </div>
      </section>

      @if (selectedProvider() !== null) {
      <section class="card">
        <h2>Endpoint</h2>
        <label class="field">
          <span class="field-label">Ollama base URL</span>
          <input
            type="text"
            class="text-input"
            data-test="endpoint-input"
            [value]="endpoint()"
            (input)="onEndpointInput($event)"
            placeholder="http://localhost:11434"
            autocomplete="off"
            spellcheck="false"
          />
        </label>
        @if (!endpointValid()) {
          <p class="error" data-test="endpoint-error">
            Endpoint must be an absolute http:// or https:// URL.
          </p>
        }
      </section>

      <section class="card">
        <h2>Model</h2>
        <div class="model-row">
          <select
            class="select"
            data-test="model-select"
            [disabled]="loading() || models().length === 0"
            (change)="onModelChange($event)"
          >
            <option value="" [selected]="selectedModel() === null">— Select a model —</option>
            @for (m of models(); track m.name) {
              <option [value]="m.name" [selected]="selectedModel() === m.name">
                {{ m.name }}
              </option>
            }
          </select>
          <button
            type="button"
            class="ghost"
            data-test="refresh-models"
            (click)="refreshModels()"
            [disabled]="loading() || !endpointValid()"
          >
            Refresh
          </button>
        </div>
        @if (loading()) {
          <p class="loading" data-test="loading">Loading models from Ollama…</p>
        }
        @if (loadError(); as msg) {
          <p class="error" data-test="load-error">{{ msg }}</p>
        }
      </section>

      <div class="actions">
        <button
          type="button"
          class="primary"
          data-test="save"
          [disabled]="!canSave()"
          (click)="save()"
        >
          Save
        </button>
        @if (saved()) {
          <span class="saved-msg" role="status" data-test="saved-msg">Saved</span>
        }
      </div>
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
      .llm {
        padding: 32px;
        max-width: 720px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 24px;
      }
      h1 {
        font-size: 20px;
        margin: 0;
      }
      .lede {
        margin: 0;
        color: #7b8299;
        font-size: 13px;
      }
      .card {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 16px;
      }
      .card h2 {
        font-size: 14px;
        margin: 0 0 12px;
        color: #e4e8f0;
        font-weight: 600;
        letter-spacing: 0.02em;
      }
      .radio-group {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .radio {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 14px;
      }
      .radio--disabled {
        color: #7b8299;
      }
      .radio--disabled em {
        font-style: italic;
        color: #7b8299;
        font-size: 12px;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .field-label {
        font-size: 13px;
        color: #7b8299;
      }
      .text-input,
      .select {
        background: #0f1117;
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px 10px;
        font-size: 14px;
        font-family: 'JetBrains Mono', 'Cascadia Code', ui-monospace, monospace;
      }
      .select {
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .model-row {
        display: flex;
        gap: 8px;
        align-items: center;
      }
      .model-row .select {
        flex: 1;
      }
      .actions {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .primary {
        background: #5a8fd4;
        color: #0f1117;
        border: none;
        border-radius: 6px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
      }
      .primary:disabled {
        background: #3d4258;
        color: #7b8299;
        cursor: not-allowed;
      }
      .ghost {
        background: transparent;
        color: #e4e8f0;
        border: 1px solid #252b3b;
        border-radius: 6px;
        padding: 8px 12px;
        font-size: 13px;
        cursor: pointer;
      }
      .ghost:disabled {
        color: #3d4258;
        cursor: not-allowed;
      }
      .error {
        color: #d45a5a;
        font-size: 13px;
        margin: 8px 0 0;
      }
      .loading {
        color: #7b8299;
        font-size: 13px;
        margin: 8px 0 0;
      }
      .saved-msg {
        font-size: 13px;
        color: #3aaf7b;
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
export class LlmComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  readonly llmConfig = inject(LlmConfigStore);

  readonly models = signal<readonly OllamaModelInfo[]>([]);
  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly selectedProvider = signal<LlmProvider | null>(null);
  readonly selectedModel = signal<string | null>(null);
  readonly saved = signal<boolean>(false);

  readonly endpoint = computed<string>(() => this.llmConfig.endpoint());

  readonly endpointValid = computed<boolean>(() => isHttpUrl(this.endpoint()));

  readonly canSave = computed<boolean>(
    () =>
      this.selectedProvider() !== null &&
      this.selectedModel() !== null &&
      this.endpointValid() &&
      !this.loading(),
  );

  ngOnInit(): void {
    void this.llmConfig.loadFromVault().then(() => {
      const persistedProvider = this.llmConfig.provider();
      this.selectedProvider.set(persistedProvider);
      this.selectedModel.set(this.llmConfig.model());
      if (persistedProvider !== null) {
        void this.refreshModels();
      }
    });
  }

  onProviderChange(provider: LlmProvider): void {
    this.selectedProvider.set(provider);
    this.saved.set(false);
    if (this.models().length === 0) {
      void this.refreshModels();
    }
  }

  onEndpointInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.llmConfig.setEndpointDraft(value);
    this.saved.set(false);
  }

  onModelChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.selectedModel.set(value.length === 0 ? null : value);
    this.saved.set(false);
  }

  async refreshModels(): Promise<void> {
    if (!this.endpointValid()) {
      this.loadError.set('Endpoint must be an absolute http:// or https:// URL.');
      this.models.set([]);
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const res = await this.bridge.llmListOllamaModels({ endpoint: this.endpoint() });
      this.models.set(res.models);
      // If the previously selected model is gone, reset.
      const selected = this.selectedModel();
      if (selected !== null && !res.models.some((m) => m.name === selected)) {
        this.selectedModel.set(null);
      }
    } catch (err: unknown) {
      this.models.set([]);
      this.loadError.set(this.describeError(err));
    } finally {
      this.loading.set(false);
    }
  }

  async save(): Promise<void> {
    const provider = this.selectedProvider();
    const model = this.selectedModel();
    if (provider === null || model === null || !this.endpointValid()) return;
    try {
      await this.llmConfig.setSelection(provider, model, this.endpoint());
      this.saved.set(true);
    } catch (err: unknown) {
      this.loadError.set(this.describeError(err));
    }
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
    this.saved.set(false);
  }

  private describeError(err: unknown): string {
    if (err instanceof ScoutError && err.code === 'llm/ollama-unreachable') {
      return `Could not reach Ollama at ${this.endpoint()}. Is it running and listening on that address?`;
    }
    if (err instanceof ScoutError) {
      return `${err.code}: ${err.message}`;
    }
    if (err instanceof Error) {
      return err.message;
    }
    return 'Unknown error while talking to Ollama.';
  }
}
