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
