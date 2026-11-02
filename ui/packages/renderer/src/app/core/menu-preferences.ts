import { Injectable, signal } from '@angular/core';

const SHOW_LOGS_KEY = 'scout:show-logs';

function readShowLogs(): boolean {
  try {
    return globalThis.localStorage?.getItem(SHOW_LOGS_KEY) !== '0';
  } catch {
    return true;
  }
}

/** Which optional entries the sidebar shows, kept in localStorage; nothing here is secret. */
@Injectable({ providedIn: 'root' })
export class MenuPreferences {
  private readonly _showLogs = signal<boolean>(readShowLogs());

  /** True when Logs is in the sidebar and its route opens; on unless the owner turned it off. */
  readonly showLogs = this._showLogs.asReadonly();

  setShowLogs(show: boolean): void {
    this._showLogs.set(show);
    try {
      globalThis.localStorage?.setItem(SHOW_LOGS_KEY, show ? '1' : '0');
    } catch {
      // storage unavailable: the choice lasts for this session only
    }
  }
}
