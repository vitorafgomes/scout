import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import { MatButtonModule } from '@angular/material/button';

import type { FeatureViewModel } from '../core/features.store';
import { IconComponent } from '../core/icon.component';

const SCOPE_LABELS: Readonly<Record<string, string>> = {
  'channels.list': 'Channel overview',
  'playlistItems.list': 'Latest uploads',
  'videos.list': 'Video statistics',
  'search.list': 'Search',
  'commentThreads.list': 'Comments',
  'youtubeAnalytics reports.query': 'Analytics',
  'youtubeReporting reports.list': 'Reach reports',
  'api.openverse.org and the host of the picture it names': 'Free-licence pictures',
  'youtubeReporting jobs.create': 'Reach reporting job',
  'videos.insert': 'Upload a video',
  'videos.update': 'Video details',
  'thumbnails.set': 'Thumbnail',
  'playlistItems.insert': 'Add to a playlist',
  'comments.insert': 'Reply to a comment',
  'comments.setModerationStatus': 'Moderate a comment',
};

/** A scope as the owner reads it: the catalog's words, with the YouTube API method names in plain words. */
export function scopeLabel(scope: string): string {
  return SCOPE_LABELS[scope] ?? scope;
}

/** Everything a feature's side panel lists below its connection: the error, the reads, the permissions with Allow and Revoke writes in the grant box, and the control-plane toggle and credentials. */
@Component({
  selector: 'sc-feature-details',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, MatButtonModule],
  template: `
    @if (connection === null && feature.status === 'error' && feature.lastError !== null) {
      <p class="err" [attr.data-test]="'err-' + feature.id">{{ feature.lastError }}</p>
    }

    @if (readRows().length > 0) {
      <section
        class="block"
        [attr.aria-labelledby]="'reads-title-' + feature.id"
        [attr.data-test]="'reads-' + feature.id"
      >
        <h3
          [id]="'reads-title-' + feature.id"
          class="block-title"
          [attr.data-test]="'reads-summary-' + feature.id"
        >
          Reads <span class="count">{{ readRows().length }}</span>
        </h3>
        <ul class="rows">
          @for (row of readRows(); track row.scope) {
            <li class="row" [attr.data-test]="'scope-row-' + row.scope">
              <span class="label" [attr.data-test]="'label-' + row.scope">{{
                scopeLabel(row.scope)
              }}</span>
              <span class="what" [attr.data-test]="'what-' + row.scope">{{ row.summary }}</span>
            </li>
          }
        </ul>
      </section>
    }

    @if (feature.writeClass !== 'none') {
      <section
        class="block"
        [attr.aria-labelledby]="'permissions-title-' + feature.id"
        [attr.data-test]="'permissions-' + feature.id"
      >
        <h3
          [id]="'permissions-title-' + feature.id"
          class="block-title"
          [attr.data-test]="'permissions-summary-' + feature.id"
        >
          Permissions <span class="count">{{ writeRows().length }}</span>
        </h3>
        <ul class="rows">
          @for (row of writeRows(); track row.scope) {
            <li class="row" [attr.data-test]="'scope-row-' + row.scope">
              <span class="head">
                <span class="label" [attr.data-test]="'label-' + row.scope">{{
                  scopeLabel(row.scope)
                }}</span>
                <span
                  class="access"
                  [class.write]="row.access === 'write'"
                  [class.irreversible]="row.access === 'irreversible'"
                  [attr.data-test]="'access-' + row.scope"
                  >{{ row.access === 'irreversible' ? 'Cannot be undone' : 'Write' }}</span
                >
              </span>
              <span class="what" [attr.data-test]="'what-' + row.scope">{{ row.summary }}</span>
            </li>
          }
        </ul>
        <div class="grant">
          <span class="state" [class.on]="feature.granted" [attr.data-test]="'grant-' + feature.id">
            @if (feature.granted && feature.grantedAt !== null) {
              Write permission: granted on {{ feature.grantedAt | date: 'yyyy-MM-dd' }}
            } @else if (feature.granted) {
              Write permission: granted
            } @else {
              Write permission: not granted
            }
          </span>
          @if (connection !== null) {
            @if (feature.granted) {
              <button
                type="button"
                matButton="outlined"
                [attr.data-test]="'revoke-writes-' + feature.id"
                [disabled]="feature.busy"
                (click)="revokeWrites.emit()"
              >
                <sc-icon name="close" [size]="16" />Revoke writes
              </button>
            } @else {
              <button
                type="button"
                matButton="filled"
                [attr.data-test]="'allow-writes-' + feature.id"
                [disabled]="feature.busy || connection !== 'connected'"
                [attr.aria-describedby]="'grant-reason-' + feature.id"
                (click)="allowWrites.emit()"
              >
                <sc-icon name="check" [size]="16" />Allow writes
              </button>
              <span
                class="reason"
                [id]="'grant-reason-' + feature.id"
                [attr.data-test]="'grant-reason-' + feature.id"
                >{{
                  connection === 'connected'
                    ? 'You review the list before anything is allowed.'
                    : 'Connect ' + feature.displayName + ' first to allow writes.'
                }}</span
              >
            }
          }
        </div>
        @if (connection !== null && feature.lastError !== null) {
          <p class="err" [attr.data-test]="'grant-err-' + feature.id">
            {{ feature.lastError }}
          </p>
        }
      </section>
    }

    @if (connection === null) {
      <footer class="foot">
        <label class="switch">
          <input
            type="checkbox"
            [checked]="feature.enabled"
            [disabled]="toggleDisabled()"
            [attr.data-test]="'toggle-' + feature.id"
            (change)="$any($event.target).checked = feature.enabled; toggle.emit()"
          />
          <span>{{ feature.enabled ? 'Enabled' : 'Disabled' }}</span>
        </label>

        <div class="group">
          @if (feature.hasCredentials) {
            <button
              type="button"
              class="btn quiet"
              [attr.data-test]="'clear-' + feature.id"
              [disabled]="feature.busy"
              (click)="clearCredentials.emit()"
            >
              <sc-icon name="trash" [size]="16" />Clear credentials
            </button>
          }

          <button
            type="button"
            class="btn primary"
            [attr.data-test]="'cta-' + feature.id"
            [disabled]="feature.busy"
            (click)="openCredentialDialog.emit()"
          >
            <sc-icon [name]="feature.hasCredentials ? 'edit' : 'plug'" [size]="16" />{{
              feature.hasCredentials ? 'Update credentials' : 'Add credentials'
            }}
          </button>
        </div>
      </footer>
    }
  `,
  styles: [
    `
      :host {
        display: flex;
        flex-direction: column;
        gap: 14px;
        min-width: 0;
        font-size: 14px;
        line-height: 1.5;
        color: var(--sc-fg);
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .block {
        border-top: 1px solid var(--sc-line);
        padding-top: 12px;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .block-title {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
        line-height: normal;
        color: var(--sc-fg);
      }
      .count {
        font-weight: 500;
        color: var(--sc-muted);
      }
      .rows {
        margin: 0;
        padding: 0;
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .row {
        display: flex;
        flex-direction: column;
        gap: 2px;
        font-size: 13px;
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .label {
        font-weight: 500;
        color: var(--sc-fg);
        overflow-wrap: anywhere;
      }
      .what {
        color: var(--sc-muted);
      }
      .access {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        font-size: 11px;
        font-weight: 600;
        border-radius: 10px;
        padding: 1px 8px;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .access.write {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .access.irreversible {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .grant {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
      }
      .state {
        margin-right: auto;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 2px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 500;
        line-height: normal;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .state.on {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .grant button sc-icon {
        margin-right: 8px;
      }
      .foot {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        flex-wrap: wrap;
        border-top: 1px solid var(--sc-line);
        padding-top: 12px;
      }
      .group {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .switch {
        display: inline-flex;
        align-items: center;
        gap: 10px;
        cursor: pointer;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .switch input {
        appearance: none;
        width: 38px;
        height: 22px;
        border-radius: 999px;
        background: var(--sc-line);
        position: relative;
        transition: background 0.15s;
        margin: 0;
        cursor: pointer;
      }
      .switch input::after {
        content: '';
        position: absolute;
        top: 3px;
        left: 3px;
        width: 16px;
        height: 16px;
        border-radius: 50%;
        background: var(--sc-on-accent);
        transition: transform 0.15s;
      }
      .switch input:checked {
        background: var(--sc-ok);
      }
      .switch input:checked::after {
        transform: translateX(16px);
      }
      .switch input:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .switch input:disabled {
        opacity: 0.45;
        cursor: default;
      }
      .reason {
        flex-basis: 100%;
        font-size: 12px;
        line-height: normal;
        color: var(--sc-muted);
      }
      .btn {
        font: inherit;
        font-size: 14px;
        font-weight: 500;
        border-radius: 18px;
        padding: 0 14px;
        cursor: pointer;
        border: 1px solid var(--sc-line);
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        min-height: 36px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .btn:hover {
        filter: brightness(1.12);
      }
      .btn:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .btn.primary {
        background: var(--sc-accent-strong);
        border-color: var(--sc-accent-strong);
        color: var(--sc-on-accent);
      }
      .btn.quiet {
        background: transparent;
      }
      .btn:disabled {
        opacity: 0.45;
        cursor: default;
        filter: none;
      }
      @media (prefers-reduced-motion: reduce) {
        .switch input,
        .switch input::after {
          transition: none;
        }
      }
    `,
  ],
})
export class FeatureDetailsComponent {
  @Input({ required: true }) feature!: FeatureViewModel;
  /** Set for a feature whose connection the main process owns: hides the toggle and credential actions and offers Allow writes. */
  @Input() connection: 'connected' | 'not-connected' | 'unknown' | null = null;

  @Output() readonly toggle = new EventEmitter<void>();
  @Output() readonly openCredentialDialog = new EventEmitter<void>();
  @Output() readonly clearCredentials = new EventEmitter<void>();
  @Output() readonly allowWrites = new EventEmitter<void>();
  @Output() readonly revokeWrites = new EventEmitter<void>();

  /** The words a person reads for a scope; a scope already written in words is shown as it is. */
  scopeLabel(scope: string): string {
    return scopeLabel(scope);
  }

  readRows(): NonNullable<FeatureViewModel['scopeMatrix']> {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access === 'read');
  }

  writeRows(): NonNullable<FeatureViewModel['scopeMatrix']> {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access !== 'read');
  }

  /** The toggle is locked while a request for this feature is in flight, and while a feature that needs credentials has none. */
  toggleDisabled(): boolean {
    if (this.feature.busy) return true;
    if (this.feature.requiresCredentials && !this.feature.hasCredentials) return true;
    return false;
  }
}
