import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { IconComponent } from '../core/icon.component';
import { ConversationMode, type ConversationState } from './conversation-mode';

const STATE_LABELS: Readonly<Record<ConversationState, string>> = {
  off: '',
  listening: 'Listening',
  thinking: 'Thinking',
  speaking: 'Speaking',
};

function prefersReducedMotion(): boolean {
  try {
    return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
  } catch {
    return false;
  }
}

/**
 * The voice conversation button beside the dictation microphone: a round accent button with a sound wave. On, the wave
 * moves gently while Scout listens or speaks, unless the system asks for reduced motion, and the state is written
 * beside it: listening, thinking or speaking. While Scout listens, what it heard so far of the line being spoken is
 * written after the state.
 */
@Component({
  selector: 'sc-talk-button',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      class="talk"
      data-test="talk"
      [class.on]="mode.on()"
      [class.moving]="moving()"
      [attr.data-state]="mode.state()"
      [attr.aria-pressed]="mode.on()"
      [attr.aria-label]="mode.on() ? 'End the voice conversation' : 'Start a voice conversation'"
      [title]="title()"
      [disabled]="disabled() && !mode.on()"
      (click)="mode.toggle()"
    >
      <sc-icon class="glyph" name="sound-wave" [size]="16" />
    </button>
    @if (label(); as text) {
      <span class="state" data-test="talk-state" aria-live="polite">{{ text }}</span>
    }
    @if (mode.heard(); as partial) {
      <span class="heard" data-test="talk-heard" aria-live="polite">{{ partial }}</span>
    }
    @if (mode.error(); as reason) {
      <span class="talk-error" role="alert" data-test="talk-error">{{ reason }}</span>
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .talk {
        width: 32px;
        height: 32px;
        padding: 0;
        border: none;
        border-radius: 50%;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: inline-flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex: none;
        -webkit-app-region: no-drag;
      }
      .talk:focus-visible {
        outline: 2px solid var(--sc-focus);
        outline-offset: 2px;
      }
      .talk:disabled {
        opacity: 0.4;
        cursor: default;
      }
      .talk.moving .glyph {
        animation: sc-wave 1.1s ease-in-out infinite;
      }
      @keyframes sc-wave {
        0%,
        100% {
          transform: scaleY(1);
        }
        50% {
          transform: scaleY(0.65);
        }
      }
      .state {
        font-size: 12px;
        color: var(--sc-fg-2);
      }
      .heard {
        font-size: 12px;
        color: var(--sc-fg-2);
        font-style: italic;
        max-width: 24em;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .talk-error {
        font-size: 12px;
        color: var(--sc-error);
      }
    `,
  ],
})
export class TalkButtonComponent {
  protected readonly mode = inject(ConversationMode);
  /** Off while something else holds the microphone or no AI Source is connected; a conversation already on can always be ended. */
  readonly disabled = input(false);
  /** Why the button is off, given as its title and so as its description to a screen reader; shown only while it is off. */
  readonly offReason = input<string | null>(null);
  private readonly still = prefersReducedMotion();
  readonly label = computed(() => STATE_LABELS[this.mode.state()]);
  readonly title = computed(() => {
    if (this.mode.on()) return 'End the voice conversation (Esc)';
    const reason = this.offReason();
    return this.disabled() && reason !== null ? reason : 'Start a voice conversation';
  });
  readonly moving = computed(() => {
    const state = this.mode.state();
    return !this.still && (state === 'listening' || state === 'speaking');
  });
}
