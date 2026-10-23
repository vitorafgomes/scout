import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import type { ProviderKeyValidateResponse } from '@scout/shared';

import {
  type CloudLlmProvider,
  DEFAULT_CLOUD_MODELS,
  LlmConfigStore,
} from '../core/llm-config.store';
import { LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';

type FilterKind = 'all' | 'local' | 'cloud';
type CardKind = 'local' | 'cloud';
type ConnectionState = 'connected' | 'not-connected' | 'unknown';
type CloudState = 'in-use' | 'connected' | 'not-connected';
type CloudForm = 'key' | 'model';

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

interface CardModel {
  readonly id: string;
  readonly kind: CardKind;
  readonly title: string;
  readonly description: string;
  readonly iconPath: string;
  readonly primaryAction: string;
  readonly secondaryLabel: string | null;
  readonly secondaryUrl: string | null;
  readonly primaryIsScan: boolean;
  readonly disabled: boolean;
}

const DEFAULT_OLLAMA_ENDPOINT = 'http://localhost:11434';
const GUARD_CARD_ID = 'typesafe-jev';
const DEFAULT_LM_STUDIO_ENDPOINT = 'http://localhost:1234';

const CLOUD_PROVIDERS: Readonly<
  Record<string, { readonly provider: CloudLlmProvider; readonly defaultModel: string }>
> = Object.freeze({
  'openai-api': { provider: 'OpenAI', defaultModel: DEFAULT_CLOUD_MODELS.OpenAI },
  'google-gemini': { provider: 'Gemini', defaultModel: DEFAULT_CLOUD_MODELS.Gemini },
});

/** The OpenAI, Google Gemini, OpenRouter and Ollama marks are from Simple Icons (CC0-1.0); the others are generic. */
const ICONS = Object.freeze({
  localModels:
    'M12 3c-2.5 0-4 1.8-4 4v1H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7c0-2.2-1.5-4-4-4Zm-2 5V7a2 2 0 1 1 4 0v1Zm-1 5a1 1 0 1 1 2 0 1 1 0 0 1-2 0Zm4 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0Z',
  ollama:
    'M16.361 10.26a.894.894 0 0 0-.558.47l-.072.148.001.207c0 .193.004.217.059.353.076.193.152.312.291.448.24.238.51.3.872.205a.86.86 0 0 0 .517-.436.752.752 0 0 0 .08-.498c-.064-.453-.33-.782-.724-.897a1.06 1.06 0 0 0-.466 0zm-9.203.005c-.305.096-.533.32-.65.639a1.187 1.187 0 0 0-.06.52c.057.309.31.59.598.667.362.095.632.033.872-.205.14-.136.215-.255.291-.448.055-.136.059-.16.059-.353l.001-.207-.072-.148a.894.894 0 0 0-.565-.472 1.02 1.02 0 0 0-.474.007Zm4.184 2c-.131.071-.223.25-.195.383.031.143.157.288.353.407.105.063.112.072.117.136.004.038-.01.146-.029.243-.02.094-.036.194-.036.222.002.074.07.195.143.253.064.052.076.054.255.059.164.005.198.001.264-.03.169-.082.212-.234.15-.525-.052-.243-.042-.28.087-.355.137-.08.281-.219.324-.314a.365.365 0 0 0-.175-.48.394.394 0 0 0-.181-.033c-.126 0-.207.03-.355.124l-.085.053-.053-.032c-.219-.13-.259-.145-.391-.143a.396.396 0 0 0-.193.032zm.39-2.195c-.373.036-.475.05-.654.086-.291.06-.68.195-.951.328-.94.46-1.589 1.226-1.787 2.114-.04.176-.045.234-.045.53 0 .294.005.357.043.524.264 1.16 1.332 2.017 2.714 2.173.3.033 1.596.033 1.896 0 1.11-.125 2.064-.727 2.493-1.571.114-.226.169-.372.22-.602.039-.167.044-.23.044-.523 0-.297-.005-.355-.045-.531-.288-1.29-1.539-2.304-3.072-2.497a6.873 6.873 0 0 0-.855-.031zm.645.937a3.283 3.283 0 0 1 1.44.514c.223.148.537.458.671.662.166.251.26.508.303.82.02.143.01.251-.043.482-.08.345-.332.705-.672.957a3.115 3.115 0 0 1-.689.348c-.382.122-.632.144-1.525.138-.582-.006-.686-.01-.853-.042-.57-.107-1.022-.334-1.35-.68-.264-.28-.385-.535-.45-.946-.03-.192.025-.509.137-.776.136-.326.488-.73.836-.963.403-.269.934-.46 1.422-.512.187-.02.586-.02.773-.002zm-5.503-11a1.653 1.653 0 0 0-.683.298C5.617.74 5.173 1.666 4.985 2.819c-.07.436-.119 1.04-.119 1.503 0 .544.064 1.24.155 1.721.02.107.031.202.023.208a8.12 8.12 0 0 1-.187.152 5.324 5.324 0 0 0-.949 1.02 5.49 5.49 0 0 0-.94 2.339 6.625 6.625 0 0 0-.023 1.357c.091.78.325 1.438.727 2.04l.13.195-.037.064c-.269.452-.498 1.105-.605 1.732-.084.496-.095.629-.095 1.294 0 .67.009.803.088 1.266.095.555.288 1.143.503 1.534.071.128.243.393.264.407.007.003-.014.067-.046.141a7.405 7.405 0 0 0-.548 1.873c-.062.417-.071.552-.071.991 0 .56.031.832.148 1.279L3.42 24h1.478l-.05-.091c-.297-.552-.325-1.575-.068-2.597.117-.472.25-.819.498-1.296l.148-.29v-.177c0-.165-.003-.184-.057-.293a.915.915 0 0 0-.194-.25 1.74 1.74 0 0 1-.385-.543c-.424-.92-.506-2.286-.208-3.451.124-.486.329-.918.544-1.154a.787.787 0 0 0 .223-.531c0-.195-.07-.355-.224-.522a3.136 3.136 0 0 1-.817-1.729c-.14-.96.114-2.005.69-2.834.563-.814 1.353-1.336 2.237-1.475.199-.033.57-.028.776.01.226.04.367.028.512-.041.179-.085.268-.19.374-.431.093-.215.165-.333.36-.576.234-.29.46-.489.822-.729.413-.27.884-.467 1.352-.561.17-.035.25-.04.569-.04.319 0 .398.005.569.04a4.07 4.07 0 0 1 1.914.997c.117.109.398.457.488.602.034.057.095.177.132.267.105.241.195.346.374.43.14.068.286.082.503.045.343-.058.607-.053.943.016 1.144.23 2.14 1.173 2.581 2.437.385 1.108.276 2.267-.296 3.153-.097.15-.193.27-.333.419-.301.322-.301.722-.001 1.053.493.539.801 1.866.708 3.036-.062.772-.26 1.463-.533 1.854a2.096 2.096 0 0 1-.224.258.916.916 0 0 0-.194.25c-.054.109-.057.128-.057.293v.178l.148.29c.248.476.38.823.498 1.295.253 1.008.231 2.01-.059 2.581a.845.845 0 0 0-.044.098c0 .006.329.009.732.009h.73l.02-.074.036-.134c.019-.076.057-.3.088-.516.029-.217.029-1.016 0-1.258-.11-.875-.295-1.57-.597-2.226-.032-.074-.053-.138-.046-.141.008-.005.057-.074.108-.152.376-.569.607-1.284.724-2.228.031-.26.031-1.378 0-1.628-.083-.645-.182-1.082-.348-1.525a6.083 6.083 0 0 0-.329-.7l-.038-.064.131-.194c.402-.604.636-1.262.727-2.04a6.625 6.625 0 0 0-.024-1.358 5.512 5.512 0 0 0-.939-2.339 5.325 5.325 0 0 0-.95-1.02 8.097 8.097 0 0 1-.186-.152.692.692 0 0 1 .023-.208c.208-1.087.201-2.443-.017-3.503-.19-.924-.535-1.658-.98-2.082-.354-.338-.716-.482-1.15-.455-.996.059-1.8 1.205-2.116 3.01a6.805 6.805 0 0 0-.097.726c0 .036-.007.066-.015.066a.96.96 0 0 1-.149-.078A4.857 4.857 0 0 0 12 3.03c-.832 0-1.687.243-2.456.698a.958.958 0 0 1-.148.078c-.008 0-.015-.03-.015-.066a6.71 6.71 0 0 0-.097-.725C8.997 1.392 8.337.319 7.46.048a2.096 2.096 0 0 0-.585-.041Zm.293 1.402c.248.197.523.759.682 1.388.03.113.06.244.069.292.007.047.026.152.041.233.067.365.098.76.102 1.24l.002.475-.12.175-.118.178h-.278c-.324 0-.646.041-.954.124l-.238.06c-.033.007-.038-.003-.057-.144a8.438 8.438 0 0 1 .016-2.323c.124-.788.413-1.501.696-1.711.067-.05.079-.049.157.013zm9.825-.012c.17.126.358.46.498.888.28.854.36 2.028.212 3.145-.019.14-.024.151-.057.144l-.238-.06a3.693 3.693 0 0 0-.954-.124h-.278l-.119-.178-.119-.175.002-.474c.004-.669.066-1.19.214-1.772.157-.623.434-1.185.68-1.382.078-.062.09-.063.159-.012z',
  lmStudio: 'M4 4h3v16H4Zm5 6h3v10H9Zm5-4h3v14h-3Zm5 8h3v6h-3Z',
  openai:
    'M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z',
  gemini:
    'M11.04 19.32Q12 21.51 12 24q0-2.49.93-4.68.96-2.19 2.58-3.81t3.81-2.55Q21.51 12 24 12q-2.49 0-4.68-.93a12.3 12.3 0 0 1-3.81-2.58 12.3 12.3 0 0 1-2.58-3.81Q12 2.49 12 0q0 2.49-.96 4.68-.93 2.19-2.55 3.81a12.3 12.3 0 0 1-3.81 2.58Q2.49 12 0 12q2.49 0 4.68.96 2.19.93 3.81 2.55t2.55 3.81',
  openRouter:
    'M16.778 1.844v1.919q-.569-.026-1.138-.032-.708-.008-1.415.037c-1.93.126-4.023.728-6.149 2.237-2.911 2.066-2.731 1.95-4.14 2.75-.396.223-1.342.574-2.185.798-.841.225-1.753.333-1.751.333v4.229s.768.108 1.61.333c.842.224 1.789.575 2.185.799 1.41.798 1.228.683 4.14 2.75 2.126 1.509 4.22 2.11 6.148 2.236.88.058 1.716.041 2.555.005v1.918l7.222-4.168-7.222-4.17v2.176c-.86.038-1.611.065-2.278.021-1.364-.09-2.417-.357-3.979-1.465-2.244-1.593-2.866-2.027-3.68-2.508.889-.518 1.449-.906 3.822-2.59 1.56-1.109 2.614-1.377 3.978-1.466.667-.044 1.418-.017 2.278.02v2.176L24 6.014Z',
  customApi: 'M4 7h10v2H4Zm0 4h6v2H4Zm0 4h10v2H4ZM20 6l-1.5 1.5L20 9h-3v2h3l-1.5 1.5L20 14l4-4Z',
  shield: 'M12 2 4 5v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V5Z',
});

@Component({
  selector: 'sc-sources',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="header">
        <div class="header-left">
          <h1>AI Sources</h1>
          <p class="lede">
            Connect your favorite cloud APIs or run models privately on your device. Configure
            authentication and local servers below.
          </p>
        </div>
        <div class="filter" role="tablist" aria-label="Filter providers">
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'all'"
            [class.filter-chip--active]="filter() === 'all'"
            class="filter-chip"
            data-test="filter-all"
            (click)="setFilter('all')"
          >
            All
          </button>
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'local'"
            [class.filter-chip--active]="filter() === 'local'"
            class="filter-chip"
            data-test="filter-local"
            (click)="setFilter('local')"
          >
            Local
          </button>
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="filter() === 'cloud'"
            [class.filter-chip--active]="filter() === 'cloud'"
            class="filter-chip"
            data-test="filter-cloud"
            (click)="setFilter('cloud')"
          >
            Cloud
          </button>
        </div>
      </header>

      @if (showLocal()) {
        <section data-test="section-local">
          <h2 class="section-title"><span class="section-rule"></span>Local &amp; On-Device</h2>
          <div class="grid">
            @for (card of localCards; track card.id) {
              <article class="card" [attr.data-test]="'card-' + card.id">
                <div class="card-head">
                  <div class="icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="22" height="22">
                      <path [attr.d]="card.iconPath" fill="currentColor" />
                    </svg>
                  </div>
                  <span
                    class="badge"
                    [class.badge--ok]="stateOf(card.id) === 'connected' || localInUse(card.id)"
                    [class.badge--off]="stateOf(card.id) !== 'connected' && !localInUse(card.id)"
                    [attr.data-test]="'badge-' + card.id"
                  >
                    <span class="badge-dot" aria-hidden="true"></span>
                    {{
                      localInUse(card.id)
                        ? 'In use'
                        : stateOf(card.id) === 'connected'
                          ? 'Connected'
                          : 'Not Connected'
                    }}
                  </span>
                </div>
                <h3 class="card-title">{{ card.title }}</h3>
                <p class="card-desc">{{ card.description }}</p>
                <div class="card-actions">
                  @if (card.primaryIsScan) {
                    <button
                      type="button"
                      class="action action--scan"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="isCardDisabled(card) || scanning() === card.id"
                      (click)="onPrimaryAction(card)"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M10 4a6 6 0 1 0 3.74 10.68l4.29 4.3 1.41-1.42-4.29-4.29A6 6 0 0 0 10 4Zm0 2a4 4 0 1 1 0 8 4 4 0 0 1 0-8Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                    </button>
                  } @else {
                    <button
                      type="button"
                      class="action action--primary"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="isCardDisabled(card)"
                      [title]="
                        isCardDisabled(card) && card.id === 'local-models'
                          ? 'Install Ollama or LM Studio to manage downloaded models'
                          : ''
                      "
                      (click)="onPrimaryAction(card)"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M12 3a1 1 0 0 1 1 1v9.59l3.3-3.3 1.4 1.42L12 17.42 6.3 11.7l1.4-1.41L11 13.6V4a1 1 0 0 1 1-1Zm-7 16h14v2H5Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                    </button>
                  }
                  @if (
                    card.id === 'ollama' &&
                    stateOf('ollama') === 'connected' &&
                    !localInUse('ollama')
                  ) {
                    <button
                      type="button"
                      class="action action--primary"
                      data-test="use-ollama"
                      aria-label="Use Ollama"
                      [disabled]="saving()"
                      (click)="useOllama()"
                    >
                      Use
                    </button>
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
                      class="secondary"
                      [attr.data-test]="'secondary-' + card.id"
                      (click)="openExternal(card.secondaryUrl)"
                    >
                      {{ card.secondaryLabel }}
                      <svg viewBox="0 0 24 24" width="11" height="11" aria-hidden="true">
                        <path
                          d="M14 3h7v7h-2V6.41l-9.29 9.3-1.42-1.42L17.59 5H14Zm-9 4h6v2H7v10h10v-6h2v8H5Z"
                          fill="currentColor"
                        />
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
        <section data-test="section-cloud">
          <h2 class="section-title"><span class="section-rule"></span>Cloud APIs</h2>
          <div class="grid">
            @for (card of cloudCards; track card.id) {
              <article class="card" [attr.data-test]="'card-' + card.id">
                <div class="card-head">
                  <div class="icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="22" height="22">
                      <path [attr.d]="card.iconPath" fill="currentColor" />
                    </svg>
                  </div>
                  <span
                    class="badge"
                    [class.badge--ok]="cloudStateOf(card.id) !== 'not-connected'"
                    [class.badge--off]="cloudStateOf(card.id) === 'not-connected'"
                    [attr.data-test]="'badge-' + card.id"
                  >
                    <span class="badge-dot" aria-hidden="true"></span>
                    {{ cloudBadgeOf(card.id) }}
                  </span>
                </div>
                <h3 class="card-title">{{ card.title }}</h3>
                <p class="card-desc">{{ card.description }}</p>
                <div class="card-actions">
                  @if (isKeyed(card.id)) {
                    @switch (cloudStateOf(card.id)) {
                      @case ('in-use') {
                        <p class="in-use-model" [attr.data-test]="'in-use-model-' + card.id">
                          Model: {{ activeModel() }}
                        </p>
                        <div class="form-buttons">
                          <button
                            type="button"
                            class="action"
                            [attr.data-test]="'change-model-' + card.id"
                            [attr.aria-label]="'Change ' + card.title + ' model'"
                            [attr.aria-expanded]="formOf(card.id) === 'model'"
                            [disabled]="saving()"
                            (click)="toggleForm(card.id, 'model')"
                          >
                            Change model
                          </button>
                          <button
                            type="button"
                            class="action"
                            [attr.data-test]="'disconnect-' + card.id"
                            [attr.aria-label]="'Disconnect ' + card.title"
                            [disabled]="saving()"
                            (click)="disconnectProvider(card.id)"
                          >
                            Disconnect
                          </button>
                        </div>
                      }
                      @case ('connected') {
                        <div class="form-buttons">
                          <button
                            type="button"
                            class="action action--primary"
                            [attr.data-test]="'use-' + card.id"
                            [attr.aria-label]="'Use ' + card.title"
                            [disabled]="saving()"
                            (click)="useProvider(card.id)"
                          >
                            Use
                          </button>
                          <button
                            type="button"
                            class="action"
                            [attr.data-test]="'disconnect-' + card.id"
                            [attr.aria-label]="'Disconnect ' + card.title"
                            [disabled]="saving()"
                            (click)="disconnectProvider(card.id)"
                          >
                            Disconnect
                          </button>
                        </div>
                        <button
                          type="button"
                          class="secondary"
                          [attr.data-test]="'change-key-' + card.id"
                          [attr.aria-label]="'Change ' + card.title + ' key'"
                          [attr.aria-expanded]="formOf(card.id) === 'key'"
                          [disabled]="saving()"
                          (click)="toggleForm(card.id, 'key')"
                        >
                          Change key
                        </button>
                      }
                      @default {
                        <button
                          type="button"
                          class="action action--primary action--wide"
                          [attr.data-test]="'action-' + card.id"
                          [attr.aria-label]="'Connect ' + card.title"
                          [attr.aria-expanded]="formOf(card.id) === 'key'"
                          (click)="toggleForm(card.id, 'key')"
                        >
                          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                            <path
                              d="M7 14a5 5 0 1 1 4.9-6h7.6l2 2-3 3-2-2-2 2-2-2-1.6 1.6A5 5 0 0 1 7 14Zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"
                              fill="currentColor"
                            />
                          </svg>
                          <span>{{ card.primaryAction }}</span>
                          <svg
                            class="chevron"
                            viewBox="0 0 24 24"
                            width="14"
                            height="14"
                            aria-hidden="true"
                          >
                            <path d="m9 6 6 6-6 6-1.4-1.4L12.2 12 7.6 7.4Z" fill="currentColor" />
                          </svg>
                        </button>
                      }
                    }
                    @if (formOf(card.id) === 'key') {
                      <form
                        class="key-form"
                        [attr.data-test]="'key-form-' + card.id"
                        (submit)="saveKey($event, card.id, keyInput)"
                      >
                        <label class="field">
                          <span class="field-label">API key</span>
                          <input
                            #keyInput
                            type="password"
                            autocomplete="off"
                            class="text-input"
                            [attr.data-test]="'api-key-' + card.id"
                          />
                        </label>
                        @if (formError(); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="form-buttons">
                          <button
                            type="submit"
                            class="action action--primary"
                            [disabled]="saving()"
                            [attr.data-test]="'save-' + card.id"
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            class="action"
                            [attr.data-test]="'cancel-' + card.id"
                            (click)="cancelForm()"
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    }
                    @if (formOf(card.id) === 'model') {
                      <form
                        class="key-form"
                        [attr.data-test]="'model-form-' + card.id"
                        (submit)="saveModel($event, card.id, modelInput)"
                      >
                        <label class="field">
                          <span class="field-label">Model</span>
                          <input
                            #modelInput
                            type="text"
                            class="text-input"
                            [value]="activeModel() ?? defaultModelOf(card.id)"
                            [attr.data-test]="'model-' + card.id"
                          />
                        </label>
                        @if (formError(); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="form-buttons">
                          <button
                            type="submit"
                            class="action action--primary"
                            [disabled]="saving()"
                            [attr.data-test]="'save-model-' + card.id"
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            class="action"
                            [attr.data-test]="'cancel-model-' + card.id"
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
                    <button
                      type="button"
                      class="action action--primary action--wide"
                      [attr.data-test]="'action-' + card.id"
                      [attr.aria-expanded]="formOf(card.id) === 'key'"
                      (click)="toggleForm(card.id, 'key')"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M7 14a5 5 0 1 1 4.9-6h7.6l2 2-3 3-2-2-2 2-2-2-1.6 1.6A5 5 0 0 1 7 14Zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                      <svg
                        class="chevron"
                        viewBox="0 0 24 24"
                        width="14"
                        height="14"
                        aria-hidden="true"
                      >
                        <path d="m9 6 6 6-6 6-1.4-1.4L12.2 12 7.6 7.4Z" fill="currentColor" />
                      </svg>
                    </button>
                    @if (cloudStateOf(card.id) === 'connected' || guardCheckFailed()) {
                      <button
                        type="button"
                        class="action"
                        data-test="disconnect-typesafe-jev"
                        aria-label="Disconnect TypeSafe (Jev)"
                        [disabled]="saving()"
                        (click)="disconnectGuard()"
                      >
                        Disconnect
                      </button>
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
                        <label class="field">
                          <span class="field-label">API key</span>
                          <input
                            #guardKeyInput
                            type="password"
                            autocomplete="off"
                            class="text-input"
                            [attr.data-test]="'api-key-' + card.id"
                          />
                        </label>
                        @if (formError(); as error) {
                          <p class="form-error" role="alert">{{ error }}</p>
                        }
                        <div class="form-buttons">
                          <button
                            type="submit"
                            class="action action--primary"
                            [disabled]="saving()"
                            [attr.data-test]="'save-' + card.id"
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            class="action"
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
                    <button
                      type="button"
                      class="action action--primary action--wide"
                      [attr.data-test]="'action-' + card.id"
                      [disabled]="card.disabled"
                      [title]="card.disabled && card.id !== 'chatgpt-plus' ? 'Coming soon' : ''"
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                        <path
                          d="M7 14a5 5 0 1 1 4.9-6h7.6l2 2-3 3-2-2-2 2-2-2-1.6 1.6A5 5 0 0 1 7 14Zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"
                          fill="currentColor"
                        />
                      </svg>
                      <span>{{ card.primaryAction }}</span>
                      <svg
                        class="chevron"
                        viewBox="0 0 24 24"
                        width="14"
                        height="14"
                        aria-hidden="true"
                      >
                        <path d="m9 6 6 6-6 6-1.4-1.4L12.2 12 7.6 7.4Z" fill="currentColor" />
                      </svg>
                    </button>
                  }
                </div>
              </article>
            }
          </div>
        </section>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        color: #e4e8f0;
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .page {
        padding: 32px 40px 48px;
        max-width: 1400px;
        margin: 0 auto;
        display: flex;
        flex-direction: column;
        gap: 28px;
      }
      .header {
        display: flex;
        justify-content: space-between;
        gap: 24px;
        align-items: flex-start;
      }
      .header-left {
        display: flex;
        flex-direction: column;
        gap: 8px;
        max-width: 720px;
      }
      h1 {
        font-size: 26px;
        margin: 0;
        font-weight: 700;
      }
      .lede {
        margin: 0;
        color: #7b8299;
        font-size: 13px;
        line-height: 1.5;
      }
      .filter {
        background: #171b26;
        border: 1px solid #252b3b;
        border-radius: 8px;
        padding: 4px;
        display: inline-flex;
        gap: 2px;
      }
      .filter-chip {
        background: transparent;
        color: #7b8299;
        border: none;
        border-radius: 6px;
        padding: 6px 16px;
        font-size: 13px;
        font-weight: 500;
        cursor: pointer;
        font-family: inherit;
      }
      .filter-chip:hover {
        color: #e4e8f0;
      }
      .filter-chip--active {
        background: #2a3245;
        color: #e4e8f0;
      }
      .section-title {
        font-size: 12px;
        font-weight: 500;
        color: #7b8299;
        text-transform: none;
        letter-spacing: 0.02em;
        margin: 0 0 14px;
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .section-rule {
        display: inline-block;
        width: 22px;
        height: 1px;
        background: #3d4258;
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
        gap: 16px;
      }
      .card {
        background: #141822;
        border: 1px solid #252b3b;
        border-radius: 12px;
        padding: 18px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        min-height: 220px;
      }
      .card-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
      }
      .icon {
        width: 38px;
        height: 38px;
        border-radius: 8px;
        background: #1e2333;
        display: flex;
        align-items: center;
        justify-content: center;
        color: #e4e8f0;
      }
      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 11px;
        font-weight: 600;
        padding: 3px 8px;
        border-radius: 999px;
        border: 1px solid transparent;
      }
      .badge-dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: currentColor;
      }
      .badge--ok {
        color: #4ade80;
        background: rgba(74, 222, 128, 0.08);
        border-color: rgba(74, 222, 128, 0.25);
      }
      .badge--off {
        color: #7b8299;
        background: rgba(123, 130, 153, 0.08);
        border-color: rgba(123, 130, 153, 0.2);
      }
      .card-title {
        font-size: 16px;
        font-weight: 700;
        margin: 0;
      }
      .card-desc {
        font-size: 12px;
        line-height: 1.5;
        color: #7b8299;
        margin: 0;
        flex: 1;
      }
      .card-actions {
        display: flex;
        flex-direction: column;
        gap: 8px;
        align-items: stretch;
      }
      .action {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        justify-content: center;
        font-family: inherit;
        font-size: 13px;
        font-weight: 500;
        border-radius: 8px;
        cursor: pointer;
        padding: 9px 12px;
        border: 1px solid transparent;
      }
      .action--scan {
        background: #1e2333;
        color: #e4e8f0;
        border-color: #2a3245;
        justify-content: flex-start;
      }
      .action--scan:hover:enabled {
        background: #252b3d;
      }
      .action--primary {
        background: #1e2333;
        color: #e4e8f0;
        border-color: #2a3245;
      }
      .action--wide {
        justify-content: space-between;
      }
      .action--wide .chevron {
        margin-left: auto;
      }
      .action:disabled {
        opacity: 0.55;
        cursor: not-allowed;
      }
      .secondary {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        font-size: 12px;
        color: #6b89c4;
        align-self: center;
        background: transparent;
        border: none;
        padding: 4px 0;
        cursor: pointer;
        font-family: inherit;
      }
      .secondary:hover {
        text-decoration: underline;
      }
      .key-form {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .field-label {
        font-size: 12px;
        color: #7b8299;
      }
      .text-input {
        background: #0f1117;
        color: #e4e8f0;
        border: 1px solid #2a3245;
        border-radius: 6px;
        padding: 7px 10px;
        font-family: inherit;
        font-size: 13px;
      }
      .form-buttons {
        display: flex;
        gap: 8px;
      }
      .form-buttons .action {
        flex: 1;
        background: transparent;
        color: #e4e8f0;
        border-color: #2a3245;
      }
      .form-buttons .action--primary {
        background: #1e2333;
      }
      .form-error {
        margin: 0;
        font-size: 12px;
        color: #f87171;
      }
      .in-use-model {
        margin: 0;
        font-size: 12px;
        color: #e4e8f0;
      }
      .saved {
        margin: 0;
        font-size: 12px;
        color: #4ade80;
      }
    `,
  ],
})
export class SourcesComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly router = inject(Router);
  private readonly localModelsStore = inject(LocalModelsStore);
  private readonly llmConfig = inject(LlmConfigStore);

  readonly filter = signal<FilterKind>('all');
  readonly openForm = signal<{ readonly id: string; readonly kind: CloudForm } | null>(null);
  readonly formError = signal<string | null>(null);
  readonly saving = signal<boolean>(false);
  readonly savedCard = signal<string | null>(null);
  readonly savedMessage = signal<string>('');
  readonly actionError = signal<string | null>(null);
  readonly activeModel = this.llmConfig.model;
  readonly scanning = signal<string | null>(null);
  readonly guardCardId = GUARD_CARD_ID;
  private readonly guardConnected = signal<boolean>(false);
  readonly guardNotice = signal<string | null>(null);
  readonly guardError = signal<string | null>(null);
  readonly guardCheckFailed = signal<boolean>(false);
  private readonly states = signal<Readonly<Record<string, ConnectionState>>>({
    ollama: 'unknown',
    'lm-studio': 'not-connected',
  });

  readonly runtimeReachable = computed<boolean>(
    () => this.stateOf('ollama') === 'connected' || this.stateOf('lm-studio') === 'connected',
  );

  readonly hasSelectedLocalModel = computed<boolean>(() => {
    const sel = this.localModelsStore.selected();
    return sel.transcription !== null || sel.analysis !== null;
  });

  readonly localModelsConnected = computed<boolean>(
    () => this.runtimeReachable() && this.hasSelectedLocalModel(),
  );

  readonly showLocal = computed<boolean>(() => this.filter() !== 'cloud');
  readonly showCloud = computed<boolean>(() => this.filter() !== 'local');

  readonly localCards: readonly CardModel[] = Object.freeze([
    {
      id: 'local-models',
      kind: 'local',
      title: 'Local Models',
      description:
        'Download and run AI models natively inside the app. Private, completely offline, and absolutely no API keys required.',
      iconPath: ICONS.localModels,
      primaryAction: 'Manage Downloaded Models',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
    {
      id: 'ollama',
      kind: 'local',
      title: 'Ollama',
      description:
        'Run powerful AI models locally on your machine. Private, fast, and completely offline with no API keys required.',
      iconPath: ICONS.ollama,
      primaryAction: 'Scan for Ollama Server',
      secondaryLabel: 'Download Ollama',
      secondaryUrl: 'https://ollama.com/download',
      primaryIsScan: true,
      disabled: false,
    },
    {
      id: 'lm-studio',
      kind: 'local',
      title: 'LM Studio',
      description:
        'Discover, download, and run any compatible local LLM. Start the local server in LM Studio to connect automatically.',
      iconPath: ICONS.lmStudio,
      primaryAction: 'Scan for LM Studio',
      secondaryLabel: 'Download LM Studio',
      secondaryUrl: 'https://lmstudio.ai',
      primaryIsScan: true,
      disabled: false,
    },
  ]);

  readonly cloudCards: readonly CardModel[] = Object.freeze([
    {
      id: 'chatgpt-plus',
      kind: 'cloud',
      title: 'ChatGPT Plus',
      description:
        'Not supported: a ChatGPT Plus or Pro subscription does not include API access. Use the OpenAI API card with an API key instead.',
      iconPath: ICONS.openai,
      primaryAction: 'Not supported',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
    {
      id: 'openai-api',
      kind: 'cloud',
      title: 'OpenAI API',
      description:
        'Provide your developer API key to build with GPT-4o, GPT-4 Turbo, and DALL·E models directly.',
      iconPath: ICONS.openai,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'google-gemini',
      kind: 'cloud',
      title: 'Google Gemini',
      description: 'Fast, multimodal AI models from Google including Gemini 2.5 Pro and Flash.',
      iconPath: ICONS.gemini,
      primaryAction: 'Connect',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: GUARD_CARD_ID,
      kind: 'cloud',
      title: 'TypeSafe (Jev)',
      description:
        "Read-only guard: TypeSafe's Jev model checks each request, with the recent conversation, and refuses ones that ask Scout to change something.",
      iconPath: ICONS.shield,
      primaryAction: 'Add API Key',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: false,
    },
    {
      id: 'openrouter',
      kind: 'cloud',
      title: 'OpenRouter',
      description:
        'A unified interface to access hundreds of AI models through a single API connection.',
      iconPath: ICONS.openRouter,
      primaryAction: 'Configure OpenRouter',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
    {
      id: 'custom-api',
      kind: 'cloud',
      title: 'Custom API',
      description:
        'Connect to any OpenAI-compatible API endpoint. Works with self-hosted models, vLLM, LocalAI, and more.',
      iconPath: ICONS.customApi,
      primaryAction: 'Configure Endpoint',
      secondaryLabel: null,
      secondaryUrl: null,
      primaryIsScan: false,
      disabled: true,
    },
  ]);

  ngOnInit(): void {
    void this.onScan('ollama');
    void this.onScan('lm-studio');
    void this.llmConfig.loadFromVault();
    void this.loadGuardState();
  }

  setFilter(kind: FilterKind): void {
    this.filter.set(kind);
  }

  stateOf(id: string): ConnectionState {
    if (id === 'local-models') {
      return this.localModelsConnected() ? 'connected' : 'not-connected';
    }
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

  cloudBadgeOf(id: string): string {
    switch (this.cloudStateOf(id)) {
      case 'in-use':
        return 'In use';
      case 'connected':
        return 'Connected';
      default:
        return 'Not Connected';
    }
  }

  defaultModelOf(id: string): string {
    return CLOUD_PROVIDERS[id]?.defaultModel ?? '';
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
  }

  cancelForm(): void {
    this.formError.set(null);
    this.openForm.set(null);
  }

  async saveKey(event: Event, id: string, keyInput: HTMLInputElement): Promise<void> {
    event.preventDefault();
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined || this.saving()) return;
    const apiKey = keyInput.value;
    keyInput.value = '';
    if (apiKey.trim().length === 0) {
      this.formError.set('Enter an API key.');
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    const wasInUse = this.llmConfig.activeCloudProvider() === provider;
    let result: ProviderKeyValidateResponse;
    try {
      result = await this.llmConfig.connect(provider, apiKey);
    } catch (err: unknown) {
      this.formError.set(err instanceof Error ? err.message : 'Could not save the API key.');
      return;
    } finally {
      this.saving.set(false);
    }
    if (!result.ok) {
      this.formError.set(rejectionMessage(provider, result));
      return;
    }
    this.openForm.set(null);
    this.savedCard.set(id);
    if (wasInUse || this.llmConfig.activeCloudProvider() !== provider) {
      this.savedMessage.set(`Connected. Choose Use to switch Scout to ${provider}.`);
      return;
    }
    this.savedMessage.set('');
    await this.restartIfRunning({
      restarting: `Connected. Restarting Scout to use ${provider}.`,
      idle: `Connected. Scout will use ${provider} the next time it starts.`,
      failed: `Connected. Restart Scout from the status screen to use ${provider}.`,
    });
  }

  async useProvider(id: string): Promise<void> {
    const entry = CLOUD_PROVIDERS[id];
    if (entry === undefined) return;
    await this.applyModel(id, entry.provider, entry.defaultModel);
  }

  async saveModel(event: Event, id: string, modelInput: HTMLInputElement): Promise<void> {
    event.preventDefault();
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined) return;
    if (modelInput.value.trim().length === 0) {
      this.formError.set('Enter a model.');
      return;
    }
    await this.applyModel(id, provider, modelInput.value);
  }

  async disconnectProvider(id: string): Promise<void> {
    const provider = CLOUD_PROVIDERS[id]?.provider;
    if (provider === undefined || this.saving()) return;
    this.beginAction(id);
    const wasActive = this.llmConfig.activeCloudProvider() === provider;
    let keyError: string | null = null;
    try {
      await this.llmConfig.disconnect(provider);
    } catch (err: unknown) {
      keyError = err instanceof Error ? err.message : 'Could not remove the API key.';
    } finally {
      this.saving.set(false);
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
    }
    if (!movedOff) {
      this.savedMessage.set('Disconnected.');
      return;
    }
    const next = this.llmConfig.activeCloudProvider();
    const settled =
      next === null
        ? 'Disconnected. No AI provider is connected; Scout will say so until you connect one.'
        : `Disconnected. Scout now answers with ${next}.`;
    await this.restartIfRunning({
      restarting: settled,
      idle: settled,
      failed: `${settled} Restart Scout from the status screen to apply it.`,
    });
  }

  localInUse(id: string): boolean {
    return id === 'ollama' && this.llmConfig.provider() === 'Ollama';
  }

  async useOllama(): Promise<void> {
    if (this.saving()) return;
    this.beginAction('ollama');
    try {
      await this.llmConfig.useOllama();
    } catch (err: unknown) {
      this.actionError.set(err instanceof Error ? err.message : 'Could not switch to Ollama.');
      return;
    } finally {
      this.saving.set(false);
    }
    await this.restartIfRunning({
      restarting: 'Restarting Scout to use Ollama.',
      idle: 'Scout will use Ollama the next time it starts.',
      failed: 'Restart Scout from the status screen to use Ollama.',
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
    this.saving.set(true);
    this.formError.set(null);
    try {
      await this.bridge.vaultStore({ key: 'guard.typesafe-api-key', value: key });
    } catch (err: unknown) {
      this.formError.set(err instanceof Error ? err.message : 'Could not save the API key.');
      return;
    } finally {
      this.saving.set(false);
    }
    this.guardConnected.set(true);
    this.guardCheckFailed.set(false);
    this.openForm.set(null);
    this.savedMessage.set('Saved.');
    this.savedCard.set(GUARD_CARD_ID);
    await this.restartIfRunning(GUARD_RESTART_MESSAGES);
  }

  async disconnectGuard(): Promise<void> {
    if (this.saving()) return;
    this.saving.set(true);
    this.savedCard.set(null);
    this.actionError.set(null);
    this.guardNotice.set(null);
    this.guardError.set(null);
    try {
      await this.bridge.vaultDelete({ key: 'guard.typesafe-api-key' });
    } catch (err: unknown) {
      this.guardError.set(err instanceof Error ? err.message : 'Could not remove the API key.');
      return;
    } finally {
      this.saving.set(false);
    }
    this.guardConnected.set(false);
    this.guardCheckFailed.set(false);
    this.guardNotice.set('Disconnected. The read-only guard is off.');
    await this.restartIfRunning(GUARD_RESTART_MESSAGES);
    this.savedMessage.set('');
  }

  private async loadGuardState(): Promise<void> {
    try {
      const { exists } = await this.bridge.vaultHas({ key: 'guard.typesafe-api-key' });
      this.guardConnected.set(exists);
    } catch {
      this.guardConnected.set(false);
      this.guardCheckFailed.set(true);
      this.guardError.set('Could not check the TypeSafe key; the read-only guard may still be on.');
    }
  }

  private async applyModel(id: string, provider: CloudLlmProvider, model: string): Promise<void> {
    if (this.saving()) return;
    this.beginAction(id);
    try {
      await this.llmConfig.use(provider, model);
    } catch (err: unknown) {
      this.actionError.set(err instanceof Error ? err.message : `Could not switch to ${provider}.`);
      return;
    } finally {
      this.saving.set(false);
    }
    await this.restartIfRunning({
      restarting: `Restarting Scout to use ${provider}.`,
      idle: `Scout will use ${provider} the next time it starts.`,
      failed: `Restart Scout from the status screen to use ${provider}.`,
    });
  }

  private beginAction(id: string): void {
    this.saving.set(true);
    this.openForm.set(null);
    this.formError.set(null);
    this.actionError.set(null);
    this.savedMessage.set('');
    this.savedCard.set(id);
  }

  private async restartIfRunning(messages: RestartMessages): Promise<void> {
    try {
      const { state } = await this.bridge.workerStatus();
      if (state !== 'running' && state !== 'starting') {
        this.savedMessage.set(messages.idle);
        return;
      }
      this.savedMessage.set(messages.restarting);
      await this.bridge.workerStop();
      await this.bridge.workerStart();
    } catch (err: unknown) {
      this.savedMessage.set(messages.failed);
      this.actionError.set(err instanceof Error ? err.message : 'Could not restart Scout.');
    }
  }

  isCardDisabled(card: CardModel): boolean {
    if (card.id === 'local-models') {
      return !this.runtimeReachable();
    }
    return card.disabled;
  }

  onPrimaryAction(card: CardModel): void {
    if (card.id === 'local-models') {
      if (this.runtimeReachable()) {
        void this.router.navigateByUrl('/sources/local-models');
      }
      return;
    }
    if (card.primaryIsScan) {
      void this.onScan(card.id);
    }
  }

  async onScan(id: string): Promise<void> {
    if (id !== 'ollama' && id !== 'lm-studio') return;
    this.scanning.set(id);
    try {
      if (id === 'ollama') {
        await this.bridge.llmListOllamaModels({ endpoint: DEFAULT_OLLAMA_ENDPOINT });
      } else {
        await this.bridge.llmListLmStudioModels({ endpoint: DEFAULT_LM_STUDIO_ENDPOINT });
      }
      // Connected = endpoint reachable. A reachable server with zero
      // pulled models still counts as installed and connected.
      this.patchState(id, 'connected');
    } catch {
      this.patchState(id, 'not-connected');
    } finally {
      this.scanning.set(null);
    }
  }

  openExternal(url: string): void {
    void this.bridge.appOpenExternal({ url }).catch(() => {
      // Silent: the renderer cannot do better than hand off to the main process.
    });
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
