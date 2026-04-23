import { Injectable, computed, inject, signal } from '@angular/core';

import { ScoutBridgeService } from './scout-bridge';

export type WizardStep = 'welcome' | 'provision' | 'token' | 'oauth' | 'launch' | 'ready';

export interface WizardGuild {
  readonly id: string;
  readonly name: string;
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

  async reset(): Promise<void> {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._intentsAcknowledged.set(false);
    // Best-effort clear of vault entries; swallow errors so reset never throws
    // back to the caller (UI already navigated away).
    try {
      await this.bridge.vaultDelete({ key: 'discord.bot-token' });
    } catch {
      // ignore
    }
    try {
      await this.bridge.vaultDelete({ key: 'discord.client-id' });
    } catch {
      // ignore
    }
  }
}

export const WIZARD_STEPS: readonly WizardStep[] = WIZARD_STEP_ORDER;
