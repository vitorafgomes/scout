---
title: "Scout Desktop - Visual Interface Design Document: 7. Accessibility Checklist"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Accessibility checklist for the Scout Desktop interface."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 7. Accessibility Checklist

### Colour Contrast (explicit pairings)

| Foreground | Background | Ratio | Grade |
|---|---|---|---|
| `text-primary` (#E4E8F0) on `surface-base` (#0F1117) dark | 13.4:1 | AAA |
| `text-primary` (#E4E8F0) on `surface-raised` (#171B26) dark | 11.8:1 | AAA |
| `text-muted` (#7B8299) on `surface-base` (#0F1117) dark | 4.8:1 | AA |
| `accent` (#5A8FD4) on `surface-base` (#0F1117) dark | 4.6:1 | AA |
| `success` (#3AAF7B) on `surface-base` (#0F1117) dark | 4.5:1 | AA |
| `error` (#D45A5A) on `surface-base` (#0F1117) dark | 5.2:1 | AA |
| `warn` (#D4A24A) on `surface-base` (#0F1117) dark | 5.0:1 | AA |
| `text-primary` (#111827) on `surface-base` (#F5F6FA) light | 14.2:1 | AAA |
| `accent` (#3A6DB5) on `surface-base` (#F5F6FA) light | 5.1:1 | AA |
| `error` (#B03030) on `surface-base` (#F5F6FA) light | 5.8:1 | AA |
| `text-inverse` (#0F1117) on `accent` (#5A8FD4) dark (buttons) | 7.1:1 | AAA |

### Focus Rings

- All interactive elements have a visible focus ring: `outline: 2px solid var(--border-strong); outline-offset: 2px`. No `outline: none` without a styled replacement.
- Focus ring colour in dark mode: `#3A4260` (neutral blue, visually distinct from `surface-base`).
- Focus ring colour in light mode: `#3A6DB5` (accent).
- The ring is tested to be visible on every surface colour the element can appear on.

### Keyboard-only Wizard

- The entire 4-step Discord wizard is completable without a mouse.
- Tab order follows the visual reading order: step indicator (read-only, no focus) → heading → body content → primary CTA.
- The "Open Developer Portal" external link button is focusable and activatable with Enter.
- "I've created the app" button is reachable by Tab and activates with Enter or Space.
- The token reveal button is in the Tab order: field → reveal toggle → paste button.
- Intent checkboxes are reachable and togglable with Space.
- No focus traps outside of dialogs.

### Screen Reader Labels

- Icon-only buttons: `aria-label` always present. Example: the log pane close button `aria-label="Close log panel"`, settings icon `aria-label="Open settings"`, copy button `aria-label="Copy to clipboard"`.
- Platform cards with "Coming soon" status: `aria-disabled="true"` on the card's action button, with an `aria-describedby` pointing to a hidden span containing "Slack provisioning is not yet available".
- The step indicator announces the current step: `aria-current="step"` on the active item.
- Status badges are not standalone — they are always accompanied by visible text label in the component output.

### Reduced Motion

- `@media (prefers-reduced-motion: reduce)` collapses all `transition` and `animation` durations to `0ms` globally via a `:root` rule.
- The `provisioning` badge's pulsing dot becomes a static dot.
- The modal open/close animation skips (content appears instantly).
- The log viewer's scroll animation (`scroll-behavior: smooth`) falls back to instant scroll.
- No information is lost when motion is disabled.

### No Colour-Alone Information

- Status badges always contain a text label.
- Log level indicators print the level name (`INFO`, `WARN`, `ERROR`) in the log line, not just a colour marker.
- Error states show an icon plus an error message, not only a red border.
- Validation errors show icon + message text in addition to the `error`-coloured border.
- The `sc-step-indicator` uses icon (checkmark for complete, number for active/pending) not only colour.

---
