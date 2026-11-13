import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import {
  type AdminKeyProvider,
  type ChatGptStatus,
  type OpenRouterKeyState,
  type OrgSpendState,
  type ProviderKeyValidateResponse,
  type SourceUsage,
  type SourceUsageTotals,
  type UsageSnapshot,
  RESTART_DEFERRED_LINE,
} from '@scout/shared';

import { confirmAction } from '../core/confirm-dialog.component';

import {
  type ActiveCloudProvider,
  type CloudLlmProvider,
  DEFAULT_CLOUD_MODELS,
  KeyStoredNotSwitchedError,
  LlmConfigStore,
  providerLabel,
} from '../core/llm-config.store';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';
import { type ChipTone, StatusChipComponent } from '../core/status-chip.component';
import {
  OllamaModelsDialogComponent,
  type OllamaModelsDialogData,
} from './ollama-models-dialog.component';

type FilterKind = 'all' | 'local' | 'cloud';
type CardKind = 'local' | 'cloud';
type ConnectionState = 'connected' | 'not-connected' | 'unknown';
type CloudState = 'in-use' | 'connected' | 'not-connected';
type CloudForm = 'key';

interface RestartMessages {
  readonly restarting: string;
  readonly idle: string;
  readonly failed: string;
}

const GUARD_RESTART_MESSAGES: RestartMessages = {
  restarting: 'Saved. Restarting Scout to use the read-only guard.',
  idle: 'Saved. Scout will use the read-only guard the next time it starts.',
  failed: 'Saved. Restart Scout from the status screen to use the read-only guard.',
};

const GUARD_DISCONNECT_MESSAGES: RestartMessages = {
  restarting: 'Disconnected. Restarting Scout to turn the read-only guard off.',
  idle: 'Disconnected. The read-only guard is off.',
  failed: 'Disconnected. Restart Scout from the status screen to turn the read-only guard off.',
};

interface CardModel {
  readonly id: string;
  readonly kind: CardKind;
  readonly title: string;
  readonly description: string;
  readonly help?: string;
  readonly icon: CardIcon;
  readonly primaryAction: string;
  readonly secondaryLabel: string | null;
  readonly secondaryUrl: string | null;
  readonly disabled: boolean;
}

const DEFAULT_OLLAMA_ENDPOINT = 'http://localhost:11434';
const GUARD_CARD_ID = 'typesafe-jev';
const DEFAULT_LM_STUDIO_ENDPOINT = 'http://localhost:1234';
const CHATGPT_SIGNED_OUT: ChatGptStatus = Object.freeze({
  signedIn: false,
  email: null,
  planUsageGranted: false,
});

/**
 * A card's icon: the provider's official logo as a local file per theme (`asset`; the same file twice where the owner
 * publishes one variant for both, since the page may not load remote images), or a generic mark drawn as a path in
 * the text colour, filled (`glyph`) or as a muted outline (`stroke`), for a card that is not a provider's.
 */
export type CardIcon =
  | { readonly kind: 'glyph'; readonly path: string }
  | { readonly kind: 'stroke'; readonly path: string }
  | { readonly kind: 'asset'; readonly dark: string; readonly light: string };

interface UsageRow {
  readonly label: string;
  readonly value: string;
  readonly test: string;
  readonly warn?: boolean;
}

interface UsageNote {
  readonly text: string;
  readonly test: string;
  readonly warn?: boolean;
}

interface UsageView {
  readonly rows: readonly UsageRow[];
  readonly notes: readonly UsageNote[];
  readonly page: string | null;
}

/** The stroke glyphs of the screen's buttons, links and notes, each a path on a 24 by 24 grid. */
const GLYPHS = Object.freeze({
  plug: 'M9 2v6M15 2v6M7 8h10v4a5 5 0 0 1-10 0zM12 17v5',
  unplug: 'M9 2v6M15 2v6M7 8h10v4a5 5 0 0 1-7.5 4.3M12 17v5M3 3l18 18',
  key: 'M14 7a4 4 0 1 1-3.5 6L4 19.5V21h3v-2h2v-2h2l1.4-1.4',
  server: 'M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01',
  busy: 'M12 3a9 9 0 1 0 9 9',
  search: 'M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM20 20l-3.5-3.5',
  external: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  arrowOut: 'M7 17L17 7M8 7h9v9',
  cube: 'M12 2l9 5v10l-9 5-9-5V7zM12 12l9-5M12 12v10M12 12L3 7',
  signOut: 'M15 4h4v16h-4M10 17l5-5-5-5M15 12H3',
  save: 'M5 3h11l3 3v15H5zM8 3v5h7V3M8 21v-7h8v7',
  info: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 11v5M12 8v.5',
  check: 'M20 6L9 17l-5-5',
});

/** How the Disconnect confirmation names what stops: the provider's short name; a card absent here, OpenRouter, has its own sentence. */
const DISCONNECT_NAMES: Readonly<Record<string, string>> = Object.freeze({
  'openai-api': 'OpenAI',
  'google-gemini': 'Google Gemini',
  'claude-api': 'Claude',
});

const GiB = 1024 * 1024 * 1024;

/** Which worker provider each card counts, and the provider's own usage page where it reports no balance to Scout. */
const USAGE_SOURCES: Readonly<
  Record<string, { readonly source: string; readonly page: string | null }>
> = Object.freeze({
  ollama: { source: 'Ollama', page: null },
  'chatgpt-plus': { source: 'ChatGptPlan', page: 'https://chatgpt.com/#settings' },
  'openai-api': { source: 'OpenAI', page: 'https://platform.openai.com/usage' },
  'google-gemini': { source: 'Gemini', page: 'https://aistudio.google.com/usage' },
  'claude-api': { source: 'Anthropic', page: 'https://platform.claude.com/usage' },
  openrouter: { source: 'OpenRouter', page: null },
});

const NOT_REPORTED = 'Not reported by the provider';
const TOKENS = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 });

function money(amount: number): string {
  return amount > 0 && amount < 0.01 ? 'under US$ 0.01' : `US$ ${amount.toFixed(2)}`;
}

function clockTime(iso: string): string {
  const at = new Date(iso);
  return `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
}

function scoutLine(totals: SourceUsageTotals | undefined): string {
  const tokens = `${TOKENS.format(totals?.tokens ?? 0)} tokens`;
  return totals?.cost === null || totals?.cost === undefined
    ? tokens
    : `${tokens} · ${money(totals.cost)}`;
}

function openRouterRows(state: OpenRouterKeyState | null): UsageRow[] {
  const info = state?.info ?? null;
  if (info === null) {
    return [
      {
        label: 'Credits',
        value: state?.error ? 'Could not be read' : 'Not read yet',
        test: 'usage-balance-openrouter',
      },
    ];
  }
  const rows: UsageRow[] =
    info.limit === null
      ? [{ label: 'Credits', value: 'No limit on this key', test: 'usage-balance-openrouter' }]
      : [
          {
            label: 'Credits',
            value:
              info.limitRemaining === null
                ? `${NOT_REPORTED} (limit ${money(info.limit)})`
                : `${money(info.limitRemaining)} of ${money(info.limit)} left`,
            test: 'usage-balance-openrouter',
          },
          {
            label: 'Resets',
            value:
              info.limitReset === null
                ? 'Never'
                : info.limitReset.charAt(0).toUpperCase() + info.limitReset.slice(1),
            test: 'usage-reset-openrouter',
          },
        ];
  if (info.usageMonthly !== null) {
    rows.push({
      label: 'OpenRouter this month',
      value: money(info.usageMonthly),
      test: 'usage-provider-month-openrouter',
    });
  }
  return rows;
}

/** The cards that take an optional Admin key, and the provider it reads the organization's spend from. */
const ADMIN_KEY_PROVIDERS: Readonly<Record<string, AdminKeyProvider>> = Object.freeze({
  'openai-api': 'OpenAI',
  'claude-api': 'Anthropic',
});

const ORG_SPEND_HELP =
  "Optional. An Admin key can read your organization's billing; Scout uses it only to show this month's spend and keeps it on this computer.";

function orgSpendLine(spend: OrgSpendState): string {
  if (spend.error === 'invalid-key') return 'The Admin key was rejected';
  if (spend.amount === null) return spend.error === null ? 'Not read yet' : 'Could not be read';
  const code = spend.currency?.toUpperCase() ?? 'USD';
  const prefix = code === 'USD' ? 'US$' : code;
  return `${prefix} ${spend.amount.toFixed(2)} spent this month (organization)`;
}

export const OPENROUTER_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/openrouter-glyph-cloud.svg',
  light: 'assets/brands/openrouter-glyph-ink.svg',
});

export const OPENAI_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/openai-blossom-white.svg',
  light: 'assets/brands/openai-blossom-black.svg',
});

export const GEMINI_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/gemini-spark.svg',
  light: 'assets/brands/gemini-spark.svg',
});

export const OLLAMA_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/ollama-white.svg',
  light: 'assets/brands/ollama-black.svg',
});

const LM_STUDIO_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/lm-studio-icon-color.svg',
  light: 'assets/brands/lm-studio-icon-color.svg',
});

/** The Claude spark from Anthropic's press kit, one variant for both themes, for the Claude API card. */
export const CLAUDE_ICON: CardIcon = Object.freeze({
  kind: 'asset',
  dark: 'assets/brands/claude-spark-clay.svg',
  light: 'assets/brands/claude-spark-clay.svg',
});

const CLOUD_CARD_TITLES: Readonly<Record<string, string>> = Object.freeze({
  'openai-api': 'OpenAI API',
  'google-gemini': 'Google Gemini',
  'claude-api': 'Claude API',
  openrouter: 'OpenRouter',
});

const CLOUD_PROVIDERS: Readonly<Record<string, { readonly provider: CloudLlmProvider }>> =
  Object.freeze({
    'openai-api': { provider: 'OpenAI' },
    'google-gemini': { provider: 'Gemini' },
    'claude-api': { provider: 'Anthropic' },
    openrouter: { provider: 'OpenRouter' },
  });

/** Generic marks for the cards that are not a provider's. */
const ICONS = Object.freeze({
  customApi: 'M8 8l-4 4 4 4M16 8l4 4-4 4',
  shield: 'M12 2 4 5v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V5Z',
});

@Component({
  selector: 'sc-sources',
  standalone: true,
  imports: [MatButtonModule, NgTemplateOutlet, RouterLink, StatusChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <div class="header-left">
          <h1>AI Sources</h1>
          <p class="lede">
            Connect a model on this computer or a cloud provider. Scout answers only with the
            sources you connect.
          </p>
        </div>
        <div class="filter" role="group" aria-label="Show sources">
          <button
            type="button"
            class="filter-chip"
            [attr.aria-pressed]="filter() === 'all'"
            data-test="filter-all"
            (click)="setFilter('all')"
          >
            All
          </button>
          <button
            type="button"
            class="filter-chip"
            [attr.aria-pressed]="filter() === 'local'"
            data-test="filter-local"
            (click)="setFilter('local')"
          >
            On this computer
          </button>
          <button
            type="button"
            class="filter-chip"
            [attr.aria-pressed]="filter() === 'cloud'"
            data-test="filter-cloud"
            (click)="setFilter('cloud')"
          >
            Cloud
          </button>
        </div>
      </header>

      @if (showLocal()) {
        <section class="section" aria-labelledby="sec-local" data-test="section-local">
          <h2 id="sec-local" class="section-title">On this computer</h2>
          <div class="grid">
            @for (card of localCards; track card.id) {
              <article
                class="card"
                [class.card--in-use]="localInUse(card.id)"
                [attr.aria-labelledby]="'c-' + card.id"
                [attr.data-test]="'card-' + card.id"
              >
                <div class="card-head">
                  <ng-container
                    [ngTemplateOutlet]="iconBlock"
                    [ngTemplateOutletContext]="{
                      $implicit: card.icon,
                      name: card.title,
                      id: card.id,
                    }"
                  />
                  <h3 class="card-title" [id]="'c-' + card.id">{{ card.title }}</h3>
                  <sc-status-chip
                    [attr.data-test]="'badge-' + card.id"
                    [tone]="
                      stateOf(card.id) === 'connected'
                        ? 'ok'
                        : stateOf(card.id) === 'unknown' && !localInUse(card.id)
                          ? 'info'
                          : 'neutral'
                    "
                    [label]="
                      localInUse(card.id)
                        ? 'In use'
                        : stateOf(card.id) === 'connected'
                          ? card.id === 'lm-studio'
                            ? 'Detected'
                            : 'Connected'
                          : stateOf(card.id) === 'unknown'
                            ? 'Checking...'
                            : 'Not connected'
                    "
                  />
                </div>
                <p class="card-desc">{{ card.description }}</p>
                <ng-container
                  [ngTemplateOutlet]="usageBlock"
                  [ngTemplateOutletContext]="{ $implicit: card.id }"
                />
                <div class="card-actions">
                  <div class="row">
                    @if (card.id === 'ollama' && stateOf('ollama') === 'connected') {
                      <button
                        type="button"
                        matButton="filled"
                        class="btn"
                        data-test="manage-ollama-models"
                        aria-haspopup="dialog"
                        (click)="openOllamaModels()"
                      >
                        <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                          <path [attr.d]="glyphs.server" /></svg
                        >Manage local models
                      </button>
                    }
                    @if (
                      card.id === 'ollama' &&
                      stateOf('ollama') === 'connected' &&
                      !localInUse('ollama')
                    ) {
                      <button
                        type="button"
                        matButton="outlined"
                        class="btn"
                        data-test="use-ollama"
                        aria-label="Use Ollama"
                        [disabled]="saving()"
                        (click)="useOllama()"
                      >
                        <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                          <path [attr.d]="glyphs.check" /></svg
                        >Use
                      </button>
                    }
                    <button
                      type="button"
                      [matButton]="scanLook(card.id)"
                      class="btn"
                      [class.btn--busy]="scanning().has(card.id)"
                      [attr.aria-busy]="scanning().has(card.id) ? 'true' : null"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="card.disabled || scanning().has(card.id)"
                      (click)="onScan(card.id)"
                    >
                      <svg
                        class="glyph"
                        [class.glyph--busy]="scanning().has(card.id)"
                        viewBox="0 0 24 24"
                        aria-hidden="true"
                      >
                        <path [attr.d]="scanGlyph(card.id)" /></svg
                      >{{ scanLabel(card.id) }}
                    </button>
                  </div>
                  @if (
                    card.id === 'ollama' &&
                    stateOf('ollama') === 'not-connected' &&
                    !scanning().has('ollama')
                  ) {
                    <p class="server-note" data-test="ollama-not-answering">
                      Ollama is not answering. Open the Ollama app or run ollama serve, then scan
                      again.
                    </p>
                  }
                  @if (card.id === 'ollama' && ollamaFreed() === 'freed') {
                    <p class="server-note" role="status" data-test="ollama-freed">
                      Ollama freed its models from memory. It keeps running for other programs.
                    </p>
                  } @else if (card.id === 'ollama' && ollamaFreed() === 'could-not-ask') {
                    <p class="server-note" role="status" data-test="ollama-not-freed">
                      Scout could not ask Ollama to free its memory. Ollama frees it by itself after
                      a few minutes without use.
                    </p>
                  }
                  @if (card.id === 'lm-studio' && stateOf('lm-studio') === 'connected') {
                    <p class="server-note" data-test="lm-studio-note">
                      Scout cannot answer with LM Studio yet.
                    </p>
                  }
                  @if (card.id === 'ollama' && savedCard() === 'ollama') {
                    <p class="saved" role="status" data-test="saved-ollama">
                      {{ savedMessage() }}
                    </p>
                    @if (actionError(); as error) {
                      <p class="form-error" role="alert">{{ error }}</p>
                    }
                  }
                  @if (card.secondaryLabel && card.secondaryUrl) {
                    <button
                      type="button"
                      class="link"
                      [attr.data-test]="'secondary-' + card.id"
                      (click)="openExternal(card.secondaryUrl)"
                    >
                      {{ card.secondaryLabel }}
                      <svg class="glyph glyph--link" viewBox="0 0 24 24" aria-hidden="true">
                        <path [attr.d]="glyphs.arrowOut" />
                      </svg>
                    </button>
                  }
                </div>
              </article>
            }
          </div>
        </section>
      }
      @if (showCloud()) {
        <section class="section" aria-labelledby="sec-cloud" data-test="section-cloud">
          <h2 id="sec-cloud" class="section-title">Cloud</h2>
          <div class="grid">
            <article
              class="card"
              [class.card--in-use]="chatgptInUse()"
              aria-labelledby="c-chatgpt-plus"
              data-test="card-chatgpt-plus"
            >
              <div class="card-head">
                <ng-container
                  [ngTemplateOutlet]="iconBlock"
                  [ngTemplateOutletContext]="{
                    $implicit: chatgptIcon,
                    name: chatgptName,
                    id: 'chatgpt-plus',
                  }"
                />
                <h3 class="card-title" id="c-chatgpt-plus">{{ chatgptName }}</h3>
                <sc-status-chip
                  data-test="badge-chatgpt-plus"
                  [tone]="!chatgptLoaded() ? 'info' : chatgpt().signedIn ? 'ok' : 'neutral'"
                  [label]="
                    !chatgptLoaded()
                      ? 'Checking...'
                      : chatgptInUse()
                        ? 'In use'
                        : chatgpt().signedIn
                          ? 'Connected'
                          : 'Not connected'
                  "
                />
              </div>
              <p class="card-desc">
                Sign in with your ChatGPT Plus or Pro plan. Usage counts against your plan's limits.
              </p>
              @if (!chatgptLoaded()) {
                <div
                  class="skeleton"
                  role="status"
                  aria-label="Checking your ChatGPT sign-in"
                  data-test="chatgpt-checking"
                >
                  <span class="skeleton-bar skeleton-bar--wide"></span>
                  <span class="skeleton-bar"></span>
                  <span class="skeleton-text">Checking your ChatGPT sign-in...</span>
                </div>
              } @else {
                @if (chatgpt().signedIn) {
                  <p class="account" data-test="chatgpt-account">
                    {{ chatgpt().email ?? 'Signed in' }}
                    @if (chatgptInUse()) {
                      <span data-test="chatgpt-model"
                        >· answering with <strong>{{ chatgptModel() }}</strong></span
                      >
                    }
                  </p>
                  @if (chatgptInUse() && noAllowedUsers()) {
                    <p class="form-error" data-test="access-warning-chatgpt">
                      Nobody can use this connection from chat until you add allowed users in
                      Settings.
                    </p>
                  }
                }
                <ng-container
                  [ngTemplateOutlet]="usageBlock"
                  [ngTemplateOutletContext]="{ $implicit: 'chatgpt-plus' }"
                />
                <div class="card-actions">
                  @if (chatgpt().signedIn) {
                    @if (!chatgpt().planUsageGranted) {
                      <p class="form-error" role="alert" data-test="chatgpt-no-plan-usage">
                        OpenAI did not grant plan usage to Scout for this account, so Scout cannot
                        answer through it.
                      </p>
                    }
                    <div class="row row--center">
                      @if (chatgpt().planUsageGranted) {
                        <a
                          matButton="outlined"
                          class="btn"
                          routerLink="/llm"
                          [queryParams]="{ tab: 'analysis' }"
                          data-test="choose-model-chatgpt-plus"
                          ><svg class="glyph glyph--thin" viewBox="0 0 24 24" aria-hidden="true">
                            <path [attr.d]="glyphs.cube" /></svg
                          >Choose a model</a
                        >
                      }
                      <button
                        type="button"
                        matButton
                        class="btn btn--text"
                        data-test="chatgpt-sign-out"
                        [disabled]="saving()"
                        (click)="signOutChatGpt()"
                      >
                        <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                          <path [attr.d]="glyphs.signOut" /></svg
                        >Sign out
                      </button>
                    </div>
                  } @else {
                    <div class="row">
                      <button
                        type="button"
                        matButton="filled"
                        class="btn"
                        data-test="chatgpt-sign-in"
                        [disabled]="chatgptBusy()"
                        (click)="signInChatGpt()"
                      >
                        <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                          <path [attr.d]="glyphs.plug" /></svg
                        >{{
                          chatgptBusy()
                            ? 'Waiting for the browser...'
                            : chatgptNeedsSignInAgain()
                              ? 'Sign in again'
                              : 'Continue with ChatGPT'
                        }}
                      </button>
                      @if (chatgptBusy()) {
                        <button
                          type="button"
                          matButton="outlined"
                          class="btn"
                          data-test="chatgpt-cancel"
                          (click)="cancelChatGptSignIn()"
                        >
                          Cancel
                        </button>
                      }
                    </div>
                  }
                  @if (chatgptError(); as error) {
                    <p class="form-error" role="alert" data-test="chatgpt-error">{{ error }}</p>
                  }
                  @if (savedCard() === 'chatgpt-plus') {
                    <p class="saved" role="status" data-test="saved-chatgpt-plus">
                      {{ savedMessage() }}
                    </p>
                    @if (actionError(); as error) {
                      <p class="form-error" role="alert">{{ error }}</p>
                    }
                  }
                </div>
              }
            </article>
            @for (card of cloudCards; track card.id) {
              <article
                class="card"
                [class.card--in-use]="cloudStateOf(card.id) === 'in-use'"
                [class.card--soon]="card.disabled"
                [attr.aria-labelledby]="'c-' + card.id"
                [attr.data-test]="'card-' + card.id"
              >
                <div class="card-head">
                  <ng-container
                    [ngTemplateOutlet]="iconBlock"
                    [ngTemplateOutletContext]="{
                      $implicit: card.icon,
                      name: card.title,
                      id: card.id,
                    }"
                  />
                  <h3 class="card-title" [id]="'c-' + card.id">{{ card.title }}</h3>
                  <sc-status-chip
                    [attr.data-test]="'badge-' + card.id"
                    [tone]="cloudToneOf(card)"
                    [label]="card.disabled ? 'Coming soon' : cloudBadgeOf(card.id)"
                  />
                </div>
                <p class="card-desc">{{ card.description }}</p>
                @if (card.help) {
                  <p class="card-desc" [attr.data-test]="'help-' + card.id">{{ card.help }}</p>
                }
                @if (isKeyed(card.id) && cloudStateOf(card.id) === 'in-use') {
                  <p class="account" [attr.data-test]="'in-use-model-' + card.id">
                    Answering with <strong>{{ activeModel() }}</strong>
                  </p>
                }
                <ng-container
                  [ngTemplateOutlet]="usageBlock"
                  [ngTemplateOutletContext]="{ $implicit: card.id }"
                />
                @if (adminProviderOf(card.id); as adminProvider) {
                  <details class="org-spend" [attr.data-test]="'org-spend-' + card.id">
                    <summary>Organization spend</summary>
                    <p class="org-help" [attr.data-test]="'org-spend-help-' + card.id">
                      {{ orgSpendHelp }}
                    </p>
                    @if (orgSpend()[adminProvider]; as spend) {
                      <p
                        class="org-spend-value"
                        [class.org-spend-value--error]="spend.error === 'invalid-key'"
                        [attr.data-test]="'org-spend-value-' + card.id"
                      >
                        {{ orgSpendLine(spend) }}
                      </p>
                      @if (spend.error !== null && spend.readAt !== null) {
                        <p class="usage-note" [attr.data-test]="'org-spend-as-of-' + card.id">
                          Spend as of {{ clockTime(spend.readAt) }}; the latest read failed.
                        </p>
                      }
                      <button
                        type="button"
                        matButton="outlined"
                        class="btn btn--small btn--danger"
                        [attr.data-test]="'admin-key-remove-' + card.id"
                        [disabled]="adminBusy()"
                        (click)="confirmRemoveAdminKey(card.id)"
                      >
                        Remove
                      </button>
                      @if (adminErrorFor(adminProvider); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                      }
                    } @else if (adminForm() === adminProvider) {
                      <form
                        class="key-form"
                        [attr.data-test]="'admin-key-form-' + card.id"
                        (submit)="saveAdminKey($event, adminProvider, adminKeyInput)"
                      >
                        <label class="field-label" [for]="'a-' + card.id">Admin key</label>
                        <input
                          #adminKeyInput
                          [id]="'a-' + card.id"
                          type="password"
                          autocomplete="off"
                          class="text-input"
                          [attr.data-test]="'admin-key-' + card.id"
                        />
                        @if (adminErrorFor(adminProvider); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="row">
                          <button
                            type="submit"
                            matButton="filled"
                            class="btn btn--small"
                            [disabled]="adminBusy()"
                            [attr.data-test]="'admin-key-save-' + card.id"
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            matButton="outlined"
                            class="btn btn--small"
                            (click)="adminForm.set(null)"
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    } @else {
                      <button
                        type="button"
                        matButton="outlined"
                        class="btn btn--small"
                        [attr.data-test]="'admin-key-add-' + card.id"
                        (click)="openAdminForm(adminProvider)"
                      >
                        Add Admin key
                      </button>
                    }
                  </details>
                }
                <div class="card-actions">
                  @if (isKeyed(card.id)) {
                    @if (cloudStateOf(card.id) !== 'not-connected') {
                      <div class="row">
                        <button
                          type="button"
                          matButton="outlined"
                          class="btn"
                          [attr.data-test]="'change-key-' + card.id"
                          [attr.aria-label]="'Change ' + card.title + ' key'"
                          [attr.aria-expanded]="formOf(card.id) === 'key'"
                          [disabled]="saving()"
                          (click)="toggleForm(card.id, 'key')"
                        >
                          <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                            <path [attr.d]="glyphs.key" /></svg
                          >Change key
                        </button>
                        <button
                          type="button"
                          matButton="outlined"
                          class="btn btn--danger"
                          [attr.data-test]="'disconnect-' + card.id"
                          [attr.aria-label]="'Disconnect ' + card.title"
                          [disabled]="saving()"
                          (click)="confirmDisconnect(card.id)"
                        >
                          <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                            <path [attr.d]="glyphs.unplug" /></svg
                          >Disconnect
                        </button>
                      </div>
                    } @else if (!cloudChecking(card.id) && formOf(card.id) !== 'key') {
                      <div class="row">
                        <button
                          type="button"
                          matButton="filled"
                          class="btn"
                          [attr.data-test]="'action-' + card.id"
                          [attr.aria-label]="'Connect ' + card.title"
                          [attr.aria-expanded]="formOf(card.id) === 'key'"
                          (click)="toggleForm(card.id, 'key')"
                        >
                          <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                            <path [attr.d]="glyphs.plug" /></svg
                          >{{ card.primaryAction }}
                        </button>
                      </div>
                    }
                    @if (readFailed() && cloudStateOf(card.id) === 'not-connected') {
                      <p
                        class="form-error"
                        role="alert"
                        [attr.data-test]="'read-failed-' + card.id"
                      >
                        Could not check the {{ card.title }} key; it may still be stored.
                      </p>
                    }
                    @if (cloudStateOf(card.id) !== 'not-connected') {
                      <a
                        class="link"
                        routerLink="/llm"
                        [queryParams]="{ tab: 'analysis' }"
                        [attr.data-test]="'choose-model-' + card.id"
                        >Choose a model in Models, Analysis</a
                      >
                    }
                    @if (formOf(card.id) === 'key') {
                      <form
                        class="key-form"
                        [attr.data-test]="'key-form-' + card.id"
                        (submit)="saveKey($event, card.id, keyInput)"
                      >
                        <label class="field-label" [for]="'k-' + card.id">API key</label>
                        <input
                          #keyInput
                          [id]="'k-' + card.id"
                          type="password"
                          autocomplete="off"
                          class="text-input"
                          [attr.aria-describedby]="'k-' + card.id + '-help'"
                          [attr.data-test]="'api-key-' + card.id"
                        />
                        <span class="field-help" [id]="'k-' + card.id + '-help'"
                          >Stored on this computer only.</span
                        >
                        @if (formError(); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="row form-buttons">
                          <button
                            type="submit"
                            matButton="filled"
                            class="btn"
                            [disabled]="saving()"
                            [attr.data-test]="'save-' + card.id"
                          >
                            <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                              <path [attr.d]="glyphs.save" /></svg
                            >Save
                          </button>
                          <button
                            type="button"
                            matButton="outlined"
                            class="btn"
                            [attr.data-test]="'cancel-' + card.id"
                            (click)="cancelForm()"
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    }
                    @if (savedCard() === card.id) {
                      <p class="saved" role="status" [attr.data-test]="'saved-' + card.id">
                        {{ savedMessage() }}
                      </p>
                      @if (actionError(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                      }
                    }
                  } @else if (card.id === guardCardId) {
                    @if (!cloudChecking(card.id)) {
                      <div class="row">
                        @if (formOf(card.id) !== 'key') {
                          <button
                            type="button"
                            matButton="filled"
                            class="btn"
                            [attr.data-test]="'action-' + card.id"
                            [attr.aria-expanded]="formOf(card.id) === 'key'"
                            (click)="toggleForm(card.id, 'key')"
                          >
                            <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                              <path [attr.d]="glyphs.key" /></svg
                            >{{ card.primaryAction }}
                          </button>
                        }
                        @if (cloudStateOf(card.id) === 'connected' || guardCheckFailed()) {
                          <button
                            type="button"
                            matButton="outlined"
                            class="btn btn--danger"
                            data-test="disconnect-typesafe-jev"
                            aria-label="Disconnect TypeSafe (Jev)"
                            [disabled]="saving()"
                            (click)="confirmDisconnectGuard()"
                          >
                            <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                              <path [attr.d]="glyphs.unplug" /></svg
                            >Disconnect
                          </button>
                        }
                      </div>
                    }
                    @if (guardNotice(); as notice) {
                      <p class="saved" role="status" data-test="disconnected-typesafe-jev">
                        {{ notice }}
                      </p>
                      @if (actionError(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                      }
                    }
                    @if (guardError(); as error) {
                      <p class="form-error" role="alert" data-test="guard-error">{{ error }}</p>
                    }
                    @if (formOf(card.id) === 'key') {
                      <form
                        class="key-form"
                        [attr.data-test]="'key-form-' + card.id"
                        (submit)="saveGuardKey($event, guardKeyInput)"
                      >
                        <label class="field-label" [for]="'k-' + card.id">API key</label>
                        <input
                          #guardKeyInput
                          [id]="'k-' + card.id"
                          type="password"
                          autocomplete="off"
                          class="text-input"
                          [attr.aria-describedby]="'k-' + card.id + '-help'"
                          [attr.data-test]="'api-key-' + card.id"
                        />
                        <span class="field-help" [id]="'k-' + card.id + '-help'"
                          >Stored on this computer only.</span
                        >
                        @if (formError(); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="row form-buttons">
                          <button
                            type="submit"
                            matButton="filled"
                            class="btn"
                            [disabled]="saving()"
                            [attr.data-test]="'save-' + card.id"
                          >
                            <svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">
                              <path [attr.d]="glyphs.save" /></svg
                            >Save
                          </button>
                          <button
                            type="button"
                            matButton="outlined"
                            class="btn"
                            [attr.data-test]="'cancel-' + card.id"
                            (click)="cancelForm()"
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    }
                    @if (savedCard() === card.id) {
                      <p class="saved" role="status" [attr.data-test]="'saved-' + card.id">
                        {{ savedMessage() }}
                      </p>
                      @if (actionError(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                      }
                    }
                  } @else {
                    <p class="soon-note" [attr.data-test]="'action-' + card.id">
                      Not available yet. It arrives in a later version.
                    </p>
                  }
                </div>
              </article>
            }
          </div>
        </section>
      }
    </main>
    <ng-template #iconBlock let-icon let-name="name" let-id="id">
      @switch (icon.kind) {
        @case ('asset') {
          <span
            class="icon"
            role="img"
            [attr.aria-label]="name + ' logo'"
            [attr.data-test]="'icon-' + id"
          >
            <img class="mark mark--dark" [src]="icon.dark" alt="" width="22" height="22" />
            <img class="mark mark--light" [src]="icon.light" alt="" width="22" height="22" />
          </span>
        }
        @case ('stroke') {
          <span
            class="icon icon--muted"
            role="img"
            [attr.aria-label]="name + ' logo'"
            [attr.data-test]="'icon-' + id"
          >
            <svg class="glyph glyph--thin glyph--tile" viewBox="0 0 24 24" aria-hidden="true">
              <path [attr.d]="icon.path" />
            </svg>
          </span>
        }
        @default {
          <span
            class="icon"
            role="img"
            [attr.aria-label]="name + ' logo'"
            [attr.data-test]="'icon-' + id"
          >
            <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
              <path [attr.d]="icon.path" fill="currentColor" />
            </svg>
          </span>
        }
      }
    </ng-template>
    <ng-template #usageBlock let-id>
      @if (usageView(id); as view) {
        <div class="usage" role="group" aria-label="Usage" [attr.data-test]="'usage-' + id">
          @for (row of view.rows; track row.test) {
            <div class="usage-row" [attr.data-test]="row.test">
              <span class="usage-label">{{ row.label }}</span>
              <span class="usage-value" [class.usage-value--warn]="row.warn === true">{{
                row.value
              }}</span>
            </div>
          }
          @for (note of view.notes; track note.test) {
            <p
              class="usage-note"
              [class.usage-note--warn]="note.warn === true"
              [attr.data-test]="note.test"
            >
              <svg class="glyph glyph--note" viewBox="0 0 24 24" aria-hidden="true">
                <path [attr.d]="glyphs.info" /></svg
              >{{ note.text }}
            </p>
          }
          <p class="usage-note" [attr.data-test]="'usage-own-' + id">
            Scout's own count, from the usage each answer reports.
          </p>
          @if (view.page; as page) {
            <button
              type="button"
              matButton="outlined"
              class="btn btn--small usage-page"
              [attr.data-test]="'usage-page-' + id"
              (click)="openExternal(page)"
            >
              <svg class="glyph glyph--page" viewBox="0 0 24 24" aria-hidden="true">
                <path [attr.d]="glyphs.external" /></svg
              >Open the provider's usage page
            </button>
          }
        </div>
      }
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: block;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .page {
        padding: 24px 32px 40px;
        max-width: 1200px;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 16px;
        font-size: 14px;
      }
      .header {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 12px;
      }
      .header-left {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
        min-width: 0;
      }
      h1 {
        margin: 0;
        font-size: 20px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 13px;
        color: var(--sc-muted);
      }
      .filter {
        display: flex;
        gap: 2px;
        padding: 3px;
        border: 1px solid var(--sc-line);
        border-radius: 20px;
        background: var(--sc-bg);
      }
      .filter-chip {
        height: 30px;
        padding: 0 14px;
        border: 0;
        border-radius: 16px;
        background: transparent;
        color: var(--sc-muted);
        font: inherit;
        font-weight: 500;
        cursor: pointer;
      }
      .filter-chip[aria-pressed='true'] {
        background: var(--sc-pressed);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .section {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .section-title {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
        color: var(--sc-muted);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .grid {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
      }
      .card {
        flex: 1 1 300px;
        min-width: 0;
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .card--in-use {
        border: 2px solid var(--sc-accent);
        padding: 15px;
      }
      .card--soon {
        border-style: dashed;
      }
      .card-head {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .icon {
        width: 36px;
        height: 36px;
        flex: none;
        border-radius: 10px;
        background: var(--sc-surface-2);
        display: flex;
        align-items: center;
        justify-content: center;
        color: var(--sc-fg);
      }
      .icon--muted {
        color: var(--sc-muted);
      }
      .mark {
        object-fit: contain;
      }
      .mark--light {
        display: none;
      }
      :host-context([data-theme='light']) .mark--dark {
        display: none;
      }
      :host-context([data-theme='light']) .mark--light {
        display: inline;
      }
      .card-title {
        margin: 0;
        margin-right: auto;
        font-size: 15px;
        font-weight: 600;
      }
      .card-desc {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
      .card-actions {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .row--center {
        align-items: center;
      }
      .btn {
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-outlined-disabled-label-text-color: var(--sc-muted);
        --mat-button-outlined-disabled-outline-color: var(--sc-line);
        --mat-button-text-label-text-color: var(--sc-accent);
        align-self: flex-start;
      }
      .btn--danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      .btn--small {
        --mat-button-outlined-container-height: 32px;
        --mat-button-outlined-horizontal-padding: 12px;
        --mat-button-outlined-label-text-size: 13px;
        --mat-button-filled-container-height: 32px;
        --mat-button-filled-horizontal-padding: 12px;
        --mat-button-filled-label-text-size: 13px;
      }
      .glyph {
        width: 17px;
        height: 17px;
        flex: none;
        margin-right: 8px;
        vertical-align: middle;
        fill: none;
        stroke: currentColor;
        stroke-width: 2;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
      .glyph--thin {
        stroke-width: 1.8;
      }
      .glyph--busy {
        stroke-width: 2.4;
      }
      .glyph--tile {
        width: 20px;
        height: 20px;
        margin: 0;
      }
      .glyph--link {
        width: 14px;
        height: 14px;
        margin: 0;
      }
      .glyph--note {
        width: 14px;
        height: 14px;
        margin: 0;
        stroke-width: 2.2;
      }
      .glyph--page {
        width: 15px;
        height: 15px;
        margin-right: 6px;
      }
      .link {
        align-self: flex-start;
        display: inline-flex;
        align-items: center;
        gap: 4px;
        padding: 0;
        border: 0;
        background: transparent;
        color: var(--sc-accent);
        font: inherit;
        font-size: 13px;
        font-weight: 500;
        text-decoration: none;
        cursor: pointer;
      }
      .server-note {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .account {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
      }
      .account strong {
        color: var(--sc-fg);
        font-weight: 600;
      }
      .skeleton {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .skeleton-bar {
        height: 12px;
        width: 50%;
        border-radius: 6px;
        background: var(--sc-surface-2);
      }
      .skeleton-bar--wide {
        width: 70%;
      }
      .skeleton-text {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .usage {
        display: flex;
        flex-direction: column;
        gap: 6px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
      }
      .usage-row {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        font-size: 13px;
      }
      .usage-label {
        color: var(--sc-muted);
        margin-right: auto;
      }
      .usage-value {
        font-weight: 600;
        color: var(--sc-fg);
      }
      .usage-value--warn {
        color: var(--sc-on-warn-container);
      }
      .usage-note {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .usage-note--warn {
        color: var(--sc-on-warn-container);
      }
      .org-spend {
        border-top: 1px solid var(--sc-line);
        padding-top: 8px;
        font-size: 13px;
      }
      .org-spend summary {
        cursor: pointer;
        font-weight: 600;
      }
      .org-help {
        margin: 6px 0;
        font-size: 12px;
        line-height: 1.5;
        color: var(--sc-muted);
      }
      .org-spend-value {
        margin: 0 0 6px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .org-spend-value--error {
        color: var(--sc-error);
      }
      .key-form {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .field-label {
        font-size: 13px;
        font-weight: 500;
      }
      .text-input {
        height: 40px;
        padding: 0 12px;
        box-sizing: border-box;
        border-radius: 10px;
        border: 1px solid var(--sc-line);
        background: var(--sc-bg);
        color: var(--sc-fg);
        font: inherit;
      }
      .field-help {
        font-size: 12px;
        color: var(--sc-muted);
      }
      .form-buttons {
        padding-top: 4px;
      }
      .form-error {
        margin: 0;
        font-size: 12px;
        color: var(--sc-error);
      }
      .saved {
        margin: 0;
        font-size: 12px;
        color: var(--sc-on-ok-container);
      }
      .soon-note {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
    `,
  ],
})
export class SourcesComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly llmConfig = inject(LlmConfigStore);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notice = inject(NoticeService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly glyphs = GLYPHS;

  readonly filter = signal<FilterKind>('all');
  readonly openForm = signal<{ readonly id: string; readonly kind: CloudForm } | null>(null);
  readonly formError = signal<string | null>(null);
  readonly saving = signal<boolean>(false);
  private idle: Promise<void> = Promise.resolve();
  private settle: () => void = () => undefined;
  readonly savedCard = signal<string | null>(null);
  readonly savedMessage = signal<string>('');
  readonly actionError = signal<string | null>(null);
  readonly activeModel = this.llmConfig.model;
  readonly ollamaFreed = this.llmConfig.ollamaFreed;
  readonly scanning = signal<ReadonlySet<string>>(new Set());
  readonly chatgpt = signal<ChatGptStatus>(CHATGPT_SIGNED_OUT);
  readonly chatgptLoaded = signal<boolean>(false);
  readonly guardLoaded = signal<boolean>(false);
  readonly cloudLoaded = signal<boolean>(false);
  readonly usage = signal<UsageSnapshot>({});
  readonly openRouterKey = signal<OpenRouterKeyState | null>(null);
  /** This month's organization spend per Admin key provider; null while no Admin key is stored. */
  readonly orgSpend = signal<Partial<Record<AdminKeyProvider, OrgSpendState | null>>>({});
  readonly adminForm = signal<AdminKeyProvider | null>(null);
  private readonly adminError = signal<{
    readonly provider: AdminKeyProvider;
    readonly message: string;
  } | null>(null);
  readonly adminBusy = signal<boolean>(false);
  readonly orgSpendHelp = ORG_SPEND_HELP;
  readonly orgSpendLine = orgSpendLine;
  readonly clockTime = clockTime;
  readonly chatgptBusy = signal<boolean>(false);
  private chatgptCancelled = false;
  readonly chatgptError = signal<string | null>(null);
  readonly noAllowedUsers = signal(false);
  readonly chatgptIcon: CardIcon = OPENAI_ICON;
  readonly chatgptName = providerLabel('ChatGptPlan');
  readonly ollamaModelsOpen = signal<boolean>(false);
  /** The models the last Ollama scan listed; null until a scan answers. */
  private readonly ollamaInstalled = signal<readonly { readonly sizeBytes: number }[] | null>(null);
  readonly chatgptModel = computed<string | null>(() => this.llmConfig.model());
  readonly chatgptInUse = computed<boolean>(
    () =>
      this.chatgpt().signedIn &&
      this.chatgpt().planUsageGranted &&
      this.llmConfig.provider() === 'ChatGptPlan',
  );
  readonly chatgptNeedsSignInAgain = computed<boolean>(
    () => !this.chatgpt().signedIn && this.llmConfig.provider() === 'ChatGptPlan',
  );
  readonly guardCardId = GUARD_CARD_ID;
  /** True when the last vault read could not tell which cloud keys are stored. */
  readonly readFailed = this.llmConfig.readFailed;
  private readonly guardConnected = signal<boolean>(false);
  readonly guardNotice = signal<string | null>(null);
  readonly guardError = signal<string | null>(null);
  readonly guardCheckFailed = signal<boolean>(false);
  private readonly states = signal<Readonly<Record<string, ConnectionState>>>({
    ollama: 'unknown',
    'lm-studio': 'unknown',
  });

  readonly showLocal = computed<boolean>(() => this.filter() !== 'cloud');
  readonly showCloud = computed<boolean>(() => this.filter() !== 'local');

  readonly localCards: readonly CardModel[] = Object.freeze([
    {
      id: 'ollama',
      kind: 'local',
      title: 'Ollama',
      description:
        'Runs open models on this computer. Private, and offline once a model is downloaded.',
      icon: OLLAMA_ICON,
      primaryAction: 'Connect',
      secondaryLabel: 'Download Ollama',
      secondaryUrl: 'https://ollama.com/download',
      disabled: false,
    },
    {
      id: 'lm-studio',
      kind: 'local',
      title: 'LM Studio',
      description:
        "Runs local models through LM Studio's server. Start the server in LM Studio, then scan.",
      icon: LM_STUDIO_ICON,
      primaryAction: 'Scan for LM Studio',
      secondaryLabel: 'Download LM Studio',
      secondaryUrl: 'https://lmstudio.ai',
      disabled: false,
    },
  ]);

  readonly cloudCards: readonly CardModel[] = Object.freeze([
    {
      id: 'openai-api',
      kind: 'cloud',
      title: 'OpenAI API',
      description: 'Use your OpenAI developer API key. Billing goes to your OpenAI account.',
      icon: OPENAI_ICON,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: false,
    },
    {
      id: 'google-gemini',
      kind: 'cloud',
      title: 'Google Gemini',
      description:
        "Google's Gemini models, such as Gemini 2.5 Pro and Flash, with your Gemini API key.",
      icon: GEMINI_ICON,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: false,
    },
    {
      id: 'claude-api',
      kind: 'cloud',
      title: 'Claude API',
      description: "Anthropic's Claude models, with your own Anthropic API key.",
      help: 'A Claude Pro or Max subscription cannot be used by other apps; Scout needs an Anthropic API key.',
      icon: CLAUDE_ICON,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: false,
    },
    {
      id: GUARD_CARD_ID,
      kind: 'cloud',
      title: 'TypeSafe (Jev)',
      description:
        'Read-only guard: checks each request with the recent conversation and refuses ones that ask Scout to change something.',
      icon: { kind: 'glyph', path: ICONS.shield },
      primaryAction: 'Add API key',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: false,
    },
    {
      id: 'openrouter',
      kind: 'cloud',
      title: 'OpenRouter',
      description:
        'A unified interface to access hundreds of AI models through a single API connection.',
      icon: OPENROUTER_ICON,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: false,
    },
    {
      id: 'custom-api',
      kind: 'cloud',
      title: 'Custom API',
      description: 'Any OpenAI-compatible endpoint, such as a model server you host yourself.',
      icon: { kind: 'stroke', path: ICONS.customApi },
      primaryAction: 'Configure Endpoint',
      secondaryLabel: null,
      secondaryUrl: null,
      disabled: true,
    },
  ]);

  ngOnInit(): void {
    void this.onScan('ollama');
    void this.onScan('lm-studio');
    const cloudLoaded = (): void => this.cloudLoaded.set(true);
    void this.llmConfig.loadFromVault().then(cloudLoaded, cloudLoaded);
    void this.loadGuardState();
    this.bridge
      .chatgptStatus()
      .then(
        (status) => this.applyChatGptStatus(status),
        () => this.applyChatGptStatus(CHATGPT_SIGNED_OUT),
      )
      .finally(() => this.chatgptLoaded.set(true));
    this.bridge.usageGet().then(
      (usage) => this.usage.set(usage),
      () => undefined,
    );
    this.destroyRef.onDestroy(this.bridge.onUsageChanged((usage) => this.usage.set(usage)));
    this.refreshOpenRouterKey();
    for (const provider of ['OpenAI', 'Anthropic'] as const) void this.refreshOrgSpend(provider);
    this.bridge.accessGet().then(
      (res) => this.noAllowedUsers.set(res.userIds.length === 0),
      () => this.noAllowedUsers.set(false),
    );
  }

  private refreshOpenRouterKey(): void {
    this.bridge.usageOpenRouterKey().then(
      (state) => this.openRouterKey.set(state),
      () => this.openRouterKey.set(null),
    );
  }

  private refreshOrgSpend(provider: AdminKeyProvider): Promise<void> {
    const set = (spend: OrgSpendState | null): void =>
      this.orgSpend.update((all) => ({ ...all, [provider]: spend }));
    return this.bridge
      .usageOrgSpend({ provider })
      .then(set, () => set({ amount: null, currency: null, readAt: null, error: 'unexpected' }));
  }

  /** The Admin key provider of a card that offers one while it is connected or still holds an Admin key; null otherwise. */
  adminProviderOf(id: string): AdminKeyProvider | null {
    const provider = ADMIN_KEY_PROVIDERS[id];
    if (provider === undefined) return null;
    const stored = (this.orgSpend()[provider] ?? null) !== null;
    return this.cloudStateOf(id) !== 'not-connected' || stored ? provider : null;
  }

  adminErrorFor(provider: AdminKeyProvider): string | null {
    const error = this.adminError();
    return error?.provider === provider ? error.message : null;
  }

  openAdminForm(provider: AdminKeyProvider): void {
    this.adminError.set(null);
    this.adminForm.set(provider);
  }

  async saveAdminKey(
    event: Event,
    provider: AdminKeyProvider,
    keyInput: HTMLInputElement,
  ): Promise<void> {
    event.preventDefault();
    if (this.adminBusy()) return;
    const adminKey = keyInput.value.trim();
    if (adminKey.length === 0) {
      this.adminError.set({ provider, message: 'Enter an Admin key.' });
      return;
    }
    this.adminBusy.set(true);
    this.adminError.set(null);
    try {
      const result = await this.bridge.usageAdminKeySave({ provider, adminKey });
      if (!result.ok) {
        if (result.reason === 'invalid-key') keyInput.value = '';
        this.adminError.set({
          provider,
          message:
            result.reason === 'invalid-key'
              ? 'The Admin key was rejected'
              : result.reason === 'unreachable'
                ? `${provider} could not be reached; the Admin key was not saved.`
                : `${provider} gave an unexpected answer; the Admin key was not saved.`,
        });
        return;
      }
      keyInput.value = '';
      this.adminForm.set(null);
      await this.refreshOrgSpend(provider);
    } catch (err: unknown) {
      this.adminError.set({
        provider,
        message: err instanceof Error ? err.message : 'Could not save the Admin key.',
      });
    } finally {
      this.adminBusy.set(false);
    }
  }

  async confirmRemoveAdminKey(id: string): Promise<void> {
    const provider = ADMIN_KEY_PROVIDERS[id];
    const card = this.cloudCards.find((c) => c.id === id);
    if (provider === undefined || card === undefined || this.adminBusy()) return;
    const confirmed = await confirmAction(this.dialog, {
      title: 'Remove the Admin key?',
      body: `Scout deletes the ${card.title} Admin key stored on this computer and stops showing the organization spend.`,
      confirmLabel: 'Remove',
    });
    if (!confirmed) return;
    this.adminBusy.set(true);
    try {
      await this.bridge.usageAdminKeyRemove({ provider });
      this.orgSpend.update((all) => ({ ...all, [provider]: null }));
    } catch (err: unknown) {
      this.adminError.set({
        provider,
        message: err instanceof Error ? err.message : 'Could not remove the Admin key.',
      });
    } finally {
      this.adminBusy.set(false);
    }
  }

  private applyChatGptStatus(status: ChatGptStatus): void {
    this.chatgpt.set(status);
    this.llmConfig.setChatGptConnected(status.signedIn && status.planUsageGranted);
  }

  async signInChatGpt(): Promise<void> {
    if (this.chatgptBusy()) return;
    this.chatgptBusy.set(true);
    this.chatgptCancelled = false;
    this.chatgptError.set(null);
    this.savedCard.set(null);
    let status: ChatGptStatus;
    try {
      status = await this.bridge.chatgptSignIn();
    } catch (err: unknown) {
      if (!this.chatgptCancelled) {
        this.chatgptError.set(
          err instanceof Error ? err.message : 'Could not sign in with ChatGPT.',
        );
      }
      return;
    } finally {
      this.chatgptBusy.set(false);
    }
    this.applyChatGptStatus(status);
    while (this.saving()) await this.idle;
    if (
      status.signedIn &&
      status.planUsageGranted &&
      this.llmConfig.activeCloudProvider() === null
    ) {
      await this.applyModel('chatgpt-plus', 'ChatGptPlan', DEFAULT_CLOUD_MODELS.ChatGptPlan);
    }
  }

  async cancelChatGptSignIn(): Promise<void> {
    this.chatgptCancelled = true;
    await this.bridge.chatgptCancelSignIn().catch(() => undefined);
  }

  async signOutChatGpt(): Promise<void> {
    if (this.saving()) return;
    await this.exclusive(() => this.signOutAndMoveOff());
  }

  private async signOutAndMoveOff(): Promise<void> {
    this.chatgptError.set(null);
    this.actionError.set(null);
    this.openForm.set(null);
    let signedOut: string;
    try {
      const { revocationConfirmed } = await this.bridge.chatgptSignOut();
      this.chatgpt.set(CHATGPT_SIGNED_OUT);
      signedOut = revocationConfirmed
        ? 'Signed out of ChatGPT.'
        : 'Signed out on this computer, but OpenAI did not confirm the remote sign-out.';
      this.savedMessage.set(signedOut);
      this.savedCard.set('chatgpt-plus');
    } catch (err: unknown) {
      this.chatgptError.set(err instanceof Error ? err.message : 'Could not sign out of ChatGPT.');
      return;
    }
    let wasInUse: boolean;
    try {
      wasInUse = await this.llmConfig.disconnectChatGpt();
    } catch (err: unknown) {
      this.actionError.set(err instanceof Error ? err.message : 'Could not change the provider.');
      return;
    }
    if (!wasInUse) return;
    const next = this.llmConfig.activeCloudProvider();
    const settled =
      next === null
        ? `${signedOut} No AI provider is connected; Scout will say so until you connect one.`
        : `${signedOut} Scout now answers with ${providerLabel(next)}.`;
    this.savedMessage.set(settled);
    await this.restartIfRunning({
      restarting: settled,
      idle: settled,
      failed: `${settled} Restart Scout from the status screen to apply it.`,
    });
  }

  setFilter(kind: FilterKind): void {
    this.filter.set(kind);
  }

  stateOf(id: string): ConnectionState {
    return this.states()[id] ?? 'unknown';
  }

  isKeyed(id: string): boolean {
    return CLOUD_PROVIDERS[id] !== undefined;
  }

  cloudStateOf(id: string): CloudState {
    if (id === GUARD_CARD_ID) {
      return this.guardConnected() ? 'connected' : 'not-connected';
    }
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined) return 'not-connected';
    if (provider === this.llmConfig.activeCloudProvider()) return 'in-use';
    return this.llmConfig.connectedProviders().has(provider) ? 'connected' : 'not-connected';
  }

  cloudChecking(id: string): boolean {
    if (this.cloudStateOf(id) !== 'not-connected') return false;
    return id === GUARD_CARD_ID ? !this.guardLoaded() : this.isKeyed(id) && !this.cloudLoaded();
  }

  /** The chip tone of a cloud card: Coming soon on the info pair, a connected card on the ok pair. */
  cloudToneOf(card: CardModel): ChipTone {
    if (card.disabled || this.cloudChecking(card.id)) return 'info';
    return this.cloudStateOf(card.id) !== 'not-connected' ? 'ok' : 'neutral';
  }

  /** A scan button is the card's filled Connect or Scan until its source answers, outlined while it scans or after. */
  scanLook(id: string): 'filled' | 'outlined' {
    return this.scanning().has(id) || (id === 'ollama' && this.stateOf(id) === 'connected')
      ? 'outlined'
      : 'filled';
  }

  scanLabel(id: string): string {
    const name = id === 'ollama' ? 'Ollama' : 'LM Studio';
    if (this.scanning().has(id)) return `Scanning for ${name}...`;
    if (id !== 'ollama') return 'Scan for LM Studio';
    return this.stateOf(id) === 'connected' ? 'Scan again' : 'Connect';
  }

  scanGlyph(id: string): string {
    if (this.scanning().has(id)) return GLYPHS.busy;
    return id === 'ollama' && this.stateOf(id) !== 'connected' ? GLYPHS.plug : GLYPHS.search;
  }

  /** Opens the Ollama models in a dialog; Use this there makes Ollama the provider in use. */
  openOllamaModels(): void {
    this.ollamaModelsOpen.set(true);
    const ref = this.dialog.open<OllamaModelsDialogComponent, OllamaModelsDialogData>(
      OllamaModelsDialogComponent,
      {
        data: { chosen: () => void this.useOllama() },
        panelClass: 'sc-dialog',
        backdropClass: 'sc-dialog-backdrop',
        width: '560px',
        maxWidth: 'calc(100vw - 32px)',
        position: { top: '160px' },
        autoFocus: 'dialog',
        restoreFocus: true,
      },
    );
    ref.afterClosed().subscribe(() => this.ollamaModelsOpen.set(false));
  }

  cloudBadgeOf(id: string): string {
    if (this.cloudChecking(id)) return 'Checking...';
    switch (this.cloudStateOf(id)) {
      case 'in-use':
        return 'In use';
      case 'connected':
        return 'Connected';
      default:
        return 'Not connected';
    }
  }

  formOf(id: string): CloudForm | null {
    const open = this.openForm();
    return open !== null && open.id === id ? open.kind : null;
  }

  toggleForm(id: string, kind: CloudForm): void {
    if (CLOUD_PROVIDERS[id] === undefined && id !== GUARD_CARD_ID) return;
    this.formError.set(null);
    this.savedCard.set(null);
    this.actionError.set(null);
    this.guardNotice.set(null);
    this.guardError.set(null);
    this.openForm.update((open) => (open?.id === id && open.kind === kind ? null : { id, kind }));
    if (this.formOf(id) === kind) this.focusAfterRender(`api-key-${id}`);
  }

  cancelForm(): void {
    const id = this.openForm()?.id;
    this.formError.set(null);
    this.openForm.set(null);
    if (id === undefined) return;
    this.focusAfterRender(
      this.cloudStateOf(id) === 'not-connected' || id === GUARD_CARD_ID
        ? `action-${id}`
        : `change-key-${id}`,
    );
  }

  private focusAfterRender(test: string): void {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>(`[data-test="${test}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  async saveKey(event: Event, id: string, keyInput: HTMLInputElement): Promise<void> {
    event.preventDefault();
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined || this.saving()) return;
    const apiKey = keyInput.value;
    if (apiKey.trim().length === 0) {
      keyInput.value = '';
      this.formError.set('Enter an API key.');
      return;
    }
    await this.exclusive(() => this.checkAndStoreKey(id, provider, apiKey, keyInput));
  }

  private async checkAndStoreKey(
    id: string,
    provider: CloudLlmProvider,
    apiKey: string,
    keyInput: HTMLInputElement,
  ): Promise<void> {
    this.formError.set(null);
    const wasInUse = this.llmConfig.activeCloudProvider() === provider;
    const label = providerLabel(provider);
    let result: ProviderKeyValidateResponse;
    try {
      result = await this.llmConfig.connect(provider, apiKey);
    } catch (err: unknown) {
      if (err instanceof KeyStoredNotSwitchedError) {
        this.openForm.set(null);
        this.savedCard.set(id);
        keyInput.value = '';
        this.savedMessage.set(
          `Connected, not switched: Scout still answers as before, not with ${label}.`,
        );
        this.actionError.set(err.message);
        return;
      }
      this.formError.set(err instanceof Error ? err.message : 'Could not save the API key.');
      return;
    }
    if (!result.ok) {
      if (result.reason === 'invalid-key') keyInput.value = '';
      this.formError.set(rejectionMessage(provider, result));
      return;
    }
    keyInput.value = '';
    if (provider === 'OpenRouter') this.refreshOpenRouterKey();
    this.openForm.set(null);
    this.savedCard.set(id);
    if (wasInUse) {
      this.savedMessage.set('');
      await this.restartIfRunning({
        restarting: `Key changed. Restarting Scout to use the new ${label} key.`,
        idle: `Key changed. Scout will use the new ${label} key the next time it starts.`,
        failed: `Key changed. Restart Scout from the status screen to use the new ${label} key.`,
      });
      return;
    }
    if (this.llmConfig.activeCloudProvider() !== provider) {
      this.savedMessage.set(
        `Connected. Choose a model in Models → Analysis to switch Scout to ${label}.`,
      );
      return;
    }
    this.savedMessage.set('');
    await this.restartIfRunning({
      restarting: `Connected. Restarting Scout to use ${label}.`,
      idle: `Connected. Scout will use ${label} the next time it starts.`,
      failed: `Connected. Restart Scout from the status screen to use ${label}.`,
    });
  }

  async confirmDisconnect(id: string): Promise<void> {
    const card = this.cloudCards.find((c) => c.id === id);
    if (card === undefined || this.saving()) return;
    const name = DISCONNECT_NAMES[id];
    const confirmed = await confirmAction(this.dialog, {
      title: `Disconnect ${card.title}?`,
      body:
        name === undefined
          ? `Scout deletes the stored ${card.title} key from this computer and stops reading its balance. Your credits stay on your ${card.title} account.`
          : this.cloudStateOf(id) === 'in-use'
            ? `Scout deletes the stored API key from this computer. Answers stop using ${name} until you connect it again with a key.`
            : 'Scout deletes the stored API key from this computer.',
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (confirmed) await this.disconnectProvider(id);
  }

  async confirmDisconnectGuard(): Promise<void> {
    if (this.saving()) return;
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect TypeSafe (Jev)?',
      body: 'Scout deletes the TypeSafe key stored on this computer and the read-only guard turns off.',
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (confirmed) await this.disconnectGuard();
  }

  usageView(id: string): UsageView | null {
    const entry = USAGE_SOURCES[id];
    if (entry === undefined || !this.usageShown(id)) return null;
    const counted: SourceUsage | undefined = this.usage()[entry.source];
    const balance: UsageRow[] =
      id === 'openrouter'
        ? openRouterRows(this.openRouterKey())
        : id === 'ollama'
          ? [
              { label: 'Cost', value: 'Local, free', test: 'usage-balance-ollama' },
              ...this.ollamaInstalledRow(),
            ]
          : id === 'chatgpt-plus' && counted?.limitAt
            ? [
                {
                  label: 'Plan usage',
                  value: `Usage limit reached at ${clockTime(counted.limitAt)}`,
                  test: 'usage-limit-chatgpt-plus',
                  warn: true,
                },
              ]
            : [
                {
                  label: id === 'chatgpt-plus' ? 'Plan usage' : 'Balance',
                  value: NOT_REPORTED,
                  test: `usage-balance-${id}`,
                },
              ];
    const notes: UsageNote[] = [];
    if (counted?.lastReported === false) {
      notes.push({ text: 'Usage not reported for this model', test: `usage-unreported-${id}` });
    }
    const keyState = id === 'openrouter' ? this.openRouterKey() : null;
    if (keyState?.error && keyState.readAt !== null) {
      notes.push({
        text: `Balance as of ${clockTime(keyState.readAt)}; the latest read failed.`,
        test: 'usage-as-of-openrouter',
        warn: true,
      });
    }
    return {
      rows: [
        ...balance,
        { label: 'Scout today', value: scoutLine(counted?.today), test: `usage-today-${id}` },
        { label: 'Scout this month', value: scoutLine(counted?.month), test: `usage-month-${id}` },
      ],
      notes,
      page: entry.page,
    };
  }

  private ollamaInstalledRow(): UsageRow[] {
    const models = this.ollamaInstalled();
    if (models === null) return [];
    const bytes = models.reduce((sum, model) => sum + model.sizeBytes, 0);
    return [
      {
        label: 'Models installed',
        value: `${models.length} · ${(bytes / GiB).toFixed(1)} GB`,
        test: 'usage-installed-ollama',
      },
    ];
  }

  private usageShown(id: string): boolean {
    switch (id) {
      case 'ollama':
        return this.stateOf('ollama') === 'connected' || this.localInUse('ollama');
      case 'chatgpt-plus':
        return this.chatgpt().signedIn;
      default:
        return this.cloudStateOf(id) !== 'not-connected';
    }
  }

  async disconnectProvider(id: string): Promise<void> {
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined || this.saving()) return;
    await this.exclusive(async () => {
      this.beginAction(id);
      const wasActive = this.llmConfig.activeCloudProvider() === provider;
      let keyError: string | null = null;
      try {
        await this.llmConfig.disconnect(provider);
      } catch (err: unknown) {
        keyError = err instanceof Error ? err.message : 'Could not remove the API key.';
      }
      const chatgpt = this.chatgpt();
      if (
        chatgpt.signedIn &&
        chatgpt.planUsageGranted &&
        !this.llmConfig.connectedProviders().has('ChatGptPlan')
      ) {
        await this.bridge.chatgptStatus().then(
          (status) => this.applyChatGptStatus(status),
          () => undefined,
        );
      }
      const movedOff = wasActive && this.llmConfig.activeCloudProvider() !== provider;
      if (keyError !== null && !movedOff) {
        this.actionError.set(keyError);
        return;
      }
      if (keyError !== null) {
        this.actionError.set(
          `Disconnected from Scout, but the stored key could not be removed: ${keyError}`,
        );
      } else {
        this.notice.show(
          `${CLOUD_CARD_TITLES[id] ?? id} disconnected. The key was deleted from this computer.`,
          'success',
          6000,
          { label: 'Dismiss', run: () => undefined },
        );
      }
      if (!movedOff) {
        this.savedCard.set(null);
        return;
      }
      const next = this.llmConfig.activeCloudProvider();
      const settled =
        next === null
          ? 'Disconnected. No AI provider is connected; Scout will say so until you connect one.'
          : `Disconnected. Scout now answers with ${providerLabel(next)}.`;
      await this.restartIfRunning({
        restarting: settled,
        idle: settled,
        failed: `${settled} Restart Scout from the status screen to apply it.`,
      });
    });
  }

  localInUse(id: string): boolean {
    return id === 'ollama' && this.llmConfig.provider() === 'Ollama';
  }

  async useOllama(): Promise<void> {
    if (this.saving()) return;
    await this.exclusive(async () => {
      this.beginAction('ollama');
      try {
        await this.llmConfig.useOllama();
      } catch (err: unknown) {
        this.actionError.set(err instanceof Error ? err.message : 'Could not switch to Ollama.');
        return;
      }
      await this.restartIfRunning({
        restarting: 'Restarting Scout to use Ollama.',
        idle: 'Scout will use Ollama the next time it starts.',
        failed: 'Restart Scout from the status screen to use Ollama.',
      });
    });
  }

  async saveGuardKey(event: Event, keyInput: HTMLInputElement): Promise<void> {
    event.preventDefault();
    if (this.saving()) return;
    const key = keyInput.value.trim();
    if (key.length === 0) {
      this.formError.set('Enter an API key.');
      return;
    }
    await this.exclusive(async () => {
      this.formError.set(null);
      try {
        await this.bridge.vaultStore({ key: 'guard.typesafe-api-key', value: key });
      } catch (err: unknown) {
        this.formError.set(err instanceof Error ? err.message : 'Could not save the API key.');
        return;
      }
      this.guardConnected.set(true);
      this.guardCheckFailed.set(false);
      this.openForm.set(null);
      this.savedMessage.set('Saved.');
      this.savedCard.set(GUARD_CARD_ID);
      await this.restartIfRunning(GUARD_RESTART_MESSAGES);
    });
  }

  async disconnectGuard(): Promise<void> {
    if (this.saving()) return;
    await this.exclusive(async () => {
      this.savedCard.set(null);
      this.actionError.set(null);
      this.guardNotice.set(null);
      this.guardError.set(null);
      try {
        await this.bridge.vaultDelete({ key: 'guard.typesafe-api-key' });
      } catch (err: unknown) {
        this.guardError.set(err instanceof Error ? err.message : 'Could not remove the API key.');
        return;
      }
      this.guardConnected.set(false);
      this.guardCheckFailed.set(false);
      this.guardNotice.set(GUARD_DISCONNECT_MESSAGES.idle);
      await this.restartIfRunning(GUARD_DISCONNECT_MESSAGES);
      this.guardNotice.set(this.savedMessage());
      this.savedMessage.set('');
    });
  }

  private async loadGuardState(): Promise<void> {
    try {
      const { exists } = await this.bridge.vaultHas({ key: 'guard.typesafe-api-key' });
      this.guardConnected.set(exists);
    } catch {
      this.guardConnected.set(false);
      this.guardCheckFailed.set(true);
      this.guardError.set('Could not check the TypeSafe key; the read-only guard may still be on.');
    } finally {
      this.guardLoaded.set(true);
    }
  }

  private async applyModel(
    id: string,
    provider: ActiveCloudProvider,
    model: string,
  ): Promise<void> {
    if (this.saving()) return;
    await this.exclusive(async () => {
      this.beginAction(id);
      try {
        await this.llmConfig.use(provider, model);
      } catch (err: unknown) {
        this.actionError.set(
          err instanceof Error ? err.message : `Could not switch to ${providerLabel(provider)}.`,
        );
        return;
      }
      await this.restartIfRunning({
        restarting: `Restarting Scout to use ${providerLabel(provider)}.`,
        idle: `Scout will use ${providerLabel(provider)} the next time it starts.`,
        failed: `Restart Scout from the status screen to use ${providerLabel(provider)}.`,
      });
    });
  }

  private beginAction(id: string): void {
    this.openForm.set(null);
    this.formError.set(null);
    this.actionError.set(null);
    this.savedMessage.set('');
    this.savedCard.set(id);
  }

  /** Runs one provider action at a time: saving stays true until its restart has finished. */
  private async exclusive(work: () => Promise<void>): Promise<void> {
    this.saving.set(true);
    this.idle = new Promise<void>((resolve) => (this.settle = resolve));
    try {
      await work();
    } finally {
      this.saving.set(false);
      this.settle();
    }
  }

  private async restartIfRunning(messages: RestartMessages): Promise<void> {
    try {
      this.savedMessage.set(messages.restarting);
      const result = await this.bridge.workerRestart({ reason: 'AI Source' });
      if (result.outcome === 'failed') {
        this.savedMessage.set(messages.failed);
        this.actionError.set(result.reason);
        return;
      }
      this.savedMessage.set(
        result.outcome === 'none'
          ? messages.idle
          : result.outcome === 'deferred'
            ? RESTART_DEFERRED_LINE
            : messages.restarting,
      );
    } catch (err: unknown) {
      this.savedMessage.set(messages.failed);
      this.actionError.set(err instanceof Error ? err.message : 'Could not restart Scout.');
    }
  }

  async onScan(id: string): Promise<void> {
    if (id !== 'ollama' && id !== 'lm-studio') return;
    this.scanning.update((ids) => new Set([...ids, id]));
    try {
      if (id === 'ollama') {
        const listed = await this.bridge.llmListOllamaModels({ endpoint: DEFAULT_OLLAMA_ENDPOINT });
        this.ollamaInstalled.set(listed.models);
      } else {
        await this.bridge.llmListLmStudioModels({ endpoint: DEFAULT_LM_STUDIO_ENDPOINT });
      }
      this.patchState(id, 'connected');
    } catch {
      this.patchState(id, 'not-connected');
      if (id === 'ollama') this.ollamaInstalled.set(null);
    } finally {
      this.scanning.update((ids) => new Set([...ids].filter((scanned) => scanned !== id)));
    }
  }

  openExternal(url: string): void {
    void this.bridge.appOpenExternal({ url }).catch(() => undefined);
  }

  private patchState(id: string, state: ConnectionState): void {
    this.states.update((prev) => ({ ...prev, [id]: state }));
  }
}

function rejectionMessage(
  provider: CloudLlmProvider,
  result: Extract<ProviderKeyValidateResponse, { ok: false }>,
): string {
  switch (result.reason) {
    case 'invalid-key':
      return `That key was rejected by ${provider}.`;
    case 'unreachable':
      return `${provider} could not be reached; check the connection and try again.`;
    default:
      return result.status === undefined
        ? `${provider} gave an unexpected answer; the key was not saved.`
        : `${provider} answered with HTTP ${result.status}; the key was not saved.`;
  }
}
