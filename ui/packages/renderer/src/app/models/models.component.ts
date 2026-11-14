import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { RESTART_DEFERRED_LINE, type LlmModel, type WorkerRestartResponse } from '@scout/shared';

import {
  type ActiveCloudProvider,
  LlmConfigStore,
  type LlmProvider,
  providerLabel,
} from '../core/llm-config.store';
import {
  type LocalModelCatalogEntry,
  WHISPER_IDS,
  ollamaTagForCatalogId,
} from '../core/local-models.catalog';
import { LocalModelsStore, NO_TOOLS } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { IconComponent } from '../core/icon.component';
import { LocalModelsComponent } from '../sources/local-models.component';
import {
  CLAUDE_ICON,
  type CardIcon,
  GEMINI_ICON,
  OLLAMA_ICON,
  OPENROUTER_ICON,
  OPENAI_ICON,
} from '../sources/sources.component';
import { FAMILY_ORDER, type ModelTag, familyOf, isRecommended, tagsOf } from './model-tags';
import { SpeechVoicesComponent } from './speech-voices.component';

type TaskTab = 'transcription' | 'analysis' | 'translation' | 'speech';

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

interface TranslationChoice {
  readonly model: string;
  readonly provider: string;
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
  {
    provider: 'ChatGptPlan',
    key: 'chatgpt',
    title: providerLabel('ChatGptPlan'),
    label: 'ChatGPT',
  },
  { provider: 'OpenAI', key: 'openai', title: providerLabel('OpenAI'), label: 'OpenAI' },
  { provider: 'Gemini', key: 'gemini', title: providerLabel('Gemini'), label: 'Gemini' },
  { provider: 'Anthropic', key: 'claude', title: providerLabel('Anthropic'), label: 'Claude' },
  {
    provider: 'OpenRouter',
    key: 'openrouter',
    title: providerLabel('OpenRouter'),
    label: 'OpenRouter',
  },
]);

const LM_STUDIO_ENDPOINT = 'http://localhost:1234';
const ROW_CAP = 6;

const FILTERS: readonly { readonly id: ModelFilter; readonly label: string }[] = Object.freeze([
  { id: 'all', label: 'All' },
  { id: 'recommended', label: 'Recommended' },
  { id: 'fast', label: 'Fast' },
  { id: 'local', label: 'On this computer' },
]);

/** Each group's official logo, the same local files the AI Sources cards show. */
const GROUP_LOGOS: Readonly<Record<string, CardIcon>> = Object.freeze({
  chatgpt: OPENAI_ICON,
  openai: OPENAI_ICON,
  gemini: GEMINI_ICON,
  claude: CLAUDE_ICON,
  ollama: OLLAMA_ICON,
  openrouter: OPENROUTER_ICON,
});

@Component({
  selector: 'sc-models',
  standalone: true,
  imports: [
    IconComponent,
    LocalModelsComponent,
    MatButtonModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    NgTemplateOutlet,
    RouterLink,
    SpeechVoicesComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="page-head">
        <h1>Models</h1>
        <p class="lede">
          Choose the model Scout uses for each task. Each source you connect in AI Sources lists its
          models here.
        </p>
      </header>
      <nav class="tabs" role="tablist" aria-label="Model task">
        <button
          type="button"
          role="tab"
          id="tab-analysis"
          [attr.aria-selected]="tab() === 'analysis'"
          aria-controls="models-panel"
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
          id="tab-transcription"
          [attr.aria-selected]="tab() === 'transcription'"
          aria-controls="models-panel"
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
          id="tab-translation"
          [attr.aria-selected]="tab() === 'translation'"
          aria-controls="models-panel"
          [class.tab--active]="tab() === 'translation'"
          class="tab"
          data-test="tab-translation"
          (click)="setTab('translation')"
        >
          Translation
        </button>
        <button
          type="button"
          role="tab"
          id="tab-speech"
          [attr.aria-selected]="tab() === 'speech'"
          aria-controls="models-panel"
          [class.tab--active]="tab() === 'speech'"
          class="tab"
          data-test="tab-speech"
          (click)="setTab('speech')"
        >
          Speech
        </button>
      </nav>

      @if (tab() === 'speech') {
        <sc-speech-voices id="models-panel" role="tabpanel" aria-labelledby="tab-speech" />
      }

      @if (tab() === 'transcription') {
        <section
          class="panel"
          id="models-panel"
          role="tabpanel"
          aria-labelledby="tab-transcription"
          data-test="panel-transcription"
        >
          <sc-local-models kind="transcription" [embedded]="true" />
        </section>
      }
      @if (tab() === 'analysis') {
        <section
          class="panel"
          id="models-panel"
          role="tabpanel"
          aria-labelledby="tab-analysis"
          data-test="panel-analysis"
        >
          @if (
            inUse() !== null ||
            statusMessage() !== null ||
            restartFailedFor() !== null ||
            actionError() !== null
          ) {
            <section class="strip" aria-label="Model in use" data-test="in-use-strip">
              @if (inUse(); as use) {
                <div class="inuse" data-test="in-use">
                  <span class="inuse-label"><sc-icon name="check" [size]="13" />In use</span>
                  <span class="inuse-model">{{ use.model }}</span>
                  <span class="inuse-via">via {{ use.source }}</span>
                </div>
              }
              @if (inUseNoTools()) {
                <p class="no-tools-line" role="status" data-test="in-use-no-tools">
                  {{ noTools }}
                </p>
              }
              @if (actionError(); as error) {
                <div class="alert" role="alert" data-test="analysis-error">
                  <sc-icon name="error" [size]="16" />
                  <span>{{ error }}</span>
                </div>
              }
              @if (statusMessage(); as message) {
                <div
                  class="status"
                  [class.status--busy]="restarting()"
                  role="status"
                  data-test="analysis-status"
                >
                  @if (restarting()) {
                    <mat-spinner diameter="15" aria-hidden="true" />
                  }
                  <span>
                    {{ message }}
                    @if (restarting()) {
                      Chat answers resume in a few seconds.
                    }
                  </span>
                </div>
                @if (restarting()) {
                  <mat-progress-bar
                    mode="indeterminate"
                    aria-label="Restarting Scout"
                    data-test="analysis-restart-progress"
                  />
                }
              } @else if (restartFailedFor(); as label) {
                <p class="status" role="status" data-test="analysis-status">
                  Restart Scout in
                  <a routerLink="/settings" class="empty-link" data-test="analysis-restart-settings"
                    >Settings, Scout worker</a
                  >, to use {{ label }}.
                </p>
              }
            </section>
          }
          @if (inUse() === null && !noSource()) {
            <p class="nudge" data-test="no-active-model-nudge">
              No model in use for this task. Pick one below.
            </p>
          }

          <ng-container [ngTemplateOutlet]="modelTools" />

          @if (noSource()) {
            <div class="empty" data-test="no-source">
              <span class="empty-mark" aria-hidden="true"><sc-icon name="plug" [size]="22" /></span>
              <span>No source is connected yet, so there is no model to choose.</span>
              <a matButton="filled" routerLink="/sources" data-test="no-source-cta">
                <sc-icon name="plug" [size]="17" />
                Connect a source in AI Sources
              </a>
            </div>
          }

          @if (cloudBlocks().length > 0 || localListed()) {
            <div class="groups" role="radiogroup" aria-label="Analysis model">
              @for (block of cloudBlocks(); track block.provider) {
                @let view = cloudView(block);
                <details
                  class="group"
                  [attr.data-test]="'provider-' + block.key"
                  [open]="cloudSelectedModel(block.provider) !== null || narrowing()"
                  (toggle)="onGroupToggle(block.key, $event)"
                >
                  <summary [attr.data-test]="'group-summary-' + block.key">
                    <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
                      <path [attr.d]="chevronIcon" />
                    </svg>
                    <ng-container
                      [ngTemplateOutlet]="logo"
                      [ngTemplateOutletContext]="{ $implicit: block.key, name: block.title }"
                    />
                    <span class="gtitle">
                      <b>
                        {{ block.title }}
                        @if (cloudSelectedModel(block.provider) !== null) {
                          <span class="gbadge gbadge--use">In use</span>
                        } @else {
                          <span
                            class="gbadge"
                            [class.gbadge--plan]="block.provider === 'ChatGptPlan'"
                            >{{ block.provider === 'ChatGptPlan' ? 'Plan' : 'Cloud' }}</span
                          >
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
                            role="radio"
                            class="opt"
                            [class.option--selected]="cloudSelectedModel(block.provider) === row.id"
                            [attr.aria-checked]="cloudSelectedModel(block.provider) === row.id"
                            [attr.tabindex]="
                              analysisTabStop() === block.key + ':' + row.id ? 0 : -1
                            "
                            [disabled]="saving()"
                            [attr.data-test]="'model-' + block.key + '-' + row.id"
                            (click)="chooseCloud(block, row.id)"
                            (keydown)="onRowKey($event)"
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
                            @if (cloudSelectedModel(block.provider) === row.id) {
                              <ng-container [ngTemplateOutlet]="rowState" />
                            }
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

              @if (localListed() && !ollamaDisconnected()) {
                @let view = localView();
                <details
                  class="group"
                  data-test="provider-ollama"
                  [open]="localSelectedTitle() !== null || narrowing()"
                  (toggle)="onGroupToggle('ollama', $event)"
                >
                  <summary data-test="group-summary-ollama">
                    <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
                      <path [attr.d]="chevronIcon" />
                    </svg>
                    <ng-container
                      [ngTemplateOutlet]="logo"
                      [ngTemplateOutletContext]="{ $implicit: 'ollama', name: 'Ollama' }"
                    />
                    <span class="gtitle">
                      <b>
                        Local LLM
                        @if (localSelectedTitle() !== null) {
                          <span class="gbadge gbadge--use">In use</span>
                        } @else {
                          <span class="gbadge">On this computer</span>
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
                        role="radio"
                        class="opt"
                        [class.option--selected]="isLocalSelected(row.id)"
                        [attr.aria-checked]="isLocalSelected(row.id)"
                        [attr.tabindex]="analysisTabStop() === 'ollama:' + row.id ? 0 : -1"
                        [disabled]="saving()"
                        [attr.data-test]="'option-' + row.id"
                        (click)="chooseLocal(row.id)"
                        (keydown)="onRowKey($event)"
                      >
                        <span class="radio" aria-hidden="true"></span>
                        <span class="name">
                          {{ row.name }}
                          @for (tag of row.tags; track tag) {
                            <span class="tag" [class.tag--rec]="tag === 'Recommended'">{{
                              tag
                            }}</span>
                          }
                          @if (store.cannotCallTools(row.id)) {
                            <span class="tag tag--warn" [attr.data-test]="'no-tools-' + row.id"
                              >No tools</span
                            >
                          }
                          @if (row.note; as note) {
                            <small>{{ note }}</small>
                          }
                        </span>
                        @if (isLocalSelected(row.id)) {
                          <ng-container [ngTemplateOutlet]="rowState" />
                        }
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
              }
            </div>
          }
          @if (localRuntime() === true && !localListed()) {
            <div class="empty" data-test="empty-state">
              <p>No models downloaded yet.</p>
              <a routerLink="/sources" class="empty-link" data-test="empty-cta">
                Download one on the Ollama card in AI Sources.
              </a>
            </div>
          } @else if (localRuntime() === null) {
            <div class="checking" role="status" data-test="local-checking">
              <mat-spinner diameter="16" aria-hidden="true" />
              <span>Checking the local runtime...</span>
              <span class="skeleton" aria-hidden="true"><span></span><span></span></span>
            </div>
          } @else if (localRuntime() === false) {
            <div class="offline" data-test="local-not-connected">
              <span
                class="logo"
                role="img"
                aria-label="Local LLM logo"
                data-test="logo-local-runtime"
              >
                <sc-icon name="cube" [size]="20" />
              </span>
              <span class="gtitle">
                <b>Local LLM <span class="gbadge">Not connected</span></b>
                <span>
                  No local runtime is running. Start Ollama or LM Studio and download a model in AI
                  Sources to see it here.
                </span>
              </span>
              <a routerLink="/sources" class="go">Open AI Sources</a>
            </div>
          }
        </section>
      }
      @if (tab() === 'translation') {
        <section
          class="panel"
          id="models-panel"
          role="tabpanel"
          aria-labelledby="tab-translation"
          data-test="panel-translation"
        >
          <header class="panel-head">
            <h2>Translation</h2>
            <p class="lede">Choose which model translates the lines of a live transcript</p>
            <p class="lede lede--note">
              Translation runs on the provider in use for Analysis, so only its models are listed.
              The target language is chosen in the live transcript.
            </p>
          </header>

          @if (needsVoiceModel()) {
            <div class="notice" role="note" data-test="translation-needs-voice">
              <sc-icon name="info" [size]="16" />
              <span class="notice-text">
                Live translation needs a voice model to transcribe first; none is installed.
              </span>
              <button
                type="button"
                class="notice-action"
                data-test="translation-choose-voice"
                (click)="setTab('transcription')"
              >
                <sc-icon name="mic" [size]="16" />
                Choose a voice model
              </button>
            </div>
          }

          @if (listening()) {
            <p class="nudge" role="status" data-test="translation-listen-warning">
              A live listen is running. Saving a choice restarts Scout once the listen ends.
            </p>
          }

          @if (translationProvider() === 'None') {
            <p class="nudge" data-test="translation-no-provider">
              No AI Source is in use, so nothing is translated. Connect one in
              <a routerLink="/sources" class="empty-link">AI Sources</a>.
            </p>
          } @else {
            @if (translationStale(); as stale) {
              <p class="nudge" data-test="translation-stale">
                Not in use: {{ stale.model }} was chosen for {{ labelOf(stale.provider) }}, which is
                not the provider in use. Translation uses the Analysis model.
              </p>
            }

            <ng-container [ngTemplateOutlet]="modelTools" />

            @let view = translationView();
            <div class="group group--flat" data-test="translation-provider">
              <div class="gtitle">
                <b>{{ labelOf(translationProvider()) }}</b>
                <span data-test="translation-selected">
                  Selected:
                  <span class="sel">{{ translationModel() ?? 'Same as Analysis' }}</span>
                </span>
              </div>
              <div class="list" role="radiogroup" aria-label="Translation model">
                <button
                  type="button"
                  role="radio"
                  class="opt"
                  [class.option--selected]="translationModel() === null"
                  [attr.aria-checked]="translationModel() === null"
                  [attr.tabindex]="translationTabStop() === null ? 0 : -1"
                  [disabled]="saving()"
                  data-test="translation-same"
                  (click)="chooseTranslation(null)"
                  (keydown)="onRowKey($event)"
                >
                  <span class="radio" aria-hidden="true"></span>
                  <span class="name">
                    Same as Analysis
                    @if (inUse(); as use) {
                      <small>{{ use.model }}</small>
                    }
                  </span>
                </button>
                @switch (translationList().state) {
                  @case ('failed') {
                    <p
                      class="form-error list-note"
                      role="alert"
                      data-test="translation-models-error"
                    >
                      Could not load the {{ labelOf(translationProvider()) }} model list.
                    </p>
                  }
                  @case ('ready') {
                    @for (row of view.rows; track row.id) {
                      @if (row.familyHead) {
                        <div class="fam">{{ row.family }}</div>
                      }
                      <button
                        type="button"
                        role="radio"
                        class="opt"
                        [class.option--selected]="translationModel() === row.id"
                        [attr.aria-checked]="translationModel() === row.id"
                        [attr.tabindex]="translationTabStop() === row.id ? 0 : -1"
                        [disabled]="saving()"
                        [attr.data-test]="'translation-' + row.id"
                        (click)="chooseTranslation(row.id)"
                        (keydown)="onRowKey($event)"
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
                      <p class="list-note" data-test="translation-no-match">No model matches</p>
                    }
                    @if (view.hidden > 0) {
                      <button
                        type="button"
                        class="more"
                        data-test="translation-show-all"
                        (click)="expand('translation')"
                      >
                        Show all {{ countLabel(view.total) }}
                      </button>
                    }
                  }
                  @default {
                    <p class="provider-desc list-note" data-test="translation-models-loading">
                      Loading models...
                    </p>
                  }
                }
              </div>
            </div>
          }

          @if (statusMessage(); as message) {
            <p class="status" role="status" data-test="translation-status">{{ message }}</p>
          } @else if (restartFailedFor(); as label) {
            <p class="status" role="status" data-test="translation-status">
              Restart Scout in
              <a routerLink="/settings" class="empty-link" data-test="translation-restart-settings"
                >Settings, Scout worker</a
              >, to use {{ label }}.
            </p>
          }
          @if (actionError(); as error) {
            <p class="form-error" role="alert" data-test="translation-error">{{ error }}</p>
          }
        </section>
      }

      <ng-template #logo let-key let-name="name">
        @if (logoOf(key); as mark) {
          <span
            class="logo"
            role="img"
            [attr.aria-label]="name + ' logo'"
            [attr.data-test]="'logo-' + key"
          >
            <img class="mark mark--dark" [src]="mark.dark" alt="" width="20" height="20" />
            <img class="mark mark--light" [src]="mark.light" alt="" width="20" height="20" />
          </span>
        }
      </ng-template>

      <ng-template #rowState>
        @if (restarting()) {
          <span class="row-state" data-test="row-restarting">Restarting Scout...</span>
        } @else if (restartFailedFor() !== null) {
          <span class="row-state row-state--failed" data-test="row-restart-failed"
            >Restart failed</span
          >
        }
      </ng-template>

      <ng-template #modelTools>
        <div class="tools">
          <div class="search">
            <label for="model-search">Search models</label>
            <input
              id="model-search"
              type="search"
              placeholder="For example mini, flash, gemma"
              data-test="model-search"
              [value]="query()"
              (input)="onSearch($event)"
            />
          </div>
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
      </ng-template>
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .page {
        padding: 24px 32px 40px;
        max-width: 1200px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 16px;
        box-sizing: border-box;
        font-size: 14px;
      }
      .page-head {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .page-head h1 {
        margin: 0;
        font-size: 20px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .tabs {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        border-bottom: 1px solid var(--sc-line);
      }
      .tab {
        height: 42px;
        padding: 0 16px;
        border: 0;
        border-bottom: 2px solid transparent;
        background: transparent;
        color: var(--sc-muted);
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .tab:hover {
        color: var(--sc-fg);
      }
      .tab--active {
        color: var(--sc-fg);
        font-weight: 600;
        border-bottom-color: var(--sc-accent);
      }
      .panel {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .groups {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .panel-head {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .panel-head h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .lede--note {
        margin-top: 2px;
      }
      .empty {
        border: 1px dashed var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 28px 16px;
        text-align: center;
        color: var(--sc-fg-2);
        display: flex;
        flex-direction: column;
        gap: 10px;
        align-items: center;
      }
      .empty p {
        margin: 0;
      }
      .empty-mark {
        width: 44px;
        height: 44px;
        border-radius: 50%;
        background: var(--sc-surface-2);
        color: var(--sc-muted);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .empty-link,
      .go,
      .more {
        color: var(--sc-accent);
        font-weight: 500;
        text-decoration: none;
      }
      .empty-link:hover,
      .go:hover,
      .more:hover {
        text-decoration: underline;
      }
      .provider-desc {
        margin: 0;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .nudge {
        margin: 0;
        color: var(--sc-on-warn-container);
        background: var(--sc-warn-container);
        font-size: 13px;
        border-radius: 10px;
        padding: 10px 12px;
      }
      .nudge .empty-link {
        color: var(--sc-on-warn-container);
        text-decoration: underline;
      }
      .notice {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
        font-size: 13px;
      }
      .notice-text {
        margin-right: auto;
      }
      .notice-action {
        height: 36px;
        padding: 0 14px;
        border-radius: 18px;
        border: 1px solid var(--sc-on-info-container);
        background: transparent;
        color: var(--sc-on-info-container);
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .form-error {
        margin: 0;
        color: var(--sc-error);
        font-size: 13px;
      }
      .status {
        margin: 0;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .status--busy {
        display: flex;
        align-items: center;
        gap: 6px;
        color: var(--sc-on-info-container);
      }
      .alert {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
        font-size: 13px;
      }
      .strip {
        position: sticky;
        top: 0;
        z-index: 3;
        display: flex;
        flex-direction: column;
        gap: 8px;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: 14px;
        padding: 14px 16px;
        box-shadow: var(--mat-sys-level2);
      }
      .inuse {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
      }
      .inuse-label {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 3px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 600;
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .inuse-model {
        font-weight: 600;
      }
      .inuse-via {
        color: var(--sc-muted);
        font-size: 13px;
      }
      .tools {
        display: flex;
        gap: 12px;
        align-items: flex-end;
        flex-wrap: wrap;
      }
      .search {
        flex: 1 1 280px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .search label {
        font-size: 13px;
        font-weight: 500;
      }
      .search input {
        height: 40px;
        padding: 0 12px;
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
        box-sizing: border-box;
      }
      .chips {
        display: flex;
        gap: 2px;
        padding: 3px;
        border: 1px solid var(--sc-line);
        border-radius: 20px;
        background: var(--sc-bg);
      }
      .chip {
        height: 32px;
        padding: 0 14px;
        border: 0;
        border-radius: 16px;
        background: transparent;
        color: var(--sc-muted);
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .chip[aria-pressed='true'] {
        background: var(--sc-pressed);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .group {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
      }
      summary {
        list-style: none;
        cursor: pointer;
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        min-height: 56px;
        border-radius: var(--sc-radius);
      }
      summary::-webkit-details-marker {
        display: none;
      }
      .chev {
        width: 16px;
        height: 16px;
        flex: none;
        fill: none;
        stroke: var(--sc-muted);
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
        border-radius: 10px;
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        color: var(--sc-fg);
        display: flex;
        align-items: center;
        justify-content: center;
        flex: none;
      }
      .mark {
        object-fit: contain;
      }
      .mark--light {
        display: none;
      }
      :host-context([data-theme='light']) .mark--dark {
        display: none;
      }
      :host-context([data-theme='light']) .mark--light {
        display: inline;
      }
      .gtitle {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .gtitle b {
        font-weight: 600;
        font-size: 14px;
        color: var(--sc-fg);
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
      }
      .sel {
        font-family: var(--sc-mono);
        color: var(--sc-fg-2);
      }
      .gbadge {
        display: inline-flex;
        align-items: center;
        padding: 2px 9px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 600;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .gbadge--use {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .gbadge--plan {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .count {
        color: var(--sc-muted);
        font-size: 12px;
        flex: none;
        font-variant-numeric: tabular-nums;
      }
      .list {
        border-top: 1px solid var(--sc-line);
        display: flex;
        flex-direction: column;
        gap: 6px;
        padding: 8px;
      }
      .list-note {
        margin: 0;
        padding: 8px;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .list-note.form-error {
        color: var(--sc-error);
      }
      .fam {
        font-size: 12px;
        font-weight: 600;
        color: var(--sc-muted);
        padding: 8px 6px 2px;
      }
      .opt {
        display: flex;
        gap: 12px;
        align-items: center;
        padding: 10px 14px;
        border: 1px solid var(--sc-line);
        border-radius: 12px;
        background: var(--sc-surface);
        color: var(--sc-fg);
        font: inherit;
        text-align: left;
        cursor: pointer;
        min-height: 48px;
        width: 100%;
      }
      .opt:hover:not(:disabled) {
        background: var(--sc-surface-2);
      }
      .opt.option--selected {
        background: var(--sc-surface-2);
        border-color: var(--sc-accent);
      }
      .opt:disabled {
        cursor: not-allowed;
        opacity: 0.7;
      }
      .radio {
        width: 18px;
        height: 18px;
        flex: 0 0 18px;
        border-radius: 50%;
        border: 2px solid var(--sc-muted);
        box-sizing: border-box;
        position: relative;
      }
      .option--selected .radio {
        border-color: var(--sc-accent);
      }
      .option--selected .radio::after {
        content: '';
        position: absolute;
        inset: 3px;
        border-radius: 50%;
        background: var(--sc-accent);
      }
      .name {
        font-weight: 600;
        font-size: 14px;
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        flex: 1 1 auto;
        min-width: 0;
      }
      .name small {
        font-weight: 400;
        font-size: 12px;
        color: var(--sc-muted);
        flex-basis: 100%;
      }
      .tag {
        padding: 1px 8px;
        border-radius: 10px;
        font-size: 11px;
        font-weight: 600;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .tag--rec {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .tag--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .no-tools-line {
        margin: 0;
        padding: 8px 10px;
        border-radius: 8px;
        font-size: 13px;
        line-height: 1.45;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .row-state {
        flex: none;
        font-size: 12px;
        white-space: nowrap;
        color: var(--sc-on-info-container);
      }
      .row-state--failed {
        color: var(--sc-error);
      }
      .checking {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px 14px;
        border: 1px dashed var(--sc-line);
        border-radius: 12px;
        color: var(--sc-fg-2);
        font-size: 13px;
      }
      .skeleton {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .skeleton span {
        height: 10px;
        width: 70%;
        border-radius: 5px;
        background: var(--sc-surface-2);
      }
      .skeleton span + span {
        width: 45%;
      }
      .more {
        display: flex;
        align-items: center;
        justify-content: center;
        padding: 10px 16px;
        border: 0;
        font: inherit;
        font-size: 13px;
        background: transparent;
        width: 100%;
        cursor: pointer;
        min-height: 44px;
      }
      .offline {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
        padding: 12px 14px;
        border: 1px dashed var(--sc-line);
        border-radius: 12px;
        color: var(--sc-fg-2);
      }
      .offline .gtitle {
        color: var(--sc-fg-2);
      }
      .go {
        font-size: 13px;
        flex: none;
        min-height: 44px;
        display: inline-flex;
        align-items: center;
      }
      .group--flat {
        padding: 12px 14px;
      }
      @media (prefers-reduced-motion: reduce) {
        .chev {
          transition: none;
        }
      }
    `,
  ],
})
export class ModelsComponent implements OnInit {
  readonly store = inject(LocalModelsStore);
  private readonly llmConfig = inject(LlmConfigStore);
  /** True after the owner disconnected Ollama: Analysis lists none of its models. */
  readonly ollamaDisconnected = this.llmConfig.ollamaDisconnected;
  private readonly bridge = inject(ScoutBridgeService);
  private readonly route = inject(ActivatedRoute);

  readonly filters = FILTERS;
  readonly chevronIcon = 'M6 3l5 5-5 5';

  readonly tab = signal<TaskTab>(
    ((tab) =>
      tab === 'transcription' || tab === 'translation' || tab === 'speech' ? tab : 'analysis')(
      this.route.snapshot.queryParamMap.get('tab'),
    ),
  );
  readonly saving = signal<boolean>(false);
  readonly statusMessage = signal<string | null>(null);
  /** The source Scout should switch to after a restart that failed, so the line can point to Settings, Scout worker. */
  readonly restartFailedFor = signal<string | null>(null);
  private readonly saveError = signal<string | null>(null);
  private readonly restartError = signal<string | null>(null);
  /** What failed: the save of a choice, the restart, or both, then each in a sentence of its own. */
  readonly actionError = computed<string | null>(() => {
    const save = this.saveError();
    const restart = this.restartError();
    const sentence = (reason: string): string => (reason.endsWith('.') ? reason : `${reason}.`);
    return save !== null && restart !== null
      ? `The choice was not saved: ${sentence(save)} Scout did not restart: ${sentence(restart)}`
      : (save ?? restart);
  });
  /** True while main is asked to restart Scout and has not answered the latest request yet. */
  readonly restarting = signal<boolean>(false);
  private restartRequests = 0;
  private restartFailure: string | null = null;
  private readonly lists = signal<Partial<Record<ActiveCloudProvider, ModelList>>>({});
  readonly query = signal<string>('');
  readonly filter = signal<ModelFilter>('all');
  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Whether Ollama or LM Studio answers on its default endpoint, the check AI Sources makes; null until it is known. */
  readonly localRuntime = signal<boolean | null>(null);
  /** The saved translation model and the provider it was chosen under; null while none is saved. */
  readonly translationChoice = signal<TranslationChoice | null>(null);
  /** Whether main has a verified Whisper model, read when the Translation tab opens; null until it answers or when it cannot. */
  readonly voiceModelInstalled = signal<boolean | null>(null);
  /** True while no Whisper model is installed, so live translation has nothing to translate. */
  readonly needsVoiceModel = computed<boolean>(
    () =>
      !(this.voiceModelInstalled() ?? this.store.downloaded().some((id) => WHISPER_IDS.has(id))),
  );
  /** Whether a live listen is open in the quick-ask window, read when the Translation tab opens and kept current. */
  readonly listening = signal<boolean>(false);

  readonly narrowing = computed<boolean>(
    () => this.query().trim() !== '' || this.filter() !== 'all',
  );

  /** Every chat model the local Ollama lists; a model that only turns text into embeddings is never a row. */
  readonly localAnalysis = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.downloadedFor('analysis').filter((m) => !this.store.isEmbeddingsOnly(m.id)),
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

  /** What the desktop says about a model that cannot call tools. */
  readonly noTools = NO_TOOLS;

  /** True while the Ollama model in use, with Ollama the provider in use or none saved, cannot call tools. */
  readonly inUseNoTools = computed<boolean>(() => {
    const id = this.store.selectedFor('analysis');
    return this.localSelectedTitle() !== null && id !== null && this.store.cannotCallTools(id);
  });

  readonly cloudBlocks = computed<readonly CloudBlock[]>(() =>
    CLOUD_BLOCKS.filter((block) => this.llmConfig.connectedProviders().has(block.provider)),
  );

  /** True when no cloud source is connected and no local runtime answers, so there is no model to list. */
  readonly noSource = computed<boolean>(
    () => this.cloudBlocks().length === 0 && this.localRuntime() === false,
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

  /** The provider translation runs on: the one Analysis uses, Ollama while none is saved. */
  readonly translationProvider = computed<LlmProvider>(() => this.llmConfig.provider() ?? 'Ollama');

  /** The saved translation model when it was chosen under the provider in use; null means the Analysis model. */
  readonly translationModel = computed<string | null>(() => {
    const choice = this.translationChoice();
    return choice !== null && choice.provider === this.translationProvider() ? choice.model : null;
  });

  readonly translationStale = computed<TranslationChoice | null>(() => {
    const choice = this.translationChoice();
    return choice !== null && choice.provider !== this.translationProvider() ? choice : null;
  });

  readonly translationList = computed<ModelList>(() => {
    const provider = this.translationProvider();
    return provider === 'Ollama' || provider === 'None'
      ? { state: 'ready', models: [] }
      : this.listOf(provider);
  });

  readonly translationView = computed<RowView>(() => {
    const provider = this.translationProvider();
    const rows =
      provider === 'Ollama'
        ? this.localAnalysis().flatMap((m): ModelRow[] => {
            const tag = ollamaTagForCatalogId(m.id);
            return tag === null
              ? []
              : [
                  {
                    id: tag,
                    name: m.title,
                    title: m.title,
                    note: this.localNote(m),
                    family: null,
                    tags: tagsOf(m.id, m),
                  },
                ];
          })
        : provider === 'None'
          ? []
          : this.cloudRows(provider);
    return this.view('translation', rows, this.translationModel(), provider === 'Ollama');
  });

  readonly localView = computed<RowView>(() =>
    this.view(
      'ollama',
      this.localAnalysis().map((m) => ({
        id: m.id,
        name: m.title,
        title: m.title,
        note: this.localNote(m),
        family: null,
        tags: tagsOf(m.id, m),
      })),
      this.ollamaInUse() ? this.store.selectedFor('analysis') : null,
      true,
    ),
  );

  /** The Analysis groups open on the screen, by key, as their toggle events report them; a group that leaves the screen is dropped, so it comes back as its details element starts. */
  private readonly openGroups = signal<ReadonlySet<string>>(new Set());

  /** The one Analysis row in the tab order, as `<group>:<model>`: the checked row in an open group, else the first row of the first open group; null while every group is closed. */
  readonly analysisTabStop = computed<string | null>(() => {
    const open = this.openGroups();
    const rows = [
      ...this.cloudBlocks().flatMap((block) =>
        this.listOf(block.provider).state === 'ready' && open.has(block.key)
          ? this.cloudView(block).rows.map((row) => ({
              key: `${block.key}:${row.id}`,
              checked: this.cloudSelectedModel(block.provider) === row.id,
            }))
          : [],
      ),
      ...(this.localListed() && open.has('ollama')
        ? this.localView().rows.map((row) => ({
            key: `ollama:${row.id}`,
            checked: this.isLocalSelected(row.id),
          }))
        : []),
    ];
    return (rows.find((row) => row.checked) ?? rows[0])?.key ?? null;
  });

  /** The Translation model row in the tab order while it is chosen and shown; null puts Same as Analysis, the first row, there. */
  readonly translationTabStop = computed<string | null>(() => {
    const model = this.translationModel();
    return model !== null &&
      this.translationList().state === 'ready' &&
      this.translationView().rows.some((row) => row.id === model)
      ? model
      : null;
  });

  constructor() {
    effect(() => {
      const rendered = new Set([
        ...this.cloudBlocks().map((block) => block.key),
        ...(this.localListed() ? ['ollama'] : []),
      ]);
      this.openGroups.update((keys) =>
        [...keys].every((key) => rendered.has(key))
          ? keys
          : new Set([...keys].filter((key) => rendered.has(key))),
      );
    });
    inject(DestroyRef).onDestroy(
      this.bridge.onLocalListening(({ listening }) => this.listening.set(listening)),
    );
  }

  async ngOnInit(): Promise<void> {
    const probe = this.probeLocalRuntime();
    if (this.tab() === 'translation') {
      void this.readListening();
      void this.readVoiceModels();
    }
    await this.llmConfig.loadFromVault();
    const chatgpt = await this.bridge.chatgptStatus().catch(() => null);
    this.llmConfig.setChatGptConnected(
      chatgpt !== null && chatgpt.signedIn && chatgpt.planUsageGranted,
    );
    const active = this.llmConfig.activeCloudProvider();
    const listed = new Set(this.cloudBlocks().map((block) => block.provider));
    if (active !== null) listed.add(active);
    await Promise.all([
      probe,
      this.readTranslationChoice(),
      ...[...listed].map((provider) => this.loadModels(provider)),
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
    return this.view(
      block.key,
      this.cloudRows(block.provider),
      this.cloudSelectedModel(block.provider),
      false,
    );
  }

  private cloudRows(provider: ActiveCloudProvider): ModelRow[] {
    const models = this.modelsOf(provider);
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
    return rows;
  }

  onGroupToggle(key: string, event: Event): void {
    const open = (event.target as HTMLDetailsElement).open;
    this.openGroups.update((keys) => {
      const next = new Set(keys);
      if (open) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  /** Moves the focus, never the choice, to the next or previous row of the radio group with the arrow keys, wrapping and skipping the rows of a closed group; Space or Enter chooses, as a click does. */
  onRowKey(event: KeyboardEvent): void {
    const step =
      event.key === 'ArrowDown' || event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowUp' || event.key === 'ArrowLeft'
          ? -1
          : 0;
    if (step === 0) return;
    event.preventDefault();
    const row = event.currentTarget as HTMLElement;
    const rows = [
      ...(row.closest('[role="radiogroup"]')?.querySelectorAll<HTMLElement>('[role="radio"]') ??
        []),
    ].filter((radio) => radio.closest('details:not([open])') === null);
    rows[(rows.indexOf(row) + step + rows.length) % rows.length]?.focus();
  }

  setTab(tab: TaskTab): void {
    this.tab.set(tab);
    if (tab === 'translation') {
      void this.readListening();
      void this.readVoiceModels();
    }
  }

  labelOf(provider: string): string {
    return provider === 'ChatGptPlan' ? 'ChatGPT' : provider;
  }

  /** Saves `model` as the translation model under the provider in use, or with null goes back to the Analysis model; a running Scout restarts when the model translation uses changes. */
  async chooseTranslation(model: string | null): Promise<void> {
    if (this.saving()) return;
    if (model === null ? this.translationChoice() === null : model === this.translationModel()) {
      return;
    }
    const provider = this.translationProvider();
    const before = this.translationModel();
    this.beginAction();
    try {
      if (model === null) {
        await this.bridge.vaultDelete({ key: 'llm.translation-model' });
        await this.bridge.vaultDelete({ key: 'llm.translation-provider' });
        this.translationChoice.set(null);
      } else {
        await this.bridge.vaultStore({ key: 'llm.translation-model', value: model });
        await this.bridge.vaultStore({ key: 'llm.translation-provider', value: provider });
        this.translationChoice.set({ model, provider });
      }
    } catch (err: unknown) {
      this.saveError.set(
        err instanceof Error ? err.message : 'Could not save the translation model.',
      );
      return;
    } finally {
      this.saving.set(false);
    }
    if (model === before) return;
    await this.restartIfRunning(
      model === null ? 'the Analysis model for translation' : `${model} for translation`,
    );
  }

  /** A group's official logo files, or null for a group without one. */
  logoOf(key: string): { readonly dark: string; readonly light: string } | null {
    const icon = GROUP_LOGOS[key];
    return icon?.kind === 'asset' ? icon : null;
  }

  /** A catalog model's description; for any other the details Ollama lists, or nothing when none is known. */
  private localNote(m: LocalModelCatalogEntry): string | null {
    return m.description !== '' ? m.description : this.store.detailsOf(m.id) || null;
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
    if (ollamaTagForCatalogId(id) === null || this.saving() || this.isLocalSelected(id)) return;
    this.beginAction();
    try {
      await this.store.select('analysis', id);
      await this.llmConfig.useOllama();
    } catch (err: unknown) {
      this.saveError.set(err instanceof Error ? err.message : 'Could not switch to Ollama.');
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
      this.saveError.set(
        err instanceof Error ? err.message : `Could not switch to ${block.title}.`,
      );
      return;
    } finally {
      this.saving.set(false);
    }
    await this.restartIfRunning(block.title);
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
      this.store.refreshOllama(),
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

  private async readTranslationChoice(): Promise<void> {
    const read = (key: 'llm.translation-model' | 'llm.translation-provider') =>
      this.bridge.vaultGetPublic({ key }).then(
        (res) => res.value,
        () => null,
      );
    const [model, provider] = await Promise.all([
      read('llm.translation-model'),
      read('llm.translation-provider'),
    ]);
    this.translationChoice.set(model !== null && provider !== null ? { model, provider } : null);
  }

  private async readVoiceModels(): Promise<void> {
    try {
      const { installed } = await this.bridge.whisperListInstalled();
      this.voiceModelInstalled.set(installed.some((model) => model.verified));
    } catch {
      this.voiceModelInstalled.set(null);
    }
  }

  private async readListening(): Promise<void> {
    const { listening } = await this.bridge.localListening().catch(() => ({ listening: false }));
    this.listening.set(listening);
  }

  private beginAction(): void {
    this.saving.set(true);
    if (!this.restarting()) {
      this.statusMessage.set(null);
      this.restartFailure = null;
    }
    this.restartFailedFor.set(null);
    this.saveError.set(null);
    this.restartError.set(null);
  }

  private async restartIfRunning(label: string): Promise<void> {
    const request = ++this.restartRequests;
    this.restarting.set(true);
    this.restartFailedFor.set(null);
    this.restartError.set(null);
    this.statusMessage.set(`Restarting Scout to use ${label}.`);
    let result: WorkerRestartResponse;
    try {
      result = await this.bridge.workerRestart({ reason: label });
    } catch (err: unknown) {
      result = {
        outcome: 'failed',
        reason: err instanceof Error ? err.message : 'Could not restart Scout.',
      };
    }
    if (result.outcome === 'failed') this.restartFailure = result.reason;
    else if (result.outcome !== 'none') this.restartFailure = null;
    if (request !== this.restartRequests) return;
    this.restarting.set(false);
    const failure = this.restartFailure;
    if (failure !== null) {
      this.statusMessage.set(null);
      this.restartFailedFor.set(label);
      this.restartError.set(failure);
      return;
    }
    this.statusMessage.set(
      result.outcome === 'none'
        ? `Scout will use ${label} the next time it starts.`
        : result.outcome === 'deferred'
          ? RESTART_DEFERRED_LINE
          : null,
    );
  }
}
