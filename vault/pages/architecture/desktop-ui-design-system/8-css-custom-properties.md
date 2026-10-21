---
title: "Scout Desktop - Visual Interface Design Document: 8. CSS Custom Properties"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "CSS custom properties for Scout Desktop dark and light themes, applied via the data-theme attribute."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 8. CSS Custom Properties

```css
:root[data-theme="dark"] {
  /* Surfaces */
  --surface-base:     #0F1117;
  --surface-raised:   #171B26;
  --surface-overlay:  #1E2333;
  --surface-sunken:   #0A0D13;

  /* Text */
  --text-primary:     #E4E8F0;
  --text-muted:       #7B8299;
  --text-disabled:    #3D4258;
  --text-inverse:     #0F1117;

  /* Accent */
  --accent:           #5A8FD4;
  --accent-hover:     #6FA3E8;
  --accent-muted:     #1E2E45;

  /* Semantic colours */
  --success:          #3AAF7B;
  --success-muted:    #0D2B1E;
  --warn:             #D4A24A;
  --warn-muted:       #2B2010;
  --error:            #D45A5A;
  --error-muted:      #2B1010;

  /* Borders */
  --border-subtle:    #252B3B;
  --border-strong:    #3A4260;

  /* Shadows */
  --shadow-raised:    0 1px 4px rgba(0,0,0,0.35), 0 0 1px rgba(0,0,0,0.2);
  --shadow-overlay:   0 8px 32px rgba(0,0,0,0.48), 0 0 1px rgba(0,0,0,0.25);

  /* Typography */
  --font-ui:   Inter, -apple-system, BlinkMacSystemFont, "Segoe UI",
               Helvetica, Arial, sans-serif;
  --font-mono: "JetBrains Mono", "Geist Mono", "Cascadia Code",
               ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;

  /* Type scale */
  --text-xs:   11px;
  --text-sm:   13px;
  --text-base: 14px;
  --text-lg:   16px;
  --text-xl:   20px;
  --text-2xl:  26px;
  --text-3xl:  34px;

  /* Spacing */
  --space-1:   4px;
  --space-2:   8px;
  --space-3:   12px;
  --space-4:   16px;
  --space-6:   24px;
  --space-8:   32px;
  --space-12:  48px;
  --space-16:  64px;

  /* Radius */
  --radius-sm: 3px;
  --radius-md: 6px;
  --radius-lg: 10px;
  --radius-full: 9999px;

  /* Motion */
  --duration-fast: 120ms;
  --duration-base: 200ms;
  --easing-out:    cubic-bezier(0.0, 0.0, 0.2, 1.0);
  --easing-inout:  cubic-bezier(0.4, 0.0, 0.2, 1.0);
}

:root[data-theme="light"] {
  /* Surfaces */
  --surface-base:     #F5F6FA;
  --surface-raised:   #FFFFFF;
  --surface-overlay:  #FFFFFF;
  --surface-sunken:   #EBEDF5;

  /* Text */
  --text-primary:     #111827;
  --text-muted:       #6B7280;
  --text-disabled:    #C4C9D8;
  --text-inverse:     #FFFFFF;

  /* Accent */
  --accent:           #3A6DB5;
  --accent-hover:     #2E5A9A;
  --accent-muted:     #DDEAF8;

  /* Semantic colours */
  --success:          #1E7A52;
  --success-muted:    #D6F5E8;
  --warn:             #996218;
  --warn-muted:       #FFF4D6;
  --error:            #B03030;
  --error-muted:      #FDE8E8;

  /* Borders */
  --border-subtle:    #D8DCE8;
  --border-strong:    #3A6DB5;

  /* Shadows */
  --shadow-raised:    0 1px 3px rgba(0,0,0,0.10), 0 0 1px rgba(0,0,0,0.06);
  --shadow-overlay:   0 8px 24px rgba(0,0,0,0.14), 0 0 1px rgba(0,0,0,0.08);

  /* All other tokens (font, spacing, radius, motion) inherit from dark root */
}

@media (prefers-reduced-motion: reduce) {
  :root {
    --duration-fast: 0ms;
    --duration-base: 0ms;
  }
}
```

The theme is applied by setting `data-theme="dark"` or `data-theme="light"` on the `<html>` element. The Angular `ThemeService` writes this attribute and persists the choice to `localStorage`. On startup, the service reads `localStorage` first, then `prefers-color-scheme`, defaulting to `dark`.

---
