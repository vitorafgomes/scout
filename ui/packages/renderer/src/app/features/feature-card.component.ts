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
        <div class="title-block">
          <h3 class="title">{{ feature.displayName }}</h3>
          <p class="desc">{{ feature.description }}</p>
        </div>
        @if (connection !== null) {
          @if (connection !== 'unknown') {
            <span
              class="badge"
              [class.badge--ok]="connection === 'connected'"
              [class.badge--off]="connection === 'not-connected'"
              [attr.data-test]="'badge-' + feature.id"
            >
              {{ connection === 'connected' ? 'Connected' : 'Not connected' }}
            </span>
          }
        } @else {
          <span
            class="badge"
            [class.badge--ok]="feature.status === 'enabled'"
            [class.badge--warn]="feature.status === 'needs-credentials'"
            [class.badge--off]="feature.status === 'disabled'"
            [class.badge--err]="feature.status === 'error'"
            [attr.data-test]="'badge-' + feature.id"
          >
            {{ badgeLabel() }}
          </span>
        }
      </header>

      @if (connection === null && feature.status === 'error' && feature.lastError !== null) {
        <p class="err" [attr.data-test]="'err-' + feature.id">{{ feature.lastError }}</p>
      }

      <ng-content />

      @if (readRows().length > 0) {
        <section class="permissions" [attr.data-test]="'reads-' + feature.id">
          <h4>Reads</h4>
          <ul class="scope-rows">
            @for (row of readRows(); track row.scope) {
              <li [attr.data-test]="'scope-row-' + row.scope">
                <code>{{ row.scope }}</code>
                <span class="access" [attr.data-test]="'access-' + row.scope">{{
                  row.access
                }}</span>
                <span class="row-actions">{{ row.actions.join(', ') }}</span>
              </li>
            }
          </ul>
        </section>
      }

      @if (feature.writeClass !== 'none') {
        <section class="permissions" [attr.data-test]="'permissions-' + feature.id">
          <h4>Permissions</h4>
          <ul class="scope-rows">
            @for (row of writeRows(); track row.scope) {
              <li [attr.data-test]="'scope-row-' + row.scope">
                <code>{{ row.scope }}</code>
                <span
                  class="access"
                  [class.access--write]="row.access === 'write'"
                  [class.access--irreversible]="row.access === 'irreversible'"
                  [attr.data-test]="'access-' + row.scope"
                  >{{ row.access }}</span
                >
                <span class="row-actions">{{ row.actions.join(', ') }}</span>
              </li>
            }
          </ul>
          <p class="grant" [attr.data-test]="'grant-' + feature.id">
            @if (feature.granted && feature.grantedAt !== null) {
              Write permission: granted on {{ feature.grantedAt | date: 'yyyy-MM-dd' }}
            } @else if (feature.granted) {
              Write permission: granted
            } @else {
              Write permission: not granted
            }
          </p>
          @if (connection !== null) {
            @if (feature.granted) {
              <button
                type="button"
                class="btn btn--secondary"
                [attr.data-test]="'revoke-writes-' + feature.id"
                [disabled]="feature.busy"
                (click)="revokeWrites.emit()"
              >
                Revoke writes
              </button>
            } @else {
              <button
                type="button"
                class="btn btn--primary"
                [attr.data-test]="'allow-writes-' + feature.id"
                [disabled]="feature.busy || connection !== 'connected'"
                (click)="allowWrites.emit()"
              >
                Allow writes
              </button>
            }
            @if (feature.lastError !== null) {
              <p class="err" [attr.data-test]="'grant-err-' + feature.id">
                {{ feature.lastError }}
              </p>
            }
          }
        </section>
      }

      @if (connection === null) {
        <footer class="actions">
          <label class="toggle">
            <input
              type="checkbox"
              [checked]="feature.enabled"
              [disabled]="toggleDisabled()"
              [attr.data-test]="'toggle-' + feature.id"
              (change)="$any($event.target).checked = feature.enabled; toggle.emit()"
            />
            <span>{{ feature.enabled ? 'Enabled' : 'Disabled' }}</span>
          </label>

          @if (feature.hasCredentials) {
            <button
              type="button"
              class="btn btn--secondary"
              [attr.data-test]="'clear-' + feature.id"
              [disabled]="feature.busy"
              (click)="clearCredentials.emit()"
            >
              Clear credentials
            </button>
          }

          <button
            type="button"
            class="btn btn--primary"
            [attr.data-test]="'cta-' + feature.id"
            [disabled]="feature.busy"
            (click)="openCredentialDialog.emit()"
          >
            {{ feature.hasCredentials ? 'Update credentials' : 'Add credentials' }}
          </button>
        </footer>
      }
    </article>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .card-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 12px;
      }
      .title {
        margin: 0 0 4px;
        font-size: 16px;
        color: #e4e8f0;
      }
      .desc {
        margin: 0;
        font-size: 13px;
        color: #7b8299;
      }
      .badge {
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        padding: 3px 8px;
        border-radius: 999px;
        background: #1e2333;
        color: #7b8299;
        white-space: nowrap;
      }
      .badge--ok {
        background: #133022;
        color: #4ade80;
      }
      .badge--warn {
        background: #3a2a10;
        color: #fbbf24;
      }
      .badge--off {
        background: #1e2333;
        color: #7b8299;
      }
      .badge--err {
        background: #3a1414;
        color: #f87171;
      }
      .err {
        margin: 0;
        font-size: 12px;
        color: #f87171;
      }
      .permissions {
        border-top: 1px solid #252b3b;
        padding-top: 10px;
        display: flex;
        flex-direction: column;
        gap: 8px;
        align-items: flex-start;
      }
      .permissions h4 {
        margin: 0;
        font-size: 12px;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: #7b8299;
      }
      .scope-rows {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 12px;
        color: #e4e8f0;
      }
      .scope-rows code {
        font-family: 'JetBrains Mono', ui-monospace, monospace;
        font-size: 11px;
        margin-right: 6px;
      }
      .access {
        font-size: 10px;
        text-transform: uppercase;
        padding: 1px 6px;
        border-radius: 999px;
        background: #1e2333;
        color: #7b8299;
        margin-right: 6px;
      }
      .access--write {
        background: #3a2a10;
        color: #fbbf24;
      }
      .access--irreversible {
        background: #3a1414;
        color: #f87171;
      }
      .grant {
        margin: 0;
        font-size: 13px;
        color: #e4e8f0;
      }
      .actions {
        display: flex;
        gap: 8px;
        align-items: center;
        flex-wrap: wrap;
      }
      .toggle {
        display: inline-flex;
        gap: 6px;
        align-items: center;
        font-size: 13px;
        color: #e4e8f0;
        margin-right: auto;
      }
      .btn {
        font-size: 13px;
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
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

  // Toggle is locked when the feature requires credentials but has none, and
  // when an in-flight request is pending against this card.
  toggleDisabled(): boolean {
    if (this.feature.busy) return true;
    if (this.feature.requiresCredentials && !this.feature.hasCredentials) return true;
    return false;
  }
}
