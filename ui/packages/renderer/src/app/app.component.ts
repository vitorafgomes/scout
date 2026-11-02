import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { ShellComponent } from './shell/shell.component';

@Component({
  selector: 'sc-app',
  standalone: true,
  imports: [ShellComponent, RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (quickAsk) {
      <router-outlet />
    } @else {
      <sc-shell />
    }
  `,
})
export class AppComponent {
  /** The quick-ask and floating button windows load the app on their routes and show them without the wizard shell. */
  readonly quickAsk = ['#/quick-ask', '#/floating-button'].some(
    (route) => globalThis.location?.hash.startsWith(route) ?? false,
  );
}
