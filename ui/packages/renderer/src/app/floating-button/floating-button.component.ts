import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import { ScoutBridgeService } from '../core/scout-bridge';
import { BubbleStateStore, type BubbleState } from './bubble-state';

const SHOWN: Readonly<Record<BubbleState, { readonly label: string; readonly title: string }>> = {
  idle: { label: 'Ask Scout', title: 'Ask Scout' },
  listening: {
    label: 'Ask Scout, listening',
    title: 'Scout is listening. Open to see the transcript.',
  },
  answering: {
    label: 'Ask Scout, answering',
    title: 'Scout is answering. Open to follow the answer.',
  },
  failed: {
    label: 'Ask Scout, the last answer failed',
    title: 'Scout could not answer. Open to see why.',
  },
};

/**
 * The round Scout button in its own small always-on-top window; a click opens the quick-ask window.
 * The page around it is transparent, so only the circle shows. While a live listen is open it shows a
 * recording ring and dot, ahead of every other state so an open microphone is never hidden; otherwise,
 * from the conversation main sends to every window, a moving ring while Scout answers and a badge when
 * the last answer failed; a stop by the owner is not a failure. The pointer on the button asks main for the card
 * beside it, in a window of its own, and leaving the button hides it; the card's text is the button's description,
 * and the native title shows only while the card does not.
 */
@Component({
  selector: 'sc-floating-button',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (state() === 'answering') {
      <span class="ring" aria-hidden="true" data-test="floating-ring"></span>
    }
    @if (state() === 'listening') {
      <span class="ring recording" aria-hidden="true" data-test="floating-recording-ring"></span>
    }
    <button
      type="button"
      data-test="floating-button"
      aria-describedby="floating-card-text"
      [attr.aria-label]="shown().label"
      [attr.title]="cardShown() ? null : shown().title"
      (click)="open()"
      (pointerenter)="showCard()"
      (pointerleave)="hideCard()"
    >
      <svg
        viewBox="0 0 24 24"
        width="26"
        height="26"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
        focusable="false"
      >
        <rect x="4" y="8" width="16" height="12" rx="3"></rect>
        <path d="M12 8V4M9 4h6"></path>
        <circle cx="9" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
        <circle cx="15" cy="14" r="1.2" fill="currentColor" stroke="none"></circle>
      </svg>
    </button>
    @if (state() === 'failed') {
      <span class="badge" aria-hidden="true" data-test="floating-badge">!</span>
    }
    @if (state() === 'listening') {
      <span class="badge recording" aria-hidden="true" data-test="floating-recording-dot"></span>
    }
    <span id="floating-card-text" hidden>{{ store.card().title }}. {{ store.card().line }}</span>
  `,
  styles: [
    `
      :host {
        position: relative;
        display: flex;
        align-items: center;
        justify-content: center;
        width: 64px;
        height: 64px;
        background: transparent;
      }
      button {
        position: relative;
        width: 56px;
        height: 56px;
        border: none;
        border-radius: 50%;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        padding: 0;
      }
      button:hover {
        background: color-mix(in srgb, var(--sc-accent-strong) 86%, black);
      }
      button:focus-visible {
        outline: 2px solid var(--sc-on-accent);
        outline-offset: -4px;
      }
      .ring {
        position: absolute;
        left: 4px;
        top: 4px;
        width: 56px;
        height: 56px;
        border-radius: 50%;
        border: 3px solid var(--sc-accent);
        box-sizing: border-box;
        pointer-events: none;
        animation: sc-ring 1.4s ease-out infinite;
      }
      .ring.recording {
        border-color: var(--sc-recording);
      }
      @keyframes sc-ring {
        0% {
          transform: scale(1);
          opacity: 0.7;
        }
        100% {
          transform: scale(1.15);
          opacity: 0;
        }
      }
      .badge {
        position: absolute;
        right: 2px;
        top: 2px;
        min-width: 18px;
        height: 18px;
        padding: 0 4px;
        box-sizing: border-box;
        border-radius: 9px;
        background: var(--sc-danger);
        color: var(--sc-on-danger);
        font:
          700 11px/18px Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
        text-align: center;
        pointer-events: none;
      }
      .badge.recording {
        background: var(--sc-recording);
      }
      @media (prefers-reduced-motion: reduce) {
        .ring {
          animation: none;
          opacity: 0.5;
        }
      }
    `,
  ],
})
export class FloatingButtonComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly store = inject(BubbleStateStore);

  readonly listening = this.store.listening;
  /** Listening first; answering while a turn runs; failed when the last turn failed for any reason but the owner's stop. */
  readonly state = this.store.state;
  readonly shortcut = this.store.shortcut;
  /** True while main reports the card shown for the latest request. */
  readonly cardShown = signal(false);
  private cardRequests = 0;
  readonly shown = computed(() => {
    const shown = SHOWN[this.state()];
    const shortcut = this.shortcut();
    return this.state() === 'idle' && shortcut !== null
      ? { ...shown, title: `${shown.title} (${shortcut})` }
      : shown;
  });

  ngOnInit(): void {
    document.documentElement.style.background = 'transparent';
    document.body.style.background = 'transparent';
    document.body.style.margin = '0';
    document.body.style.overflow = 'hidden';
    this.store.start(this.destroyRef);
  }

  showCard(): void {
    this.askCard(true);
  }

  hideCard(): void {
    this.cardShown.set(false);
    this.askCard(false);
  }

  private askCard(shown: boolean): void {
    const request = ++this.cardRequests;
    this.bridge
      .localBubbleCard({ shown })
      .then((answer) => {
        if (request === this.cardRequests) this.cardShown.set(answer.shown);
      })
      .catch(() => {
        if (request === this.cardRequests) this.cardShown.set(false);
      });
  }

  open(): void {
    this.bridge.localOpen().catch(() => undefined);
  }
}
