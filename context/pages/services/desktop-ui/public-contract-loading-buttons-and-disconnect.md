---
title: "Desktop UI: Public contract: Loading, buttons and Disconnect"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Loading, buttons and Disconnect."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Public contract

- **Loading, buttons and Disconnect** - while a card's check is still running its chip reads "Checking..." instead of Not connected: the Ollama and LM Studio scans (both start unknown, `ui/packages/renderer/src/app/sources/sources.component.ts:1421`), ChatGPT Plus until its status answers, OpenAI API and Google Gemini until the vault read ends, and TypeSafe until its key check ends (`:1748`). Each local card's scan is disabled only while its own scan runs and then reads "Scanning for Ollama..." or "Scanning for LM Studio..." with `aria-busy`; Ollama's reads Connect while it does not answer and Scan again once it does. A keyed card whose check is still running offers no Connect. LM Studio reads "Detected", not Connected, while its server answers, with "Scout cannot answer with LM Studio yet." (`ui/packages/renderer/src/app/sources/sources.component.ts:485`), because Scout has no LM Studio provider. The main action of a card (Connect, Save, Continue with ChatGPT, Manage local models, Add API key) is a Material filled 40 px pill on `--sc-accent-strong` under `--sc-on-accent`, content-wide; Change key, Disconnect (in `--sc-error`), Use, Scan again and Cancel are outlined pills, Sign out a text button in the accent (`:1241`). Disconnect on OpenAI API or Google Gemini opens "Disconnect <provider>?" (`:2041`), the shared confirmation with the artboard's sentence for that card, whose Cancel holds the focus (`ui/packages/renderer/src/app/core/confirm-dialog.component.ts:39`, `:112`); only Disconnect in the dialog deletes the key, and a confirmed Disconnect whose key was deleted says "<card> disconnected. The key was deleted from this computer." in a notice with Dismiss.
