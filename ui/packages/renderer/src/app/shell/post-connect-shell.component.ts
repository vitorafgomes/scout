import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

interface NavItem {
  readonly path: string;
  readonly label: string;
  readonly testId: string;
  readonly iconPath: string;
}

const NAV_ITEMS: readonly NavItem[] = Object.freeze([
  {
    path: '/sources',
    label: 'AI Sources',
    testId: 'nav-sources',
    iconPath:
      'M12 2 4 6v6c0 4.5 3.4 8.5 8 10 4.6-1.5 8-5.5 8-10V6Zm0 4.2 5 2.5v3.3c0 3.2-2.2 6-5 7.2-2.8-1.2-5-4-5-7.2V8.7Z',
  },
  {
    path: '/llm',
    label: 'Models',
    testId: 'nav-models',
    iconPath:
      'M12 3a4 4 0 0 0-4 4v1H6a3 3 0 0 0-3 3v3a3 3 0 0 0 3 3h12a3 3 0 0 0 3-3v-3a3 3 0 0 0-3-3h-2V7a4 4 0 0 0-4-4Zm-2 5V7a2 2 0 1 1 4 0v1Z',
  },
  {
    path: '/features',
    label: 'Features',
    testId: 'nav-features',
    iconPath: 'M4 4h6v6H4Zm10 0h6v6h-6Zm-10 10h6v6H4Zm10 0h6v6h-6Z',
  },
  {
    path: '/status',
    label: 'Status',
    testId: 'nav-status',
    iconPath:
      'M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm0 18a8 8 0 1 1 8-8 8 8 0 0 1-8 8Zm-1-13h2v6h-2Zm0 8h2v2h-2Z',
  },
  {
    path: '/logs',
    label: 'Logs',
    testId: 'nav-logs',
    iconPath: 'M4 4h16v2H4Zm0 5h16v2H4Zm0 5h16v2H4Zm0 5h10v2H4Z',
  },
  {
    path: '/settings',
    label: 'Settings',
    testId: 'nav-settings',
    iconPath:
      'M12 8a4 4 0 1 0 4 4 4 4 0 0 0-4-4Zm9.4 4-.7-1.3 1-2.3-1.4-1.4-2.3 1-1.3-.7L16 5h-2l-.6 2.3-1.4.7-2.3-1L8.3 8.4l1 2.3-.7 1.3L6.3 13l1.4 1.4 2.3-1 1.3.7.6 2.3h2l.7-2.3 1.4-.7 2.3 1 1.4-1.4-1-2.3.7-1.3Z',
  },
] as const);

@Component({
  selector: 'sc-post-connect-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell">
      <nav class="rail" aria-label="Primary">
        <div class="brand" aria-hidden="true">Scout</div>
        <ul class="nav-list" role="list">
          @for (item of navItems; track item.path) {
            <li class="nav-item" role="listitem">
              <a
                class="nav-link"
                [routerLink]="item.path"
                routerLinkActive="nav-link--active"
                [attr.data-test]="item.testId"
              >
                <svg
                  class="nav-icon"
                  viewBox="0 0 24 24"
                  width="16"
                  height="16"
                  aria-hidden="true"
                  focusable="false"
                >
                  <path [attr.d]="item.iconPath" fill="currentColor" />
                </svg>
                <span class="nav-label">{{ item.label }}</span>
              </a>
            </li>
          }
        </ul>
      </nav>
      <main class="main" role="main">
        <router-outlet />
      </main>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
        background: var(--sc-bg);
        color: var(--sc-text);
      }
      .shell {
        display: grid;
        grid-template-columns: 240px 1fr;
        min-height: 100vh;
      }
      .rail {
        background: var(--sc-surface);
        border-right: 1px solid var(--sc-border);
        padding: 24px 12px;
        display: flex;
        flex-direction: column;
        gap: 24px;
      }
      .brand {
        font-size: 20px;
        font-weight: 700;
        color: var(--sc-text);
        padding: 0 8px;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
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
        padding: 8px 12px;
        border-radius: 6px;
        color: var(--sc-text-muted);
        font-size: 14px;
        text-decoration: none;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .nav-link:hover {
        background: var(--sc-surface-3);
        color: var(--sc-text);
      }
      .nav-link--active {
        background: var(--sc-surface-3);
        color: var(--sc-text);
      }
      .nav-icon {
        flex-shrink: 0;
      }
      .main {
        overflow-y: auto;
      }
    `,
  ],
})
export class PostConnectShellComponent {
  readonly navItems: readonly NavItem[] = NAV_ITEMS;
}
