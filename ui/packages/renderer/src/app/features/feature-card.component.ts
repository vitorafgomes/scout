import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

import type { FeatureViewModel } from '../core/features.store';
import { IconComponent, type IconName } from '../core/icon.component';

import { FeatureLogoComponent } from './feature-logo.component';

/** The colour of a card's state chip. */
export type ChipTone = 'ok' | 'warn' | 'off' | 'err';

/** A feature's state as its chip reads it. */
export interface CardChip {
  readonly label: string;
  readonly tone: ChipTone;
}

/** How many reads, writes and writes that cannot be undone a feature declares. */
export interface PermissionCounts {
  readonly read: number;
  readonly write: number;
  readonly irreversible: number;
}

/** The one action a card offers beside Details. */
export interface CardAction {
  readonly label: string;
  readonly icon: IconName;
  readonly filled: boolean;
  readonly disabled: boolean;
}

/** The chip of a feature: the connection main owns when there is one, Desktop reading On; else the control plane's status. Null while the connection is not known yet. */
export function featureChip(
  feature: FeatureViewModel,
  connection: 'connected' | 'not-connected' | 'unknown' | null,
): CardChip | null {
  if (connection === 'unknown') return null;
  if (connection === 'connected') {
    return { label: feature.id === 'desktop' ? 'On' : 'Connected', tone: 'ok' };
  }
  if (connection === 'not-connected') return { label: 'Not connected', tone: 'off' };
  switch (feature.status) {
    case 'enabled':
      return { label: 'Enabled', tone: 'ok' };
    case 'needs-credentials':
      return { label: 'Needs credentials', tone: 'warn' };
    case 'disabled':
      return { label: 'Disabled', tone: 'off' };
    case 'error':
      return { label: 'Error', tone: 'err' };
  }
}

/** The glyph a state chip leads with, per tone: a check, a warning triangle, a circled cross, or a dash for a feature that is off. */
export const CHIP_GLYPHS: Readonly<Record<ChipTone, string>> = {
  ok: 'M20 6L9 17l-5-5',
  warn: 'M12 3l9 16H3zM12 10v4',
  err: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM9 9l6 6M15 9l-6 6',
  off: 'M6 12h12',
};

/** The feature's declared rows counted by access. */
export function permissionCounts(feature: FeatureViewModel): PermissionCounts {
  const rows = feature.scopeMatrix ?? [];
  return {
    read: rows.filter((r) => r.access === 'read').length,
    write: rows.filter((r) => r.access === 'write').length,
    irreversible: rows.filter((r) => r.access === 'irreversible').length,
  };
}

/** One feature in the Features grid, always the same height: its name, state, one fact, its permission counts, one action and Details. */
@Component({
  selector: 'sc-feature-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FeatureLogoComponent, IconComponent, MatButtonModule],
  template: `
    <article
      class="card"
      [attr.data-test]="'card-' + id()"
      [attr.aria-labelledby]="'feature-title-' + id()"
    >
      <div class="head">
        <sc-feature-logo [feature]="id()" />
        <div class="title-block">
          <h2 class="title" [id]="'feature-title-' + id()">{{ name() }}</h2>
          @if (chip(); as c) {
            <span
              class="badge"
              [class.badge--ok]="c.tone === 'ok'"
              [class.badge--warn]="c.tone === 'warn'"
              [class.badge--err]="c.tone === 'err'"
              [attr.data-test]="'badge-' + id()"
            >
              <svg
                class="badge-glyph"
                viewBox="0 0 24 24"
                width="13"
                height="13"
                fill="none"
                stroke="currentColor"
                stroke-width="2.4"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path [attr.d]="glyphs[c.tone]" /></svg
              ><span class="badge-text">{{ c.label }}</span></span
            >
          }
        </div>
      </div>

      <p
        class="fact"
        [class.fact--err]="factError()"
        [attr.title]="fact()"
        [attr.data-test]="'fact-' + id()"
      >
        {{ fact() }}
      </p>

      <ul
        class="perms"
        [attr.aria-label]="name() + ' permissions'"
        [attr.data-test]="'perms-' + id()"
      >
        @if (counts(); as n) {
          @if (n.read > 0) {
            <li class="perm">Read {{ n.read }}</li>
          }
          @if (n.write > 0) {
            <li class="perm perm--write">Write {{ n.write }}</li>
          }
          @if (n.irreversible > 0) {
            <li class="perm perm--irreversible">Cannot be undone {{ n.irreversible }}</li>
          }
        }
        @if (noPermissions(); as none) {
          <li class="perm-none">{{ none }}</li>
        }
      </ul>

      <div class="foot">
        @if (action(); as a) {
          <button
            type="button"
            class="act"
            [matButton]="a.filled ? 'filled' : 'outlined'"
            [disabled]="a.disabled"
            [attr.aria-describedby]="'feature-title-' + id()"
            [attr.data-test]="'act-' + id()"
            (click)="act.emit()"
          >
            <sc-icon [name]="a.icon" [size]="16" /><span class="act-text">{{ a.label }}</span>
          </button>
        }
        <button
          type="button"
          matButton="outlined"
          class="details"
          aria-haspopup="dialog"
          [attr.aria-label]="name() + ' details'"
          [attr.aria-expanded]="expanded()"
          [attr.data-test]="'details-' + id()"
          (click)="details.emit()"
        >
          <svg
            viewBox="0 0 24 24"
            width="16"
            height="16"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <path d="M14 4v16M17 9h1M17 12h1" /></svg
          >Details
        </button>
      </div>
    </article>
  `,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        height: 100%;
      }
      .card {
        height: 100%;
        box-sizing: border-box;
        overflow: hidden;
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        color: var(--sc-fg);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        min-width: 0;
        font-size: 14px;
      }
      .head {
        display: flex;
        align-items: flex-start;
        gap: 12px;
        min-width: 0;
      }
      .title-block {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 4px;
      }
      .title {
        margin: 0;
        max-width: 100%;
        font-size: 16px;
        font-weight: 600;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        max-width: 100%;
        box-sizing: border-box;
        padding: 2px 10px;
        border-radius: 12px;
        font-size: 12px;
        font-weight: 500;
        white-space: nowrap;
        overflow: hidden;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .badge-text {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .badge-glyph {
        flex: 0 0 13px;
      }
      .badge--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .badge--warn {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .badge--err {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .fact {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .fact--err {
        color: var(--sc-error);
      }
      .perms {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        gap: 6px;
        min-width: 0;
        overflow: hidden;
        white-space: nowrap;
      }
      .perm {
        flex: 0 1 auto;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        padding: 2px 8px;
        border-radius: 10px;
        font-size: 12px;
        font-weight: 600;
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .perm--write {
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
      }
      .perm--irreversible {
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .perm-none {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .foot {
        margin-top: auto;
        display: flex;
        align-items: center;
        gap: 8px;
        min-width: 0;
      }
      .act,
      .details {
        --mat-button-outlined-horizontal-padding: 12px;
        --mat-button-filled-horizontal-padding: 12px;
      }
      .act {
        min-width: 0;
        white-space: nowrap;
        overflow: hidden;
      }
      .act-text {
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .details {
        margin-left: auto;
        flex: 0 0 auto;
      }
      button sc-icon,
      button svg {
        margin-right: 6px;
        flex: none;
      }
    `,
  ],
})
export class FeatureCardComponent {
  readonly glyphs = CHIP_GLYPHS;
  readonly id = input.required<string>();
  readonly name = input.required<string>();
  readonly chip = input<CardChip | null>(null);
  readonly fact = input.required<string>();
  readonly factError = input(false);
  readonly counts = input<PermissionCounts | null>(null);
  /** What the permission line says for a feature that declares no rows of its own. */
  readonly noPermissions = input<string | null>(null);
  readonly action = input<CardAction | null>(null);
  readonly expanded = input(false);

  readonly act = output<void>();
  readonly details = output<void>();
}
