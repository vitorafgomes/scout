import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { readIpcError, type ThemeChoice, type ThemeState } from '@scout/shared';

import type { IconName } from './icon.component';
import { ScoutBridgeService } from './scout-bridge';

/** One of the three theme choices as the switch shows it: its name, its label for screen readers and its icon. */
export interface ThemeOption {
  readonly choice: ThemeChoice;
  readonly label: string;
  readonly ariaLabel: string;
  readonly icon: IconName;
}

/** Light, Dark and System, in the order the top bar and Settings show them. */
export const THEME_OPTIONS: readonly ThemeOption[] = Object.freeze([
  { choice: 'light', label: 'Light', ariaLabel: 'Light theme', icon: 'sun' },
  { choice: 'dark', label: 'Dark', ariaLabel: 'Dark theme', icon: 'moon' },
  { choice: 'system', label: 'System', ariaLabel: 'Follow the system theme', icon: 'monitor' },
]);

/**
 * The theme this window paints with: main's answer and pushes set `data-theme` on `html` to the resolved scheme; until
 * main answers the stylesheet follows the system preference, which main's choice already drives.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly document = inject(DOCUMENT);
  private readonly _state = signal<ThemeState | null>(null);
  private readonly _error = signal<string | null>(null);
  private started = false;

  /** The owner's choice once main answered; null before. */
  readonly choice = computed<ThemeChoice | null>(() => this._state()?.choice ?? null);
  /** Why the last choice could not be applied; null when it was. */
  readonly error = this._error.asReadonly();

  /** Reads the choice from main and follows its pushes for the life of the window; a second call does nothing. */
  start(): void {
    if (this.started) return;
    this.started = true;
    this.bridge.onTheme((state) => this.apply(state));
    void this.bridge
      .themeGet()
      .then((state) => this.apply(state))
      .catch(() => undefined);
  }

  /** Asks main for a choice and shows main's answer; a refusal keeps the current theme and sets `error`. */
  async set(choice: ThemeChoice): Promise<void> {
    this._error.set(null);
    try {
      this.apply(await this.bridge.themeSet(choice));
    } catch (err: unknown) {
      this._error.set(readIpcError(err).message);
    }
  }

  private apply(state: ThemeState): void {
    this._state.set(state);
    this.document.documentElement.setAttribute('data-theme', state.resolved);
  }
}
