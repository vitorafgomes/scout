import { Injectable, computed, inject, signal } from '@angular/core';

import { PersistentStateService } from './persistent-state';
import { ScoutBridgeService } from './scout-bridge';

export type WizardStep = 'welcome' | 'provision' | 'token' | 'oauth' | 'launch' | 'ready';

export interface WizardGuild {
  readonly id: string;
  readonly name: string;
}

export interface LoadFromVaultResult {
  readonly hasProvisioning: boolean;
}

const LOCAL_ONLY_KEY = 'scout:local-only';

function readLocalOnly(): boolean {
  try {
    return globalThis.localStorage?.getItem(LOCAL_ONLY_KEY) === '1';
  } catch {
    return false;
  }
}

const WIZARD_STEP_ORDER: readonly WizardStep[] = Object.freeze([
  'welcome',
  'provision',
  'token',
  'oauth',
  'launch',
  'ready',
] as const);

@Injectable({ providedIn: 'root' })
export class WizardStore {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly persistent = inject(PersistentStateService);

  private readonly _currentStep = signal<WizardStep>('welcome');
  private readonly _clientId = signal<string | null>(null);
  private readonly _guild = signal<WizardGuild | null>(null);
  // True once loadFromVault confirms the vault holds discord.bot-token.
  // Allows launch without clientId/guild (token-only provisioning).
  private readonly _hasToken = signal<boolean>(false);
  private readonly _slackConnected = signal<boolean>(false);
  private readonly _localOnly = signal<boolean>(readLocalOnly());
  private landed = false;

  readonly currentStep = this._currentStep.asReadonly();
  readonly clientId = this._clientId.asReadonly();
  readonly guild = this._guild.asReadonly();
  readonly slackConnected = this._slackConnected.asReadonly();

  readonly activeStepIndex = computed<number>(() => {
    const step = this._currentStep();
    const index = WIZARD_STEP_ORDER.indexOf(step);
    return index < 0 ? 0 : index;
  });

  // Token alone is sufficient to launch; clientId/guild are optional metadata.
  readonly hasDiscord = computed<boolean>(
    () => this._hasToken() || (this._clientId() !== null && this._guild() !== null),
  );

  /**
   * True when Scout can run: a chat platform, Discord or Slack, is set up, or the owner chose
   * to use Scout only from this computer through quick ask.
   */
  readonly hasProvisioning = computed<boolean>(
    () => this.hasDiscord() || this._slackConnected() || this._localOnly(),
  );

  /** True when the owner skipped chat setup and has connected neither Discord nor Slack since. */
  readonly usesOnlyThisComputer = computed<boolean>(
    () => this._localOnly() && !this.hasDiscord() && !this._slackConnected(),
  );

  /**
   * The screen the app opens on, answered once per launch and after `loadFromVault`: Chat when
   * the owner uses Scout only from this computer, otherwise null to stay where the app is.
   */
  takeLandingUrl(): string | null {
    if (this.landed) return null;
    this.landed = true;
    return this.usesOnlyThisComputer() ? '/chat' : null;
  }

  /** Remembers that the owner skipped chat setup and uses Scout only from this computer. */
  useLocalOnly(): void {
    this._localOnly.set(true);
    try {
      globalThis.localStorage?.setItem(LOCAL_ONLY_KEY, '1');
    } catch {
      // storage unavailable: the choice lasts for this session only
    }
  }

  setSlackConnected(connected: boolean): void {
    this._slackConnected.set(connected);
  }

  goTo(step: WizardStep): void {
    this._currentStep.set(step);
  }

  setClientId(id: string): void {
    this._clientId.set(id);
  }

  setGuild(guild: WizardGuild): void {
    this._guild.set(guild);
  }

  // Pure renderer-side reset: clears in-memory signals only. Does NOT touch
  // the vault and does NOT stop the worker. Used when navigating away from
  // a wizard step without destroying provisioning state.
  async reset(): Promise<void> {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._hasToken.set(false);
  }

  // Destructive counterpart of reset: clears signals AND deletes every
  // provisioning-related vault key. Callers that also want to stop the
  // worker must do so explicitly before calling this method.
  async reprovision(): Promise<void> {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._hasToken.set(false);

    // Best-effort clear; swallow errors so reprovisioning never throws back
    // to the caller (UI has typically already navigated away).
    const keys = [
      'discord.bot-token',
      'discord.client-id',
      'discord.guild-id',
      'discord.guild-name',
    ] as const;
    for (const key of keys) {
      try {
        await this.bridge.vaultDelete({ key });
      } catch {
        // ignore
      }
    }

    this.persistent.clear();
  }

  // Rehydrates the store from vault + persistent state. The bot token is
  // the only real secret and lives exclusively in the vault: its mere
  // existence gates "is fully provisioned". clientId, guildId and guildName
  // are non-secrets mirrored in localStorage so the renderer can display
  // them and hint the worker without ever reading vault contents.
  async loadFromVault(): Promise<LoadFromVaultResult> {
    const slack = this.readSlackConnected();
    let hasToken = false;
    try {
      const res = await this.bridge.vaultHas({ key: 'discord.bot-token' });
      hasToken = res.exists === true;
    } catch {
      hasToken = false;
    }

    const slackConnected = await slack;
    this._slackConnected.set(slackConnected);

    if (!hasToken) {
      return { hasProvisioning: slackConnected || this._localOnly() };
    }

    this._hasToken.set(true);

    // Restore optional metadata from localStorage when available.
    const persisted = this.persistent.get();
    if (persisted !== null) {
      this._clientId.set(persisted.clientId);
      this._guild.set({ id: persisted.guildId, name: persisted.guildName });
    }

    return { hasProvisioning: true };
  }

  private async readSlackConnected(): Promise<boolean> {
    try {
      return (await this.bridge.slackStatus()).connected === true;
    } catch {
      return false;
    }
  }
}

export const WIZARD_STEPS: readonly WizardStep[] = WIZARD_STEP_ORDER;
