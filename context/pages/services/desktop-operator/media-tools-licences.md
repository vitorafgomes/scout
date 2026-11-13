---
title: "Desktop operator window: Media tools: Licences"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop operator window service page."
page_type: service
status: active
description: "Read when a change touches: Licences."
source: - "context/pages/services/desktop-ui.md" - ui/packages/main/src - ui/packages/renderer/src/app/production - ui/packages/renderer/src/app/assembly - ui/packages/renderer/src/app/reports - ui/packages/renderer/src/app/experiments - ui/packages/renderer/src/app/comments - ui/build
reliability: high
---

## Media tools

- **Licences.** `LICENSE.txt` beside the programs is the archive's (GNU LGPL version 3), `NOTICE.txt` names where the source of that exact build is, and `OFL.txt` sits beside the fonts. A new build is a new table row: the tag, URL, SHA-256, folder, version and source change together, and the cache key with them.
