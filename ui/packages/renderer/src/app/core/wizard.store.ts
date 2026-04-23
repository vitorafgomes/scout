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
  private readonly _intentsAcknowledged = signal<boolean>(false);

  readonly currentStep = this._currentStep.asReadonly();
  readonly clientId = this._clientId.asReadonly();
  readonly guild = this._guild.asReadonly();
  readonly intentsAcknowledged = this._intentsAcknowledged.asReadonly();

  readonly activeStepIndex = computed<number>(() => {
    const step = this._currentStep();
    const index = WIZARD_STEP_ORDER.indexOf(step);
    return index < 0 ? 0 : index;
  });

  readonly hasProvisioning = computed<boolean>(
    () => this._clientId() !== null && this._guild() !== null,
  );

  goTo(step: WizardStep): void {
    this._currentStep.set(step);
  }

  setClientId(id: string): void {
    this._clientId.set(id);
  }

  setGuild(guild: WizardGuild): void {
    this._guild.set(guild);
  }

  markIntentsAcknowledged(): void {
    this._intentsAcknowledged.set(true);
  }

  // Pure renderer-side reset: clears in-memory signals only. Does NOT touch
  // the vault and does NOT stop the worker. Used when navigating away from
  // a wizard step without destroying provisioning state.
  async reset(): Promise<void> {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._intentsAcknowledged.set(false);
  }

  // Destructive counterpart of reset: clears signals AND deletes every
  // provisioning-related vault key. Callers that also want to stop the
  // worker must do so explicitly before calling this method.
  async reprovision(): Promise<void> {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._intentsAcknowledged.set(false);

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
    let hasToken = false;
    try {
      const res = await this.bridge.vaultHas({ key: 'discord.bot-token' });
      hasToken = res.exists === true;
    } catch {
      hasToken = false;
    }

    const persisted = this.persistent.get();
    if (!hasToken || persisted === null) {
      return { hasProvisioning: false };
    }

    this._clientId.set(persisted.clientId);
    this._guild.set({ id: persisted.guildId, name: persisted.guildName });
    return { hasProvisioning: true };
  }
}

export const WIZARD_STEPS: readonly WizardStep[] = WIZARD_STEP_ORDER;
