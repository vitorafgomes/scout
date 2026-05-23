import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ProviderStatusDto, WhisperVariant } from '@scout/shared';
import {
  ProviderLifecycleUnsupportedError,
  ProviderNotManagedError,
  ProviderStartTimeoutError,
  ProviderUnreachableError,
  WorkerHttpUnavailableError,
} from '@scout/shared';

import {
  lookupCatalog,
  ollamaTagForCatalogId,
  type LocalModelBadge,
  type LocalModelCatalogEntry,
  type LocalModelKind,
} from '../core/local-models.catalog';
import { LocalModelsStore } from '../core/local-models.store';
import { LogStore } from '../core/log.store';
import { ScoutBridgeService } from '../core/scout-bridge';

const OLLAMA_ENDPOINT = 'http://localhost:11434';

// Catalog <-> Ollama tag mapping lives in core/local-models.catalog so the
// store and the component agree on a single source of truth.
const ollamaTagFor = ollamaTagForCatalogId;

const CATALOG_TO_WHISPER: Readonly<Record<string, WhisperVariant>> = Object.freeze({
  'whisper-large-v3': 'large-v3',
  'whisper-large-v3-turbo': 'large-v3-turbo',
});

const WHISPER_TO_CATALOG: Readonly<Record<WhisperVariant, string>> = Object.freeze({
  'large-v3': 'whisper-large-v3',
  'large-v3-turbo': 'whisper-large-v3-turbo',
});

function whisperVariantFor(catalogId: string): WhisperVariant | null {
  return CATALOG_TO_WHISPER[catalogId] ?? null;
}

const GiB = 1024 * 1024 * 1024;
const MiB = 1024 * 1024;
const DOWNLOAD_DURATION_MS = 3000;
const DOWNLOAD_TICK_MS = 80;

function formatBytes(n: number): string {
  if (n >= GiB) return `${(n / GiB).toFixed(1).replace(/\.0$/, '')} GB`;
  if (n >= MiB) return `${Math.round(n / MiB)} MB`;
  return `${n} B`;
}

@Component({
  selector: 'sc-local-models',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <nav class="crumb" aria-label="Breadcrumb">
        <a routerLink="/sources" class="crumb-link" data-test="crumb-back">
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path
              d="m15 18-6-6 6-6 1.4 1.4L11.8 12l4.6 4.6Z"
              fill="currentColor"
            />
          </svg>
          AI Sources
        </a>
        <span class="crumb-sep">/</span>
        <span class="crumb-current">Local Models</span>
      </nav>

      <nav class="tabs" role="tablist" aria-label="Local model kind">
        <button
          type="button"
          role="tab"
          class="tab"
          [class.tab--active]="tab() === 'transcription'"
          [attr.aria-selected]="tab() === 'transcription'"
          data-test="kind-transcription"
          (click)="setTab('transcription')"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
            <path
              d="M9 4a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0Zm-3 7a6 6 0 0 0 12 0h-2a4 4 0 0 1-8 0Zm5 8h2v3h-2Z"
              fill="currentColor"
            />
          </svg>
          Local Transcriptions
        </button>
        <button
          type="button"
          role="tab"
          class="tab"
          [class.tab--active]="tab() === 'analysis'"
          [attr.aria-selected]="tab() === 'analysis'"
          data-test="kind-analysis"
          (click)="setTab('analysis')"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
            <path
              d="m12 3 2 6 6 2-6 2-2 6-2-6-6-2 6-2Z"
              fill="currentColor"
            />
          </svg>
          Local Analysis
        </button>
      </nav>

      <section class="panel-card" data-test="server-panel">
        <header class="panel-head">
          <div class="panel-icon" aria-hidden="true">
            @if (tab() === 'transcription') {
            <svg viewBox="0 0 24 24" width="22" height="22">
              <path
                d="M9 4a3 3 0 0 1 6 0v7a3 3 0 0 1-6 0Zm-3 7a6 6 0 0 0 12 0h-2a4 4 0 0 1-8 0Zm5 8h2v3h-2Z"
                fill="currentColor"
              />
            </svg>
            } @else {
            <svg viewBox="0 0 24 24" width="22" height="22">
              <path
                d="M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Zm0 9a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Zm3-8.5a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Zm0 9a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Z"
                fill="currentColor"
              />
            </svg>
            }
          </div>
          <div class="panel-titles">
            <div class="panel-title-row">
              <h2>{{ serverTitle() }}</h2>
              <span
                class="panel-status"
                [class.panel-status--ready]="llmStatus()?.running === true"
                data-test="panel-status"
              >
                {{ llmStatus()?.running === true ? 'Ready' : 'Stopped' }}
              </span>
            </div>
            <p class="panel-sub">{{ serverSub() }}</p>
          </div>
        </header>

        <dl class="metrics">
          <div>
            <dt>SELECTED</dt>
            <dd data-test="metric-selected">{{ selectedTitle() ?? '—' }}</dd>
          </div>
          <div>
            <dt>DOWNLOADED</dt>
            <dd data-test="metric-downloaded">{{ downloadedCount() }}</dd>
          </div>
          @if (tab() === 'transcription') {
          <div>
            <dt>GPU</dt>
            <dd>GPU off</dd>
          </div>
          <div>
            <dt>STORAGE</dt>
            <dd data-test="metric-storage">{{ storageLabel() }}</dd>
          </div>
          } @else {
          <div>
            <dt>STORAGE</dt>
            <dd data-test="metric-storage">{{ storageLabel() }}</dd>
          </div>
          <div>
            <dt>AUTOSTART</dt>
            <dd>Autostart on</dd>
          </div>
          }
        </dl>

        @if (tab() === 'analysis') {
        <div class="autostart">
          <div>
            <div class="autostart-title">Autostart</div>
            <div class="autostart-sub">
              If enabled, the server starts when a model is requested.
            </div>
          </div>
          <button
            type="button"
            class="toggle"
            [class.toggle--on]="llmStatus()?.autostart === true"
            [disabled]="autostartBusy()"
            data-test="autostart-toggle"
            (click)="onToggleAutostart()"
            aria-label="Toggle autostart">
          </button>
        </div>

        <div class="panel-actions">
          <button
            type="button"
            class="btn btn--primary"
            data-test="start-btn"
            [disabled]="busy() || llmStatus()?.running === true || llmStatus()?.installed === false"
            (click)="onStart()">
            <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
              <path d="M8 5v14l11-7Z" fill="currentColor" />
            </svg>
            Start
          </button>
          <button
            type="button"
            class="btn btn--ghost"
            data-test="stop-btn"
            [disabled]="busy() || llmStatus()?.running !== true"
            (click)="onStop()">
            <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
              <path d="M6 6h12v12H6Z" fill="currentColor" />
            </svg>
            Stop
          </button>
          <button
            type="button"
            class="btn btn--ghost"
            data-test="restart-btn"
            [disabled]="busy() || llmStatus()?.managedByWorker !== true || llmStatus()?.running !== true"
            [title]="restartDisabledTooltip()"
            (click)="onRestart()">
            <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
              <path
                d="M12 5V2L7 7l5 5V8a4 4 0 1 1-4 4H6a6 6 0 1 0 6-6Z"
                fill="currentColor"
              />
            </svg>
            Restart
          </button>
          <span class="panel-host">localhost</span>
        </div>
        @if (actionError() !== null) {
        <p class="row-error" data-test="action-error">{{ actionError() }}</p>
        }
        }

        <div class="catalog-inline">
        @for (m of catalog(); track m.id) {
        <article
          class="row"
          [class.row--selected]="isSelected(m.id)"
          [attr.data-test]="'row-' + m.id"
        >
          <div class="row-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="20" height="20">
              <path
                d="M12 3c-2.5 0-4 1.8-4 4v1H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7c0-2.2-1.5-4-4-4Z"
                fill="currentColor"
              />
            </svg>
          </div>
          <div class="row-body">
            <div class="row-title">
              <span class="row-name">{{ m.title }}</span>
              @for (b of m.badges; track b) {
              <span class="tag tag--{{ b }}">{{ badgeLabel(b) }}</span>
              }
            </div>
            <p class="row-desc">{{ m.description }}</p>
            <p class="row-meta">
              ~{{ formatSize(m.sizeBytes) }} <span class="dot">·</span>
              ~{{ formatSize(m.ramBytes) }} RAM
            </p>
            @if (errorOf(m.id); as err) {
            <p class="row-error" [attr.data-test]="'error-' + m.id">
              <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
                <path
                  d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm-1 5h2v7h-2Zm0 9h2v2h-2Z"
                  fill="currentColor"
                />
              </svg>
              <span>{{ err }}</span>
              <a routerLink="/logs" class="row-error-link" data-test="goto-logs">See logs</a>
            </p>
            }
          </div>
          <div class="row-action">
            @if (progressOf(m.id) !== null) {
            <div class="dl" [attr.data-test]="'progress-' + m.id" aria-live="polite">
              <div class="dl-bar">
                <div class="dl-fill" [style.width.%]="progressOf(m.id)"></div>
              </div>
              <span class="dl-pct">{{ progressOf(m.id) }}%</span>
            </div>
            } @else if (isDownloaded(m.id)) {
            <div class="row-controls">
              @if (isSelected(m.id)) {
              <span class="check" [attr.data-test]="'check-' + m.id" aria-label="Selected">
                <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
                  <path
                    d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm-1 14-5-5 1.4-1.4L11 13.2l6.6-6.6L19 8Z"
                    fill="currentColor"
                  />
                </svg>
              </span>
              } @else {
              <button
                type="button"
                class="btn btn--ghost"
                [attr.data-test]="'select-' + m.id"
                (click)="onSelect(m.id)"
              >
                Use this
              </button>
              }
              <button
                type="button"
                class="icon-btn"
                [attr.data-test]="'remove-' + m.id"
                title="Remove this model"
                aria-label="Remove model"
                (click)="onRemove(m.id)"
              >
                <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                  <path
                    d="M6 7h12v13a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2Zm3-4h6l1 2h4v2H4V5h4Z"
                    fill="currentColor"
                  />
                </svg>
              </button>
            </div>
            } @else {
            <button
              type="button"
              class="btn btn--ghost"
              [attr.data-test]="'download-' + m.id"
              (click)="onDownload(m.id)"
            >
              <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                <path
                  d="M12 3a1 1 0 0 1 1 1v9.59l3.3-3.3 1.4 1.42L12 17.42 6.3 11.7l1.4-1.41L11 13.6V4a1 1 0 0 1 1-1Zm-7 16h14v2H5Z"
                  fill="currentColor"
                />
              </svg>
              Download
            </button>
            }
          </div>
        </article>
        }
        </div>
      </section>

      <footer class="foot">
        <div class="foot-left" data-test="foot-summary">
          <strong>Downloads:</strong>
          {{ formatSize(store.totalDownloadedBytes()) }} <span class="dot">·</span>
          {{ store.downloaded().length }} models
          <div class="foot-sub">
            Remove downloaded local models that are not currently selected to free up disk space.
          </div>
        </div>
        <button
          type="button"
          class="btn btn--ghost"
          data-test="delete-unused"
          (click)="onDeleteUnused()"
          [disabled]="!hasUnused()"
        >
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path
              d="M6 7h12v13a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2Zm3-4h6l1 2h4v2H4V5h4Z"
              fill="currentColor"
            />
          </svg>
          Delete unused
        </button>
      </footer>
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
        max-width: 1200px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 18px;
      }
      .crumb {
        display: flex;
        gap: 10px;
        align-items: center;
        font-size: 13px;
      }
      .crumb-link {
        display: inline-flex;
        gap: 6px;
        align-items: center;
        color: #7b8299;
        text-decoration: none;
      }
      .crumb-link:hover {
        color: #e4e8f0;
      }
      .crumb-sep {
        color: #3d4258;
      }
      .crumb-current {
        color: #e4e8f0;
        font-weight: 600;
      }
      .tabs {
        background: #0f1117;
        border: 1px solid #252b3b;
        border-radius: 12px;
        padding: 4px;
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 4px;
      }
      .tab {
        background: transparent;
        color: #7b8299;
        border: none;
        border-radius: 8px;
        padding: 14px;
        font-size: 14px;
        font-weight: 600;
        cursor: pointer;
        font-family: inherit;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 8px;
      }
      .tab:hover {
        color: #e4e8f0;
      }
      .tab--active {
        background: rgba(74, 121, 200, 0.18);
        color: #e4e8f0;
      }
      .panel-card {
        background: #141822;
        border: 1px solid #252b3b;
        border-radius: 14px;
        padding: 20px 22px;
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .panel-head {
        display: flex;
        gap: 14px;
        align-items: flex-start;
      }
      .panel-icon {
        width: 42px;
        height: 42px;
        border-radius: 10px;
        background: #1e2333;
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .panel-titles {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .panel-title-row {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .panel-title-row h2 {
        margin: 0;
        font-size: 16px;
        font-weight: 700;
      }
      .panel-status {
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.06em;
        padding: 3px 8px;
        border-radius: 999px;
        color: #7b8299;
        background: rgba(123, 130, 153, 0.15);
        border: 1px solid rgba(123, 130, 153, 0.25);
      }
      .panel-status--ready {
        color: #6b89c4;
        background: rgba(107, 137, 196, 0.15);
        border-color: rgba(107, 137, 196, 0.35);
      }
      .panel-sub {
        margin: 0;
        font-size: 12px;
        color: #7b8299;
      }
      .metrics {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 16px;
        margin: 0;
      }
      .metrics > div {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .metrics dt {
        font-size: 10px;
        font-weight: 700;
        color: #7b8299;
        letter-spacing: 0.08em;
      }
      .metrics dd {
        margin: 0;
        font-size: 14px;
        color: #e4e8f0;
        font-weight: 600;
      }
      .autostart {
        display: flex;
        align-items: center;
        justify-content: space-between;
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 10px;
        padding: 12px 14px;
      }
      .autostart-title {
        font-size: 13px;
        font-weight: 600;
      }
      .autostart-sub {
        font-size: 12px;
        color: #7b8299;
      }
      .toggle {
        width: 32px;
        height: 18px;
        border-radius: 999px;
        background: #2a3245;
        position: relative;
      }
      .toggle--on {
        background: #3aaf7b;
      }
      .toggle::after {
        content: '';
        position: absolute;
        top: 2px;
        left: 2px;
        width: 14px;
        height: 14px;
        border-radius: 50%;
        background: #e4e8f0;
        transition: transform 0.15s;
      }
      .toggle--on::after {
        transform: translateX(14px);
      }
      .panel-actions {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .btn {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        background: #1e2333;
        color: #e4e8f0;
        border: 1px solid #2a3245;
        border-radius: 8px;
        padding: 8px 14px;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
        font-family: inherit;
      }
      .btn:disabled {
        opacity: 0.45;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #5a8fd4;
        color: #0f1117;
        border-color: transparent;
      }
      .btn--ghost {
        background: #1e2333;
      }
      .btn--ghost:hover:not(:disabled) {
        background: #252b3d;
      }
      .panel-host {
        margin-left: auto;
        color: #7b8299;
        font-size: 12px;
      }
      .catalog-inline {
        display: flex;
        flex-direction: column;
        gap: 0;
        margin: 4px -22px -20px;
        border-top: 1px solid #1c2030;
      }
      .row {
        display: grid;
        grid-template-columns: 38px 1fr auto;
        gap: 14px;
        padding: 16px 22px;
        border-top: 1px solid #1c2030;
      }
      .row:first-child {
        border-top: none;
      }
      .row--selected {
        background: rgba(74, 121, 200, 0.06);
      }
      .row-icon {
        width: 38px;
        height: 38px;
        border-radius: 8px;
        background: #1e2333;
        display: flex;
        align-items: center;
        justify-content: center;
        color: #7b8299;
      }
      .row-title {
        display: flex;
        gap: 8px;
        align-items: center;
        flex-wrap: wrap;
      }
      .row-name {
        font-size: 14px;
        font-weight: 700;
      }
      .row-desc {
        margin: 4px 0;
        font-size: 12px;
        color: #7b8299;
      }
      .row-meta {
        margin: 0;
        font-size: 11px;
        color: #7b8299;
      }
      .row-error {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        margin: 6px 0 0;
        padding: 6px 10px;
        font-size: 12px;
        color: #d45a5a;
        background: rgba(212, 90, 90, 0.08);
        border: 1px solid rgba(212, 90, 90, 0.25);
        border-radius: 6px;
      }
      .row-error-link {
        margin-left: auto;
        color: #6b89c4;
        font-weight: 600;
        text-decoration: none;
      }
      .row-error-link:hover {
        text-decoration: underline;
      }
      .dot {
        opacity: 0.6;
        margin: 0 4px;
      }
      .tag {
        font-size: 10px;
        font-weight: 600;
        padding: 2px 8px;
        border-radius: 999px;
        text-transform: none;
        letter-spacing: 0.02em;
      }
      .tag--recommended {
        color: #c08a3e;
        background: rgba(192, 138, 62, 0.12);
        border: 1px solid rgba(192, 138, 62, 0.35);
      }
      .tag--text-only {
        color: #6b89c4;
        background: rgba(107, 137, 196, 0.12);
        border: 1px solid rgba(107, 137, 196, 0.35);
      }
      .tag--text-image {
        color: #5fb37b;
        background: rgba(95, 179, 123, 0.12);
        border: 1px solid rgba(95, 179, 123, 0.35);
      }
      .row-action {
        display: flex;
        align-items: center;
      }
      .row-controls {
        display: flex;
        gap: 6px;
        align-items: center;
      }
      .check {
        color: #6b89c4;
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
      .icon-btn {
        background: transparent;
        border: 1px solid #2a3245;
        border-radius: 8px;
        padding: 6px;
        color: #7b8299;
        cursor: pointer;
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
      .icon-btn:hover {
        color: #d45a5a;
        border-color: #4a2b2b;
        background: rgba(212, 90, 90, 0.08);
      }
      .dl {
        display: flex;
        align-items: center;
        gap: 8px;
        min-width: 160px;
      }
      .dl-bar {
        flex: 1;
        height: 6px;
        background: #1e2333;
        border-radius: 999px;
        overflow: hidden;
      }
      .dl-fill {
        height: 100%;
        background: linear-gradient(90deg, #5a8fd4, #6b89c4);
        transition: width 120ms linear;
      }
      .dl-pct {
        font-size: 11px;
        font-variant-numeric: tabular-nums;
        color: #7b8299;
        min-width: 32px;
        text-align: right;
      }
      .foot {
        display: flex;
        align-items: center;
        justify-content: space-between;
        background: #141822;
        border: 1px solid #252b3b;
        border-radius: 12px;
        padding: 14px 18px;
      }
      .foot-left {
        font-size: 13px;
        color: #e4e8f0;
      }
      .foot-sub {
        font-size: 12px;
        color: #7b8299;
        margin-top: 4px;
      }
    `,
  ],
})
export class LocalModelsComponent implements OnInit, OnDestroy {
  readonly store = inject(LocalModelsStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly logStore = inject(LogStore);

  readonly tab = signal<LocalModelKind>('transcription');
  readonly errors = signal<Readonly<Record<string, string>>>({});
  private readonly _progress = signal<Readonly<Record<string, number>>>({});
  private readonly timers = new Map<string, ReturnType<typeof setInterval>>();
  private readonly pullRequestByModel = new Map<string, string>();
  private readonly whisperRequestByModel = new Map<string, string>();
  private readonly unsubscribeProgress: () => void;
  private readonly unsubscribeWhisperProgress: () => void;

  readonly llmStatus = signal<ProviderStatusDto | null>(null);
  readonly busy = signal<boolean>(false);
  readonly autostartBusy = signal<boolean>(false);
  readonly actionError = signal<string | null>(null);

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private pollResetTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.unsubscribeProgress = this.bridge.onLlmPullOllamaProgress((event) => {
      const catalogId = [...this.pullRequestByModel.entries()].find(
        ([, reqId]) => reqId === event.requestId,
      )?.[0];
      if (catalogId === undefined) return;
      this.setProgress(catalogId, event.percent);
    });
    this.unsubscribeWhisperProgress = this.bridge.onWhisperDownloadProgress((event) => {
      const catalogId = [...this.whisperRequestByModel.entries()].find(
        ([, reqId]) => reqId === event.requestId,
      )?.[0];
      if (catalogId === undefined) return;
      this.setProgress(catalogId, event.percent);
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      const res = await this.bridge.whisperListInstalled();
      for (const variant of res.installed) {
        const catalogId = WHISPER_TO_CATALOG[variant];
        if (!this.store.isDownloaded(catalogId)) {
          this.store.markDownloaded(catalogId);
        }
      }
    } catch {
      // Best-effort sync; UI will fall back to localStorage flags.
    }
    void this.refreshStatus();
    void this.store.hydrateFromWorker();
    this.startPolling(4000);
  }

  readonly catalog = computed<readonly LocalModelCatalogEntry[]>(() =>
    this.store.catalogFor(this.tab()),
  );

  readonly hasSelected = computed<boolean>(() => this.store.selectedFor(this.tab()) !== null);

  readonly selectedTitle = computed<string | null>(() => {
    const id = this.store.selectedFor(this.tab());
    if (id === null) return null;
    return this.catalog().find((m) => m.id === id)?.title ?? id;
  });

  readonly downloadedCount = computed<number>(() => this.store.downloadedFor(this.tab()).length);

  readonly storageLabel = computed<string>(() => {
    const bytes = this.store
      .downloadedFor(this.tab())
      .reduce((sum, m) => sum + m.sizeBytes, 0);
    return formatBytes(bytes);
  });

  readonly hasUnused = computed<boolean>(() => {
    const sel = this.store.selected();
    const kept = new Set([sel.transcription, sel.analysis].filter((v): v is string => v !== null));
    return this.store.downloaded().some((id) => !kept.has(id));
  });

  serverTitle(): string {
    return this.tab() === 'transcription' ? 'Whisper Transcription' : 'LLM Server';
  }

  serverSub(): string {
    return this.tab() === 'transcription'
      ? 'Convert speech to text locally. Larger models are more accurate but need more memory.'
      : 'Starts automatically when needed. Runs locally, private by default.';
  }

  setTab(tab: LocalModelKind): void {
    this.tab.set(tab);
  }

  isDownloaded(id: string): boolean {
    return this.store.isDownloaded(id);
  }

  isSelected(id: string): boolean {
    return this.store.selectedFor(this.tab()) === id;
  }

  progressOf(id: string): number | null {
    const val = this._progress()[id];
    return typeof val === 'number' ? val : null;
  }

  onDownload(id: string): void {
    if (this.store.isDownloaded(id)) return;
    if (
      this.timers.has(id) ||
      this.pullRequestByModel.has(id) ||
      this.whisperRequestByModel.has(id)
    ) {
      return;
    }

    const entry = lookupCatalog(id);
    if (entry?.kind === 'analysis') {
      const tag = ollamaTagFor(id);
      if (tag !== null) {
        void this.realPull(id, tag);
        return;
      }
    }
    if (entry?.kind === 'transcription') {
      const variant = whisperVariantFor(id);
      if (variant !== null) {
        void this.realWhisperDownload(id, variant);
        return;
      }
    }

    // Fall back to simulated progress for entries without a real backend.
    this.setProgress(id, 0);
    const start = Date.now();
    const handle = setInterval(() => {
      const elapsed = Date.now() - start;
      const pct = Math.min(100, Math.round((elapsed / DOWNLOAD_DURATION_MS) * 100));
      this.setProgress(id, pct);
      if (pct >= 100) {
        this.finishDownload(id);
      }
    }, DOWNLOAD_TICK_MS);
    this.timers.set(id, handle);
  }

  onRemove(id: string): void {
    const entry = lookupCatalog(id);
    if (entry?.kind === 'analysis') {
      const tag = ollamaTagFor(id);
      if (tag !== null) {
        void this.bridge
          .llmDeleteOllamaModel({ endpoint: OLLAMA_ENDPOINT, tag })
          .catch(() => {
            // best-effort; we still drop the local flag so the UI stays consistent
          })
          .finally(() => {
            this.store.markRemoved(id);
          });
        return;
      }
    }
    if (entry?.kind === 'transcription') {
      const variant = whisperVariantFor(id);
      if (variant !== null) {
        void this.bridge
          .whisperDeleteModel({ variant })
          .catch(() => {
            // best-effort; clear local flag regardless
          })
          .finally(() => {
            this.store.markRemoved(id);
          });
        return;
      }
    }
    this.store.markRemoved(id);
  }

  private async realWhisperDownload(catalogId: string, variant: WhisperVariant): Promise<void> {
    this.setProgress(catalogId, 0);
    this.clearError(catalogId);
    const requestId = `whisper:${catalogId}:${Date.now()}`;
    this.whisperRequestByModel.set(catalogId, requestId);
    try {
      await this.bridge.whisperDownloadModel({ variant, requestId });
      this.finishDownload(catalogId);
    } catch (err: unknown) {
      this.recordError(catalogId, `Whisper download ${variant}`, err);
      this._progress.update((prev) => {
        const next = { ...prev };
        delete next[catalogId];
        return next;
      });
    } finally {
      this.whisperRequestByModel.delete(catalogId);
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

  private async realPull(catalogId: string, tag: string): Promise<void> {
    this.setProgress(catalogId, 0);
    this.clearError(catalogId);
    const requestId = `${catalogId}:${Date.now()}`;
    this.pullRequestByModel.set(catalogId, requestId);
    try {
      await this.bridge.llmPullOllamaModel({ endpoint: OLLAMA_ENDPOINT, tag, requestId });
      this.finishDownload(catalogId);
    } catch (err: unknown) {
      this.recordError(catalogId, `Ollama pull ${tag}`, err);
      this._progress.update((prev) => {
        const next = { ...prev };
        delete next[catalogId];
        return next;
      });
    } finally {
      this.pullRequestByModel.delete(catalogId);
    }
  }

  onSelect(id: string): void {
    this.store.select(this.tab(), id);
  }

  onDeleteUnused(): void {
    this.store.deleteUnused();
  }

  private async refreshStatus(): Promise<void> {
    try {
      const status = await this.bridge.providerStatus({ id: 'ollama' });
      this.llmStatus.set(status);
    } catch {
      // Worker unavailable / unreachable — leave previous state.
    }
  }

  private startPolling(intervalMs: number): void {
    this.stopPolling();
    this.pollTimer = setInterval(() => void this.refreshStatus(), intervalMs);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  // Schedule a single deferred poll-reset, replacing any pending one. The
  // handle is tracked so ngOnDestroy can cancel it; otherwise the callback
  // would resurrect polling after the component is gone, leaking HTTP calls.
  private schedulePollReset(delayMs: number): void {
    if (this.pollResetTimer !== null) {
      clearTimeout(this.pollResetTimer);
      this.pollResetTimer = null;
    }
    this.pollResetTimer = setTimeout(() => {
      this.pollResetTimer = null;
      this.startPolling(4000);
    }, delayMs);
  }

  readonly restartDisabledTooltip = computed<string>(() => {
    const status = this.llmStatus();
    if (status === null) return '';
    if (status.running !== true) return 'Start the daemon first';
    if (status.managedByWorker !== true)
      return 'Cannot restart externally-started daemon. Stop it manually and click Start.';
    return '';
  });

  async onToggleAutostart(): Promise<void> {
    const status = this.llmStatus();
    if (status === null) return;
    const next = !status.autostart;
    this.autostartBusy.set(true);
    try {
      await this.bridge.providerSetAutostart({ id: 'ollama', enabled: next });
      await this.refreshStatus();
    } catch (err) {
      this.actionError.set(this.describeError(err));
    } finally {
      this.autostartBusy.set(false);
    }
  }

  async onStart(): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const status = await this.bridge.providerStart({ id: 'ollama' });
      this.llmStatus.set(status);
      this.startPolling(500);
      this.schedulePollReset(35_000);
    } catch (err) {
      this.actionError.set(this.describeError(err));
    } finally {
      this.busy.set(false);
    }
  }

  async onStop(): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const status = await this.bridge.providerStop({ id: 'ollama' });
      this.llmStatus.set(status);
      this.startPolling(500);
      this.schedulePollReset(10_000);
    } catch (err) {
      this.actionError.set(this.describeError(err));
    } finally {
      this.busy.set(false);
    }
  }

  async onRestart(): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const status = await this.bridge.providerRestart({ id: 'ollama' });
      this.llmStatus.set(status);
      this.startPolling(500);
      this.schedulePollReset(35_000);
    } catch (err) {
      this.actionError.set(this.describeError(err));
    } finally {
      this.busy.set(false);
    }
  }

  private describeError(err: unknown): string {
    if (err instanceof ProviderLifecycleUnsupportedError)
      return 'This runtime does not support remote start/stop.';
    if (err instanceof ProviderNotManagedError)
      return 'Cannot restart externally-started daemon.';
    if (err instanceof ProviderStartTimeoutError)
      return 'Daemon did not become reachable in time.';
    if (err instanceof ProviderUnreachableError) return 'Could not reach the daemon.';
    if (err instanceof WorkerHttpUnavailableError)
      return 'Worker is not running. Restart the app.';
    if (err instanceof Error) return err.message;
    return 'Unknown error';
  }

  ngOnDestroy(): void {
    this.stopPolling();
    if (this.pollResetTimer !== null) {
      clearTimeout(this.pollResetTimer);
      this.pollResetTimer = null;
    }
    for (const handle of this.timers.values()) clearInterval(handle);
    this.timers.clear();
    this.unsubscribeProgress();
    this.unsubscribeWhisperProgress();
  }

  private setProgress(id: string, pct: number): void {
    this._progress.update((prev) => ({ ...prev, [id]: pct }));
  }

  private finishDownload(id: string): void {
    const handle = this.timers.get(id);
    if (handle !== undefined) {
      clearInterval(handle);
      this.timers.delete(id);
    }
    this.store.markDownloaded(id);
    this._progress.update((prev) => {
      const next = { ...prev };
      delete next[id];
      return next;
    });
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
