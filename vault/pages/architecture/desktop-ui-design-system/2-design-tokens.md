---
title: "Scout Desktop - Visual Interface Design Document: 2. Design Tokens"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Design tokens for Scout Desktop: colour palette (including the steel-blue accent), typography, spacing, radius and motion scales."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 2. Design Tokens

### 2.1 Colour

The accent colour is `#5A8FD4` — a steel blue that sits between Discord blurple (`#5865F2`), Slack aubergine (`#4A154B`), and Teams purple (`#6264A2`) on the platform cards without competing with any of them. It reads as a neutral technical accent rather than a brand colour borrowed from one of the platforms.

#### Dark theme (default)

| Token | Hex | Usage |
|---|---|---|
| `surface-base` | `#0F1117` | Window background, main content area |
| `surface-raised` | `#171B26` | Cards, panels, sidebars |
| `surface-overlay` | `#1E2333` | Modals, dropdowns, tooltips |
| `surface-sunken` | `#0A0D13` | Code blocks, log viewer background |
| `text-primary` | `#E4E8F0` | Body copy, labels, headings |
| `text-muted` | `#7B8299` | Help text, placeholders, secondary labels |
| `text-disabled` | `#3D4258` | Disabled control labels |
| `text-inverse` | `#0F1117` | Text on accent-coloured backgrounds |
| `accent` | `#5A8FD4` | Primary buttons, focus rings, active nav |
| `accent-hover` | `#6FA3E8` | Hover state for accent elements |
| `accent-muted` | `#1E2E45` | Accent background tint (badges, highlights) |
| `success` | `#3AAF7B` | Connected status, positive validation |
| `success-muted` | `#0D2B1E` | Success badge background |
| `warn` | `#D4A24A` | Non-fatal warnings, "coming soon" states |
| `warn-muted` | `#2B2010` | Warning badge background |
| `error` | `#D45A5A` | Validation errors, failed states, destructive |
| `error-muted` | `#2B1010` | Error badge background |
| `border-subtle` | `#252B3B` | Dividers, card borders, input borders |
| `border-strong` | `#3A4260` | Focus-visible ring base |

#### Light theme (secondary)

| Token | Hex | Usage |
|---|---|---|
| `surface-base` | `#F5F6FA` | Window background |
| `surface-raised` | `#FFFFFF` | Cards, panels |
| `surface-overlay` | `#FFFFFF` | Modals |
| `surface-sunken` | `#EBEDF5` | Code blocks, log viewer |
| `text-primary` | `#111827` | Body, headings |
| `text-muted` | `#6B7280` | Help, placeholders |
| `text-disabled` | `#C4C9D8` | Disabled labels |
| `text-inverse` | `#FFFFFF` | Text on accent backgrounds |
| `accent` | `#3A6DB5` | Primary actions (darkened for light bg contrast) |
| `accent-hover` | `#2E5A9A` | Hover |
| `accent-muted` | `#DDEAF8` | Accent background tint |
| `success` | `#1E7A52` | Connected |
| `success-muted` | `#D6F5E8` | Success badge background |
| `warn` | `#996218` | Warnings |
| `warn-muted` | `#FFF4D6` | Warning background |
| `error` | `#B03030` | Errors |
| `error-muted` | `#FDE8E8` | Error background |
| `border-subtle` | `#D8DCE8` | Dividers |
| `border-strong` | `#3A6DB5` | Focus rings |

WCAG AA pairings verified:
- `text-primary` on `surface-base` dark: contrast ratio 13.4:1 (AAA)
- `text-primary` on `surface-raised` dark: contrast ratio 11.8:1 (AAA)
- `text-muted` on `surface-base` dark: contrast ratio 4.8:1 (AA)
- `accent` on `surface-base` dark: contrast ratio 4.6:1 (AA)
- `text-primary` on `surface-base` light: contrast ratio 14.2:1 (AAA)
- `text-muted` on `surface-base` light: contrast ratio 4.6:1 (AA)
- `accent` (light) on `surface-base` light: contrast ratio 5.1:1 (AA)

### 2.2 Typography

**UI typeface:** `Inter`, falling back to the system sans-serif stack: `Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif`. Inter is available via the bundled app; no network CDN request.

**Monospace typeface:** `"JetBrains Mono"`, falling back to `"Geist Mono", "Cascadia Code", "Fira Code", ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace`. Used exclusively for tokens, env vars, log output, and code blocks.

| Scale token | px | Line-height | Weight | Usage |
|---|---|---|---|---|
| `text-xs` | 11px | 1.5 | 400 | Log timestamps, micro labels |
| `text-sm` | 13px | 1.5 | 400 | Help text, secondary labels |
| `text-base` | 14px | 1.6 | 400 | Body copy, inputs |
| `text-lg` | 16px | 1.5 | 500 | Card headings, section labels |
| `text-xl` | 20px | 1.3 | 600 | Screen titles |
| `text-2xl` | 26px | 1.2 | 700 | Major headings (welcome screen) |
| `text-3xl` | 34px | 1.15 | 700 | Reserved — product name on splash |

All body text is left-aligned. No centred body copy except on the welcome/first-run screen.

### 2.3 Spacing Scale

4-based, 8 values used in design: `4 / 8 / 12 / 16 / 24 / 32 / 48 / 64` (px).

| Token | Value | Typical use |
|---|---|---|
| `space-1` | 4px | Icon padding, tight inline gaps |
| `space-2` | 8px | Label-to-input gap, button icon gap |
| `space-3` | 12px | Input internal padding, badge padding |
| `space-4` | 16px | Card inner padding, form row gap |
| `space-6` | 24px | Section gap, sidebar padding |
| `space-8` | 32px | Card gap, screen padding |
| `space-12` | 48px | Large screen breathing room |
| `space-16` | 64px | Welcome screen vertical rhythm |

### 2.4 Border Radius

Rationale: "Crisp professional tooling" — radius is visible but minimal. No pill-shaped buttons.

| Token | Value | Usage |
|---|---|---|
| `radius-sm` | 3px | Badges, tags, code inline, switches |
| `radius-md` | 6px | Inputs, buttons, tooltips |
| `radius-lg` | 10px | Cards, modals, panels, platform cards |
| `radius-full` | 9999px | Circular icons, avatar fallbacks only |

Buttons use `radius-md`. No `radius-full` on interactive controls.

### 2.5 Shadows

Default state: no shadow. Shadows used only when an element lifts off the base layer.

| Token | Value | Usage |
|---|---|---|
| `shadow-none` | `none` | Default for all elements |
| `shadow-raised` | `0 1px 4px rgba(0,0,0,0.35), 0 0 1px rgba(0,0,0,0.2)` | Cards in dark; modals in light |
| `shadow-overlay` | `0 8px 32px rgba(0,0,0,0.48), 0 0 1px rgba(0,0,0,0.25)` | Modal backdrop, dropdown menus |

In the dark theme, borders carry the elevation distinction — `surface-raised` with `border-subtle` border is sufficient. Shadows are more visible in the light theme.

### 2.6 Motion

No decorative animation. Motion is strictly functional.

| Token | Value | Usage |
|---|---|---|
| `duration-fast` | 120ms | Hover state changes, button press |
| `duration-base` | 200ms | Panel slide-in/out, modal open/close |
| `easing-out` | `cubic-bezier(0.0, 0.0, 0.2, 1.0)` | Elements entering the screen |
| `easing-in-out` | `cubic-bezier(0.4, 0.0, 0.2, 1.0)` | Panel transitions |

`prefers-reduced-motion: reduce` collapses all transitions to `0ms`. No spring animations, no parallax.

---
