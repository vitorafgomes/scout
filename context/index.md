---
title: "Scout Project Context Index"
version: "1.1"
date: 2026-10-21
changes: "Added the Desktop UI service page and the control-plane client initiative on 2026-10-22"
page_type: index
status: active
description: "The catalogue of Scout's context folder: every page, grouped by kind, with one line on what it covers."
updated: 2026-11-06
source:
  - chat
  - owner-notes (technical notes migrated 2026-10-21)
  - src/
  - tests/
reliability: high
---

# Scout Project Context Index

The content catalogue for this context folder. Start here, follow wikilinks. If the context folder can't answer, grep source, then `/wiki-crystallize` the findings back.

- Operation contract: [[CLAUDE]] (how Claude operates on this context folder)
- Page schema: [[wiki-schema]] | Config: [[wiki-config]]
- Templates: `templates/`

> **Coverage note (2026-10-21):** founded in one day. The pages under `services/`, `invariants/`,
> `conventions/` and `architecture/project-map` were written from the code at the founding commit
> and carry verified `file:line` citations (`reliability: high`). Everything else was migrated
> verbatim from the owner's notes dated 2026-04 to 2026-07 and was **not re-verified
> against the code** (`reliability: medium`): a `status: stub` or `proposed` there means the source
> note was still a draft. Where a migrated page and a code-derived page disagree, the code-derived
> page is the fact and the disagreement is a finding to log. The owner's private notes (plans,
> handoffs, sessions, audits) stay outside this repo.
>
> Known gaps the founding pass recorded, for the first review: the read-only contract is a
> convention, not an enforced allow-list ([[pages/invariants/no-executive-writes]]); secrets are kept
> out of source by `.gitignore` alone, with no scanning in CI ([[pages/invariants/secrets-never-in-source]]);
> the control plane mutates the host (Ollama models, the `ollama serve` process) on the user's
> behalf; ADR 0032's amendment of 2026-11-11 rules the download and delete of local models as a reviewed local write on the owner's click.

## Areas

Each area lists its own pages. Open the area you need.

| Area | Pages |
|---|---|
| [[pages/architecture/index\|Architecture]] | 41 |
| [[pages/conventions/index\|Conventions]] | 4 |
| [[pages/decisions/index\|Decisions]] | 55 |
| [[pages/domain/index\|Domain]] | 3 |
| [[pages/history/index\|History]] | 31 |
| [[pages/invariants/index\|Invariants]] | 6 |
| [[pages/overview/index\|Overview]] | 1 |
| [[pages/roadmap/index\|Roadmap]] | 21 |
| [[pages/runbooks/index\|Runbooks]] | 2 |
| [[pages/services/index\|Services]] | 25 |
