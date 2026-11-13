import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import {
  OLLAMA_MODEL_NAME_RULE,
  OLLAMA_MODEL_NOT_FOUND,
  isOllamaModelName,
  readIpcError,
  type WhisperVariant,
} from '@scout/shared';

import {
  SKILL_MODEL,
  WHISPER_IDS,
  formatBytes,
  lookupCatalog,
  ollamaTagForCatalogId,
  type LocalModelBadge,
  type LocalModelCatalogEntry,
  type LocalModelKind,
} from '../core/local-models.catalog';
import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { LlmConfigStore } from '../core/llm-config.store';
import {
  DOWNLOAD_CANCELLED,
  LocalModelsStore,
  NO_TOOLS,
  ollamaKeyForName,
} from '../core/local-models.store';
import { LogStore } from '../core/log.store';
import { ScoutBridgeService } from '../core/scout-bridge';

const OLLAMA_ENDPOINT = 'http://localhost:11434';
const OLLAMA_LIBRARY = 'https://ollama.com/library';
const NOT_ANSWERING =
  'Ollama is not answering. Open the Ollama app or run ollama serve, then scan again.';
const POLL_MS = 4000;
export const SWITCH_DEFERRED =
  'The voice model is ready; Scout restarts to use it when the live listen ends.';

const ollamaTagFor = ollamaTagForCatalogId;

const CATALOG_TO_WHISPER: Readonly<Record<string, WhisperVariant>> = Object.freeze({
  'whisper-tiny': 'tiny',
  'whisper-base': 'base',
  'whisper-small': 'small',
  'whisper-large-v3': 'large-v3',
  'whisper-large-v3-turbo': 'large-v3-turbo',
});

function whisperVariantFor(catalogId: string): WhisperVariant | null {
  return CATALOG_TO_WHISPER[catalogId] ?? null;
}

const GiB = 1024 * 1024 * 1024;
const MiB = 1024 * 1024;

/** What a Download or a Pull by name of an Ollama model that failed says, from the code main put in the message. */
function pullFailure(tag: string, err: unknown): string {
  const { code, message } = readIpcError(err);
  if (code === OLLAMA_MODEL_NOT_FOUND) return `The Ollama library has no model named ${tag}.`;
  if (code === 'llm/ollama-unreachable') return NOT_ANSWERING;
  return `Ollama could not download ${tag}: ${message}`;
}

@Component({
  selector: 'sc-local-models',
  standalone: true,
  imports: [IconComponent, NgTemplateOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (tab() === 'analysis') {
      <div class="ollama" data-test="kind-ollama">
        <section
          class="server"
          data-test="server-panel"
          [attr.aria-labelledby]="'local-' + tab() + '-title'"
        >
          <div class="server-head">
            <div class="server-titles">
              <h3 class="list-title" [id]="'local-' + tab() + '-title'">{{ serverTitle() }}</h3>
              <p class="panel-sub" data-test="panel-sub">{{ serverSub() }}</p>
            </div>
            <span
              class="chip"
              [class.chip--ok]="store.ollamaAnswers() === true"
              data-test="panel-status"
            >
              {{ serverState() }}
            </span>
          </div>
          <dl class="metrics">
            <div>
              <dt>SELECTED</dt>
              <dd data-test="metric-selected">{{ selectedTitle() ?? '—' }}</dd>
            </div>
            <div>
              <dt>DOWNLOADED</dt>
              <dd data-test="metric-downloaded">{{ downloadedCount() }}</dd>
            </div>
            <div>
              <dt>STORAGE</dt>
              <dd data-test="metric-storage">{{ storageLabel() }}</dd>
            </div>
          </dl>
          <p class="panel-sub" data-test="server-line">{{ serverLine() }}</p>
          @if (inUseNoTools()) {
            <p class="no-tools-line" role="status" data-test="in-use-no-tools">{{ noTools }}</p>
          }
        </section>
        <h3 class="list-title" id="skill-model-title">Skill matching</h3>
        <ul class="models" aria-labelledby="skill-model-title">
          <li
            class="model"
            [class.model--installed]="skillInstalled()"
            [attr.data-test]="'row-' + skill.id"
            [attr.aria-labelledby]="'model-' + skill.id"
          >
            <div class="model-line">
              <div class="model-text">
                <span class="model-title">
                  <span [id]="'model-' + skill.id">{{ skill.tag }}</span>
                  @if (skillInstalled()) {
                    <span class="chip chip--ok" data-test="skill-installed">
                      <sc-icon name="check" [size]="13" />
                      Installed
                    </span>
                  }
                </span>
                <span class="model-meta">{{ formatSize(skill.sizeBytes) }} on disk</span>
              </div>
              @if (progressOf(skill.id) !== null) {
                <button
                  type="button"
                  class="pill"
                  [attr.data-test]="'cancel-' + skill.id"
                  [attr.aria-label]="'Cancel the download of ' + skill.tag"
                  (click)="onCancel(skill.id)"
                >
                  <sc-icon name="close" [size]="16" />
                  Cancel
                </button>
              } @else if (!skillInstalled()) {
                <button
                  type="button"
                  class="pill"
                  [attr.data-test]="'download-' + skill.id"
                  [attr.aria-label]="'Download ' + skill.tag"
                  (click)="onDownload(skill.id)"
                >
                  <sc-icon name="download" [size]="16" />
                  Download
                </button>
              }
            </div>
            <p class="model-desc">
              Scout uses this model to match a message to a skill. Without it, Scout picks a skill
              by the words of the message.
            </p>
            @if (progressOf(skill.id) !== null) {
              <div class="progress" [attr.data-test]="'progress-' + skill.id" aria-live="polite">
                <div
                  class="progress-bar"
                  role="progressbar"
                  aria-valuemin="0"
                  aria-valuemax="100"
                  [attr.aria-valuenow]="progressOf(skill.id)"
                  [attr.aria-label]="'Downloading ' + skill.tag"
                >
                  <div class="progress-fill" [style.width.%]="progressOf(skill.id)"></div>
                </div>
                <span class="model-meta">{{ skillProgressText() }}</span>
              </div>
            }
            @if (errorOf(skill.id); as err) {
              <p class="row-error" role="alert" [attr.data-test]="'error-' + skill.id">
                <sc-icon name="error" [size]="14" />
                <span>{{ err }}</span>
                <a routerLink="/logs" class="row-error-link" data-test="goto-logs">See logs</a>
              </p>
            }
          </li>
        </ul>
        <h3 class="list-title">Installed</h3>
        @if (installedModels().length === 0) {
          <div class="empty" data-test="no-installed">
            <svg class="empty-glyph" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01" />
            </svg>
            No models installed yet. Pull one below.
          </div>
        }
        <ul class="models" data-test="installed-models">
          @for (m of installedModels(); track m.id) {
            <ng-container
              [ngTemplateOutlet]="ollamaRow"
              [ngTemplateOutletContext]="{ $implicit: m }"
            />
          }
        </ul>
        <h3 class="list-title" id="pull-title">Pull by name</h3>
        <form
          class="pull"
          aria-labelledby="pull-title"
          data-test="pull-by-name"
          (submit)="onPull($event)"
        >
          <label class="pull-label" for="pull-name"
            >Any model of the Ollama library, as name or name:tag</label
          >
          <div class="model-line">
            <input
              id="pull-name"
              class="pull-input"
              type="text"
              autocomplete="off"
              spellcheck="false"
              placeholder="For example llama3.2:3b"
              data-test="pull-name"
              [class.pull-input--invalid]="pullRefused()"
              [value]="pullName()"
              [disabled]="pullBusy()"
              [attr.aria-invalid]="pullRefused()"
              [attr.aria-describedby]="
                pullRefused() || pullError() !== null ? 'pull-name-error' : null
              "
              (input)="onPullInput($event)"
            />
            @if (pullBusy()) {
              <button
                type="button"
                class="pill"
                data-test="pull-cancel"
                [attr.aria-label]="'Cancel the download of ' + pullTag()"
                (click)="onCancel(pullKey()!)"
              >
                <sc-icon name="close" [size]="16" />
                Cancel
              </button>
            } @else {
              <button type="submit" class="pill" data-test="pull-submit" [disabled]="!canPull()">
                <sc-icon name="download" [size]="16" />
                Pull
              </button>
            }
          </div>
          @if (pullRefused()) {
            <p class="pull-refused" id="pull-name-error" data-test="pull-refused">
              {{ nameRule }}
            </p>
          }
          @if (pullBusy()) {
            <div class="progress" data-test="pull-progress" aria-live="polite">
              <div
                class="progress-bar"
                role="progressbar"
                aria-valuemin="0"
                aria-valuemax="100"
                [attr.aria-valuenow]="progressOf(pullKey()!)"
                [attr.aria-label]="'Downloading ' + pullTag()"
              >
                <div class="progress-fill" [style.width.%]="progressOf(pullKey()!)"></div>
              </div>
              <span class="model-meta">{{ progressOf(pullKey()!) }}%</span>
            </div>
          }
          @if (pullError(); as err) {
            <p class="row-error" role="alert" id="pull-name-error" data-test="pull-error">
              <sc-icon name="error" [size]="14" />
              <span data-test="pull-error-reason">{{ err }}</span>
              <a routerLink="/logs" class="row-error-link" data-test="goto-logs">See logs</a>
            </p>
          }
          @if (pulled(); as name) {
            <p class="metric-note" role="status" data-test="pull-done">
              Pulled {{ name }}. It is under Installed: Use this makes it the model Scout uses.
            </p>
          }
          <div>
            <button type="button" class="link" data-test="browse-library" (click)="onBrowse()">
              Browse the Ollama library
            </button>
          </div>
          @if (browseError(); as err) {
            <p class="row-error" role="alert" data-test="browse-error">
              <sc-icon name="error" [size]="14" />
              <span>{{ err }}</span>
            </p>
          }
        </form>
        <h3 class="list-title">Recommended</h3>
        @if (!skillInstalled()) {
          <p class="metric-note" data-test="skill-model-note">
            The first download also gets {{ skill.tag }} ({{ formatSize(skill.sizeBytes) }}), which
            Scout uses to match a message to a skill.
          </p>
        }
        <ul class="models" data-test="available-models">
          @for (m of availableModels(); track m.id) {
            <ng-container
              [ngTemplateOutlet]="ollamaRow"
              [ngTemplateOutletContext]="{ $implicit: m }"
            />
          }
        </ul>
      </div>
    } @else {
      <main class="page" [class.page--embedded]="embedded()">
        <section
          class="panel-card"
          data-test="server-panel"
          [attr.aria-labelledby]="'local-' + tab() + '-title'"
        >
          <header class="panel-head">
            <div class="panel-icon" aria-hidden="true">
              <sc-icon [name]="tab() === 'transcription' ? 'mic' : 'cube'" [size]="20" />
            </div>
            <div class="panel-titles">
              <h2 [id]="'local-' + tab() + '-title'">{{ serverTitle() }}</h2>
              <p class="panel-sub" data-test="panel-sub">{{ serverSub() }}</p>
            </div>
            <span class="chip" data-test="panel-status">On this computer</span>
          </header>

          @if (tab() === 'transcription' && preferenceNote(); as note) {
            <p class="switch-note" role="status" data-test="whisper-preference-note">
              <sc-icon name="warning" [size]="16" />
              <span>{{ note }}</span>
            </p>
          }
          @if (tab() === 'transcription' && switchNotice(); as notice) {
            <p class="switch-note" role="status" data-test="whisper-switch-notice">
              <sc-icon name="mic" [size]="16" />
              <span>{{ notice }}</span>
            </p>
          }

          @if (tab() === 'transcription' && store.whisperTableError(); as reason) {
            <p class="row-error" role="alert" data-test="whisper-table-error">
              <sc-icon name="error" [size]="14" />
              <span>Scout could not list the voice models: {{ reason }}</span>
            </p>
          }

          <div class="catalog-inline">
            @for (m of catalog(); track m.id) {
              <article
                class="row"
                [class.row--selected]="isSelected(m.id)"
                [attr.data-test]="'row-' + m.id"
                [attr.aria-labelledby]="'model-' + m.id"
              >
                <div class="row-body">
                  <div class="row-title">
                    <span class="row-name" [id]="'model-' + m.id">{{ m.title }}</span>
                    @for (b of m.badges; track b) {
                      <span class="tag tag--{{ b }}">{{ badgeLabel(b) }}</span>
                    }
                    @if (isDownloaded(m.id) && isSelected(m.id)) {
                      <span class="chip chip--ok" [attr.data-test]="'check-' + m.id">
                        <sc-icon name="check" [size]="13" />
                        In use
                      </span>
                    } @else if (isDownloaded(m.id)) {
                      <span class="chip" [attr.data-test]="'downloaded-' + m.id">Downloaded</span>
                    }
                  </div>
                  <p class="row-desc">{{ m.description }}</p>
                  <p class="row-meta">
                    {{ formatSize(m.sizeBytes) }} on disk <span class="dot">·</span>
                    {{ formatSize(m.ramBytes) }} RAM
                  </p>
                  @if (errorOf(m.id); as err) {
                    <p class="row-error" role="alert" [attr.data-test]="'error-' + m.id">
                      <sc-icon name="error" [size]="14" />
                      <span>{{ err }}</span>
                      <a routerLink="/logs" class="row-error-link" data-test="goto-logs"
                        >See logs</a
                      >
                    </p>
                  }
                </div>
                <div class="row-action">
                  @if (progressOf(m.id) !== null) {
                    <div class="dl" [attr.data-test]="'progress-' + m.id" aria-live="polite">
                      <span class="dl-label">Downloading</span>
                      <div
                        class="dl-bar"
                        role="progressbar"
                        aria-valuemin="0"
                        aria-valuemax="100"
                        [attr.aria-valuenow]="progressOf(m.id)"
                        [attr.aria-labelledby]="'model-' + m.id"
                      >
                        <div class="dl-fill" [style.width.%]="progressOf(m.id)"></div>
                      </div>
                      <span class="dl-pct">{{ progressOf(m.id) }}%</span>
                    </div>
                    <button
                      type="button"
                      class="btn"
                      [attr.data-test]="'cancel-' + m.id"
                      [attr.aria-label]="'Cancel the download of ' + m.title"
                      (click)="onCancel(m.id)"
                    >
                      Cancel
                    </button>
                  } @else if (isDownloaded(m.id)) {
                    @if (!isSelected(m.id)) {
                      <button
                        type="button"
                        class="btn"
                        [attr.data-test]="'select-' + m.id"
                        [disabled]="switching() !== null"
                        (click)="onSelect(m.id)"
                      >
                        <sc-icon name="check" [size]="16" />
                        {{ switching() === m.id ? 'Switching…' : 'Use this' }}
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn--danger"
                      [attr.data-test]="'remove-' + m.id"
                      [attr.aria-label]="'Remove ' + m.title"
                      (click)="onRemove(m.id)"
                    >
                      <sc-icon name="trash" [size]="16" />
                      Remove
                    </button>
                  } @else {
                    <button
                      type="button"
                      class="btn"
                      [attr.data-test]="'download-' + m.id"
                      (click)="onDownload(m.id)"
                    >
                      <sc-icon name="download" [size]="16" />
                      Download
                    </button>
                  }
                </div>
              </article>
            }
          </div>

          @if (tab() === 'transcription') {
            <div class="gpu" data-test="metric-gpu">
              <span class="gpu-label">Graphics card (GPU)</span>
              <span class="gpu-value" data-test="metric-gpu-value">Not reported</span>
              <span class="metric-note" data-test="metric-gpu-source">
                The Whisper runtime does not tell Scout whether it uses a GPU.
              </span>
            </div>

            <footer class="foot">
              <div class="foot-left" data-test="foot-summary">
                <strong>Downloads:</strong>
                {{ storageLabel() }} <span class="dot">·</span> {{ downloadedCount() }}
                {{ downloadedCount() === 1 ? 'model' : 'models' }}
                <div class="foot-sub">
                  Remove downloaded local models that are not currently selected to free up disk
                  space.
                </div>
              </div>
              <button
                type="button"
                class="btn btn--danger"
                data-test="delete-unused"
                (click)="onDeleteUnused()"
                [disabled]="!hasUnused()"
              >
                <sc-icon name="trash" [size]="16" />
                Delete unused
              </button>
            </footer>
          }
        </section>
      </main>
    }
    <ng-template #ollamaRow let-m>
      <li
        class="model"
        [class.model--installed]="isDownloaded(m.id)"
        [attr.data-test]="'row-' + m.id"
        [attr.aria-labelledby]="'model-' + m.id"
      >
        <div class="model-line">
          <div class="model-text">
            <span class="model-title">
              <span [id]="'model-' + m.id">{{ m.title }}</span>
              @for (b of m.badges; track b) {
                @if (b === 'recommended') {
                  <span class="tag tag--recommended">{{ badgeLabel(b) }}</span>
                }
              }
              @if (isDownloaded(m.id) && isSelected(m.id)) {
                <span class="chip chip--ok" [attr.data-test]="'check-' + m.id">
                  <sc-icon name="check" [size]="13" />
                  In use
                </span>
              } @else if (isDownloaded(m.id)) {
                <span class="visually-hidden" [attr.data-test]="'downloaded-' + m.id"
                  >Downloaded</span
                >
              }
              @if (store.isEmbeddingsOnly(m.id)) {
                <span class="chip" [attr.data-test]="'embeddings-' + m.id">Embeddings only</span>
              }
              @if (store.cannotCallTools(m.id)) {
                <span class="chip chip--warn" [attr.data-test]="'no-tools-' + m.id">No tools</span>
              }
            </span>
            <span class="model-meta">{{ metaOf(m) }}</span>
          </div>
          @if (progressOf(m.id) !== null) {
            <button
              type="button"
              class="pill"
              [attr.data-test]="'cancel-' + m.id"
              [attr.aria-label]="'Cancel the download of ' + m.title"
              (click)="onCancel(m.id)"
            >
              <sc-icon name="close" [size]="16" />
              Cancel
            </button>
          } @else if (isDownloaded(m.id)) {
            @if (!isSelected(m.id) && !store.isEmbeddingsOnly(m.id)) {
              <button
                type="button"
                class="pill"
                [attr.data-test]="'select-' + m.id"
                [disabled]="switching() !== null"
                [attr.aria-describedby]="
                  store.cannotCallTools(m.id) ? 'no-tools-note-' + m.id : null
                "
                (click)="onSelect(m.id)"
              >
                <sc-icon name="check" [size]="16" />
                {{ switching() === m.id ? 'Switching…' : 'Use this' }}
              </button>
            }
            <button
              type="button"
              class="pill pill--danger"
              [attr.data-test]="'remove-' + m.id"
              [attr.aria-label]="'Remove ' + m.title"
              [disabled]="isSelected(m.id)"
              [attr.aria-describedby]="isSelected(m.id) ? 'in-use-' + m.id : null"
              (click)="askRemove(m.id)"
            >
              <sc-icon name="trash" [size]="16" />
              Remove
            </button>
          } @else {
            <button
              type="button"
              class="pill"
              [attr.data-test]="'download-' + m.id"
              [attr.aria-label]="'Download ' + m.title"
              (click)="onDownload(m.id)"
            >
              <sc-icon name="download" [size]="16" />
              Download
            </button>
          }
        </div>
        @if (m.description !== '') {
          <p class="model-desc">{{ m.description }}</p>
        }
        @if (store.cannotCallTools(m.id)) {
          <p
            class="no-tools"
            [id]="'no-tools-note-' + m.id"
            [attr.data-test]="'no-tools-note-' + m.id"
          >
            {{ noTools }}
          </p>
        }
        @if (isDownloaded(m.id) && isSelected(m.id)) {
          <p class="model-meta" [id]="'in-use-' + m.id" [attr.data-test]="'in-use-note-' + m.id">
            In use. Choose another model before removing it.
          </p>
        }
        @if (progressOf(m.id) !== null) {
          <div class="progress" [attr.data-test]="'progress-' + m.id" aria-live="polite">
            <div
              class="progress-bar"
              role="progressbar"
              aria-valuemin="0"
              aria-valuemax="100"
              [attr.aria-valuenow]="progressOf(m.id)"
              [attr.aria-label]="'Downloading ' + m.title"
            >
              <div class="progress-fill" [style.width.%]="progressOf(m.id)"></div>
            </div>
            <span class="model-meta">{{ progressText(m) }}</span>
          </div>
        }
        @if (errorOf(m.id); as err) {
          <p class="row-error" role="alert" [attr.data-test]="'error-' + m.id">
            <sc-icon name="error" [size]="14" />
            <span>{{ err }}</span>
            <a routerLink="/logs" class="row-error-link" data-test="goto-logs">See logs</a>
          </p>
        }
        @if (removing() === m.id) {
          <div
            class="confirm"
            role="alertdialog"
            [attr.aria-labelledby]="'rm-' + m.id"
            [attr.data-test]="'remove-confirm-' + m.id"
            (keydown.escape)="cancelRemove($event, m.id)"
          >
            <span class="confirm-text" [id]="'rm-' + m.id">
              Remove {{ m.title }}? {{ formatSize(m.sizeBytes) }} is deleted from this computer.
            </span>
            <button
              type="button"
              class="pill pill--quiet"
              [attr.data-test]="'remove-cancel-' + m.id"
              (click)="cancelRemove($event, m.id)"
            >
              Cancel
            </button>
            <button
              type="button"
              class="pill pill--danger-solid"
              [attr.data-test]="'remove-ok-' + m.id"
              (click)="onRemoveConfirmed(m.id)"
            >
              <sc-icon name="trash" [size]="15" />
              Remove
            </button>
          </div>
        }
      </li>
    </ng-template>
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
      }
      .ollama {
        display: flex;
        flex-direction: column;
        gap: 14px;
        color: var(--sc-fg);
      }
      .server {
        display: flex;
        flex-direction: column;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
      }
      .server-head {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .server-titles {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
        min-width: 0;
      }
      .list-title {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
        color: var(--sc-muted);
      }
      .empty {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 14px;
        border: 1px dashed var(--sc-line);
        border-radius: 10px;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .empty-glyph {
        width: 20px;
        height: 20px;
        flex: none;
        fill: none;
        stroke: currentColor;
        stroke-width: 1.8;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
      .models {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .model {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 10px 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
      }
      .model--installed {
        background: var(--sc-bg);
      }
      .model-line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .model-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
        min-width: 0;
      }
      .model-title {
        display: inline-flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
        font-weight: 600;
      }
      .model-meta {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .model-desc {
        margin: 0;
        font-size: 12px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      .pill {
        height: 36px;
        padding: 0 14px;
        box-sizing: border-box;
        border-radius: 18px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font: inherit;
        font-size: 14px;
        font-weight: 500;
        cursor: pointer;
      }
      .pull {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin: 0;
      }
      .pull-label {
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .pull-input {
        flex: 1 1 220px;
        height: 36px;
        box-sizing: border-box;
        padding: 0 12px;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
      }
      .pull-input--invalid {
        border-color: var(--sc-error);
      }
      .pull-refused {
        margin: 0;
        font-size: 12px;
        color: var(--sc-error);
      }
      .link {
        padding: 0;
        border: 0;
        background: transparent;
        color: var(--sc-accent);
        font: inherit;
        font-size: 13px;
        font-weight: 600;
        text-decoration: underline;
        cursor: pointer;
      }
      .pill:disabled {
        color: var(--sc-muted);
        cursor: not-allowed;
      }
      .pill--danger {
        color: var(--sc-error);
      }
      .pill--quiet {
        height: 34px;
        border-radius: 17px;
        background: var(--sc-surface);
      }
      .pill--danger-solid {
        height: 34px;
        border-radius: 17px;
        border: 0;
        gap: 6px;
        background: var(--sc-danger);
        color: var(--sc-on-danger);
      }
      .progress {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .progress-bar {
        height: 6px;
        border-radius: 3px;
        background: var(--sc-surface-2);
        overflow: hidden;
      }
      .progress-fill {
        height: 100%;
        background: var(--sc-accent);
      }
      .confirm {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
        padding: 10px 12px;
        border-radius: 8px;
        background: var(--sc-error-container);
      }
      .confirm-text {
        margin-right: auto;
        font-size: 13px;
        color: var(--sc-fg);
      }
      .visually-hidden {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }
      .page--embedded {
        padding: 0;
        max-width: none;
      }
      .panel-card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .panel-head {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        align-items: center;
      }
      .panel-icon {
        width: 36px;
        height: 36px;
        border-radius: 10px;
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .panel-titles {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .panel-titles h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .panel-sub {
        margin: 0;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 2px 9px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 600;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .chip--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .chip--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .no-tools {
        margin: 0;
        font-size: 12px;
        line-height: 1.5;
        color: var(--sc-warn);
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
      .metrics {
        display: grid;
        grid-template-columns: repeat(3, 1fr);
        gap: 16px;
        margin: 0;
      }
      .metrics > div {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .metrics dt {
        font-size: 11px;
        font-weight: 700;
        color: var(--sc-muted);
        letter-spacing: 0.08em;
      }
      .metrics dd {
        margin: 0;
        font-size: 14px;
        color: var(--sc-fg);
        font-weight: 600;
      }
      .metric-note {
        margin: 0;
        font-size: 12px;
        font-weight: 400;
        color: var(--sc-muted);
      }
      .panel-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .btn {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        height: 36px;
        padding: 0 14px;
        border-radius: 18px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
      }
      .btn:hover:not(:disabled) {
        background: var(--sc-surface-2);
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: var(--sc-accent-strong);
        border-color: transparent;
        color: var(--sc-on-accent);
      }
      .btn--primary:hover:not(:disabled) {
        background: var(--sc-accent-strong);
      }
      .btn--danger {
        color: var(--sc-error);
      }
      .panel-host {
        margin-left: auto;
        color: var(--sc-muted);
        font-size: 12px;
      }
      .switch-note {
        display: flex;
        align-items: center;
        gap: 10px;
        margin: 0;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
        font-size: 13px;
      }
      .catalog-inline {
        display: flex;
        flex-direction: column;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
        padding: 12px 0;
        border-top: 1px solid var(--sc-line);
      }
      .row-body {
        flex: 1 1 320px;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 3px;
      }
      .row-title {
        display: flex;
        gap: 8px;
        align-items: center;
        flex-wrap: wrap;
      }
      .row-name {
        font-size: 14px;
        font-weight: 600;
      }
      .row-desc {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .row-meta {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .row-error {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        margin: 6px 0 0;
        padding: 6px 10px;
        font-size: 12px;
        color: var(--sc-on-error-container);
        background: var(--sc-error-container);
        border-radius: 8px;
      }
      .row-error-link {
        margin-left: auto;
        color: var(--sc-on-error-container);
        font-weight: 600;
      }
      .dot {
        margin: 0 2px;
      }
      .tag {
        font-size: 11px;
        font-weight: 600;
        padding: 1px 8px;
        border-radius: 10px;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .tag--recommended {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .tag--text-only {
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
      }
      .row-action {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: flex-end;
        gap: 8px;
        flex: 0 1 300px;
      }
      .dl {
        display: flex;
        align-items: center;
        gap: 8px;
        width: 240px;
        font-size: 12px;
      }
      .dl-label {
        color: var(--sc-fg-2);
      }
      .dl-bar {
        flex: 1;
        height: 6px;
        background: var(--sc-surface-2);
        border-radius: 3px;
        overflow: hidden;
      }
      .dl-fill {
        height: 100%;
        background: var(--sc-accent);
        transition: width 120ms linear;
      }
      .dl-pct {
        font-variant-numeric: tabular-nums;
        color: var(--sc-muted);
        min-width: 32px;
        text-align: right;
      }
      .gpu {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 4px 12px;
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
        font-size: 13px;
      }
      .gpu-label {
        margin-right: auto;
      }
      .gpu-value {
        font-weight: 600;
      }
      .gpu .metric-note {
        flex-basis: 100%;
      }
      .foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
      }
      .foot-left {
        font-size: 13px;
        color: var(--sc-fg);
      }
      .foot-sub {
        font-size: 12px;
        color: var(--sc-muted);
        margin-top: 4px;
      }
      @media (prefers-reduced-motion: reduce) {
        .dl-fill {
          transition: none;
        }
      }
    `,
  ],
})
export class LocalModelsComponent implements OnInit, OnDestroy {
  readonly store = inject(LocalModelsStore);
  private readonly llmConfig = inject(LlmConfigStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);

  private readonly dialog = inject(MatDialog);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** The kind of model this view manages: Whisper in Models, Transcription; Ollama inside the AI Sources Ollama card. */
  readonly kind = input<LocalModelKind>('transcription');
  /** True inside another screen, which provides the page padding. */
  readonly embedded = input<boolean>(false);
  /** Use this on an Ollama model, once `llm.model` is saved: the host makes Ollama the provider and restarts Scout. */
  readonly ollamaModelChosen = output<string>();
  /** The Whisper model "Use this" is switching to while main saves it and restarts Scout. */
  readonly switching = signal<string | null>(null);
  /** Main deferred the restart until the live listen ends. */
  readonly switchNotice = signal<string | null>(null);
  readonly tab = computed<LocalModelKind>(() => this.kind());
  readonly errors = signal<Readonly<Record<string, string>>>({});

  private pollTimer: ReturnType<typeof setInterval> | null = null;

  async ngOnInit(): Promise<void> {
    if (this.tab() === 'transcription') void this.store.loadWhisperTable();
    if (this.tab() === 'analysis') void this.store.openOllama();
    await this.store.refreshWhisper();
    this.pollTimer = setInterval(() => {
      void (this.tab() === 'analysis' ? this.store.refreshOllama() : this.store.refreshWhisper());
    }, POLL_MS);
  }

  readonly catalog = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.catalogFor(this.tab()),
  );

  readonly hasSelected = computed<boolean>(() => this.store.selectedFor(this.tab()) !== null);

  /** True while Ollama is the provider in use, or none is saved yet; only then is an Ollama model In use. */
  private readonly ollamaInUse = computed<boolean>(() => {
    const provider = this.llmConfig.provider();
    return provider === null || provider === 'Ollama';
  });

  /** What the desktop says about a model that cannot call tools. */
  readonly noTools = NO_TOOLS;

  /** True while the Ollama model in use, with Ollama the provider in use or none saved, cannot call tools. */
  readonly inUseNoTools = computed<boolean>(() => {
    const id = this.ollamaInUse() ? this.store.selectedFor('analysis') : null;
    return id !== null && this.store.cannotCallTools(id);
  });

  readonly selectedTitle = computed<string | null>(() => {
    const id =
      this.tab() === 'transcription'
        ? this.store.whisperInUse()
        : this.ollamaInUse()
          ? this.store.selectedFor('analysis')
          : null;
    if (id === null || (this.tab() === 'analysis' && !this.store.isDownloaded(id))) return null;
    return this.catalog().find((m) => m.id === id)?.title ?? id;
  });

  readonly downloadedCount = computed<number>(() => this.store.downloadedFor(this.tab()).length);

  readonly storageLabel = computed<string>(() => {
    const bytes = this.store.downloadedFor(this.tab()).reduce((sum, m) => sum + m.sizeBytes, 0);
    return formatBytes(bytes);
  });

  /** The LLM Server chip: Running while Ollama answers its list, Not running when it does not, Checking before. */
  readonly serverState = computed<string>(() => {
    const answers = this.store.ollamaAnswers();
    return answers === null ? 'Checking' : answers ? 'Running' : 'Not running';
  });

  /** The line under the LLM Server metrics: who runs Ollama, or how to start one that does not answer. */
  readonly serverLine = computed<string>(() =>
    this.store.ollamaAnswers() === false
      ? NOT_ANSWERING
      : 'Ollama is run by this computer, not by Scout.',
  );

  readonly hasUnused = computed<boolean>(() => this.unusedWhisper().length > 0);

  private unusedWhisper(): readonly string[] {
    const kept = [this.store.whisperInUse(), this.store.whisperPreferred()];
    return this.store.downloaded().filter((id) => WHISPER_IDS.has(id) && !kept.includes(id));
  }

  serverTitle(): string {
    return this.tab() === 'transcription' ? 'Local Whisper' : 'LLM Server';
  }

  serverSub(): string {
    return this.tab() === 'transcription'
      ? `Turns speech into text on this computer, with no cloud provider. ${this.downloadedCount()} downloaded · ${this.storageLabel()} on disk.`
      : 'Runs on this computer, private by default.';
  }

  isDownloaded(id: string): boolean {
    return this.store.isDownloaded(id);
  }

  isSelected(id: string): boolean {
    return this.tab() === 'transcription'
      ? this.store.whisperInUse() === id
      : this.ollamaInUse() && this.store.selectedFor('analysis') === id;
  }

  /**
   * Why the saved choice is not what Scout uses: "base is selected but not downloaded; Scout uses large-v3-turbo.",
   * or, for a downloaded choice the running worker has not loaded yet, "base is selected; Scout switches to it when
   * it restarts."
   */
  readonly preferenceNote = computed<string | null>(() => {
    const preferred = this.store.whisperPreferred();
    const inUse = this.store.whisperInUse();
    if (preferred === null || preferred === inUse) return null;
    const name = (id: string): string => id.replace(/^whisper-/, '');
    if (this.store.isDownloaded(preferred)) {
      return `${name(preferred)} is selected; Scout switches to it when it restarts.`;
    }
    return inUse === null
      ? `${name(preferred)} is selected but not downloaded; no voice model is installed.`
      : `${name(preferred)} is selected but not downloaded; Scout uses ${name(inUse)}.`;
  });

  progressOf(id: string): number | null {
    return this.store.progressOf(id);
  }

  onDownload(id: string): void {
    if (this.store.isDownloaded(id) || this.store.progressOf(id) !== null) return;
    void this.startDownload(id);
  }

  /** Cancel on a row that is downloading; the row goes back to Download without an error. */
  onCancel(id: string): void {
    void this.store.cancelDownload(id).catch((err: unknown) => {
      this.recordError(id, `Cancel download`, err);
    });
  }

  /**
   * Downloads a model, or pulls it `byName`, and with an Ollama model other than the skill-matching one also pulls
   * that one while it is missing. Resolves true once it is downloaded, false when it failed, which its row and the
   * Pull by name field then say, or was cancelled.
   */
  private async startDownload(id: string, byName = false): Promise<boolean> {
    this.clearError(id);
    const download = this.store.download(id, { byName });
    if (id !== SKILL_MODEL.id && ollamaTagFor(id) !== null) void this.startDownload(SKILL_MODEL.id);
    try {
      await download;
      if (WHISPER_IDS.has(id)) await this.store.refreshWhisper();
      return true;
    } catch (err: unknown) {
      if (err instanceof Error && err.message === DOWNLOAD_CANCELLED) return false;
      const tag = ollamaTagFor(id);
      if (tag === null) this.recordError(id, `Whisper download ${id}`, err);
      else this.recordError(id, `Ollama pull ${tag}`, new Error(pullFailure(tag, err)));
      return false;
    }
  }

  readonly nameRule = OLLAMA_MODEL_NAME_RULE;

  /** What the owner typed in Pull by name. */
  readonly pullName = signal('');
  /** The key of the model the last Pull by name asked for, or null before the first. */
  readonly pullKey = signal<string | null>(null);
  /** The name the last Pull by name finished, or null. */
  readonly pulled = signal<string | null>(null);
  /** Why the browser could not open the Ollama library, or null. */
  readonly browseError = signal<string | null>(null);

  readonly pullRefused = computed<boolean>(() => {
    const name = this.pullName().trim();
    return !this.pullBusy() && name !== '' && !isOllamaModelName(name);
  });

  readonly pullBusy = computed<boolean>(() => {
    const key = this.pullKey();
    return key !== null && this.store.progressOf(key) !== null;
  });

  readonly canPull = computed<boolean>(
    () => !this.pullBusy() && isOllamaModelName(this.pullName().trim()),
  );

  readonly pullTag = computed<string>(() => ollamaTagFor(this.pullKey() ?? '') ?? '');

  readonly pullError = computed<string | null>(() => {
    const key = this.pullKey();
    return key === null || this.pullBusy() ? null : this.errorOf(key);
  });

  onPullInput(event: Event): void {
    this.pullName.set((event.target as HTMLInputElement).value);
    this.pulled.set(null);
    const key = this.pullKey();
    if (key !== null && !this.pullBusy()) this.clearError(key);
  }

  /**
   * Pull or Enter in Pull by name: a model name, trimmed, is pulled through the same path as a Download, Cancel and
   * the skill-matching model included; anything else reaches nothing. Once Ollama lists it the field empties and the
   * model is under Installed; it is never put in use.
   */
  onPull(event?: Event): void {
    event?.preventDefault();
    const name = this.pullName().trim();
    if (!this.canPull()) return;
    const key = ollamaKeyForName(name);
    this.pullKey.set(key);
    this.pulled.set(null);
    if (this.store.progressOf(key) !== null) return;
    void this.startDownload(key, true).then((done) => {
      if (!done || this.pullKey() !== key) return;
      this.pullName.set('');
      this.pulled.set(ollamaTagFor(key));
    });
  }

  /** Opens the Ollama library in the owner's browser through main; Scout itself asks that site for nothing. */
  onBrowse(): void {
    this.browseError.set(null);
    this.bridge.appOpenExternal({ url: OLLAMA_LIBRARY }).catch((err: unknown) => {
      this.browseError.set('Could not open the browser. The library is at ollama.com/library.');
      this.logStore.appendDiagnostic(`[Browse the Ollama library] ${readIpcError(err).message}`);
    });
  }

  /** The Ollama model whose Remove waits for the owner's answer in its row; null when none does. */
  readonly removing = signal<string | null>(null);

  /** Every model Ollama lists but the skill-matching one, which has a row of its own: the catalog ones first. */
  readonly installedModels = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.downloadedFor(this.tab()),
  );

  readonly availableModels = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.catalog().filter((m) => !this.store.isDownloaded(m.id)),
  );

  /**
   * What an Ollama row says under its name: for a catalog model what it reads, its size on disk and the memory it
   * needs; for any other the details Ollama lists.
   */
  metaOf(m: LocalModelCatalogEntry): string {
    if (lookupCatalog(m.id) === null) return this.store.detailsOf(m.id);
    const reads = m.badges.includes('text-image')
      ? 'Text and images'
      : m.badges.includes('text-only')
        ? 'Text only'
        : null;
    const sizes = `${formatBytes(m.sizeBytes)} on disk · ${formatBytes(m.ramBytes)} RAM`;
    return reads === null ? sizes : `${reads} · ${sizes}`;
  }

  /** The model Scout matches a message to a skill with, which the Skill matching row downloads. */
  readonly skill = SKILL_MODEL;

  /** True once Ollama has the skill-matching model, as `LocalModelsStore.refreshOllama` last read it. */
  readonly skillInstalled = computed<boolean>(() => this.store.isDownloaded(SKILL_MODEL.id));

  skillProgressText(): string {
    const pct = this.progressOf(SKILL_MODEL.id) ?? 0;
    const done = Math.round((SKILL_MODEL.sizeBytes * pct) / 100 / MiB);
    return `${done} of ${formatBytes(SKILL_MODEL.sizeBytes)} · ${pct}%`;
  }

  progressText(m: LocalModelCatalogEntry): string {
    const pct = this.progressOf(m.id) ?? 0;
    const gb = (bytes: number): string => (bytes / GiB).toFixed(1);
    return `${gb((m.sizeBytes * pct) / 100)} of ${gb(m.sizeBytes)} GB · ${pct}%`;
  }

  askRemove(id: string): void {
    this.removing.set(id);
    afterNextRender(
      () =>
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-test="remove-cancel-${id}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  cancelRemove(event: Event, id: string): void {
    event.stopPropagation();
    this.removing.set(null);
    afterNextRender(
      () =>
        this.host.nativeElement.querySelector<HTMLElement>(`[data-test="remove-${id}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  onRemoveConfirmed(id: string): void {
    this.removing.set(null);
    this.removeConfirmed(id);
  }

  async onRemove(id: string): Promise<void> {
    const entry = this.store.lookup(id);
    const confirmed = await confirmAction(this.dialog, {
      title: `Remove ${entry?.title ?? id}?`,
      body: 'Scout deletes the downloaded model from this computer. You can download it again later.',
      confirmLabel: 'Remove',
    });
    if (confirmed) this.removeConfirmed(id);
  }

  private removeConfirmed(id: string): void {
    const entry = this.store.lookup(id);
    if (entry?.kind === 'analysis') {
      const tag = ollamaTagFor(id);
      if (tag !== null) {
        void this.remove(id, `Ollama delete ${tag}`, () =>
          this.bridge.llmDeleteOllamaModel({ endpoint: OLLAMA_ENDPOINT, tag }),
        );
        return;
      }
    }
    const variant = whisperVariantFor(id);
    if (variant !== null) {
      void this.remove(id, `Whisper delete ${variant}`, () =>
        this.bridge.whisperDeleteModel({ variant }),
      );
      return;
    }
    this.store.markRemoved(id);
  }

  private async remove(id: string, context: string, del: () => Promise<unknown>): Promise<void> {
    this.clearError(id);
    try {
      await del();
      this.store.markRemoved(id);
      if (WHISPER_IDS.has(id)) await this.store.refreshWhisper();
    } catch (err: unknown) {
      this.recordError(id, context, err);
    }
  }

  private recordError(catalogId: string, context: string, err: unknown): void {
    const message = err instanceof Error ? err.message : String(err);
    this.errors.update((prev) => ({ ...prev, [catalogId]: message }));
    this.logStore.appendDiagnostic(`[${context}] ${catalogId}: ${message}`);
  }

  private clearError(catalogId: string): void {
    if (this.errors()[catalogId] === undefined) return;
    this.errors.update((prev) => {
      const next = { ...prev };
      delete next[catalogId];
      return next;
    });
  }

  errorOf(catalogId: string): string | null {
    return this.errors()[catalogId] ?? null;
  }

  onSelect(id: string): void {
    const variant = whisperVariantFor(id);
    if (variant === null) {
      void this.useOllamaModel(id);
      return;
    }
    void this.useWhisper(id, variant);
  }

  /** Saves `llm.model` first; only a saved model reaches the host, which switches the provider and restarts Scout. */
  private async useOllamaModel(id: string): Promise<void> {
    this.clearError(id);
    try {
      await this.store.select('analysis', id);
    } catch (err: unknown) {
      this.recordError(id, `Ollama use ${ollamaTagFor(id) ?? id}`, err);
      return;
    }
    this.ollamaModelChosen.emit(id);
  }

  /**
   * Has main save the model and restart a running Scout so it loads it; the row says Switching… until main answers.
   * In use moves only once main saved it; a failed restart shows its reason and keeps the previous model in use.
   */
  private async useWhisper(id: string, variant: WhisperVariant): Promise<void> {
    if (this.switching() !== null) return;
    this.switching.set(id);
    this.switchNotice.set(null);
    this.clearError(id);
    try {
      const { restart } = await this.bridge.whisperUseModel({ variant });
      void this.store.select('transcription', id);
      await this.store.refreshWhisper();
      if (restart === 'deferred') this.switchNotice.set(SWITCH_DEFERRED);
    } catch (err: unknown) {
      const failure = readIpcError(err);
      if (failure.code === 'whisper/restart-failed') {
        void this.store.select('transcription', id);
        await this.store.refreshWhisper();
        this.switchNotice.set(
          `Saved; Scout starts with ${variant} next time. It could not restart now: ${failure.message}`,
        );
      } else {
        this.recordError(id, `Whisper use ${variant}`, new Error(failure.message));
      }
    } finally {
      this.switching.set(null);
    }
  }

  /** Asks first, then deletes each voice model not in use through main; a failed delete keeps its row and shows why. */
  async onDeleteUnused(): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: 'Delete unused models?',
      body: 'Scout deletes the voice models that are not in use from this computer. The model in use stays.',
      confirmLabel: 'Delete unused',
    });
    if (!confirmed) return;
    for (const id of this.unusedWhisper()) {
      const variant = whisperVariantFor(id);
      if (variant === null) continue;
      await this.remove(id, `Whisper delete ${variant}`, () =>
        this.bridge.whisperDeleteModel({ variant }),
      );
    }
  }

  ngOnDestroy(): void {
    if (this.pollTimer !== null) clearInterval(this.pollTimer);
  }

  badgeLabel(b: LocalModelBadge): string {
    switch (b) {
      case 'recommended':
        return 'Recommended';
      case 'text-only':
        return 'Text Only';
      case 'text-image':
        return 'Text + Image';
    }
  }

  formatSize(n: number): string {
    return formatBytes(n);
  }
}
