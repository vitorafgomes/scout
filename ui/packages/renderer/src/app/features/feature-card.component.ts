import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import type { FeatureViewModel } from '../core/features.store';

@Component({
  selector: 'sc-feature-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <article class="card" [attr.data-test]="'card-' + feature.id">
      <header class="card-head">
        <span class="logo" aria-hidden="true">
          @switch (feature.id) {
            @case ('github') {
              <svg viewBox="0 0 24 24" fill="currentColor">
                <path
                  d="M12 2a10 10 0 0 0-3.2 19.5c.5.1.7-.2.7-.5v-1.8c-2.8.6-3.4-1.2-3.4-1.2-.4-1.1-1.1-1.4-1.1-1.4-.9-.6.1-.6.1-.6 1 .1 1.5 1 1.5 1 .9 1.6 2.4 1.1 3 .9.1-.7.4-1.1.6-1.4-2.2-.3-4.6-1.1-4.6-5 0-1.1.4-2 1-2.7-.1-.3-.4-1.3.1-2.7 0 0 .8-.3 2.8 1a9.6 9.6 0 0 1 5 0c1.9-1.3 2.7-1 2.7-1 .5 1.4.2 2.4.1 2.7.6.7 1 1.6 1 2.7 0 3.9-2.4 4.7-4.6 5 .4.3.7.9.7 1.9v2.8c0 .3.2.6.7.5A10 10 0 0 0 12 2z"
                />
              </svg>
            }
            @case ('youtube') {
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linejoin="round"
              >
                <rect x="3" y="6" width="18" height="12" rx="4" />
                <path d="M10 9.5v5l4.5-2.5z" fill="currentColor" stroke="none" />
              </svg>
            }
            @case ('kubernetes') {
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linejoin="round"
              >
                <path d="M12 3l8 4v10l-8 4-8-4V7z" />
                <circle cx="12" cy="12" r="3" />
              </svg>
            }
            @case ('desktop') {
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <rect x="3" y="4" width="18" height="12" rx="2" />
                <path d="M8 20h8M12 16v4" />
              </svg>
            }
            @default {
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <path d="M9 2v6M15 2v6M6 8h12v3a6 6 0 0 1-12 0zM12 17v5" />
              </svg>
            }
          }
        </span>
        <div class="title-block">
          <h3 class="heading">
            <span class="title">{{ feature.displayName }}</span>
            @if (connection !== null) {
              @if (connection !== 'unknown') {
                <span
                  class="badge"
                  [class.badge--ok]="connection === 'connected'"
                  [class.badge--off]="connection === 'not-connected'"
                  [attr.data-test]="'badge-' + feature.id"
                  ><i></i>{{ connection === 'connected' ? 'Connected' : 'Not connected' }}</span
                >
              }
            } @else {
              <span
                class="badge"
                [class.badge--ok]="feature.status === 'enabled'"
                [class.badge--warn]="feature.status === 'needs-credentials'"
                [class.badge--off]="feature.status === 'disabled'"
                [class.badge--err]="feature.status === 'error'"
                [attr.data-test]="'badge-' + feature.id"
                ><i></i>{{ badgeLabel() }}</span
              >
            }
          </h3>
          <p class="desc">{{ feature.description }}</p>
        </div>
      </header>

      @if (connection === null && feature.status === 'error' && feature.lastError !== null) {
        <p class="err" [attr.data-test]="'err-' + feature.id">{{ feature.lastError }}</p>
      }

      <div class="connect"><ng-content /></div>

      @if (readRows().length > 0) {
        <details [attr.data-test]="'reads-' + feature.id">
          <summary [attr.data-test]="'reads-summary-' + feature.id">
            <svg
              class="chev"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M6 3l5 5-5 5" />
            </svg>
            <h4>
              Reads <span class="count">{{ readRows().length }}</span>
            </h4>
          </summary>
          <div class="body">
            <ul class="chips">
              @for (row of readRows(); track row.scope) {
                <li
                  class="chip"
                  [attr.data-test]="'scope-row-' + row.scope"
                  [title]="row.actions.join(', ')"
                >
                  {{ row.scope
                  }}<span class="visually-hidden" [attr.data-test]="'access-' + row.scope">{{
                    row.access
                  }}</span>
                </li>
              }
            </ul>
          </div>
        </details>
      }

      @if (feature.writeClass !== 'none') {
        <details [attr.data-test]="'permissions-' + feature.id" [open]="feature.lastError !== null">
          <summary [attr.data-test]="'permissions-summary-' + feature.id">
            <svg
              class="chev"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M6 3l5 5-5 5" />
            </svg>
            <h4>
              Permissions <span class="count">{{ writeRows().length }}</span>
            </h4>
            <span class="mini" [class.on]="feature.granted"
              ><i></i>
              @if (feature.granted && feature.grantedAt !== null) {
                Writes allowed since {{ feature.grantedAt | date: 'yyyy-MM-dd' }}
              } @else if (feature.granted) {
                Writes allowed
              } @else {
                Writes not allowed
              }
            </span>
          </summary>
          <div class="body">
            <ul class="rows">
              @for (row of writeRows(); track row.scope) {
                <li class="row" [attr.data-test]="'scope-row-' + row.scope">
                  <code>{{ row.scope }}</code>
                  <span
                    class="access"
                    [class.write]="row.access === 'write'"
                    [class.irreversible]="row.access === 'irreversible'"
                    [attr.data-test]="'access-' + row.scope"
                    >{{ row.access }}</span
                  >
                  <span class="what" [attr.data-test]="'what-' + row.scope">{{
                    row.actions.join(', ')
                  }}</span>
                </li>
              }
            </ul>
            <div class="grant">
              <span
                class="state"
                [class.on]="feature.granted"
                [attr.data-test]="'grant-' + feature.id"
                ><i></i>
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
                    class="btn"
                    [attr.data-test]="'revoke-writes-' + feature.id"
                    [disabled]="feature.busy"
                    (click)="revokeWrites.emit()"
                  >
                    Revoke writes
                  </button>
                } @else {
                  <button
                    type="button"
                    class="btn primary"
                    [attr.data-test]="'allow-writes-' + feature.id"
                    [disabled]="feature.busy || connection !== 'connected'"
                    (click)="allowWrites.emit()"
                  >
                    Allow writes
                  </button>
                }
              }
            </div>
            @if (connection !== null && feature.lastError !== null) {
              <p class="err" [attr.data-test]="'grant-err-' + feature.id">
                {{ feature.lastError }}
              </p>
            }
          </div>
        </details>
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
                Clear credentials
              </button>
            }

            <button
              type="button"
              class="btn primary"
              [attr.data-test]="'cta-' + feature.id"
              [disabled]="feature.busy"
              (click)="openCredentialDialog.emit()"
            >
              {{ feature.hasCredentials ? 'Update credentials' : 'Add credentials' }}
            </button>
          </div>
        </footer>
      }
    </article>
  `,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        --surface: #131c2d;
        --surface-2: #1a2436;
        --line: #253249;
        --fg: #e6e9f0;
        --fg-2: #c3cadb;
        --muted: #8a93a6;
        --accent: #7aa2f7;
        --accent-strong: #3b6ff2;
        --ok-bg: #1f4d3a;
        --ok-line: #2f7a57;
        --ok-fg: #c9f2dc;
        --warn-bg: #4a3a12;
        --warn-fg: #ffd97a;
        --off-bg: #1a2436;
        --off-fg: #8a93a6;
        --err-bg: #4a1f1f;
        --err-fg: #ffb4b4;
        --read-bg: #1a2436;
        --read-fg: #c3cadb;
        --mono: 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
        color-scheme: dark;
      }
      .card {
        border: 1px solid var(--line);
        border-radius: 12px;
        background: var(--surface);
        color: var(--fg);
        padding: 18px 20px;
        display: flex;
        flex-direction: column;
        gap: 14px;
        min-width: 0;
        font-size: 14px;
        line-height: 1.5;
      }
      .card-head {
        display: flex;
        align-items: flex-start;
        gap: 12px;
      }
      .logo {
        width: 40px;
        height: 40px;
        border-radius: 10px;
        background: var(--surface-2);
        border: 1px solid var(--line);
        display: flex;
        align-items: center;
        justify-content: center;
        flex: none;
        color: var(--fg);
      }
      .logo svg {
        width: 22px;
        height: 22px;
      }
      .title-block {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .heading {
        margin: 0;
        font-size: 16px;
        font-weight: 600;
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
      }
      .desc {
        margin: 0;
        color: var(--muted);
        font-size: 13px;
        max-width: 64ch;
      }
      .badge {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        border-radius: 999px;
        padding: 3px 9px;
        flex: none;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        background: var(--off-bg);
        color: var(--off-fg);
        white-space: nowrap;
      }
      .badge i {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: currentColor;
        display: inline-block;
      }
      .badge--ok {
        background: var(--ok-bg);
        color: var(--ok-fg);
      }
      .badge--warn {
        background: var(--warn-bg);
        color: var(--warn-fg);
      }
      .badge--off {
        background: var(--off-bg);
        color: var(--off-fg);
      }
      .badge--err {
        background: var(--err-bg);
        color: var(--err-fg);
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--err-fg);
      }
      .connect {
        background: var(--surface-2);
        border: 1px solid var(--line);
        border-radius: 10px;
        padding: 10px 12px;
      }
      .connect:not(:has(*)) {
        display: none;
      }
      details {
        border: 1px solid var(--line);
        border-radius: 10px;
        background: var(--surface);
      }
      details[open] {
        background: var(--surface-2);
      }
      summary {
        list-style: none;
        cursor: pointer;
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        min-height: 44px;
        box-sizing: border-box;
        border-radius: 10px;
      }
      summary::-webkit-details-marker {
        display: none;
      }
      summary:focus-visible {
        outline: 2px solid var(--accent);
        outline-offset: 2px;
      }
      .chev {
        width: 16px;
        height: 16px;
        flex: none;
        color: var(--muted);
        transition: transform 0.15s;
      }
      details[open] .chev {
        transform: rotate(90deg);
      }
      h4 {
        margin: 0;
        flex: 1 1 auto;
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: var(--muted);
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .count {
        font-weight: 500;
        letter-spacing: 0;
        text-transform: none;
      }
      .mini {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        color: var(--fg-2);
      }
      .mini i,
      .state i {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: var(--muted);
        display: inline-block;
        flex: none;
      }
      .mini.on i,
      .state.on i {
        background: var(--ok-line);
      }
      .body {
        padding: 0 12px 12px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .chip {
        font-family: var(--mono);
        font-size: 12px;
        background: var(--read-bg);
        color: var(--read-fg);
        border: 1px solid var(--line);
        border-radius: 6px;
        padding: 3px 8px;
        cursor: default;
      }
      .chip:hover {
        border-color: var(--accent);
      }
      .visually-hidden {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: -1px;
        padding: 0;
        overflow: hidden;
        clip: rect(0 0 0 0);
        white-space: nowrap;
        border: 0;
      }
      .rows {
        margin: 0;
        padding: 0;
        list-style: none;
        display: flex;
        flex-direction: column;
        border: 1px solid var(--line);
        border-radius: 10px;
        overflow: hidden;
      }
      .row {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 4px 12px;
        padding: 9px 12px;
        align-items: center;
        border-top: 1px solid var(--line);
      }
      .row:first-child {
        border-top: 0;
      }
      .row code {
        font-family: var(--mono);
        font-size: 12.5px;
        color: var(--fg);
        overflow-wrap: anywhere;
      }
      .what {
        grid-column: 1 / -1;
        color: var(--muted);
        font-size: 12.5px;
      }
      .access {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        border-radius: 999px;
        padding: 2px 8px;
        justify-self: end;
        background: var(--read-bg);
        color: var(--read-fg);
      }
      .access.write {
        background: var(--warn-bg);
        color: var(--warn-fg);
      }
      .access.irreversible {
        background: var(--err-bg);
        color: var(--err-fg);
      }
      .grant {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        flex-wrap: wrap;
        padding-top: 4px;
      }
      .state {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 13px;
        color: var(--fg-2);
      }
      .foot {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        flex-wrap: wrap;
        border-top: 1px solid var(--line);
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
        color: var(--fg-2);
      }
      .switch input {
        appearance: none;
        width: 38px;
        height: 22px;
        border-radius: 999px;
        background: var(--line);
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
        background: #fff;
        transition: transform 0.15s;
      }
      .switch input:checked {
        background: var(--ok-line);
      }
      .switch input:checked::after {
        transform: translateX(16px);
      }
      .switch input:focus-visible {
        outline: 2px solid var(--accent);
        outline-offset: 2px;
      }
      .switch input:disabled {
        opacity: 0.45;
        cursor: default;
      }
      .btn {
        font: inherit;
        font-size: 13px;
        font-weight: 500;
        border-radius: 8px;
        padding: 8px 12px;
        cursor: pointer;
        border: 1px solid var(--line);
        background: var(--surface-2);
        color: var(--fg);
        min-height: 40px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .btn:hover {
        filter: brightness(1.12);
      }
      .btn:focus-visible {
        outline: 2px solid var(--accent);
        outline-offset: 2px;
      }
      .btn.primary {
        background: var(--accent-strong);
        border-color: var(--accent-strong);
        color: #fff;
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
        .chev,
        .switch input,
        .switch input::after {
          transition: none;
        }
      }
    `,
  ],
})
export class FeatureCardComponent {
  @Input({ required: true }) feature!: FeatureViewModel;
  /** Set for a feature whose connection the main process owns: replaces the control-plane badge and hides the toggle and credential actions. */
  @Input() connection: 'connected' | 'not-connected' | 'unknown' | null = null;

  @Output() readonly toggle = new EventEmitter<void>();
  @Output() readonly openCredentialDialog = new EventEmitter<void>();
  @Output() readonly clearCredentials = new EventEmitter<void>();
  @Output() readonly allowWrites = new EventEmitter<void>();
  @Output() readonly revokeWrites = new EventEmitter<void>();

  readRows(): NonNullable<FeatureViewModel['scopeMatrix']> {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access === 'read');
  }

  writeRows(): NonNullable<FeatureViewModel['scopeMatrix']> {
    return (this.feature.scopeMatrix ?? []).filter((row) => row.access !== 'read');
  }

  badgeLabel(): string {
    switch (this.feature.status) {
      case 'enabled':
        return 'Enabled';
      case 'needs-credentials':
        return 'Needs credentials';
      case 'disabled':
        return 'Disabled';
      case 'error':
        return 'Error';
    }
  }

  /** The toggle is locked while a request for this card is in flight, and while a feature that needs credentials has none. */
  toggleDisabled(): boolean {
    if (this.feature.busy) return true;
    if (this.feature.requiresCredentials && !this.feature.hasCredentials) return true;
    return false;
  }
}
