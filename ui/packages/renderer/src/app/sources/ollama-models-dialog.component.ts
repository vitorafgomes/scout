import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';

import { LocalModelsComponent } from './local-models.component';

/** What the Ollama models dialog needs from AI Sources: what Use this on a model does once its model is saved. */
export interface OllamaModelsDialogData {
  readonly chosen: () => void;
}

/** The Ollama models on this computer, opened from the Ollama card's Manage local models. */
@Component({
  selector: 'sc-ollama-models-dialog',
  standalone: true,
  imports: [LocalModelsComponent, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section
      id="ollama-models"
      class="ollama-models"
      aria-labelledby="lm-title"
      aria-describedby="lm-desc"
      data-test="ollama-models"
    >
      <div class="head">
        <h2 id="lm-title">Local models in Ollama</h2>
        <button
          type="button"
          class="close"
          mat-dialog-close
          aria-label="Close"
          title="Close"
          data-test="ollama-models-close"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
        </button>
      </div>
      <p id="lm-desc" class="desc">
        The models installed in Ollama on this computer. They appear in Models, Analysis, under
        Ollama.
      </p>
      <sc-local-models kind="analysis" [embedded]="true" (ollamaModelChosen)="data.chosen()" />
    </section>
  `,
  styles: [
    `
      .ollama-models {
        display: flex;
        flex-direction: column;
        gap: 14px;
        padding: 20px;
        background: var(--sc-surface);
        color: var(--sc-fg);
        font-size: 14px;
        max-height: calc(100vh - 200px);
        overflow-y: auto;
        box-sizing: border-box;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      h2 {
        margin: 0;
        margin-right: auto;
        font-size: 17px;
        font-weight: 600;
      }
      .close {
        width: 36px;
        height: 36px;
        border-radius: 18px;
        border: 1px solid var(--sc-line);
        background: transparent;
        color: var(--sc-fg);
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
      }
      .close svg {
        width: 16px;
        height: 16px;
        fill: none;
        stroke: currentColor;
        stroke-width: 2;
        stroke-linecap: round;
      }
      .desc {
        margin: 0;
        font-size: 13px;
        line-height: 1.5;
        color: var(--sc-fg-2);
      }
    `,
  ],
})
export class OllamaModelsDialogComponent {
  readonly data = inject<OllamaModelsDialogData>(MAT_DIALOG_DATA);
}
