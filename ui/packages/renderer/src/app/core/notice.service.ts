import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

/** The tone of a notice; it picks the notice's colour. */
export type NoticeKind = 'info' | 'success' | 'error';

/** Tells the owner that something happened, as a short notice at the bottom of the window. */
@Injectable({ providedIn: 'root' })
export class NoticeService {
  private readonly snackBar = inject(MatSnackBar);

  /** Shows `message` for `ms` milliseconds, replacing the notice on screen. */
  show(message: string, kind: NoticeKind = 'info', ms = 4000): void {
    this.snackBar.open(message, undefined, {
      duration: ms,
      panelClass: ['sc-notice', `sc-notice--${kind}`],
    });
  }

  /** Closes the notice on screen, if any. */
  dismiss(): void {
    this.snackBar.dismiss();
  }
}
