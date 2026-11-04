import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

interface OperatorNavItem {
  readonly path: string;
  readonly label: string;
  readonly testId: string;
}

const NAV_ITEMS: readonly OperatorNavItem[] = Object.freeze([
  { path: '/operator/production', label: 'Production', testId: 'operator-nav-production' },
  { path: '/operator/assembly', label: 'Assembly', testId: 'operator-nav-assembly' },
  { path: '/operator/reports', label: 'Reports', testId: 'operator-nav-reports' },
  { path: '/operator/experiments', label: 'Experiments', testId: 'operator-nav-experiments' },
  { path: '/operator/comments', label: 'Comments', testId: 'operator-nav-comments' },
  { path: '/operator/approvals', label: 'Approvals', testId: 'operator-nav-approvals' },
]);

/** The YouTube operator window: its own left navigation over the operator pages, without the main window's sidebar. */
@Component({
  selector: 'sc-operator-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell">
      <nav class="rail" aria-label="YouTube operator">
        <div class="brand" aria-hidden="true">YouTube operator</div>
        <ul class="nav-list" role="list">
          @for (item of navItems; track item.path) {
            <li role="listitem">
              <a
                class="nav-link"
                [routerLink]="item.path"
                routerLinkActive="nav-link--active"
                [attr.data-test]="item.testId"
                >{{ item.label }}</a
              >
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
        background: #0f1117;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .shell {
        display: grid;
        grid-template-columns: 200px 1fr;
        min-height: 100vh;
      }
      .rail {
        background: #171b26;
        border-right: 1px solid #252b3b;
        padding: 24px 12px;
        display: flex;
        flex-direction: column;
        gap: 24px;
      }
      .brand {
        font-size: 16px;
        font-weight: 700;
        padding: 0 8px;
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
        display: block;
        padding: 8px 12px;
        border-radius: 6px;
        color: #7b8299;
        font-size: 14px;
        text-decoration: none;
      }
      .nav-link:hover {
        background: #1e2333;
        color: #e4e8f0;
      }
      .nav-link--active {
        background: #1e2e45;
        color: #e4e8f0;
      }
      .main {
        min-width: 0;
        min-height: 0;
        overflow-y: auto;
      }
    `,
  ],
})
export class OperatorShellComponent {
  readonly navItems = NAV_ITEMS;
}
