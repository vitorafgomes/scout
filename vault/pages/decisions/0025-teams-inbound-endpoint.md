---
title: "ADR 0025 - Teams reaches the worker through one validated loopback endpoint behind a persistent dev tunnel"
version: "1.0"
date: 2026-10-28
changes: "Initial ADR draft"
page_type: adr
status: proposed
description: "Scout receives Teams activities on a single POST /api/messages route bound to 127.0.0.1 in the worker, exposed by an owner-created persistent dev tunnel, registered as a Teams-managed bot, with Bot Framework JWT validation by a maintained SDK that cannot be switched off."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/roadmap/teams-adapter-spike.md
  - src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs
  - src/Scout.Host.Console/Program.cs
  - https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication?view=azure-bot-service-4.0
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/teams/core-concepts
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels
  - https://learn.microsoft.com/en-us/azure/bot-service/what-is-new?view=azure-bot-service-4.0
reliability: medium
---

# ADR 0025 - Teams reaches the worker through one validated loopback endpoint behind a persistent dev tunnel

> Teams talks to Scout through one route, `POST /api/messages`, on `127.0.0.1` inside the worker. An owner-created persistent dev tunnel exposes it. A maintained SDK validates the Bot Framework token on every request, and no setting turns that check off.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-28. Becomes accepted after phase 1 of [[pages/roadmap/teams-adapter-spike]] confirms the SDK choice and the owner answers its open questions.

## Context

[[pages/services/adapter-teams]] handles a turn once something hands it one. Nothing does: the webhook was left out (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:16`).

Unlike Discord and Slack, Teams only delivers by HTTPS POST to a public endpoint. No socket or long-poll mode was found. The worker runs on the owner's machine with no HTTP server (`src/Scout.Host.Console/Program.cs:29`).

The adapter is built on the Bot Framework SDK, which has had no support since 2025-12-31 and is archived. Its successors are the Microsoft 365 Agents SDK and the Teams SDK.

New Azure Bot resources must be single-tenant. A Teams-managed bot, registered with the Teams Developer CLI, needs no Azure subscription.

Evidence and links: [[pages/roadmap/teams-adapter-spike]].

## Decision

- **Registration:** a Teams-managed bot created with `teams app create --endpoint https://<tunnel-host>/api/messages`. An Azure-managed bot is used only if OAuth or SSO is ever needed. The client secret goes to User Secrets or the desktop vault and reaches the worker as `SCOUT__ADAPTERS__TEAMS__APPPASSWORD`. The CLI's `--env` never points at a file in the repository.
- **Exposure:** a persistent dev tunnel with anonymous access, created once by the owner and hosted by `devtunnel host <id>`. When it runs under the desktop app, the desktop supervises it.
- **Endpoint:**
  - The worker starts Kestrel only when `Scout:Adapters:Teams:AppId` is set.
  - Kestrel binds to `127.0.0.1` only and maps exactly one route, `POST /api/messages`.
  - Logs stay on stderr.
- **Validation:** a maintained SDK validates every request as the Bot Connector authentication page requires:
  - issuer `https://api.botframework.com`;
  - audience equal to the app id;
  - lifetime;
  - signature against the published OpenID keys;
  - the `serviceUrl` claim.

  Phase 1 picks the SDK: Teams SDK 2.1 is the lean, the Agents SDK the fallback. Validation **fails closed**: a metadata or key fetch failure rejects the request. **No configuration, flag or environment switches validation off.** That excludes the Agents SDK `TokenValidation:Enabled`, `skipAuth` and removing `RequireAuthorization()`, per "Implementers shouldn't expose a way to disable validation of the JWT token". Activities from a tenant other than the configured `TenantId` are dropped.
- **SDK boundary:**
  - The archived `Microsoft.Bot.*` packages leave the adapter in phase 2.
  - The new SDK's types stay inside `Scout.Adapter.Teams` ([[pages/invariants/pluggable-boundaries]]).
  - The handler forwards to `TeamsChatAdapter.ProcessActivityAsync` or its successor.
- **Classification:** the route is ingress. Every reply is a descriptive write into the originating conversation ([[pages/decisions/0009-descriptive-vs-executive-writes]]). The tunnel and the registration are owner-initiated setup, and no LLM-invocable function can reach them. [[pages/decisions/0021-allowed-users]] still decides which `29:` users get answers. The JWT proves who sent the request, not which person is asking.

## Consequences

- **Better:** Teams works with no Azure subscription and no public port on the machine. The internet-facing validator is maintained code. Streaming reuses the sink's informative, streaming, final sequence in one-on-one chats.
- **Worse:**
  - Anyone with the tunnel URL can send requests, so the JWT check is the only gate. A local process can reach the loopback port too.
  - Dev tunnels are a preview service "not recommended for production", with a 5 GB monthly bandwidth limit and 1500 requests/min per port.
  - The owner's tenant must allow custom app upload, which needs an admin.
  - The desktop gains a second supervised process.
- **Neutral but noteworthy:** the official quickstart writes the client secret into `appsettings.json`. Scout's setup must not. The CLI's key names (`ClientId`, `ClientSecret`) differ from `TeamsOptions` (`AppId`, `AppPassword`), and the adapter owns the mapping.

## Alternatives considered

- **Azure Relay Hybrid Connection:** the listener connects outbound, and a Relay token in the endpoint URL gates the address itself. It is rejected for now because it needs an Azure subscription, its pricing was not retrieved, and Bot Framework acceptance of a token-bearing URL is unverified. It stays the upgrade path behind the same handler.
- **ngrok:** it works the same way, with the same public exposure. It is rejected as the default only because dev tunnels are the documented Microsoft path and log in with the same account. Owners who prefer ngrok can point the endpoint at it.
- **Own relay in the cloud:** this puts internet-facing Scout code and a credential outside the owner's machine. Rejected.
- **Keep Bot Framework `CloudAdapter`:** the smallest diff, but it leaves the only validator of internet traffic on an archived SDK with no fixes. Rejected.
- **Status quo:** Teams stays unreachable. Rejected.

## Enforcement

Planned. None of these tests exist yet. All use `TestServer`, a local signing key and a stub OpenID handler ([[pages/conventions/testing]]):
- Missing, expired, wrong-audience, wrong-issuer or badly signed token gets 401, and the handler is never called.
- Metadata or keys timeout or 5xx gets the request rejected.
- No configuration value disables validation.
- An activity from another tenant is ignored.
- The listener is loopback-only, with exactly one mapped route.
- An outbound reply that fails (token or connector 5xx) ends the turn cleanly and logs no secret.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]], [[pages/invariants/pluggable-boundaries]].

## Related

- Services affected: [[pages/services/adapter-teams]], [[pages/services/host-console]], [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0011-backend-frontend-grpc-uds]], [[pages/decisions/0021-allowed-users]]
- Roadmap: [[pages/roadmap/teams-adapter-spike]]

## Notes

The Azure Bot quickstart says single-tenant bots are "not supported in ... Dev Tunnels", while the Teams SDK quickstart pairs a CLI-registered bot with a dev tunnel. Phase 1 tests this contradiction before acceptance. ADR 0011 is unaffected: the control plane stays on gRPC over a Unix socket, and this route is a separate listener for Teams only.
