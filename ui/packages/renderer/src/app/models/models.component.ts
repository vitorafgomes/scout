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

import {
  type ActiveCloudProvider,
  LLM_CONFIG_DEFAULT_ENDPOINT,
  LlmConfigStore,
} from '../core/llm-config.store';
import { LogStore } from '../core/log.store';
import { type LocalModelCatalogEntry, ollamaTagForCatalogId } from '../core/local-models.catalog';
import { LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { FAMILY_ORDER, type ModelTag, familyOf, isRecommended, tagsOf } from './model-tags';

type TaskTab = 'transcription' | 'analysis' | 'translation';

interface CloudBlock {
  readonly provider: ActiveCloudProvider;
  readonly key: string;
  readonly title: string;
  readonly label: string;
}

type ModelFilter = 'all' | 'recommended' | 'fast' | 'local';

interface ModelRow {
  readonly id: string;
  readonly name: string;
  readonly title: string;
  readonly note: string | null;
  readonly family: string | null;
  readonly tags: readonly ModelTag[];
}

interface ShownRow extends ModelRow {
  readonly familyHead: boolean;
}

interface RowView {
  readonly rows: readonly ShownRow[];
  readonly total: number;
  readonly hidden: number;
}

interface InUse {
  readonly model: string;
  readonly source: string;
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

const LM_STUDIO_ENDPOINT = 'http://localhost:1234';
const ROW_CAP = 6;

const FILTERS: readonly { readonly id: ModelFilter; readonly label: string }[] = Object.freeze([
  { id: 'all', label: 'All' },
  { id: 'recommended', label: 'Recommended' },
  { id: 'fast', label: 'Fast' },
  { id: 'local', label: 'Local' },
]);

const LOGOS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  openai: [
    'M12 3a4 4 0 0 1 4 4v1a4 4 0 0 1 4 4 4 4 0 0 1-4 4h-1a4 4 0 0 1-4 4 4 4 0 0 1-4-4v-1a4 4 0 0 1-4-4 4 4 0 0 1 4-4h1a4 4 0 0 1 4-4z',
  ],
  chatgpt: [
    'M7 8h10a3 3 0 0 1 3 3v6a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3v-6a3 3 0 0 1 3-3Z',
    'M9 14h.01M15 14h.01',
    'M12 8V4M9 4h6',
  ],
  gemini: [
    'M12 2l1.8 5.2L19 9l-5.2 1.8L12 16l-1.8-5.2L5 9l5.2-1.8z',
    'M19 15l.9 2.1L22 18l-2.1.9L19 21l-.9-2.1L16 18l2.1-.9z',
  ],
  ollama: [
    'M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z',
    'M8 20h8M12 16v4',
  ],
});

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

          @if (inUse(); as use) {
            <div class="inuse" data-test="in-use">
              <span class="inuse-label">In use</span>
              <span class="inuse-model">{{ use.model }}</span>
              <span class="inuse-via">via {{ use.source }}</span>
            </div>
          } @else {
            <p class="nudge" data-test="no-active-model-nudge">
              No model in use for this task. Pick one below.
            </p>
          }

          <div class="tools">
            <label class="search">
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <path [attr.d]="searchIcon" />
              </svg>
              <input
                type="search"
                placeholder="Search models, e.g. mini, flash, gemma"
                aria-label="Search models"
                data-test="model-search"
                [value]="query()"
                (input)="onSearch($event)"
              />
            </label>
            <div class="chips" role="group" aria-label="Filter models">
              @for (chip of filters; track chip.id) {
                <button
                  type="button"
                  class="chip"
                  [attr.aria-pressed]="filter() === chip.id"
                  [attr.data-test]="'filter-' + chip.id"
                  (click)="filter.set(chip.id)"
                >
                  {{ chip.label }}
                </button>
              }
            </div>
          </div>

          @for (block of cloudBlocks(); track block.provider) {
            @let view = cloudView(block);
            <details
              class="group"
              [attr.data-test]="'provider-' + block.key"
              [open]="cloudSelectedModel(block.provider) !== null || narrowing()"
            >
              <summary [attr.data-test]="'group-summary-' + block.key">
                <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
                  <path [attr.d]="chevronIcon" />
                </svg>
                <span class="logo" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    @for (d of logos[block.key]; track $index) {
                      <path [attr.d]="d" />
                    }
                  </svg>
                </span>
                <span class="gtitle">
                  <b>
                    {{ block.title }}
                    @if (cloudSelectedModel(block.provider) !== null) {
                      <span class="gbadge gbadge--use">In use</span>
                    } @else {
                      <span class="gbadge">{{
                        block.provider === 'ChatGptPlan' ? 'Plan' : 'Cloud'
                      }}</span>
                    }
                  </b>
                  @if (cloudSelectedModel(block.provider); as model) {
                    <span [attr.data-test]="'selected-pill-' + block.key">
                      Selected: <span class="sel">{{ model }}</span>
                    </span>
                  }
                </span>
                @if (listOf(block.provider).state === 'ready') {
                  <span class="count">{{ countLabel(view.total) }}</span>
                }
              </summary>
              <div class="list">
                @switch (listOf(block.provider).state) {
                  @case ('failed') {
                    <p
                      class="form-error list-note"
                      role="alert"
                      [attr.data-test]="'models-error-' + block.key"
                    >
                      Could not load the {{ block.label }} model list.
                    </p>
                  }
                  @case ('ready') {
                    @for (row of view.rows; track row.id) {
                      @if (row.familyHead) {
                        <div class="fam" data-test="family">{{ row.family }}</div>
                      }
                      <button
                        type="button"
                        class="opt"
                        [class.option--selected]="cloudSelectedModel(block.provider) === row.id"
                        [disabled]="saving()"
                        [attr.data-test]="'model-' + block.key + '-' + row.id"
                        (click)="chooseCloud(block, row.id)"
                      >
                        <span class="radio" aria-hidden="true"></span>
                        <span class="name">
                          {{ row.name }}
                          @for (tag of row.tags; track tag) {
                            <span class="tag" [class.tag--rec]="tag === 'Recommended'">{{
                              tag
                            }}</span>
                          }
                          @if (row.note; as note) {
                            <small>{{ note }}</small>
                          }
                        </span>
                      </button>
                    } @empty {
                      <p class="list-note" [attr.data-test]="'no-match-' + block.key">
                        No model matches
                      </p>
                    }
                    @if (view.hidden > 0) {
                      <button
                        type="button"
                        class="more"
                        [attr.data-test]="'show-all-' + block.key"
                        (click)="expand(block.key)"
                      >
                        Show all {{ countLabel(view.total) }}
                      </button>
                    }
                  }
                  @default {
                    <p
                      class="provider-desc list-note"
                      [attr.data-test]="'models-loading-' + block.key"
                    >
                      Loading models...
                    </p>
                  }
                }
              </div>
            </details>
          }

          @if (localListed()) {
            @let view = localView();
            <details
              class="group"
              data-test="provider-ollama"
              [open]="localSelectedTitle() !== null || narrowing()"
            >
              <summary data-test="group-summary-ollama">
                <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
                  <path [attr.d]="chevronIcon" />
                </svg>
                <span class="logo" aria-hidden="true">
                  <svg viewBox="0 0 24 24">
                    @for (d of logos['ollama']; track $index) {
                      <path [attr.d]="d" />
                    }
                  </svg>
                </span>
                <span class="gtitle">
                  <b>
                    Local LLM
                    @if (localSelectedTitle() !== null) {
                      <span class="gbadge gbadge--use">In use</span>
                    } @else {
                      <span class="gbadge gbadge--local">On device</span>
                    }
                  </b>
                  @if (localSelectedTitle(); as title) {
                    <span data-test="selected-pill">
                      Selected: <span class="sel">{{ title }}</span>
                    </span>
                  }
                </span>
                <span class="count">{{ countLabel(view.total) }}</span>
              </summary>
              <div class="list">
                @for (row of view.rows; track row.id) {
                  <button
                    type="button"
                    class="opt"
                    [class.option--selected]="isLocalSelected(row.id)"
                    [disabled]="saving()"
                    [attr.data-test]="'option-' + row.id"
                    (click)="chooseLocal(row.id)"
                  >
                    <span class="radio" aria-hidden="true"></span>
                    <span class="name">
                      {{ row.name }}
                      @for (tag of row.tags; track tag) {
                        <span class="tag" [class.tag--rec]="tag === 'Recommended'">{{ tag }}</span>
                      }
                      @if (row.note; as note) {
                        <small>{{ note }}</small>
                      }
                    </span>
                  </button>
                } @empty {
                  <p class="list-note" data-test="no-match-ollama">No model matches</p>
                }
                @if (view.hidden > 0) {
                  <button
                    type="button"
                    class="more"
                    data-test="show-all-ollama"
                    (click)="expand('ollama')"
                  >
                    Show all {{ countLabel(view.total) }}
                  </button>
                }
              </div>
            </details>
          } @else if (localRuntime() === true) {
            <div class="empty" data-test="empty-state">
              <p>No models downloaded yet.</p>
              <a routerLink="/sources/local-models" class="empty-link" data-test="empty-cta">
                Go to AI Sources → Local Models to download one.
              </a>
            </div>
          } @else if (localRuntime() === false) {
            <div class="offline" data-test="local-not-connected">
              <span class="logo" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  @for (d of logos['ollama']; track $index) {
                    <path [attr.d]="d" />
                  }
                </svg>
              </span>
              <span class="gtitle">
                <b>Local LLM <span class="gbadge">Not connected</span></b>
                <span>
                  No local runtime running. Start Ollama or LM Studio and download a model in AI
                  Sources → Local Models to see it here.
                </span>
              </span>
              <a routerLink="/sources" class="go">AI Sources</a>
            </div>
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
      .inuse {
        display: flex;
        align-items: center;
        gap: 14px;
        flex-wrap: wrap;
        background: #1e2e45;
        border: 1px solid #4267ad;
        border-radius: 12px;
        padding: 12px 16px;
      }
      .inuse-label {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: #c9d8ff;
      }
      .inuse-model {
        font-family: 'IBM Plex Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
        font-size: 15px;
        font-weight: 500;
      }
      .inuse-via {
        color: #7b8299;
        font-size: 13px;
      }
      .tools {
        display: flex;
        gap: 10px;
        align-items: center;
        flex-wrap: wrap;
      }
      .search {
        flex: 1 1 260px;
        display: flex;
        align-items: center;
        gap: 8px;
        border: 1px solid #252b3b;
        border-radius: 10px;
        background: #141822;
        padding: 0 12px;
        min-height: 44px;
      }
      .search:focus-within {
        border-color: #6b89c4;
      }
      .search svg {
        width: 16px;
        height: 16px;
        flex: none;
        fill: none;
        stroke: #7b8299;
        stroke-width: 1.8;
        stroke-linecap: round;
      }
      .search input {
        flex: 1 1 auto;
        min-width: 0;
        font: inherit;
        font-size: 14px;
        border: 0;
        background: transparent;
        color: #e4e8f0;
        outline: none;
      }
      .chips {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
      }
      .chip {
        font: inherit;
        font-size: 12.5px;
        border-radius: 999px;
        padding: 5px 11px;
        border: 1px solid #252b3b;
        background: #141822;
        color: #c3cadb;
        cursor: pointer;
        min-height: 32px;
      }
      .chip[aria-pressed='true'] {
        background: #4267ad;
        border-color: #4267ad;
        color: #fff;
      }
      .chip:focus-visible,
      summary:focus-visible {
        outline: 2px solid #6b89c4;
        outline-offset: 2px;
      }
      .group {
        border: 1px solid #252b3b;
        border-radius: 12px;
        background: #141822;
      }
      summary {
        list-style: none;
        cursor: pointer;
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        min-height: 56px;
        border-radius: 12px;
      }
      summary::-webkit-details-marker {
        display: none;
      }
      .chev {
        width: 16px;
        height: 16px;
        flex: none;
        fill: none;
        stroke: #7b8299;
        stroke-width: 2;
        stroke-linecap: round;
        stroke-linejoin: round;
        transition: transform 0.15s;
      }
      .group[open] > summary .chev {
        transform: rotate(90deg);
      }
      .logo {
        width: 36px;
        height: 36px;
        border-radius: 9px;
        background: #1e2333;
        border: 1px solid #252b3b;
        display: flex;
        align-items: center;
        justify-content: center;
        flex: none;
      }
      .logo svg {
        width: 20px;
        height: 20px;
        fill: none;
        stroke: currentColor;
        stroke-width: 2;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
      .gtitle {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
        color: #7b8299;
        font-size: 12.5px;
      }
      .gtitle b {
        font-weight: 600;
        font-size: 15px;
        color: #e4e8f0;
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
      }
      .sel {
        font-family: 'IBM Plex Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
        color: #c3cadb;
      }
      .gbadge {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        border-radius: 999px;
        padding: 2px 8px;
        background: #1e2333;
        color: #7b8299;
      }
      .gbadge--use {
        background: #1e2e45;
        color: #c9d8ff;
      }
      .gbadge--local {
        background: #1f4d3a;
        color: #c9f2dc;
      }
      .count {
        color: #7b8299;
        font-size: 12.5px;
        flex: none;
        font-variant-numeric: tabular-nums;
      }
      .list {
        border-top: 1px solid #252b3b;
        display: flex;
        flex-direction: column;
      }
      .list-note {
        margin: 0;
        padding: 12px 16px;
        color: #7b8299;
        font-size: 13px;
      }
      .list-note.form-error {
        color: #e06c75;
      }
      .fam {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: #7b8299;
        padding: 10px 16px 4px;
      }
      .opt {
        display: grid;
        grid-template-columns: 20px minmax(0, 1fr);
        gap: 12px;
        align-items: center;
        padding: 10px 16px;
        border: 0;
        border-top: 1px solid #252b3b;
        background: transparent;
        color: #e4e8f0;
        font: inherit;
        text-align: left;
        cursor: pointer;
        min-height: 48px;
        width: 100%;
      }
      .opt:hover:not(:disabled),
      .opt.option--selected {
        background: #1a1f2c;
        border-color: #252b3b;
      }
      .opt:disabled {
        cursor: not-allowed;
        opacity: 0.7;
      }
      .opt:focus-visible {
        outline: 2px solid #6b89c4;
        outline-offset: -2px;
      }
      .radio {
        width: 18px;
        height: 18px;
        border-radius: 50%;
        border: 2px solid #3a4258;
        position: relative;
      }
      .option--selected .radio {
        border-color: #4267ad;
      }
      .option--selected .radio::after {
        content: '';
        position: absolute;
        inset: 3px;
        border-radius: 50%;
        background: #4267ad;
      }
      .name {
        font-family: 'IBM Plex Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
        font-size: 13.5px;
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        min-width: 0;
      }
      .name small {
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        font-size: 12px;
        color: #7b8299;
        flex-basis: 100%;
      }
      .tag {
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        font-size: 11px;
        border-radius: 999px;
        padding: 1px 7px;
        background: #1e2333;
        color: #c3cadb;
        border: 1px solid #252b3b;
      }
      .tag--rec {
        background: #1f4d3a;
        color: #c9f2dc;
        border-color: transparent;
      }
      .more {
        display: flex;
        align-items: center;
        justify-content: center;
        padding: 10px 16px;
        border: 0;
        border-top: 1px solid #252b3b;
        color: #6b89c4;
        font: inherit;
        font-size: 13px;
        background: transparent;
        width: 100%;
        cursor: pointer;
        min-height: 44px;
      }
      .more:hover {
        text-decoration: underline;
      }
      .offline {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        border: 1px dashed #252b3b;
        border-radius: 12px;
        color: #7b8299;
      }
      .offline .gtitle b {
        color: #c3cadb;
      }
      .go {
        color: #6b89c4;
        font-size: 13px;
        text-decoration: none;
        flex: none;
        min-height: 44px;
        display: inline-flex;
        align-items: center;
      }
      .go:hover {
        text-decoration: underline;
      }
      @media (prefers-reduced-motion: reduce) {
        .chev {
          transition: none;
        }
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

  readonly filters = FILTERS;
  readonly logos = LOGOS;
  readonly searchIcon = 'M7 2.5a4.5 4.5 0 1 1 0 9 4.5 4.5 0 0 1 0-9ZM10.5 10.5 14 14';
  readonly chevronIcon = 'M6 3l5 5-5 5';

  readonly tab = signal<TaskTab>(
    this.route.snapshot.queryParamMap.get('tab') === 'analysis' ? 'analysis' : 'transcription',
  );
  readonly saving = signal<boolean>(false);
  readonly statusMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  private readonly lists = signal<Partial<Record<ActiveCloudProvider, ModelList>>>({});
  readonly query = signal<string>('');
  readonly filter = signal<ModelFilter>('all');
  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Whether Ollama or LM Studio answers on its default endpoint, the check AI Sources makes; null until it is known. */
  readonly localRuntime = signal<boolean | null>(null);

  readonly narrowing = computed<boolean>(
    () => this.query().trim() !== '' || this.filter() !== 'all',
  );

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

  readonly localListed = computed<boolean>(
    () => this.localRuntime() === true && this.localAnalysis().length > 0,
  );

  readonly localSelectedTitle = computed<string | null>(() => {
    if (!this.localListed() || !this.ollamaInUse()) return null;
    const id = this.store.selectedFor('analysis');
    return this.localAnalysis().find((m) => m.id === id)?.title ?? null;
  });

  readonly cloudBlocks = computed<readonly CloudBlock[]>(() =>
    CLOUD_BLOCKS.filter((block) => this.llmConfig.connectedProviders().has(block.provider)),
  );

  readonly inUse = computed<InUse | null>(() => {
    const local = this.localSelectedTitle();
    if (local !== null) return { model: local, source: 'Ollama' };
    for (const block of this.cloudBlocks()) {
      const model = this.cloudSelectedModel(block.provider);
      if (model !== null) return { model, source: block.title };
    }
    return null;
  });

  readonly localView = computed<RowView>(() =>
    this.view(
      'ollama',
      this.localAnalysis().map((m) => ({
        id: m.id,
        name: m.title,
        title: m.title,
        note: m.description,
        family: null,
        tags: tagsOf(m.id, m),
      })),
      this.ollamaInUse() ? this.store.selectedFor('analysis') : null,
      true,
    ),
  );

  async ngOnInit(): Promise<void> {
    const probe = this.probeLocalRuntime();
    await this.llmConfig.loadFromVault();
    const chatgpt = await this.bridge.chatgptStatus().catch(() => null);
    this.llmConfig.setChatGptConnected(
      chatgpt !== null && chatgpt.signedIn && chatgpt.planUsageGranted,
    );
    await Promise.all([
      probe,
      ...this.cloudBlocks().map((block) => this.loadModels(block.provider)),
    ]);
  }

  onSearch(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  expand(key: string): void {
    this.expanded.update((keys) => new Set(keys).add(key));
  }

  countLabel(count: number): string {
    return `${count} ${count === 1 ? 'model' : 'models'}`;
  }

  cloudView(block: CloudBlock): RowView {
    const models = this.modelsOf(block.provider);
    const ids = models.map((m) => m.id);
    const rank = (row: ModelRow): number =>
      FAMILY_ORDER.indexOf(row.family ?? 'Other') * 2 + (row.tags.includes('Recommended') ? 0 : 1);
    const rows = models
      .map((m): ModelRow => {
        const tags = tagsOf(m.id);
        return {
          id: m.id,
          name: m.id,
          title: m.displayName,
          note: m.displayName === m.id ? null : m.displayName,
          family: familyOf(m.id),
          tags: isRecommended(m.id, ids) ? ['Recommended', ...tags] : tags,
        };
      })
      .sort((a, b) => rank(a) - rank(b));
    return this.view(block.key, rows, this.cloudSelectedModel(block.provider), false);
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

  private view(
    key: string,
    rows: readonly ModelRow[],
    selected: string | null,
    local: boolean,
  ): RowView {
    const narrowing = this.narrowing();
    const shown = narrowing
      ? rows.filter((row) => this.matches(row, local))
      : this.expanded().has(key)
        ? rows
        : rows.filter(
            (row, index) =>
              index < ROW_CAP || row.id === selected || row.tags.includes('Recommended'),
          );
    return {
      rows: shown.map((row, index) => ({
        ...row,
        familyHead: row.family !== null && row.family !== shown[index - 1]?.family,
      })),
      total: rows.length,
      hidden: narrowing ? 0 : rows.length - shown.length,
    };
  }

  private matches(row: ModelRow, local: boolean): boolean {
    const query = this.query().trim().toLowerCase();
    if (
      query !== '' &&
      !row.id.toLowerCase().includes(query) &&
      !row.title.toLowerCase().includes(query)
    ) {
      return false;
    }
    switch (this.filter()) {
      case 'recommended':
        return row.tags.includes('Recommended');
      case 'fast':
        return row.tags.includes('Fast');
      case 'local':
        return local;
      default:
        return true;
    }
  }

  private async probeLocalRuntime(): Promise<void> {
    const reachable = (call: () => Promise<unknown>): Promise<boolean> =>
      call().then(
        () => true,
        () => false,
      );
    const [ollama, lmStudio] = await Promise.all([
      reachable(() => this.bridge.llmListOllamaModels({ endpoint: LLM_CONFIG_DEFAULT_ENDPOINT })),
      reachable(() => this.bridge.llmListLmStudioModels({ endpoint: LM_STUDIO_ENDPOINT })),
    ]);
    this.localRuntime.set(ollama || lmStudio);
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
