import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { readIpcError, type LocalSpeakSample, type LocalVoicesResponse } from '@scout/shared';

import { IconComponent } from '../core/icon.component';
import { providerLabel } from '../core/llm-config.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { AudioDevices } from '../speech/audio-devices';
import { WebAudioPlayer } from '../speech/audio-player';
import type { SpeechPlayer } from '../speech/speech-queue';

/**
 * What plays a voice sample on the Speech tab, on the speaker chosen in Settings; its own player, so a sample never
 * cuts an answer being read aloud.
 */
export const SAMPLE_PLAYER = new InjectionToken<SpeechPlayer>('SAMPLE_PLAYER', {
  providedIn: 'root',
  factory: () => {
    const devices = inject(AudioDevices);
    return new WebAudioPlayer(undefined, (context) => devices.route(context));
  },
});

/** The sentence every sample reads. */
export const SAMPLE_SENTENCE = 'Hello. This is how I sound when I read your answers aloud.';

/** The vault keys the Speech tab saves; main reads them for every sentence, so nothing restarts. */
export const PROVIDER_VOICE_KEY = 'speech.voice.provider';
export const LOCAL_VOICE_KEY = 'speech.voice.local';

const COLLAPSED_OTHERS = 4;

type Group = 'provider' | 'local';

interface VoiceRow {
  readonly group: Group;
  readonly id: string;
  readonly name: string;
  readonly desc: string;
  readonly isDefault: boolean;
  readonly domId: string;
}

const languages = new Intl.DisplayNames(['en'], {
  type: 'language',
  languageDisplay: 'standard',
  style: 'short',
});

/** The language of a local voice, read from the locale its id starts with, such as `en_GB`; empty when it has none. */
export function languageOf(id: string): string {
  const locale = /^piper:([a-z]{2,3}(?:_[A-Z]{2})?)-/.exec(id)?.[1];
  if (locale === undefined) return '';
  try {
    return languages.of(locale.replace('_', '-')) ?? '';
  } catch {
    return '';
  }
}

function domIdOf(group: Group, id: string): string {
  return `sp-${group}-${id.replace(/[^a-z0-9]/gi, '-')}`;
}

/**
 * The Speech tab of Models: the voice of the AI Source, shown only when it has speech, and Scout's local voices on this
 * machine, as the worker lists them; each row can play a sample, and Save stores the choice in the vault, which main
 * reads for every sentence, so the next sentence read uses it and nothing restarts.
 */
@Component({
  selector: 'sc-speech-voices',
  standalone: true,
  imports: [IconComponent, MatButtonModule, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" aria-labelledby="sp-title" data-test="panel-speech">
      <div class="head">
        <span class="badge" aria-hidden="true"><sc-icon name="speaker" [size]="20" /></span>
        <div class="head-text">
          <h2 id="sp-title">Speech</h2>
          <span class="muted"
            >The voice Scout uses to read answers aloud and to speak in conversation mode.</span
          >
        </div>
      </div>

      @if (listError(); as reason) {
        <div class="notice notice--error" role="alert" data-test="speech-list-error">
          <sc-icon name="error" [size]="16" />
          <span class="notice-text">{{ reason }}</span>
          <button type="button" class="pill" data-test="speech-list-retry" (click)="load()">
            <sc-icon name="refresh" [size]="16" />
            Try again
          </button>
        </div>
      } @else if (voices() === null) {
        <p class="muted" role="status" data-test="speech-loading">Loading voices...</p>
      } @else {
        <div class="cols">
          <section class="col" aria-labelledby="sp-prov-title">
            <div class="col-head">
              <h3 id="sp-prov-title">Voice of your AI Source</h3>
              @if (providerSpeech()) {
                <span class="muted small" data-test="speech-provider-via"
                  >via {{ providerName() }}</span
                >
              }
            </div>
            @if (providerSpeech()) {
              <div
                role="radiogroup"
                aria-labelledby="sp-prov-title"
                class="rows"
                data-test="speech-provider-voices"
              >
                @for (row of providerRows(); track row.id) {
                  <ng-container
                    *ngTemplateOutlet="voiceRow; context: { $implicit: row, list: providerRows() }"
                  />
                }
              </div>
            } @else {
              <div class="notice" role="note" data-test="speech-no-provider">
                <sc-icon name="info" [size]="16" />
                <span>Your AI Source has no voice; Scout uses its local voice.</span>
              </div>
            }
          </section>

          <section class="col" aria-labelledby="sp-local-title">
            <div class="col-head">
              <h3 id="sp-local-title">Scout's local voice</h3>
              <span class="muted small">Used when your AI Source has no voice.</span>
            </div>
            @if (localRows().length === 0) {
              <p class="muted" data-test="speech-no-local">
                Scout's local voice is not on this computer.
              </p>
            } @else {
              <div
                id="sp-local-list"
                role="radiogroup"
                aria-labelledby="sp-local-title"
                class="rows"
                data-test="speech-local-voices"
              >
                @for (row of shownLocalRows(); track row.id) {
                  <ng-container
                    *ngTemplateOutlet="
                      voiceRow;
                      context: { $implicit: row, list: shownLocalRows() }
                    "
                  />
                }
              </div>
              @if (otherLocalCount() > collapsedOthers) {
                <button
                  type="button"
                  class="pill pill--accent toggle"
                  aria-controls="sp-local-list"
                  [attr.aria-expanded]="showAll()"
                  data-test="speech-show-all"
                  (click)="showAll.set(!showAll())"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2.2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    aria-hidden="true"
                  >
                    <path [attr.d]="showAll() ? 'M6 15l6-6 6 6' : 'M6 9l6 6 6-6'" />
                  </svg>
                  {{ showAll() ? 'Show fewer' : 'Show all ' + otherLocalCount() }}
                </button>
              }
            }
          </section>
        </div>

        <div class="foot">
          <div class="foot-text">
            <span
              role="status"
              class="status"
              [class.status--ok]="saved()"
              [class.status--error]="saveError() !== null"
              data-test="speech-status"
              >{{ status() }}</span
            >
            <span class="muted small"
              >Saving does not restart anything; the next sentence read uses the new voice.</span
            >
          </div>
          <button
            type="button"
            matButton="filled"
            [disabled]="saving()"
            data-test="speech-save"
            (click)="save()"
          >
            <sc-icon name="save" [size]="17" />
            Save
          </button>
        </div>
      }
    </section>

    <ng-template #voiceRow let-row let-list="list">
      <div class="row" [class.row--on]="picked(row)" [attr.data-test]="'speech-row-' + row.id">
        <button
          type="button"
          role="radio"
          class="pick"
          [id]="row.domId"
          [attr.aria-checked]="picked(row)"
          [attr.aria-describedby]="row.desc ? row.domId + '-desc' : null"
          [attr.tabindex]="picked(row) ? 0 : -1"
          (click)="pick(row)"
          (keydown)="onKey($event, row, list)"
        >
          <span class="radio" aria-hidden="true"></span>
          <span class="pick-text">
            <span class="name">
              <span class="name-text">{{ row.name }}</span>
              @if (row.isDefault) {
                <span class="tag tag--neutral" data-test="speech-default">Default</span>
              }
              @if (inUse(row)) {
                <span class="tag tag--ok" data-test="speech-in-use"
                  ><sc-icon name="check" [size]="12" />In use</span
                >
              }
            </span>
            @if (row.desc) {
              <span class="muted small" [id]="row.domId + '-desc'">{{ row.desc }}</span>
            }
          </span>
        </button>
        <button
          type="button"
          class="pill sample"
          [class.sample--on]="playing() === row.domId"
          [attr.aria-pressed]="playing() === row.domId"
          [attr.aria-label]="
            (playing() === row.domId ? 'Stop the sample of ' : 'Hear a sample of ') + row.name
          "
          [attr.data-test]="'speech-sample-' + row.id"
          (click)="toggleSample(row)"
        >
          <sc-icon [name]="playing() === row.domId ? 'stop' : 'play'" [size]="14" />
          {{ playing() === row.domId ? 'Stop sample' : 'Hear a sample' }}
        </button>
        @if (failed()[row.domId]; as reason) {
          <span class="row-error" role="alert" [attr.data-test]="'speech-sample-error-' + row.id">
            <sc-icon name="error" [size]="14" />{{ reason }}
          </span>
        }
      </div>
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: 14px;
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }
      .badge {
        width: 36px;
        height: 36px;
        border-radius: 10px;
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        display: flex;
        align-items: center;
        justify-content: center;
      }
      .head-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
        min-width: 0;
      }
      h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      h3 {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
      }
      .muted {
        margin: 0;
        color: var(--sc-muted);
        font-size: 13px;
      }
      .small {
        font-size: 12px;
      }
      .cols {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        align-items: flex-start;
      }
      .col {
        flex: 1 1 420px;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .col-head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 8px;
      }
      .rows {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px 12px;
        padding: 8px 10px 8px 14px;
        border-radius: 12px;
        border: 1px solid var(--sc-line);
        background: transparent;
      }
      .row--on {
        border-color: var(--sc-accent);
        background: var(--sc-surface-2);
      }
      .pick {
        flex: 1 1 220px;
        min-width: 0;
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 2px 0;
        border: 0;
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .pick:focus-visible,
      .pill:focus-visible {
        outline: 2px solid var(--sc-accent);
        outline-offset: 2px;
      }
      .radio {
        width: 18px;
        height: 18px;
        flex: 0 0 18px;
        border-radius: 50%;
        border: 2px solid var(--sc-muted);
        box-sizing: border-box;
        position: relative;
      }
      .row--on .radio {
        border-color: var(--sc-accent);
      }
      .row--on .radio::after {
        content: '';
        position: absolute;
        inset: 3px;
        border-radius: 50%;
        background: var(--sc-accent);
      }
      .pick-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
      }
      .name {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .name-text {
        font-weight: 600;
      }
      .tag {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 1px 8px;
        border-radius: 10px;
        font-size: 11px;
        font-weight: 600;
      }
      .tag--neutral {
        background: var(--sc-surface-2);
        color: var(--sc-fg-2);
      }
      .tag--ok {
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .pill {
        height: 32px;
        padding: 0 12px;
        border-radius: 16px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font: inherit;
        font-size: 13px;
        font-weight: 500;
        white-space: nowrap;
        cursor: pointer;
      }
      .pill--accent {
        color: var(--sc-accent);
      }
      .toggle {
        align-self: flex-start;
      }
      .sample--on {
        background: var(--sc-surface-2);
        color: var(--sc-accent);
      }
      .row-error {
        flex-basis: 100%;
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        color: var(--sc-error);
      }
      .notice {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-info-container);
        color: var(--sc-on-info-container);
        font-size: 13px;
      }
      .notice--error {
        background: var(--sc-error-container);
        border: 1px solid var(--sc-error);
        color: var(--sc-on-error-container);
      }
      .notice--error .pill {
        background: var(--sc-surface);
      }
      .notice-text {
        margin-right: auto;
      }
      .foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding-top: 12px;
        border-top: 1px solid var(--sc-line);
      }
      .foot-text {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-right: auto;
        min-width: 0;
      }
      .status {
        font-size: 13px;
        color: var(--sc-muted);
      }
      .status--ok {
        color: var(--sc-on-ok-container);
      }
      .status--error {
        color: var(--sc-error);
      }
    `,
  ],
})
export class SpeechVoicesComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly player = inject(SAMPLE_PLAYER);
  private generation = 0;

  readonly collapsedOthers = COLLAPSED_OTHERS;
  readonly voices = signal<LocalVoicesResponse | null>(null);
  readonly listError = signal<string | null>(null);
  readonly savedProvider = signal<string | null>(null);
  readonly savedLocal = signal<string | null>(null);
  readonly providerPick = signal<string | null>(null);
  readonly localPick = signal<string | null>(null);
  readonly showAll = signal(false);
  readonly playing = signal<string | null>(null);
  readonly failed = signal<Readonly<Record<string, string>>>({});
  readonly saving = signal(false);
  readonly saved = signal(false);
  readonly saveError = signal<string | null>(null);

  readonly providerSpeech = computed(() => (this.voices()?.providerVoices.length ?? 0) > 0);
  readonly providerName = computed(() => {
    const provider = this.voices()?.provider ?? '';
    return provider === 'OpenAI' ? providerLabel('OpenAI') : provider;
  });
  readonly providerRows = computed<readonly VoiceRow[]>(() =>
    (this.voices()?.providerVoices ?? []).map((name) => ({
      group: 'provider',
      id: name,
      name,
      desc: '',
      isDefault: false,
      domId: domIdOf('provider', name),
    })),
  );
  readonly localRows = computed<readonly VoiceRow[]>(() => {
    const voices = this.voices();
    if (voices === null) return [];
    const rows = voices.localVoices.map<VoiceRow>((v) => ({
      group: 'local',
      id: v.id,
      name: v.label,
      desc: languageOf(v.id),
      isDefault: v.id === voices.defaultLocal,
      domId: domIdOf('local', v.id),
    }));
    const others = rows
      .filter((r) => !r.isDefault)
      .sort((a, b) => a.name.localeCompare(b.name, 'en', { numeric: true }));
    return [...rows.filter((r) => r.isDefault), ...others];
  });
  readonly otherLocalCount = computed(() => this.localRows().filter((r) => !r.isDefault).length);
  readonly shownLocalRows = computed<readonly VoiceRow[]>(() => {
    if (this.showAll()) return this.localRows();
    let others = 0;
    return this.localRows().filter(
      (r) => r.isDefault || r.id === this.localPick() || (others += 1) <= COLLAPSED_OTHERS,
    );
  });
  readonly providerInUse = computed(() => {
    const list = this.voices()?.providerVoices ?? [];
    const saved = this.savedProvider();
    return saved !== null && list.includes(saved) ? saved : (list[0] ?? null);
  });
  readonly localInUse = computed(() => {
    const voices = this.voices();
    const saved = this.savedLocal();
    if (voices === null) return null;
    return saved !== null && voices.localVoices.some((v) => v.id === saved)
      ? saved
      : voices.defaultLocal;
  });
  readonly status = computed(() => {
    const error = this.saveError();
    if (error !== null) return error;
    const speaking = this.providerSpeech()
      ? `${this.providerInUse() ?? ''}, the voice of your AI Source`
      : `${this.localRows().find((r) => r.id === this.localInUse())?.name ?? 'no voice'}, Scout's local voice`;
    return this.saved()
      ? `Saved. The next sentence read uses ${speaking}.`
      : `Scout reads aloud with ${speaking}.`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stopSample());
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.listError.set(null);
    this.voices.set(null);
    const read = (key: typeof PROVIDER_VOICE_KEY | typeof LOCAL_VOICE_KEY) =>
      this.bridge.vaultGetPublic({ key }).then(
        ({ value }) => value,
        () => null,
      );
    try {
      const [voices, provider, local] = await Promise.all([
        this.bridge.localVoices(),
        read(PROVIDER_VOICE_KEY),
        read(LOCAL_VOICE_KEY),
      ]);
      this.savedProvider.set(provider);
      this.savedLocal.set(local);
      this.voices.set(voices);
      this.providerPick.set(this.providerInUse());
      this.localPick.set(this.localInUse());
    } catch (err: unknown) {
      this.listError.set(readIpcError(err).message);
    }
  }

  picked(row: VoiceRow): boolean {
    return (row.group === 'provider' ? this.providerPick() : this.localPick()) === row.id;
  }

  inUse(row: VoiceRow): boolean {
    return (row.group === 'provider' ? this.providerInUse() : this.localInUse()) === row.id;
  }

  pick(row: VoiceRow): void {
    (row.group === 'provider' ? this.providerPick : this.localPick).set(row.id);
    this.saved.set(false);
    this.saveError.set(null);
  }

  onKey(event: KeyboardEvent, row: VoiceRow, list: readonly VoiceRow[]): void {
    const step =
      event.key === 'ArrowDown' || event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowUp' || event.key === 'ArrowLeft'
          ? -1
          : 0;
    if (step === 0) return;
    event.preventDefault();
    const next = list[(list.indexOf(row) + step + list.length) % list.length];
    if (next === undefined) return;
    this.pick(next);
    setTimeout(() => document.getElementById(next.domId)?.focus(), 0);
  }

  toggleSample(row: VoiceRow): void {
    if (this.playing() === row.domId) {
      this.stopSample();
      return;
    }
    void this.playSample(row);
  }

  async save(): Promise<void> {
    const provider = this.providerSpeech() ? this.providerPick() : null;
    const local = this.localPick();
    this.saving.set(true);
    this.saveError.set(null);
    try {
      if (provider !== null)
        await this.bridge.vaultStore({ key: PROVIDER_VOICE_KEY, value: provider });
      if (local !== null) await this.bridge.vaultStore({ key: LOCAL_VOICE_KEY, value: local });
      if (provider !== null) this.savedProvider.set(provider);
      if (local !== null) this.savedLocal.set(local);
      this.saved.set(true);
    } catch (err: unknown) {
      this.saved.set(false);
      this.saveError.set(`Could not save the voice: ${readIpcError(err).message}`);
    } finally {
      this.saving.set(false);
    }
  }

  private stopSample(): void {
    this.generation += 1;
    this.player.stop();
    this.playing.set(null);
  }

  private async playSample(row: VoiceRow): Promise<void> {
    this.stopSample();
    const generation = this.generation;
    const { [row.domId]: _cleared, ...rest } = this.failed();
    this.failed.set(rest);
    this.playing.set(row.domId);
    const sample: LocalSpeakSample =
      row.group === 'provider' ? { provider: row.id } : { local: row.id };
    try {
      const audio = await this.bridge.localSpeak({ text: SAMPLE_SENTENCE, sample });
      if (generation !== this.generation) return;
      await this.player.play(audio);
    } catch (err: unknown) {
      if (generation !== this.generation) return;
      this.failed.set({ ...this.failed(), [row.domId]: readIpcError(err).message });
    } finally {
      if (generation === this.generation) this.playing.set(null);
    }
  }
}
