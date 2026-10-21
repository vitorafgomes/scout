---
title: "ScoutHost.ResolvedKernel accessor for dynamic plugin registration"
version: "1.0"
date: 2026-05-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision to add a ResolvedKernel accessor on ScoutHost to support dynamic plugin registration."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-23-scout-host-kernel-accessor.md"
reliability: medium
---

# ScoutHost.ResolvedKernel accessor for dynamic plugin registration

## Context

`Scout.Core.Hosting.ScoutHost` builds its Semantic Kernel lazily inside
`BuildKernel()`. The resulting `Kernel` is stored in a private
`Lazy<Kernel>` and not registered in the DI container. Today every
`IScoutPlugin` is registered statically at startup through
`PluginRegistry`, which feeds `BuildKernel()` once; nothing in the
worker mutates `kernel.Plugins` after host construction.

The Features feature (see Phase 1 plan and the upcoming
`Scout.Plugin.GitHub` / `Scout.Plugin.Kubernetes` work) needs to add and
remove `KernelPlugin` instances **at runtime**, in response to the user
flipping toggles on the Features screen. The new
`IFeatureKernelRegistrar` (worker side) must therefore reach the same
`Kernel` that `LlmFallbackRouter` already consumes, and mutate its
`Plugins` collection.

Three reachable designs were on the table. The user approved Option A
(see [[pages/decisions/0009-descriptive-vs-executive-writes]] for the broader
Features-feature context).

## Decision

Add a single public read-only property on `Scout.Core.Hosting.ScoutHost`:

```
/// <summary>
/// The runtime Semantic Kernel built lazily on first access. Stable
/// reference for the lifetime of the host; safe to retain and mutate
/// .Plugins from external callers that own the synchronisation
/// (see Scout.Host.Worker.Features.IFeatureKernelRegistrar).
/// </summary>
public Kernel ResolvedKernel => _kernel.Value;
```

The accessor returns the same instance `BuildKernel()` already returns
to internal consumers. First access forces the lazy initialisation; all
subsequent calls return the cached reference.

External mutation of `ResolvedKernel.Plugins` is **only** permitted
through `IFeatureKernelRegistrar`, which serialises register/unregister
operations behind a `SemaphoreSlim(1, 1)`. `KernelPluginCollection` is
not thread-safe for concurrent mutation and the registrar is the single
chokepoint that guarantees this.

## Alternatives considered

### Option B — Register `Kernel` in the DI container from `AddScout`

Cleaner long-term: every consumer would inject `Kernel` directly and
the lazy hoop in `ScoutHost` could be removed. Rejected for Phase 1
because it ripples through:

- `Scout.Plugin.Chat.LlmChatServiceCollectionExtensions` currently
  builds its own throwaway kernel at registration time to extract
  `IChatCompletionService`. Reworking this without breaking the chat
  fallback router shipped in PR #48 is a bigger change than the
  Features feature warrants.
- Every `IScoutPlugin` would need to be revisited to confirm it does
  not assume a particular kernel construction order.
- The PR scope explodes from "Features foundation" to "kernel
  injection refactor", which delays the user-facing feature without
  changing what the user sees.

Revisit when the next non-trivial kernel consumer lands.

### Option C — Separate dynamic-kernel container

Plugins added by the Features feature live in their own
`KernelPluginCollection`, queried alongside the static one. Rejected
because it forces plugin authors to learn two parallel registration
paths and complicates `LlmFallbackRouter` (which currently looks at a
single kernel for function calling). Splits a single conceptual
namespace ("the LLM's tool surface") into two for no user-visible
benefit.

## Consequences

### Positive

- **One-line public-API addition on `Scout.Core`.** Minimal blast
  radius. Existing consumers untouched.
- **Backwards compatible.** Internal callers can keep using their
  private path; the accessor is purely additive.
- **Single source of truth for the LLM's tool surface.** Static
  `IScoutPlugin` plugins and dynamic Features plugins share one kernel,
  one plugin collection, one function-calling decision tree.
- **Cheap to retract.** If a future Option B refactor lands, the
  accessor can become `[Obsolete]` and consumers migrate one-by-one.

### Negative

- **External mutation of internal state.** The `Kernel` is conceptually
  internal to `ScoutHost`, and exposing it as a property lets any
  consumer mutate `.Plugins` without going through the registrar.
  Mitigation: `IFeatureKernelRegistrar` is the only documented mutator
  in this PR, the accessor's XML doc cites it explicitly, and the
  `architect-reviewer` agent is required on the PR by Scout's CLAUDE.md
  quality gate.
- **Surface area on a stable type.** Future refactors that change how
  the kernel is built (e.g. multi-kernel routing per chat session)
  must either keep this accessor honest or deprecate it deliberately.

### Operational

- The PR introducing this accessor (`feat/features` Phase 1) must run
  the `architect-reviewer` agent because it touches `Scout.Core`. The
  agent is wired into the Scout PR template's quality gates and the
  reviewer-author flow.
- `Scout.Core.Tests` gains one assertion: `ResolvedKernel` returns the
  same non-null instance on first and second access (idempotency).
- Internal callers may eventually migrate to read through
  `ResolvedKernel` for consistency, but that is non-blocking cleanup.

## References

- Triggering work: the Features feature, foundation phase, which adds
  `IFeatureKernelRegistrar` in `Scout.Host.Worker.Features` and is the
  first non-static consumer of `ScoutHost.ResolvedKernel`.
- Related ADR: [[pages/decisions/0009-descriptive-vs-executive-writes]] (the
  invariant refinement that unlocks the GitHub plugin's descriptive
  writes; consumes this accessor indirectly via the registrar).
- Scout's PR template quality gate 3:
  `architect-reviewer` agent required for any change touching
  `Scout.Core` contracts, plugin registry, LLM provider abstraction,
  or chat adapter abstraction.
