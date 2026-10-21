---
title: "Scout Desktop - Visual Interface Design Document: 4. Component Library"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Component library for Scout Desktop: Angular standalone, Signals-first components prefixed sc-, with inputs, states and accessibility notes."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 4. Component Library

All components are Angular standalone, Signals-first, and prefixed `sc-`. No zone.js-only patterns. All interactive components emit proper `ARIA` attributes.

---

### 4.1 `sc-button`

**Purpose:** All interactive triggers in the application.

**Variants and states:**

| Variant | Resting | Hover | Active | Disabled | Focus |
|---|---|---|---|---|---|
| `primary` | `accent` fill, `text-inverse` | `accent-hover` fill | 90% opacity | `text-disabled`, no fill | `border-strong` ring 2px |
| `secondary` | `border-subtle` border, transparent fill | `surface-raised` fill | 90% opacity | `text-disabled` | `border-strong` ring 2px |
| `ghost` | No border, transparent | `surface-raised` fill | 90% opacity | `text-disabled` | `border-strong` ring 2px |
| `destructive` | `error` fill, `text-inverse` | Darkened `error` | 90% opacity | `text-disabled` | `border-strong` ring 2px, `error` colour |

**Sizes:** `sm` (28px height), `md` (34px, default), `lg` (40px).

**Icon slot:** optional leading or trailing icon. Icon-only buttons must receive `aria-label`.

**External-link variant:** adds a small `arrow-up-right` SVG icon (12px) after the label. Separate from variant — combine with any variant using `[external]` input.

**Angular selector:** `sc-button` or `button[scButton]`

**Inputs:** `variant: 'primary' | 'secondary' | 'ghost' | 'destructive'`, `size: 'sm' | 'md' | 'lg'`, `loading: boolean`, `external: boolean`, `disabled: boolean`.

**Accessibility:** Uses native `<button>` element. When `loading` is true, renders a spinner and sets `aria-busy="true"` + `aria-label="Loading"`. Disabled via `disabled` attribute, not just visual style.

---

### 4.2 `sc-input`

**Purpose:** Text entry for all form fields.

**Sub-variants:**

- `text` — standard single-line input.
- `password` — masked, with reveal toggle (eye icon button). Toggle emits a Signal change, not a DOM type attribute swap, to avoid re-triggering browser password managers on toggle.
- `token-with-reveal` — extends `password` with paste detection hook and a "Copied from clipboard" inline indicator. The reveal button has `aria-label="Show token"` / `"Hide token"`.

**States:** default, focused, filled, error, disabled.

**Error state:** border becomes `error` colour, error message appears below via `sc-field-group` (not inside the input). No colour-only distinction — the `error` icon appears alongside the message text.

**Angular selector:** `sc-input`

**Inputs:** `type: 'text' | 'password' | 'token-with-reveal'`, `value: Signal<string>`, `placeholder: string`, `error: Signal<string | null>`, `disabled: boolean`.

**Accessibility:** `id` bound to a `<label for>` relationship. Error message has `role="alert"` and `aria-live="polite"`. Autocomplete set to `off` for token fields.

---

### 4.3 `sc-card`

**Purpose:** Groups related content with visual separation from the background.

**Anatomy:** `surface-raised` background, `border-subtle` 1px border, `radius-lg`, `shadow-raised` (dark only when floated). Optional header slot (title + optional badge), body slot, optional footer slot with action buttons.

**Variants:** `default` (standard), `interactive` (adds hover fill transition — used on platform cards), `inset` (for code/log blocks within a card).

**Angular selector:** `sc-card`

**Inputs:** `variant: 'default' | 'interactive' | 'inset'`, `padding: 'sm' | 'md' | 'lg'`.

---

### 4.4 `sc-platform-card`

**Purpose:** Represents one chat platform (Discord, Slack, Teams) on the Dashboard. Shows detection state, platform name, and primary action.

**Structure per card:**
- Platform logo/icon (SVG, platform brand colour)
- Platform name (`text-lg`)
- Detection badge (`sc-status-badge` variant: detected / not-detected)
- Status badge (`sc-status-badge` variant: connected / idle / error / coming-soon)
- Primary action button (`sc-button`)

**M1 states:**

| Platform | M1 Detection badge | M1 action |
|---|---|---|
| Discord | Detected / Not detected | "Provision Discord" (primary) |
| Slack | Detected / Not detected | "Coming soon" (ghost, disabled) |
| Teams | Detected / Not detected | "Coming soon" (ghost, disabled) |

"Coming soon" cards render with `text-muted` text and `warn` badge, not error. They are not interactive but are keyboard-focusable with `tabindex="0"` and `aria-disabled="true"` — users deserve to know they exist.

**Angular selector:** `sc-platform-card`

**Inputs:** `platform: 'discord' | 'slack' | 'teams'`, `detected: boolean`, `status: 'idle' | 'provisioning' | 'connected' | 'error' | 'coming-soon'`, `onAction: OutputEmitterRef<void>`.

---

### 4.5 `sc-step-indicator`

**Purpose:** Wizard progress, 4 steps for the Discord wizard.

**Appearance:** Horizontal strip of step nodes connected by thin lines. Each node is a circle (20px diameter) with a step number or a checkmark icon (completed steps). No colour-only distinction: completed steps show a checkmark icon; active step shows the step number in `accent` colour with `accent-muted` fill; future steps show the number in `text-muted` with no fill and `border-subtle` border.

**Step labels:** Optional text below each node, visible on `default` and `expanded` breakpoints, hidden at `compact`.

**Angular selector:** `sc-step-indicator`

**Inputs:** `steps: string[]`, `activeStep: Signal<number>` (zero-indexed).

**Accessibility:** `role="list"`, each step is a `role="listitem"`. Active step has `aria-current="step"`. Completed steps are labelled "Step N, completed".

---

### 4.6 `sc-status-badge`

**Purpose:** Compact status indicator used in platform cards, the titlebar, the bot-running screen.

**Variants:**

| Variant | Background | Text | Dot | Label |
|---|---|---|---|---|
| `idle` | `surface-overlay` | `text-muted` | `text-muted` | "Idle" |
| `provisioning` | `accent-muted` | `accent` | `accent` (animated pulse) | "Provisioning" |
| `connected` | `success-muted` | `success` | `success` | "Connected" |
| `error` | `error-muted` | `error` | `error` | "Error" |
| `coming-soon` | `warn-muted` | `warn` | none | "Coming soon" |
| `detected` | `success-muted` | `success` | none | "Detected" |
| `not-detected` | `surface-overlay` | `text-muted` | none | "Not detected" |

The `provisioning` dot pulses with a CSS animation. `prefers-reduced-motion: reduce` disables the pulse — the dot remains static. No other information is conveyed by the animation.

**Angular selector:** `sc-status-badge`

**Inputs:** `status: StatusVariant`, `label: string` (overrides default label if provided).

**Accessibility:** `aria-label` set to the full human-readable status (e.g. "Status: Connected"). Never icon-only.

---

### 4.7 `sc-log-viewer`

**Purpose:** Streaming log output from the Scout worker process and from the provisioning steps.

**Behaviour:**
- Monospace font (`text-xs`, `font-family: var(--font-mono)`), `surface-sunken` background.
- Lines rendered in a virtualised list (Angular CDK `VirtualScrollViewport`) to handle thousands of lines without DOM overhead.
- Auto-scroll active by default. When the user scrolls up manually, auto-scroll pauses and a "Jump to bottom" pill button appears floating at the bottom-right of the viewer.
- A "Pause" toggle button stops line appending without clearing the buffer. Useful when reading a specific line.
- Log levels colour-coded via text colour only (never background-only):
  - `DEBUG`: `text-muted`
  - `INFO`: `text-primary`
  - `WARN`: `warn`
  - `ERROR`: `error`
  - Level label is also printed as text in each line, so colour is a redundant cue.
- Timestamp column (`text-xs`, `text-muted`) fixed-width left column.
- "Clear" button clears the display buffer (does not delete the log file).
- "Copy all" copies the full buffer to clipboard, triggering a `sc-toast`.

**Angular selector:** `sc-log-viewer`

**Inputs:** `lines: Signal<LogLine[]>`, `autoScroll: boolean`, `maxLines: number` (default 5000).

**Accessibility:** `role="log"`, `aria-live="polite"`, `aria-label="Worker log output"`. The auto-scroll / pause state is announced to screen readers.

---

### 4.8 `sc-toast`

**Purpose:** Transient feedback messages. Stacked bottom-right, max 3 visible.

**Variants:** `info`, `success`, `warn`, `error`.

**Anatomy:** Leading icon (variant-specific), message text, optional action link (e.g. "Dismiss", "Open log"), close button.

**Timing:** `info` and `success` auto-dismiss after 4 seconds. `warn` after 6 seconds. `error` persists until explicitly dismissed.

**Angular selector:** `sc-toast-outlet` (host in app shell), `ScToastService` for imperative triggers.

**Accessibility:** `role="alert"` for `error`; `role="status"` for others. Stacked toasts are grouped in a `aria-live="assertive"` region.

---

### 4.9 `sc-code-block`

**Purpose:** Displaying generated commands, env var exports, OAuth URLs, config snippets.

**Anatomy:** Language label (`text-xs`, `text-muted`, top-left), code content (monospace, `surface-sunken`), copy button (top-right, icon + "Copy" label, collapses to icon-only on narrow blocks).

**Copy behaviour:** Clicking copies to clipboard. Button label changes to "Copied" for 2 seconds, then reverts. If the copy operation fails (permissions denied), a `warn` toast fires.

**Syntax highlighting:** Static, tokenised at render time using a minimal highlight library (Prism or a lightweight equivalent). Languages in scope for Scout: `bash`, `json`, `dotenv`. No runtime language autodetection.

**Angular selector:** `sc-code-block`

**Inputs:** `code: string`, `language: 'bash' | 'json' | 'dotenv' | 'text'`, `label: string` (optional override for language label).

---

### 4.10 `sc-empty-state`

**Purpose:** Placeholder when content is not yet available — no bots provisioned, empty log, no platforms detected.

**Anatomy:** Icon (SVG, `text-muted`, 40px), title (`text-lg`, `text-primary`), body (`text-sm`, `text-muted`), optional primary action button.

**Constraint:** No illustrations, no mascots. Icon is a simple line-art SVG from a consistent icon set (e.g. Lucide).

**Angular selector:** `sc-empty-state`

**Inputs:** `icon: string` (icon name from the scout icon set), `title: string`, `description: string`, `actionLabel: string | null`, `onAction: OutputEmitterRef<void>`.

---

### 4.11 `sc-dialog` / `sc-modal`

**Purpose:** Blocking overlay for confirmations, destructive actions, and any interaction that requires isolated focus.

**Anatomy:** `surface-overlay` background with `shadow-overlay`, `radius-lg`. Header (title + close button), body slot, footer (action buttons, right-aligned).

**Sizing:** `sm` (400px wide), `md` (560px, default), `lg` (720px for log viewers embedded in modals).

**Behaviour:** Opens with a 200ms fade + scale from 95% to 100%. Backdrop is `rgba(0,0,0,0.6)`. Pressing Escape closes, unless a destructive operation is in progress (close button greyed, Escape intercepted with a warning).

**Angular selector:** `sc-dialog`

**Inputs:** `title: string`, `size: 'sm' | 'md' | 'lg'`, `closeable: boolean`.

**Accessibility:** `role="dialog"`, `aria-modal="true"`, `aria-labelledby` bound to the title. Focus is trapped inside the dialog on open. Focus returns to the trigger element on close. First focusable element receives focus on open.

---

### 4.12 `sc-field-group`

**Purpose:** Wraps a form control with its label, help text, and error message. Ensures consistent layout and accessibility wiring across all form inputs.

**Anatomy:**
```
[Label text]          [optional "required" marker]
[Input / control slot]
[Help text — text-sm, text-muted]
[Error message — text-sm, error colour + icon]
```

Help text and error message are mutually exclusive in the vertical flow: error replaces help text when present.

**Angular selector:** `sc-field-group`

**Inputs:** `label: string`, `required: boolean`, `help: string | null`, `error: Signal<string | null>`.

The `id` of the inner control is automatically linked to the `<label for>` and `aria-describedby` (for help/error). This wiring is handled by the field-group component, not by each individual input.

---

### 4.13 `sc-switch`

**Purpose:** Binary toggle for settings (dark/light theme, log verbosity, auto-scroll).

**Appearance:** Pill-shaped track (32 × 18px), circular thumb (14px). Track uses `accent` when on, `border-subtle` when off. Thumb is always `surface-base` (white in dark mode). The switch does not rely on colour alone — the thumb position communicates state.

**Angular selector:** `sc-switch`

**Inputs:** `checked: Signal<boolean>`, `label: string`, `disabled: boolean`.

**Accessibility:** Renders as `<button role="switch">` with `aria-checked`. Label is rendered adjacent, not inside the button.

---

### 4.14 `sc-checkbox`

**Purpose:** Multi-select option, specifically used for Discord intent toggles in wizard step 2.

**Appearance:** 16 × 16px square, `radius-sm`. Unchecked: `border-subtle` border, transparent fill. Checked: `accent` fill, white checkmark SVG. Indeterminate: `accent` fill, horizontal dash icon.

**Angular selector:** `sc-checkbox`

**Inputs:** `checked: Signal<boolean>`, `indeterminate: boolean`, `label: string`, `help: string | null`, `disabled: boolean`.

**Accessibility:** Native `<input type="checkbox">` wrapped in a `<label>`. No custom ARIA required for the basic case.

---
