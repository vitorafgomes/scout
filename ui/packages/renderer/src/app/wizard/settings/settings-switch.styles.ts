/**
 * A Settings row with a slide toggle: the row's text on the left, the switch at the right, and Material's slide
 * toggle drawn as the artboard's 40 by 24 switch through its tokens.
 */
export const SETTINGS_SWITCH_STYLES = `
  .switch-row {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .switch-label {
    margin-right: auto;
  }
  mat-slide-toggle {
    flex: none;
    --mat-slide-toggle-track-width: 40px;
    --mat-slide-toggle-track-height: 24px;
    --mat-slide-toggle-track-outline-width: 1px;
    --mat-slide-toggle-selected-track-outline-width: 1px;
    --mat-slide-toggle-track-outline-color: var(--sc-line);
    --mat-slide-toggle-selected-track-outline-color: var(--sc-accent-strong);
    --mat-slide-toggle-handle-width: 24px;
    --mat-slide-toggle-handle-height: 16px;
    --mat-slide-toggle-unselected-handle-size: 16px;
    --mat-slide-toggle-selected-handle-size: 16px;
    --mat-slide-toggle-pressed-handle-size: 16px;
    --mat-slide-toggle-unselected-handle-horizontal-margin: 0 4px;
    --mat-slide-toggle-selected-handle-horizontal-margin: 0 4px;
    --mat-slide-toggle-unselected-pressed-handle-horizontal-margin: 0 4px;
    --mat-slide-toggle-selected-pressed-handle-horizontal-margin: 0 4px;
    --mat-slide-toggle-handle-elevation-shadow: none;
    --mat-slide-toggle-selected-track-color: var(--sc-accent-strong);
    --mat-slide-toggle-selected-hover-track-color: var(--sc-accent-strong);
    --mat-slide-toggle-selected-focus-track-color: var(--sc-accent-strong);
    --mat-slide-toggle-selected-pressed-track-color: var(--sc-accent-strong);
    --mat-slide-toggle-selected-handle-color: var(--sc-on-accent);
    --mat-slide-toggle-selected-hover-handle-color: var(--sc-on-accent);
    --mat-slide-toggle-selected-focus-handle-color: var(--sc-on-accent);
    --mat-slide-toggle-selected-pressed-handle-color: var(--sc-on-accent);
    --mat-slide-toggle-unselected-track-color: var(--sc-surface-2);
    --mat-slide-toggle-unselected-hover-track-color: var(--sc-surface-2);
    --mat-slide-toggle-unselected-focus-track-color: var(--sc-surface-2);
    --mat-slide-toggle-unselected-pressed-track-color: var(--sc-surface-2);
    --mat-slide-toggle-unselected-handle-color: var(--sc-muted);
    --mat-slide-toggle-unselected-hover-handle-color: var(--sc-muted);
    --mat-slide-toggle-unselected-focus-handle-color: var(--sc-muted);
    --mat-slide-toggle-unselected-pressed-handle-color: var(--sc-muted);
  }
`;
