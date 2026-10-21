---
title: "ADR-002 - License choice (MIT)"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision to release Scout under the MIT license, and what the license choice affects."
source:
  - "obsidian-vault/scout/06-decisions/2026-04-21-license-mit.md"
reliability: medium
---

# ADR-002 - License choice (MIT)

## Status

Accepted — 2026-04-21

## Context

Scout is open-source. The license decision affects:

- What dependencies Scout can adopt (license compatibility).
- What consumers can do with Scout (redistribution, commercial use, embedding).
- Contributor expectations around patents and attribution.

The realistic candidates for a .NET OSS project of this shape are MIT and
Apache-2.0. Both are OSI-approved, permissive, and GPL-compatible.

## Decision

**MIT** — Copyright (c) 2026 Vitor Gomes and Scout contributors.

The full text lives in the source repository at `LICENSE`.

## Rationale

- **Ecosystem alignment.** Scout's core dependency, Microsoft Semantic Kernel,
  is MIT. The .NET OSS ecosystem at large (`dotnet/runtime`, `dotnet/aspnetcore`,
  `dotnet/efcore`, most community libraries) defaults to MIT.
- **Simplicity.** MIT is short, well-understood, and rarely triggers legal
  review inside companies. Contributors from corporate environments can clear
  it quickly.
- **Permissive.** Users can embed Scout, relicense derivatives, and use it
  commercially without extra obligations beyond preserving the copyright notice.

## Alternatives considered

- **Apache-2.0** — rejected. Apache provides an explicit patent grant, which
  matters for projects backed by companies that hold software patents. Scout
  is an individual-initiated OSS project; the patent clause adds weight
  without a matching benefit. Notably, MIT does not grant patent rights
  explicitly, but this has not been a practical issue for comparable projects
  in the .NET space.
- **AGPL-3.0 / GPL-3.0** — rejected. Copyleft would deter corporate adoption
  and friction against embedding Scout in internal tooling, which is the
  primary deployment shape. It would also be incompatible with several
  dependencies.
- **Dual-license (MIT + commercial)** — rejected. Not aligned with the
  project's positioning as a community-driven OSS tool.

## Consequences

- Contributions are accepted under MIT. No CLA is required; the PR template
  includes a short DCO-style confirmation.
- `LICENSE`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, and `CONTRIBUTING.md` live
  in the source repository.
- All first-party plugins and adapters inherit the MIT license unless they
  depend on incompatibly-licensed components (none planned).
- The license badge in the source `README.md` links to `LICENSE`.
