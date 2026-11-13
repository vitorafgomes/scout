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
const STEP_KEY = 'scout:wizard-step';
const CLIENT_ID_KEY = 'scout:wizard-client-id';

function readItem(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function writeItem(key: string, value: string | null): void {
  try {
    if (value === null) globalThis.localStorage?.removeItem(key);
    else globalThis.localStorage?.setItem(key, value);
  } catch {
    return;
  }
}

/** The wizard steps a reopened app resumes on, with the page each one lives on. */
const RESUME_URLS: Readonly<Partial<Record<WizardStep, string>>> = Object.freeze({
  provision: '/provision',
  token: '/token',
  oauth: '/oauth',
});

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

  private readonly openedOn = readStep();
  private readonly _currentStep = signal<WizardStep>(this.openedOn);
  private readonly _clientId = signal<string | null>(readItem(CLIENT_ID_KEY));
  private readonly _guild = signal<WizardGuild | null>(null);
  private readonly _hasToken = signal<boolean>(false);
  private readonly _hasServer = signal<boolean>(false);
  private readonly _slackConnected = signal<boolean>(false);
  private readonly _localOnly = signal<boolean>(readItem(LOCAL_ONLY_KEY) === '1');
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

  /** True once Discord is set up: a bot token and a server chosen for it, read from the vault or chosen in this session. */
  readonly hasDiscord = computed<boolean>(
    () =>
      (this._hasToken() || this._clientId() !== null) &&
      (this._hasServer() || this._guild() !== null),
  );

  /**
   * True when Scout can run: a chat platform, Discord or Slack, is set up, or the owner chose
   * to use Scout only from this computer through quick ask.
   */
  readonly hasProvisioning = computed<boolean>(
    () => this.hasDiscord() || this._slackConnected() || this._localOnly(),
  );

  /** True when the vault keeps a bot token but no server for it, so Discord still needs Add to server. */
  readonly discordNeedsServer = computed<boolean>(() => this._hasToken() && !this.hasDiscord());

  /** True when the owner skipped chat setup and has connected neither Discord nor Slack since. */
  readonly usesOnlyThisComputer = computed<boolean>(
    () => this._localOnly() && !this.hasDiscord() && !this._slackConnected(),
  );

  /**
   * The screen the app opens on, answered once per launch and after `loadFromVault`: the Discord setup step the app
   * was closed on while nothing can run yet, else Chat when the owner uses Scout only from this computer, otherwise
   * null to stay where the app is. A Slack or local-only owner keeps that landing whatever step they left.
   */
  takeLandingUrl(): string | null {
    if (this.landed) return null;
    this.landed = true;
    const resume = RESUME_URLS[this.openedOn];
    if (resume !== undefined && !this.hasProvisioning()) return resume;
    return this.usesOnlyThisComputer() ? '/chat' : null;
  }

  /**
   * Prepares Add to server for a bot token kept without a server: the application id of this session, else the one
   * main kept in the vault. Rejects with the vault's error; resolves false when no application id is known.
   */
  async prepareAddToServer(): Promise<boolean> {
    const id =
      this._clientId() ?? (await this.bridge.vaultGetPublic({ key: 'discord.client-id' })).value;
    if (id === null || id.length === 0) return false;
    this.setClientId(id);
    this.goTo('oauth');
    return true;
  }

  /** Remembers that the owner skipped chat setup and uses Scout only from this computer. */
  useLocalOnly(): void {
    this._localOnly.set(true);
    writeItem(LOCAL_ONLY_KEY, '1');
  }

  setSlackConnected(connected: boolean): void {
    this._slackConnected.set(connected);
  }

  /** Moves the wizard to a step and remembers it, so a reopened app resumes the Discord setup there. */
  goTo(step: WizardStep): void {
    this._currentStep.set(step);
    writeItem(STEP_KEY, step);
  }

  /** Records the Discord application the pasted token belongs to and remembers it for a reopened app. */
  setClientId(id: string): void {
    this._clientId.set(id);
    writeItem(CLIENT_ID_KEY, id);
  }

  setGuild(guild: WizardGuild): void {
    this._guild.set(guild);
  }

  /** Renderer-side reset: clears the signals and the remembered step and application id; touches neither the vault nor the worker. */
  async reset(): Promise<void> {
    this.forget();
  }

  /**
   * Destructive counterpart of reset: deletes the bot token first and, only once it is gone, clears the signals and the
   * other provisioning keys (best-effort) and the persisted metadata. A token that cannot be deleted rejects with that
   * error and leaves the store as it was, so Discord never reads Not connected while its token stays. Callers that also
   * want to stop the worker must do so explicitly before calling this method.
   */
  async reprovision(): Promise<void> {
    await this.bridge.vaultDelete({ key: 'discord.bot-token' });

    this.forget();

    const keys = ['discord.client-id', 'discord.guild-id', 'discord.guild-name'] as const;
    for (const key of keys) {
      await this.bridge.vaultDelete({ key }).catch(() => undefined);
    }

    this.persistent.clear();
  }

  /**
   * Rehydrates the store from the vault and the localStorage mirror. Discord counts as set up only when the vault holds
   * the bot token and a chosen server (`discord.guild-id`); the mirror carries the application id and the server name
   * for display, is read only while the vault holds the server, and is wiped once the vault says it holds none.
   */
  async loadFromVault(): Promise<LoadFromVaultResult> {
    const slack = this.readSlackConnected();
    const server = this.readServerChosen();
    let hasToken = false;
    try {
      const res = await this.bridge.vaultHas({ key: 'discord.bot-token' });
      hasToken = res.exists === true;
    } catch {
      hasToken = false;
    }

    const [slackConnected, chosen] = await Promise.all([slack, server]);
    this._slackConnected.set(slackConnected);

    if (!hasToken) {
      return { hasProvisioning: slackConnected || this._localOnly() };
    }

    this._hasToken.set(true);

    if (chosen !== true) {
      this._hasServer.set(false);
      this._guild.set(null);
      if (chosen === false) this.persistent.clear();
      return { hasProvisioning: this.hasProvisioning() };
    }
    this._hasServer.set(true);

    const persisted = this.persistent.get();
    if (persisted !== null) {
      this._clientId.set(persisted.clientId);
      this._guild.set({ id: persisted.guildId, name: persisted.guildName });
    }

    return { hasProvisioning: true };
  }

  private forget(): void {
    this._currentStep.set('welcome');
    this._clientId.set(null);
    this._guild.set(null);
    this._hasToken.set(false);
    this._hasServer.set(false);
    writeItem(STEP_KEY, null);
    writeItem(CLIENT_ID_KEY, null);
  }

  /** Whether the vault holds a chosen Discord server; null when the vault could not answer. */
  private async readServerChosen(): Promise<boolean | null> {
    try {
      return (await this.bridge.vaultHas({ key: 'discord.guild-id' })).exists === true;
    } catch {
      return null;
    }
  }

  private async readSlackConnected(): Promise<boolean> {
    try {
      return (await this.bridge.slackStatus()).connected === true;
    } catch {
      return false;
    }
  }
}

function readStep(): WizardStep {
  const stored = readItem(STEP_KEY);
  return WIZARD_STEP_ORDER.find((step) => step === stored) ?? 'welcome';
}

export const WIZARD_STEPS: readonly WizardStep[] = WIZARD_STEP_ORDER;

/** The name of each step, as the stepper lists it and the "Step N of M" line ends with it. */
export const WIZARD_STEP_LABELS: Readonly<Record<WizardStep, string>> = Object.freeze({
  welcome: 'Chat app',
  provision: 'Create bot',
  token: 'Bot token',
  oauth: 'Add to server',
  launch: 'Launch',
  ready: 'Ready',
});

/** The "Step N of M · Name" line a wizard page shows, counted over the same steps the stepper lists. */
export function wizardStepCounter(step: WizardStep): string {
  return `Step ${WIZARD_STEPS.indexOf(step) + 1} of ${WIZARD_STEPS.length} · ${WIZARD_STEP_LABELS[step]}`;
}
