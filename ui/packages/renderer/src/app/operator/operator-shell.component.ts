import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';

import { IconComponent } from '../core/icon.component';
import { PendingApprovals } from '../core/pending-approvals.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { TopBarComponent, screenTitle } from '../shell/top-bar.component';

const APPROVALS_POLL_MS = 15_000;

interface OperatorNavItem {
  readonly path: string;
  readonly label: string;
  readonly testId: string;
  readonly glyph: string;
}

const NAV_ITEMS: readonly OperatorNavItem[] = Object.freeze([
  {
    path: '/operator/production',
    label: 'Production',
    testId: 'operator-nav-production',
    glyph: 'M3 6h18v12H3zM10 9l5 3-5 3z',
  },
  {
    path: '/operator/assembly',
    label: 'Assembly',
    testId: 'operator-nav-assembly',
    glyph: 'M4 4h16v6H4zM4 14h7v6H4zM15 14h5v6h-5z',
  },
  {
    path: '/operator/comments',
    label: 'Comments',
    testId: 'operator-nav-comments',
    glyph: 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
  },
  {
    path: '/operator/reports',
    label: 'Reports',
    testId: 'operator-nav-reports',
    glyph: 'M4 20V10M10 20V4M16 20v-7M22 20H2',
  },
  {
    path: '/operator/experiments',
    label: 'Experiments',
    testId: 'operator-nav-experiments',
    glyph: 'M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6a2 2 0 0 0 1.7-3l-5-9V3',
  },
  {
    path: '/operator/approvals',
    label: 'Approvals',
    testId: 'operator-nav-approvals',
    glyph: 'M9 11l3 3 8-8M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h9',
  },
]);

/** The operator pages that paint with the theme tokens; any other route keeps the dark canvas. */
const THEMED_ROUTES: readonly string[] = Object.freeze(NAV_ITEMS.map((item) => item.path));

/** The YouTube operator window: its own left navigation over the operator pages, without the main window's sidebar. */
@Component({
  selector: 'sc-operator-shell',
  standalone: true,
  imports: [IconComponent, RouterLink, RouterLinkActive, RouterOutlet, TopBarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell">
      <nav class="rail" aria-label="YouTube operator">
        <div class="brand" data-test="operator-brand">
          <span class="brand-tile" aria-hidden="true"><sc-icon name="video" [size]="16" /></span>
          <span class="brand-text">
            <span class="brand-name">YouTube operator</span>
            @if (channel(); as name) {
              <span class="brand-channel" data-test="operator-channel">{{ name }}</span>
            }
          </span>
        </div>
        <button type="button" class="back" data-test="operator-back-to-scout" (click)="showScout()">
          <svg
            width="15"
            height="15"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M19 12H5M12 19l-7-7 7-7" />
          </svg>
          Back to Scout
        </button>
        <ul class="nav-list" role="list">
          @for (item of navItems; track item.path) {
            <li role="listitem">
              <a
                class="nav-link"
                [routerLink]="item.path"
                routerLinkActive="nav-link--active"
                ariaCurrentWhenActive="page"
                [attr.data-test]="item.testId"
              >
                <svg
                  class="nav-icon"
                  width="18"
                  height="18"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.8"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  aria-hidden="true"
                >
                  <path [attr.d]="item.glyph" />
                </svg>
                <span>{{ item.label }}</span>
                @if (item.path === '/operator/approvals' && waiting() > 0) {
                  <span
                    class="nav-badge"
                    role="img"
                    data-test="operator-nav-approvals-badge"
                    [attr.aria-label]="waitingLabel()"
                    >{{ waiting() }}</span
                  >
                }
              </a>
            </li>
          }
        </ul>
        @if (granted()) {
          <div class="granted" data-test="operator-granted">
            <svg
              width="12"
              height="12"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2.4"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M20 6L9 17l-5-5" />
            </svg>
            YouTube feature granted
          </div>
        }
      </nav>
      <div class="content">
        <sc-top-bar [title]="title()" />
        <main class="main" [class.sc-dark-canvas]="pinnedDark()" role="main">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        position: fixed;
        inset: 0;
        overflow: hidden;
        background: var(--sc-bg);
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .shell {
        display: grid;
        grid-template-columns: 200px 1fr;
        height: 100%;
      }
      .rail {
        background: var(--sc-surface);
        border-right: 1px solid var(--sc-line);
        padding: 16px 12px;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .brand {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 4px 8px 4px;
      }
      .back {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 6px 10px 14px;
        border: 0;
        background: none;
        font: inherit;
        font-size: 13px;
        color: var(--sc-accent);
        text-decoration: none;
        cursor: pointer;
      }
      .brand-tile {
        width: 28px;
        height: 28px;
        flex: none;
        border-radius: 8px;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .brand-text {
        display: flex;
        flex-direction: column;
        min-width: 0;
      }
      .brand-name {
        font-size: 15px;
        font-weight: 600;
      }
      .brand-channel {
        font-size: 12px;
        color: var(--sc-muted);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .nav-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .nav-link {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 9px 10px;
        border-radius: 8px;
        color: var(--sc-fg-2);
        font-size: 14px;
        font-weight: 500;
        text-decoration: none;
      }
      .nav-icon {
        flex: none;
      }
      .nav-badge {
        margin-left: auto;
        min-width: 20px;
        height: 20px;
        padding: 0 6px;
        box-sizing: border-box;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 12px;
        font-weight: 600;
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
      .granted {
        margin-top: auto;
        padding: 12px 10px 0;
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        color: var(--sc-on-ok-container);
      }
      .nav-link:hover {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .nav-link--active {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .content {
        display: flex;
        flex-direction: column;
        min-width: 0;
        min-height: 0;
      }
      .main {
        flex: 1;
        min-width: 0;
        min-height: 0;
        overflow-y: auto;
        background: var(--sc-bg);
        color: var(--sc-fg);
      }
      .main.sc-dark-canvas {
        background: #0f1117;
        color: #e4e8f0;
      }
    `,
  ],
})
export class OperatorShellComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  private readonly approvals = inject(PendingApprovals);
  private grantReads = 0;

  readonly navItems = NAV_ITEMS;
  /** The signed-in YouTube channel shown under the brand; null while unknown or when the status cannot be read. */
  readonly channel = signal<string | null>(null);
  /** True while the owner has granted the YouTube feature's writes; false while unknown or when the features cannot be read. */
  readonly granted = signal(false);

  /** How many YouTube actions wait for the owner's decision, the ones this window's Approvals page lists; the badge hides at zero. */
  readonly waiting = computed<number>(
    () =>
      this.approvals
        .cards()
        .filter((card) => card.state === 'pending' && card.action.featureId === 'youtube').length,
  );

  /** The badge's accessible name. */
  readonly waitingLabel = computed<string>(() => {
    const count = this.waiting();
    return `${count} ${count === 1 ? 'action' : 'actions'} waiting for your approval`;
  });
  /** The current page's name, from the navigation entry its route belongs to. */
  readonly title = computed<string>(() => screenTitle(NAV_ITEMS, this.url(), 'YouTube operator'));

  /** True while the routed page is not on the theme tokens yet, so its area stays dark in either theme. */
  readonly pinnedDark = computed<boolean>(
    () => !THEMED_ROUTES.includes(this.url().split(/[?#]/)[0] ?? ''),
  );

  constructor() {
    const unsubscribe = this.bridge.onFeaturesChanged(() => void this.readGrant());
    const timer = setInterval(() => void this.approvals.load(), APPROVALS_POLL_MS);
    inject(DestroyRef).onDestroy(() => {
      unsubscribe();
      clearInterval(timer);
    });
    void this.approvals.load();
    void this.readGrant();
  }

  async ngOnInit(): Promise<void> {
    try {
      this.channel.set((await this.bridge.youtubeStatus()).channelTitle);
    } catch {
      this.channel.set(null);
    }
  }

  /** Asks main to bring the Scout window forward; the operator window stays open, and a call main does not answer changes nothing here. */
  async showScout(): Promise<void> {
    await this.bridge.appShowMainWindow().catch(() => undefined);
  }

  /** Reads whether the YouTube feature's writes are granted; only the latest read is shown. */
  async readGrant(): Promise<void> {
    const read = ++this.grantReads;
    let granted = false;
    try {
      granted =
        (await this.bridge.featuresList()).features.find((f) => f.id === 'youtube')?.granted ===
        true;
    } catch {
      granted = false;
    }
    if (read === this.grantReads) this.granted.set(granted);
  }
}
