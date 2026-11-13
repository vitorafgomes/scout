import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

import { ScoutBridgeService } from './scout-bridge';

/** The tone of a notice; it picks the notice's colour. */
export type NoticeKind = 'info' | 'success' | 'error';

/** A button on a notice: its label, and what pressing it does. */
export interface NoticeAction {
  readonly label: string;
  readonly run: () => void;
}

/** Tells the owner that something happened, as a short notice at the bottom of the window; an error is also kept in the bell. */
@Injectable({ providedIn: 'root' })
export class NoticeService {
  private readonly snackBar = inject(MatSnackBar);
  private readonly bridge = inject(ScoutBridgeService);

  /**
   * Shows `message` for `ms` milliseconds, replacing the notice on screen, with an `action` button when given; an
   * error notice is also recorded in the notification center, while an info or success notice the owner just saw is not.
   */
  show(message: string, kind: NoticeKind = 'info', ms = 4000, action?: NoticeAction): void {
    const ref = this.snackBar.open(message, action?.label, {
      duration: ms,
      panelClass: ['sc-notice', `sc-notice--${kind}`],
    });
    if (action !== undefined) ref.onAction().subscribe(() => action.run());
    if (kind === 'error') this.bridge.noticeRecord({ message, kind }).catch(() => undefined);
  }

  /** Closes the notice on screen, if any. */
  dismiss(): void {
    this.snackBar.dismiss();
  }
}
