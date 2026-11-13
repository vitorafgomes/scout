---
title: "ADR 0049 - Provider logos on the AI Source cards"
version: "1.0"
date: 2026-11-07
changes: "Initial ADR"
page_type: adr
status: accepted
description: "Every provider card on AI Sources shows its owner's official logo, unmodified, from a local file, in the owner's light and dark variants where it publishes them; Settings > About names each owner. The owner chose this knowing OpenAI, Google, Anthropic and Microsoft give no permission for this use and the repo is public; a logo is removed at once if its owner asks."
supersedes: ""
superseded_by: ""
source:
  - chat
  - https://openai.com/brand/
  - https://partnermarketinghub.withgoogle.com/brands/google/branding-guidelines/how-to-show-googles-brand/
  - https://www.anthropic.com/legal/trademark-guidelines
  - https://lmstudio.ai/brand
  - https://openrouter.ai/brand
  - https://github.com/ollama/ollama/tree/3d99d9779a2b095bfb1e5a025b990b835e89b829/docs
  - https://www.anthropic.com/press-kit
  - https://cdn.openai.com/brand/OpenAI-Logos-2025.zip
reliability: high
---

# ADR 0049 - Provider logos on the AI Source cards

> Each AI Source card shows the real logo of the provider it connects, as the owner's own file, never redrawn. The owners are named in About, and a logo goes the day its owner asks.

## Status

- [x] Proposed
- [x] Accepted

Updated: 2026-11-07. The owner, translated, on testing the build: the AI Sources icons are "all wrong"; asked how to handle the trademark rules, they chose "Real logo of each one".

## Context

Until now the AI Sources cards used the official OpenRouter glyph, a Simple Icons drawing of the Ollama mark, a generic glyph for LM Studio and monograms (O, C, G) for OpenAI, ChatGPT Plus and Google Gemini, because those owners' terms do not allow their logos in a third-party app without permission ([[pages/services/desktop-ui]]). The owners' rules, as read on 2026-11-07:

- **OpenAI (OpenAI API, ChatGPT Plus).** openai.com/brand: use the logo only when it directly relates to OpenAI services, do not imply endorsement, the permission is non-transferable and may be ended at any time (the page refuses automated reads; the terms come from search extracts and from Simple Icons, which removed OpenAI's icon for this reason).
- **Google (Gemini).** A Google product icon needs a Partner Marketing Hub account and approval; a text reference is free; never imply affiliation.
- **Anthropic (Claude).** anthropic.com/legal/trademark-guidelines: use only as permitted and in materials Anthropic approves beforehand; no change of colour, font or proportion; no implied endorsement.
- **Microsoft (Azure).** No Microsoft logo in any manner without an express licence. Scout has no Azure card, so no Microsoft logo is used.
- **LM Studio (Element Labs, Inc.).** lmstudio.ai/brand grants a non-exclusive, non-transferable, revocable permission to use the marks when referring to LM Studio, with the official assets, unredrawn, unrecoloured, not cropped or stretched, and without implying sponsorship. Its misuse list also says not to separate the app icon from the wordmark and not to place the logo too close to interface elements; the page offers the app icon as a download of its own, which is the file the card uses, and the owner accepted that a small icon on a card may still be read as too close to the interface.
- **OpenRouter.** openrouter.ai/brand: do not stretch, recolour or remix the marks.
- **Ollama.** No brand page or rules published; the maintainers share the SVG in their own repository.
- **TypeSafe.** No brand page, press kit or logo file published.

## Decision

- **The owner's official file, unmodified.** Every provider card shows its owner's logo from a local file under `ui/packages/renderer/src/assets/brands/`, byte for byte as downloaded: no recolouring, cropping, `viewBox` change or redraw. The page's CSP loads no remote image, so every file is bundled.
- **Light and dark.** Where the owner publishes a black and a white variant, the dark theme shows the white one and the light theme the black one; where it publishes one variant (Gemini's spark, LM Studio's colour app icon, Claude's spark) that file serves both. Nothing is recoloured to fit a theme.
- **Before a file is bundled** it is downloaded into its own empty folder and inspected: no script, no event attribute, no `foreignObject`, no reference outside the file. A spec checks the same of every file in the folder.
- **About.** Settings > About names each owner ("OpenAI and ChatGPT are trademarks of OpenAI.", "Google and Gemini are trademarks of Google.", "Anthropic and Claude are trademarks of Anthropic.", "OpenRouter is a trademark of OpenRouter, Inc.", "Ollama is a trademark of Ollama, Inc.", "LM Studio is a trademark of Element Labs, Inc."), says third-party logos are not under Scout's MIT licence, and that their use identifies the service the user connects and implies no endorsement.
- **TypeSafe** keeps a generic shield, since it publishes no logo. Local Models and Custom API are not a provider's and keep generic icons.
- **A logo is removed at once if its owner asks**, replaced by the provider's name, without waiting for a release cycle.

### Where each file came from (all downloaded 2026-11-07)

| Card | File in `assets/brands/` | Source | Kind |
|---|---|---|---|
| OpenAI API, ChatGPT Plus | `openai-blossom-white.svg`, `openai-blossom-black.svg` | OpenAI's logo pack, https://cdn.openai.com/brand/OpenAI-Logos-2025.zip (`SVGs/OpenAI-white-monoblossom.svg`, `SVGs/OpenAI-black-monoblossom.svg`) | official file |
| Google Gemini | `gemini-spark.svg` | the logo gemini.google.com declares as its `og:logo`, https://www.gstatic.com/lamda/images/gemini_sparkle_aurora_33f86dc0c0257da337c63.svg | official site's own file |
| Claude API (card added by its own change) | `claude-spark-clay.svg` | Anthropic's press kit, https://www.anthropic.com/press-kit (`Anthropic logos/Claude logos/3 Claude Spark/SVG/Claude Spark - Clay.svg`) | official file |
| Ollama | `ollama-black.svg`, `ollama-white.svg` | Ollama's repository, docs folder at commit `3d99d9779a2b095bfb1e5a025b990b835e89b829`: https://raw.githubusercontent.com/ollama/ollama/3d99d9779a2b095bfb1e5a025b990b835e89b829/docs/ollama-logo.svg and `.../docs/favicon.svg` | official file |
| LM Studio | `lm-studio-icon-color.svg` | LM Studio's brand page, https://lmstudio.ai/assets/marketing/brand/download/logos/lm-studio-icon-color.svg | official file |
| OpenRouter | `openrouter-glyph-cloud.svg`, `openrouter-glyph-ink.svg` | openrouter.ai/brand, kept from the earlier change | official file |

No Simple Icons drawing is used. The Ollama pair are two crops of the same drawing in Ollama's docs folder, so the mark's size shifts slightly between themes. The Gemini file embeds its gradient as a `data:image/jpeg` inside the SVG, which is not an outside reference.

## Consequences

- **Better:** each card is recognisable at a glance, in both themes, and the owners are named.
- **Worse, accepted by the owner:** OpenAI, Google and Anthropic have not given permission for this use, and the repo is public, so the files are redistributed with every clone and build. OpenAI's permission is described as non-transferable, Google's and Anthropic's need prior approval. Any of them may ask for removal; the answer is to remove that logo at once.
- The Models → Analysis groups still draw their own logos and may differ from AI Sources until that screen is aligned.
