import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import type { FeatureViewModel } from '../core/features.store';

@Component({
  selector: 'sc-feature-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="card" [attr.data-test]="'card-' + feature.id">
      <header class="card-head">
        <div class="title-block">
          <h3 class="title">{{ feature.displayName }}</h3>
          <p class="desc">{{ feature.description }}</p>
        </div>
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
      </header>

      @if (feature.status === 'error' && feature.lastError !== null) {
        <p class="err" [attr.data-test]="'err-' + feature.id">{{ feature.lastError }}</p>
      }

      <footer class="actions">
        <label class="toggle">
          <input
            type="checkbox"
            [checked]="feature.enabled"
            [disabled]="toggleDisabled()"
            [attr.data-test]="'toggle-' + feature.id"
            (change)="toggle.emit()"
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
    </article>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-border);
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
        color: var(--sc-text);
      }
      .desc {
        margin: 0;
        font-size: 13px;
        color: var(--sc-text-muted);
      }
      .badge {
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        padding: 3px 8px;
        border-radius: 999px;
        background: var(--sc-surface-3);
        color: var(--sc-text-muted);
        white-space: nowrap;
      }
      .badge--ok {
        background: var(--sc-success-bg);
        color: var(--sc-success-text);
      }
      .badge--warn {
        background: var(--sc-warning-bg);
        color: var(--sc-warning);
      }
      .badge--off {
        background: var(--sc-surface-3);
        color: var(--sc-text-muted);
      }
      .badge--err {
        background: var(--sc-danger-bg);
        color: var(--sc-danger-text);
      }
      .err {
        margin: 0;
        font-size: 12px;
        color: var(--sc-danger-text);
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
        color: var(--sc-text);
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
        background: var(--sc-accent);
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: var(--sc-text);
        border-color: var(--sc-border);
      }
    `,
  ],
})
export class FeatureCardComponent {
  @Input({ required: true }) feature!: FeatureViewModel;

  @Output() readonly toggle = new EventEmitter<void>();
  @Output() readonly openCredentialDialog = new EventEmitter<void>();
  @Output() readonly clearCredentials = new EventEmitter<void>();

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
