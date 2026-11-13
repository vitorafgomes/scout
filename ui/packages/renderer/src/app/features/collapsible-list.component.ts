import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  contentChild,
  input,
  signal,
  type OnInit,
} from '@angular/core';

/** Above this many rows the list scrolls inside a capped height and offers a filter. */
export const LONG_LIST = 8;

/** Where each list's open or closed choice is kept, by the list's key. */
export const COLLAPSIBLE_LISTS_STORAGE_KEY = 'scout:features.lists';

function storage(): Storage | null {
  try {
    return (globalThis as { window?: Window }).window?.localStorage ?? null;
  } catch {
    return null;
  }
}

function readOpen(): Record<string, boolean> {
  try {
    const raw = storage()?.getItem(COLLAPSIBLE_LISTS_STORAGE_KEY) ?? null;
    if (raw === null) return {};
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return {};
    return Object.fromEntries(
      Object.entries(parsed).filter(([, value]) => typeof value === 'boolean'),
    ) as Record<string, boolean>;
  } catch {
    return {};
  }
}

function forgetOpen(key: string): void {
  try {
    const stored = readOpen();
    if (!(key in stored)) return;
    storage()?.setItem(
      COLLAPSIBLE_LISTS_STORAGE_KEY,
      JSON.stringify(Object.fromEntries(Object.entries(stored).filter(([k]) => k !== key))),
    );
  } catch {
    return;
  }
}

function writeOpen(key: string, open: boolean): void {
  try {
    storage()?.setItem(
      COLLAPSIBLE_LISTS_STORAGE_KEY,
      JSON.stringify({ ...readOpen(), [key]: open }),
    );
  } catch {
    return;
  }
}

/**
 * A list that starts collapsed behind a one-line summary: a click opens it, a second click closes it, and
 * the choice is remembered per key. A list given `startOpen` opens each time it is drawn and stores nothing.
 * A long list scrolls inside a capped height and has a filter above it.
 */
@Component({
  selector: 'sc-collapsible-list',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="list" [attr.data-test]="'list-' + key()">
      <button
        type="button"
        class="disclosure"
        [attr.id]="'list-head-' + key()"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="'list-body-' + key()"
        [attr.data-test]="'list-toggle-' + key()"
        (click)="toggle()"
      >
        <svg class="chev" viewBox="0 0 16 16" aria-hidden="true">
          <path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" stroke-width="2" />
        </svg>
        <span [attr.data-test]="'list-summary-' + key()">{{ summary() }}</span>
      </button>
      @if (open()) {
        <div
          class="body"
          role="region"
          [attr.id]="'list-body-' + key()"
          [attr.aria-labelledby]="'list-head-' + key()"
          [attr.data-test]="'list-body-' + key()"
        >
          <input
            type="search"
            class="filter"
            [attr.aria-label]="filterLabel()"
            [placeholder]="filterLabel()"
            [attr.data-test]="'list-filter-' + key()"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
          />
          <ul class="rows" [class.rows--long]="items().length > long">
            @for (item of shown(); track textOf()(item)) {
              <li class="row" [attr.data-test]="'list-row-' + key()">
                <ng-container *ngTemplateOutlet="row(); context: { $implicit: item }" />
              </li>
            } @empty {
              @if (noMatch(); as none) {
                <li class="no-match" [attr.data-test]="'list-no-match-' + key()">{{ none }}</li>
              }
            }
          </ul>
        </div>
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .disclosure {
        all: unset;
        box-sizing: border-box;
        display: flex;
        align-items: center;
        gap: 8px;
        width: 100%;
        min-height: 36px;
        padding: 6px 4px;
        border-radius: 8px;
        cursor: pointer;
        font-size: 13px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .disclosure:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .chev {
        width: 14px;
        height: 14px;
        flex: none;
        color: var(--sc-muted);
        transition: transform 0.15s;
      }
      .disclosure[aria-expanded='true'] .chev {
        transform: rotate(90deg);
      }
      .body {
        display: flex;
        flex-direction: column;
        gap: var(--sc-list-body-gap, 8px);
        padding: var(--sc-list-body-padding, 4px 0 0 22px);
      }
      .filter {
        font: inherit;
        font-size: 13px;
        height: 32px;
        box-sizing: border-box;
        padding: 0 10px;
        border-radius: 8px;
        border: 1px solid var(--sc-line);
        background: var(--sc-list-filter-bg, var(--sc-surface));
        color: var(--sc-fg);
      }
      .filter:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 1px;
      }
      .rows {
        margin: 0;
        padding: 0;
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: var(--sc-list-rows-gap, 4px);
        font-size: 13px;
      }
      .row {
        padding: var(--sc-list-row-padding, 6px 10px);
        border-radius: 8px;
        background: var(--sc-list-row-bg, var(--sc-bg));
        color: var(--sc-fg);
      }
      .no-match {
        padding: 6px 10px;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .rows--long {
        max-height: var(--sc-list-max-height, 256px);
        overflow-y: auto;
      }
      @media (prefers-reduced-motion: reduce) {
        .chev {
          transition: none;
        }
      }
    `,
  ],
})
export class CollapsibleListComponent<T> implements OnInit {
  /** The key the open or closed choice is remembered under, and the suffix of every data-test hook. */
  readonly key = input.required<string>();
  /** The one line shown on the header, open or closed. */
  readonly summary = input.required<string>();
  readonly items = input.required<readonly T[]>();
  /** The text the filter matches a row against. */
  readonly textOf = input<(item: T) => string>((item) => String(item));
  readonly filterLabel = input<string>('Filter');
  /** Opens the list each time it is drawn; a close lasts while it stays drawn and is never stored. */
  readonly startOpen = input(false);

  readonly row = contentChild.required<TemplateRef<{ $implicit: T }>>(TemplateRef);

  readonly long = LONG_LIST;
  /** The line shown when the filter leaves no row; null shows nothing. */
  readonly noMatch = input<string | null>(null);
  readonly query = signal('');
  private readonly choice = signal<boolean | null>(null);

  readonly open = computed<boolean>(
    () => this.choice() ?? (this.startOpen() || (readOpen()[this.key()] ?? false)),
  );

  readonly shown = computed<readonly T[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (q === '') return this.items();
    const textOf = this.textOf();
    return this.items().filter((item) => textOf(item).toLowerCase().includes(q));
  });

  ngOnInit(): void {
    if (this.startOpen()) forgetOpen(this.key());
  }

  toggle(): void {
    const next = !this.open();
    this.choice.set(next);
    if (!this.startOpen()) writeOpen(this.key(), next);
  }
}
