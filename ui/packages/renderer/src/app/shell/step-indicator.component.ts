import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { IconComponent } from '../core/icon.component';
import {
  WIZARD_STEPS,
  WIZARD_STEP_LABELS,
  WizardStore,
  type WizardStep,
} from '../core/wizard.store';

interface StepView {
  readonly step: WizardStep;
  readonly label: string;
  readonly index: number;
  readonly state: 'done' | 'current' | 'future';
  readonly disabled: boolean;
}

const ENABLED_STEPS: ReadonlySet<WizardStep> = new Set<WizardStep>([
  'welcome',
  'provision',
  'token',
  'oauth',
]);

/** The setup steps in order: a done step with a check, the current one as a pill with its number filled, the rest numbered. */
@Component({
  selector: 'sc-step-indicator',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav aria-label="Setup progress">
      <ol class="steps">
        @for (view of views(); track view.step; let last = $last) {
          <li
            class="step"
            [attr.data-state]="view.state"
            [attr.data-disabled]="view.disabled ? 'true' : 'false'"
            [attr.aria-current]="view.state === 'current' ? 'step' : null"
          >
            <span class="marker">
              <span class="dot" aria-hidden="true">
                @if (view.state === 'done') {
                  <sc-icon class="check" name="check" [size]="14" />
                } @else {
                  <span class="num">{{ view.index + 1 }}</span>
                }
              </span>
              <span class="label">{{ view.label }}</span>
            </span>
            @if (!last) {
              <span class="dash" aria-hidden="true"></span>
            }
          </li>
        }
      </ol>
    </nav>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 16px 24px 4px;
        background: var(--sc-surface);
        border-bottom: 1px solid var(--sc-line);
      }
      .steps {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px 4px;
        margin: 0;
        padding: 0 0 12px;
        list-style: none;
      }
      .step {
        display: flex;
        align-items: center;
        gap: 4px;
        font-size: 13px;
      }
      .marker {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 4px 8px;
        border-radius: 16px;
        color: var(--sc-muted);
      }
      .dot {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 22px;
        height: 22px;
        border-radius: 12px;
        border: 1px solid var(--sc-line);
        font-size: 12px;
      }
      .step[data-state='done'] .marker {
        color: var(--sc-fg-2);
      }
      .step[data-state='done'] .dot {
        width: 24px;
        height: 24px;
        border: 0;
        background: var(--sc-ok-container);
        color: var(--sc-on-ok-container);
      }
      .step[data-state='current'] .marker {
        padding: 4px 10px 4px 4px;
        background: var(--sc-surface-2);
        color: var(--sc-fg);
        font-weight: 600;
      }
      .step[data-state='current'] .dot {
        width: 24px;
        height: 24px;
        border: 0;
        background: var(--sc-accent-strong);
        color: var(--sc-on-accent);
        font-weight: 700;
      }
      .dash {
        width: 16px;
        height: 1px;
        background: var(--sc-line);
      }
    `,
  ],
})
export class StepIndicatorComponent {
  private readonly wizard = inject(WizardStore);

  readonly views = (): readonly StepView[] => {
    const current = this.wizard.currentStep();
    const activeIndex = WIZARD_STEPS.indexOf(current);
    return WIZARD_STEPS.map((step, index): StepView => {
      const state: StepView['state'] =
        index < activeIndex ? 'done' : index === activeIndex ? 'current' : 'future';
      return {
        step,
        label: WIZARD_STEP_LABELS[step],
        index,
        state,
        disabled: !ENABLED_STEPS.has(step) && state !== 'current',
      };
    });
  };
}
