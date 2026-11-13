import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';

import { WavRecorder } from '../../quick-ask/wav-recorder';
import { AudioDevices, type AudioDevice } from '../../speech/audio-devices';
import { CONVERSATION_MICROPHONE_CONSTRAINTS } from '../../speech/conversation-mode';
import { MicrophoneTest, meterPosition } from '../../speech/level-meter';

export const DEVICE_LIST_FAILED_MESSAGE = 'Scout could not list the audio devices.';
export const HIDDEN_DEVICES_HINT =
  'Press and hold Test once so Scout can list your microphones and speakers.';

/**
 * Settings, Audio: the microphone every capture opens, the speaker read aloud plays on, and Test, which shows the
 * chosen microphone's level against the room and the speech threshold while it is held.
 */
@Component({
  selector: 'sc-audio-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" data-test="audio-section" aria-labelledby="s-audio">
      <h2 id="s-audio">Audio</h2>
      <p class="lede">
        The microphone Scout listens with, for dictation, conversation and meetings, and the speaker
        it reads answers aloud on.
      </p>
      <label class="field">
        <span>Microphone</span>
        <select data-test="audio-microphone" (change)="chooseMicrophone($any($event.target).value)">
          <option value="" [selected]="microphone() === ''">System default</option>
          @if (microphoneUnlisted()) {
            <option [value]="microphone()" selected>The chosen microphone</option>
          }
          @for (device of microphones(); track device.id) {
            <option [value]="device.id" [selected]="device.id === microphone()">
              {{ device.label }}
            </option>
          }
        </select>
      </label>
      @if (hidden()) {
        <p class="description" data-test="audio-hidden-hint">{{ hiddenHint }}</p>
      }
      @if (speakerSelectable) {
        <label class="field">
          <span>Speaker</span>
          <select data-test="audio-speaker" (change)="chooseSpeaker($any($event.target).value)">
            <option value="" [selected]="speaker() === ''">System default</option>
            @if (speakerUnlisted()) {
              <option [value]="speaker()" selected>The chosen speaker</option>
            }
            @for (device of speakers(); track device.id) {
              <option [value]="device.id" [selected]="device.id === speaker()">
                {{ device.label }}
              </option>
            }
          </select>
        </label>
      }
      <div class="test-row">
        <button
          type="button"
          class="btn"
          data-test="audio-test"
          title="Hold to hear the microphone's level"
          [attr.aria-pressed]="test.running()"
          (pointerdown)="startTest()"
          (pointerup)="stopTest()"
          (pointerleave)="stopTest()"
          (pointercancel)="stopTest()"
          (keydown.space)="pressKey($event)"
          (keyup.space)="stopTest()"
          (keydown.enter)="pressKey($event)"
          (keyup.enter)="stopTest()"
          (blur)="stopTest()"
        >
          Test
        </button>
        <div
          class="meter"
          role="meter"
          aria-label="Microphone level"
          aria-valuemin="0"
          aria-valuemax="100"
          data-test="audio-level"
          [attr.aria-valuenow]="percent(test.level())"
        >
          <div class="meter-fill" [style.width.%]="percent(test.level())"></div>
          @if (test.room(); as room) {
            <div
              class="mark mark--room"
              data-test="audio-room-mark"
              title="The room"
              [style.left.%]="percent(room)"
            ></div>
          }
          @if (test.threshold(); as threshold) {
            <div
              class="mark mark--speech"
              data-test="audio-speech-mark"
              title="Speech starts here"
              [style.left.%]="percent(threshold)"
            ></div>
          }
        </div>
      </div>
      <p class="hint">
        Hold Test and speak. The first second learns the room (the grey mark); Scout hears speech
        that passes the mark in the text colour. It stops after 10 seconds.
      </p>
      @if (listError(); as error) {
        <p class="error" role="alert" data-test="audio-list-error">{{ error }}</p>
      }
      @if (test.error(); as error) {
        <p class="error" role="alert" data-test="audio-test-error">{{ error }}</p>
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .card {
        background: var(--sc-surface);
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      h2 {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg-2);
        line-height: 1.5;
      }
      .description {
        font-size: 13px;
        color: var(--sc-muted);
        margin: 0;
      }
      .hint {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
        line-height: 1.5;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 13px;
      }
      .field > span {
        font-weight: 500;
      }
      .field select {
        height: 40px;
        padding: 0 12px;
        box-sizing: border-box;
        background: var(--sc-bg);
        border: 1px solid var(--sc-line);
        border-radius: 10px;
        color: var(--sc-fg);
        font: inherit;
      }
      .test-row {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .btn {
        height: 40px;
        padding: 0 16px;
        border-radius: 20px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        font: inherit;
        font-weight: 500;
        cursor: pointer;
        touch-action: none;
        user-select: none;
      }
      .btn[aria-pressed='true'] {
        border-color: var(--sc-accent-strong);
        background: var(--sc-surface-2);
      }
      .meter {
        position: relative;
        flex: 1;
        height: 12px;
        border-radius: 6px;
        background: var(--sc-surface-2);
        border: 1px solid var(--sc-line);
        overflow: hidden;
      }
      .meter-fill {
        height: 100%;
        background: var(--sc-accent-strong);
      }
      .mark {
        position: absolute;
        top: 0;
        bottom: 0;
        width: 2px;
      }
      .mark--room {
        background: var(--sc-muted);
      }
      .mark--speech {
        background: var(--sc-fg);
      }
      .error {
        color: var(--sc-error);
        font-size: 13px;
        margin: 0;
      }
    `,
  ],
})
export class AudioSettingsComponent implements OnInit {
  private readonly devices = inject(AudioDevices);
  readonly test = new MicrophoneTest(new WavRecorder(), () =>
    this.devices.open(CONVERSATION_MICROPHONE_CONSTRAINTS),
  );
  readonly speakerSelectable = this.devices.speakerSelectable();
  readonly microphones = signal<readonly AudioDevice[]>([]);
  readonly speakers = signal<readonly AudioDevice[]>([]);
  readonly microphone = signal(this.devices.microphone());
  readonly speaker = signal(this.devices.speaker());
  readonly listError = signal<string | null>(null);
  readonly hidden = signal(false);
  readonly hiddenHint = HIDDEN_DEVICES_HINT;
  readonly microphoneUnlisted = computed(
    () => this.microphone() !== '' && !this.microphones().some((d) => d.id === this.microphone()),
  );
  readonly speakerUnlisted = computed(
    () => this.speaker() !== '' && !this.speakers().some((d) => d.id === this.speaker()),
  );

  constructor() {
    const relist = (): void => void this.list();
    navigator.mediaDevices?.addEventListener?.('devicechange', relist);
    inject(DestroyRef).onDestroy(() => {
      navigator.mediaDevices?.removeEventListener?.('devicechange', relist);
      void this.test.stop();
    });
    let wasRunning = false;
    effect(() => {
      const running = this.test.running();
      untracked(() => {
        if (wasRunning && !running) void this.list();
        wasRunning = running;
      });
    });
  }

  ngOnInit(): void {
    void this.list();
  }

  chooseMicrophone(id: string): void {
    this.devices.setMicrophone(id);
    this.microphone.set(id);
  }

  chooseSpeaker(id: string): void {
    this.devices.setSpeaker(id);
    this.speaker.set(id);
  }

  startTest(): void {
    void this.test.start();
  }

  /** Space or Enter starts the test once per press; the key's auto-repeat never starts it again. */
  pressKey(event: Event): void {
    event.preventDefault();
    if (!(event as KeyboardEvent).repeat) this.startTest();
  }

  stopTest(): void {
    void this.test.stop();
  }

  percent(rms: number): number {
    return Math.round(meterPosition(rms) * 100);
  }

  private async list(): Promise<void> {
    try {
      const { microphones, speakers, hidden } = await this.devices.list();
      this.microphones.set(microphones);
      this.speakers.set(speakers);
      this.hidden.set(hidden);
      this.listError.set(null);
    } catch {
      this.listError.set(DEVICE_LIST_FAILED_MESSAGE);
    }
  }
}
