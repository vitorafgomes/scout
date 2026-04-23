import { ChangeDetectionStrategy, Component } from '@angular/core';

import { ShellComponent } from './shell/shell.component';

@Component({
  selector: 'sc-app',
  standalone: true,
  imports: [ShellComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sc-shell />`,
})
export class AppComponent {}
