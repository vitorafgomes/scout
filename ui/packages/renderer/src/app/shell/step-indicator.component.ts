import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { WIZARD_STEPS, WizardStore, type WizardStep } from '../core/wizard.store';

interface StepView {
  readonly step: WizardStep;
  readonly label: string;
  readonly index: number;
  readonly state: 'done' | 'current' | 'future';
  readonly disabled: boolean;
}

const STEP_LABELS: Readonly<Record<WizardStep, string>> = Object.freeze({
  welcome: 'Welcome',
  provision: 'Provision',
  token: 'Token',
  oauth: 'Authorize',
  launch: 'Launch',
  ready: 'Ready',
});

// Steps beyond these are placeholders and rendered as disabled until PR 10/11.
const ENABLED_STEPS: ReadonlySet<WizardStep> = new Set<WizardStep>([
  'welcome',
  'provision',
  'token',
  'oauth',
]);

@Component({
  selector: 'sc-step-indicator',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="steps" role="list" aria-label="Provisioning progress">
      @for (view of views(); track view.step) {
        <li
          class="step"
          role="listitem"
          [attr.data-state]="view.state"
          [attr.data-disabled]="view.disabled ? 'true' : 'false'"
          [attr.aria-current]="view.state === 'current' ? 'step' : null"
        >
          <span class="dot" aria-hidden="true">
            @if (view.state === 'done') {
              <span class="check">&#10003;</span>
            } @else {
              <span class="num">{{ view.index + 1 }}</span>
            }
          </span>
          <span class="label">{{ view.label }}</span>
        </li>
      }
    </ol>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 16px 32px;
        background: #0f1117;
        border-bottom: 1px solid #252b3b;
      }
      .steps {
        display: flex;
        align-items: center;
        gap: 12px;
        margin: 0;
        padding: 0;
        list-style: none;
        flex-wrap: wrap;
      }
      .step {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        padding: 4px 8px;
        font-size: 13px;
        color: #7b8299;
      }
      .step[data-state='done'] {
        color: #3aaf7b;
      }
      .step[data-state='current'] {
        color: #e4e8f0;
      }
      .step[data-state='future'][data-disabled='true'] {
        color: #3d4258;
      }
      .dot {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 20px;
        height: 20px;
        border-radius: 9999px;
        border: 1px solid #252b3b;
        background: transparent;
        font-size: 11px;
        font-weight: 600;
      }
      .step[data-state='current'] .dot {
        background: #1e2e45;
        color: #7c5cff;
        border-color: #7c5cff;
      }
      .step[data-state='done'] .dot {
        background: #0d2b1e;
        color: #3aaf7b;
        border-color: #3aaf7b;
      }
      .check {
        font-size: 12px;
      }
      .label {
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
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
        label: STEP_LABELS[step],
        index,
        state,
        disabled: !ENABLED_STEPS.has(step) && state !== 'current',
      };
    });
  };
}
