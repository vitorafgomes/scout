---
title: "`Plugin.CloudStatus`"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: knowledge
status: stub
description: "Read-only cloud health plugin. Answers questions about whether Azure, AWS, and Oracle Cloud services are healthy, degraded, or down."
source:
  - "obsidian-vault/scout/03-domain/cloud-status.md"
reliability: medium
---

# `Plugin.CloudStatus`

Read-only cloud health plugin. Answers questions about whether Azure, AWS,
and Oracle Cloud services are healthy, degraded, or down.

## Responsibility

- Query cloud providers' public or authenticated health APIs.
- Return a normalized `StatusReport` regardless of provider.
- Optionally let the LLM rephrase the structured report into conversational text.

No side effects. Never posts, never opens tickets, never escalates.

## Supported providers (V1)

| Provider | API | Auth | Notes |
|---|---|---|---|
| Azure | Azure Resource Health + Service Health | Managed identity or SPN | Scoped to one subscription in V1 |
| AWS | AWS Health API | IAM user with `health:Describe*` | Business/Enterprise support required for private events; public status via `status.aws.amazon.com` |
| Oracle Cloud | OCI Status endpoint | None (public) | Per-region status only |
| Kubernetes | `ListNode` / `ListPod` via `KubernetesClient` SDK | kubeconfig file or in-cluster service account | Node readiness + pod phase / waiting reasons (CrashLoopBackOff / ImagePullBackOff / Pending). Configurable namespaces. |

## Semantic Kernel functions exposed

The plugin registers these SK functions so the planner can pick them:

- `get_cloud_health(provider, scope?)` — returns `StatusReport` for one provider.
- `get_all_cloud_health()` — returns a list of `StatusReport` across all configured providers.
- `get_incidents(provider, since?)` — returns recent active and resolved incidents.

All functions declare `Capabilities.ReadOnly = true` in the plugin metadata.

## Domain model

```csharp
public record StatusReport(
    string Provider,
    string Scope,              // subscription id / account / region
    HealthLevel Overall,       // Healthy | Degraded | Unhealthy | Unknown
    IReadOnlyList<ServiceStatus> Services,
    DateTimeOffset CheckedAt);

public record ServiceStatus(
    string Service,            // e.g. "Azure SQL", "AWS RDS"
    string Region,
    HealthLevel Level,
    string? Message);

public enum HealthLevel { Healthy, Degraded, Unhealthy, Unknown }
```

## Read-only enforcement

- No SDK call may use a verb other than `Get*`, `List*`, or `Describe*`.
- The plugin's `Capabilities` declares only read scopes. Registering it with
  any write scope fails at startup.
- A static analyzer rule in CI flags any call matching `Create*`, `Update*`,
  `Delete*`, `Put*`, `Post*` inside `Plugin.CloudStatus`.

## Configuration shape

```json
"Plugins": {
  "CloudStatus": {
    "Azure": {
      "SubscriptionId": "<from-env>",
      "TenantId": "<from-env>",
      "ClientId": "<from-env>",
      "ClientSecretEnvVar": "SCOUT_AZURE_CLIENT_SECRET"
    },
    "Aws": {
      "Region": "eu-west-1",
      "Profile": "scout-readonly"
    },
    "OracleCloud": {
      "Regions": [ "eu-london-1", "us-ashburn-1" ]
    }
  }
}
```

Secrets are never stored in `appsettings.json`. The config references
environment variable names; the actual values are injected at runtime.

## Error handling

- Provider API failure → return `HealthLevel.Unknown` for that provider with
  the error message in `ServiceStatus.Message`. Do not throw to the caller.
- Auth failure → log at warning, mark provider as `Unknown`, continue with
  other providers.
- Rate limit → exponential backoff with jitter, cap at 3 retries.

## Open questions

- Granularity: V1 returns region-level status. Should resource-level
  (e.g., specific RDS instance) be in V1 or pushed to V1.1?
- Caching: should the plugin cache responses for N seconds to protect
  against chat-flood-induced API throttling? If yes, where (in-memory vs.
  distributed)?
- Multi-subscription Azure: V1 = one subscription; V2 = list.

These are planning-time decisions, resolved during implementation planning.
