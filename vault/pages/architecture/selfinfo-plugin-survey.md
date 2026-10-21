---
title: "Self/Host-Info Plugin - Scope & Security Survey"
version: "1.0"
date: 2026-07-03
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: survey
status: proposed
description: "Scoping for a plugin (Scout.Plugin.SelfInfo, working name) that answers \"where and how is Scout installed\" - install path, version, OS, resources, active config, worker health."
source:
  - "obsidian-vault/scout/02-architecture/selfinfo-plugin-survey.md"
reliability: medium
---

# Self/Host-Info Plugin - Scope & Security Survey

Scoping for a plugin (`Scout.Plugin.SelfInfo`, working name) that answers
"where and how is Scout installed" — install path, version, OS, resources,
active config, worker health. Produced 2026-07-03 by a two-angle survey
(inventory of reportable facts + info-disclosure security review).

## The decisive finding first

This is a **chatops** plugin: a Scout bot sits in a Discord/Slack workspace, so
**any member could ask it and receive details about the host machine** (a
personal/work computer). Two hard constraints shape the whole design:

1. **Read-only is NOT the same as safe-to-expose.** The existing
   descriptive/read-only invariant ([[pages/decisions/0009-descriptive-vs-executive-writes]])
   is preserved (nothing is mutated), but host introspection introduces a **new
   confidentiality axis** it doesn't cover. We must add an explicit
   **"public-safe output"** invariant.
2. **SK plugins have no per-request identity today.** A `[KernelFunction]`
   receives no caller info — `ChatMessage` carries only `UserId`/`ChannelId`/
   `Text`, and the DM-vs-guild flag is computed and discarded inside
   `DiscordMessageTranslator`, never plumbed to the kernel. So the plugin
   **cannot enforce "DM-only" or "admin-allowlist"** by itself. The only gate
   available is the coarse `Enabled` flag; any enabled plugin is reachable by
   every workspace member via @-mention or DM. Prompt injection makes this
   worse (a crafted message can make the model call it) — a confused-deputy
   risk. **Redact at the source, never via system-prompt instructions.**

## Proposed functions (5)

| Function | Returns (public-safe form) |
|---|---|
| `where_installed` | logical install role: AppImage-mount vs plain install, "config from the standard per-user location" — **no absolute paths**, drive free space in coarse buckets |
| `system_info` | OS family/name (no build/kernel), .NET major (`net10`), architecture, CPU count, time zone, culture |
| `resource_usage` | coarse health: running/healthy, uptime bucket, memory bucket — **no PID, no exact bytes** |
| `active_configuration` | capability NAMES only (which adapters/plugins/LLM provider) — **behind the admin/DM gate**; never endpoints, never token presence, to general askers |
| `worker_status` | qualitative health + which chat adapters are connected (names only) |

## Inventory — reportable fields by sensitivity

- **none/low (public-safe):** Scout's own semver, OS family, .NET major, arch,
  RID, CPU count, time zone, culture, GC/memory buckets, uptime, LLM model name,
  hosting-environment name, active-plugin names, drive free space.
- **medium (redact/gate):** install & config **paths** (embed the OS username),
  machine name, OS username, knowledge-project names, exact process path.
- **high (never expose):** any `*Token`/`*Password`/`*Secret`/`*Key`/
  `*ConnectionString` value or **presence**, hostname, local IPs/MACs/interfaces,
  exact OS build/kernel/patch, endpoint URLs/hosts/ports.

(Full field→source→codeRef table in the survey transcript; sources are free
.NET/OS APIs — `RuntimeInformation`, `Environment`, `GC`, `DriveInfo` — plus
Scout config keys and `AppContext.BaseDirectory` /
`{LocalAppData}/Scout` paths.)

## Redaction rules (source-side, mandatory)

1. **Whitelist, never blacklist** — return a fixed curated set of public-safe
   fields; never reflect over the whole `IConfiguration`/`Environment` and
   dump-then-mask.
2. **Secrets are invisible** — never read/echo/mask/length-report any value
   under `*Token/*Password/*Secret/*Key/*ConnectionString` or `Scout:Adapters:*`;
   don't list secret keys or confirm which credentials are populated.
3. **Strip the OS username everywhere** — from paths and from
   `Environment.UserName`/hostname/machine name.
4. **Placeholder all paths** — collapse home-dir paths to `~`/`<install-root>`;
   never emit raw absolute paths or directory listings, only logical roles.
5. **Coarsen versions** — .NET major only, OS family/name only (no
   build/kernel), no third-party dep versions; Scout's own semver is fine.
6. **Omit all network identity** — no hostname, IPs, MACs, interfaces, or
   endpoint URLs/hosts/ports.
7. **Omit process internals** — no PID, no parent/other processes; health only
   in coarse buckets.
8. **Fail closed** — if a field can't be produced in redacted form, omit it.
   Every returned string must be safe assuming the least-privileged workspace
   member is reading it.

## Two paths

- **Path A — ship now (recommended):** redaction-first, **`Enabled=false` by
  default** (inverting the usual `true`), curated public-safe fields only,
  treat every response as world-readable. No reliance on who's asking.
- **Path B — richer detail (real paths, exact versions, which integrations
  configured):** requires a **prerequisite feature** first — plumb request
  identity into invocation (ambient `AsyncLocal` request-context or
  `KernelArguments` carrying `UserId`/`ChannelId`/`isDm`) + `AdminUserIds`/
  `DmOnly` options, then gate sensitive fields at call time and relax redaction
  only for admin/DM callers. Until that lands, cap the plugin at the public-safe
  subset.

## Recommendation

Build it as **Path A**: `Scout:Plugins:SelfInfo:Enabled=false` default, a
hand-curated public-safe whitelist, all fields through the redaction rules, and
a new **"public-safe output"** invariant recorded alongside "read-only". Do NOT
ship it as a general host-introspection endpoint. If the team wants the detailed
capability inventory / real diagnostics, schedule the **identity-plumbing +
admin gating** predecessor and gate the sensitive fields behind it — that is
also a reusable primitive future plugins (write-capable, per-user) will need.

## Follow-up decisions for the user

1. Path A now, or block on Path B's identity plumbing? (Recommend A now.)
2. Confirm the `Enabled=false` default (opt-in) for this one plugin.
3. Should `active_configuration`/`worker_status` be Path-A public (names only)
   or held for Path B (admin-gated)?

## Cross-refs

plugin-roadmap-daily-assistant (private note) · [[pages/domain/daily-assistant-plugins]] ·
ADR [[pages/decisions/0009-descriptive-vs-executive-writes]] · improvement-backlog (private note)
