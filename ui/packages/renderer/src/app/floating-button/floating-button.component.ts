import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';

import { ScoutBridgeService } from '../core/scout-bridge';

/**
 * The round Scout button in its own small always-on-top window; a click opens the
 * quick-ask window. The page around it is transparent, so only the circle shows.
 */
@Component({
  selector: 'sc-floating-button',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" aria-label="Ask Scout" data-test="floating-button" (click)="open()">
      <svg viewBox="0 0 24 24" width="26" height="26" aria-hidden="true" focusable="false">
        <path
          d="M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H8l-4 4V6a2 2 0 0 1 2-2Zm2 4v2h12V8Zm0 4v2h8v-2Z"
          fill="currentColor"
        />
      </svg>
    </button>
  `,
  styles: [
    `
      :host {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 64px;
        height: 64px;
        background: transparent;
      }
      button {
        width: 56px;
        height: 56px;
        border: none;
        border-radius: 50%;
        background: #2563eb;
        color: #ffffff;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        padding: 0;
      }
      button:hover {
        background: #1d4ed8;
      }
      button:focus-visible {
        outline: 2px solid #ffffff;
        outline-offset: -4px;
      }
    `,
  ],
})
export class FloatingButtonComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  ngOnInit(): void {
    document.documentElement.style.background = 'transparent';
    document.body.style.background = 'transparent';
    document.body.style.margin = '0';
    document.body.style.overflow = 'hidden';
  }

  open(): void {
    this.bridge.localOpen().catch(() => undefined);
  }
}
