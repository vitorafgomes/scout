import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  TemplateRef,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';

import { IconComponent, type IconName } from '../core/icon.component';
import { MenuPreferences } from '../core/menu-preferences';
import { PendingApprovals } from '../core/pending-approvals.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { TopBarComponent, screenTitle } from './top-bar.component';
import { UpdateNoticeComponent } from './update-notice.component';

const APPROVALS_POLL_MS = 15_000;

interface NavItem {
  readonly path: string;
  readonly label: string;
  readonly testId: string;
  readonly icon: IconName;
}

const NAV_ITEMS: readonly NavItem[] = Object.freeze([
  { path: '/chat', label: 'Chat', testId: 'nav-chat', icon: 'chat' },
  { path: '/sources', label: 'AI Sources', testId: 'nav-sources', icon: 'sparkle' },
  { path: '/features', label: 'Features', testId: 'nav-features', icon: 'grid' },
  { path: '/llm', label: 'Models', testId: 'nav-models', icon: 'cube' },
  { path: '/meetings', label: 'Meetings', testId: 'nav-meetings', icon: 'calendar' },
  { path: '/review', label: 'Approvals', testId: 'nav-review', icon: 'check-square' },
  { path: '/logs', label: 'Logs', testId: 'nav-logs', icon: 'list' },
  { path: '/settings', label: 'Settings', testId: 'nav-settings', icon: 'settings' },
] as const);

/** The routes whose screens paint with the theme tokens; every other page keeps the dark canvas until it migrates. */
const THEMED_ROUTES: readonly string[] = Object.freeze([
  '/chat',
  '/sources',
  '/sources/local-models',
  '/llm',
  '/settings',
  '/',
  '/provision',
  '/token',
  '/oauth',
  '/launch',
  '/ready',
  '/features',
  '/review',
  '/meetings',
  '/logs',
]);

@Component({
  selector: 'sc-post-connect-shell',
  standalone: true,
  imports: [
    IconComponent,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    TopBarComponent,
    UpdateNoticeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:focus)': 'readYouTube()' },
  template: `
    <div class="shell">
      <nav class="rail" aria-label="Primary">
        <div class="brand" aria-hidden="true">
          <span class="brand-mark">S</span>
          <span class="brand-name">Scout</span>
        </div>
        <ul class="nav-list" role="list">
          @for (item of navItems(); track item.path) {
            <li class="nav-item" role="listitem">
              <a
                class="nav-link"
                [routerLink]="item.path"
                routerLinkActive="nav-link--active"
                ariaCurrentWhenActive="page"
                [attr.data-test]="item.testId"
              >
                <sc-icon class="nav-icon" [name]="item.icon" />
                <span class="nav-label">{{ item.label }}</span>
                @if (item.path === '/review' && waiting() > 0) {
                  <span
                    class="nav-badge"
                    role="img"
                    data-test="nav-review-badge"
                    [attr.aria-label]="waitingLabel()"
                    >{{ waiting() }}</span
                  >
                }
              </a>
            </li>
          }
          @if (youtubeConnected()) {
            <li class="nav-item" role="listitem">
              <button
                type="button"
                class="nav-link"
                data-test="nav-youtube-operator"
                (click)="openOperator()"
              >
                <sc-icon class="nav-icon" name="video" />
                <span class="nav-label">YouTube operator</span>
              </button>
            </li>
          }
        </ul>
      </nav>
      <div class="content">
        <sc-top-bar [title]="title()" [actions]="pageActions()" />
        <sc-update-notice />
        <main class="main" [class.sc-dark-canvas]="pinnedDark()" role="main">
          <router-outlet (activate)="routed.set($event)" (deactivate)="routed.set(null)" />
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
        padding: 4px 8px 16px;
        color: var(--sc-fg);
      }
      .brand-mark {
        width: 28px;
        height: 28px;
        border-radius: 8px;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 14px;
        font-weight: 700;
      }
      .brand-name {
        font-size: 15px;
        font-weight: 600;
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
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
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
      button.nav-link {
        width: 100%;
        border: 0;
        background: transparent;
        text-align: left;
        cursor: pointer;
      }
      .nav-link:hover {
        background: var(--sc-surface-2);
        color: var(--sc-fg);
      }
      .nav-link--active {
        background: var(--sc-nav-active);
        color: var(--sc-fg);
        font-weight: 600;
        box-shadow: inset 3px 0 0 var(--sc-accent);
      }
      .content {
        display: flex;
        flex-direction: column;
        min-width: 0;
        min-height: 0;
      }
      .main {
        flex: 1;
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
export class PostConnectShellComponent {
  private readonly menu = inject(MenuPreferences);
  private readonly router = inject(Router);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly approvals = inject(PendingApprovals);
  private reads = 0;

  /** How many actions wait for the owner's decision, for the badge on Approvals; the badge hides at zero. */
  readonly waiting = this.approvals.waiting;

  /** The badge's accessible name. */
  readonly waitingLabel = computed<string>(() => {
    const count = this.waiting();
    return `${count} ${count === 1 ? 'action' : 'actions'} waiting for your approval`;
  });

  /** True while main reports YouTube connected; a status main cannot read counts as not connected. */
  readonly youtubeConnected = signal(false);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /** The routed screen's component, while one is shown. */
  readonly routed = signal<unknown>(null);

  /** The routed screen's own top-bar controls, when it declares a `topBarActions` template. */
  readonly pageActions = computed<TemplateRef<unknown> | null>(() => {
    const actions = (this.routed() as { topBarActions?: unknown } | null)?.topBarActions;
    return typeof actions === 'function' ? (actions as () => TemplateRef<unknown>)() : null;
  });

  /** The current screen's name, from the navigation entry its route belongs to. */
  readonly title = computed<string>(() => screenTitle(NAV_ITEMS, this.url(), 'Scout'));

  /** True while the routed screen is not on the theme tokens yet, so its area stays dark in either theme. */
  readonly pinnedDark = computed<boolean>(
    () => !THEMED_ROUTES.includes(this.url().split(/[?#]/)[0] ?? ''),
  );

  readonly navItems = computed<readonly NavItem[]>(() =>
    this.menu.showLogs() ? NAV_ITEMS : NAV_ITEMS.filter((item) => item.path !== '/logs'),
  );

  constructor() {
    const unsubscribe = this.bridge.onFeaturesChanged(() => void this.readYouTube());
    const timer = setInterval(() => void this.approvals.load(), APPROVALS_POLL_MS);
    inject(DestroyRef).onDestroy(() => {
      unsubscribe();
      clearInterval(timer);
    });
    void this.approvals.load();
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => void this.readYouTube());
    void this.readYouTube();
  }

  /** Reads the YouTube connection again; only the latest read is shown. */
  async readYouTube(): Promise<void> {
    const read = ++this.reads;
    let connected = false;
    try {
      connected = (await this.bridge.youtubeStatus()).connected;
    } catch {
      connected = false;
    }
    if (read === this.reads) this.youtubeConnected.set(connected);
  }

  /** Opens the YouTube operator window, the same window the Features screen opens. */
  openOperator(): void {
    this.bridge.operatorOpenWindow().catch(() => undefined);
  }
}
