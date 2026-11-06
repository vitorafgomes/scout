---
title: "Teams adapter live: inbound endpoint spike"
version: "1.0"
date: 2026-10-28
updated: 2026-10-28
changes: "Initial spike page: research, options, recommendation, phased plan"
page_type: roadmap
status: active
description: "Research and design for making Microsoft Teams reach Scout on the owner's machine: what Teams needs, which .NET SDK is current, how to expose a local endpoint safely, streaming, packaging and what the desktop can automate."
source:
  - chat
  - src/Scout.Adapter.Teams/
  - src/Scout.Host.Console/Program.cs
  - https://learn.microsoft.com/en-us/azure/bot-service/abs-quickstart?view=azure-bot-service-4.0
  - https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication?view=azure-bot-service-4.0
  - https://learn.microsoft.com/en-us/azure/bot-service/what-is-new?view=azure-bot-service-4.0
  - https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-guidance
  - https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-dotnet
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/teams/sdk-comparison
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/teams/core-concepts
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/get-started/quickstart-register
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels
  - https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/migrations/v2-dotnet
  - https://learn.microsoft.com/en-us/microsoftteams/platform/agents-in-teams/quickstart-create-agent-teams-sdk
  - https://learn.microsoft.com/en-us/microsoftteams/platform/bots/streaming-ux
  - https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security
  - https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits
  - https://learn.microsoft.com/en-us/azure/azure-relay/relay-hybrid-connections-protocol
  - https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings
  - https://learn.microsoft.com/en-us/graph/api/teamsapp-publish?view=graph-rest-1.0
  - https://learn.microsoft.com/en-us/microsoft-365/extensibility/schema/root-bots?view=m365-app-1.19
  - https://azure.microsoft.com/en-us/pricing/details/bot-services/
  - https://ngrok.com/docs/pricing-limits/free-plan-limits
reliability: medium
---

# Teams adapter live: inbound endpoint spike

> When this is done, the owner installs Scout in Teams, messages it in a one-on-one chat, and gets a streamed answer from the worker on their own machine. The only door from the internet is one route that accepts nothing without a valid Bot Framework token.

## Why now

[[pages/services/adapter-teams]] holds a tested turn handler, translator and streaming sink. Nothing calls `TeamsChatAdapter.ProcessActivityAsync`: the webhook was left out on purpose (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:16`). So a configured Teams adapter receives nothing. Discord and Slack work because they open an outbound socket. Teams has no such mode (finding 1).

## Scope

- **In scope:** registering the bot, the inbound HTTPS path to the worker, validating each request, choosing the SDK, the app package and sideload, streaming, and what the desktop can automate.
- **Out of scope:** group chats and channels beyond answering @mentions (the translator already gates these), SSO or user OAuth, Teams Store publishing, meetings and calling, and production hosting outside the owner's machine.

## Findings

Each finding cites the page it rests on. "Unverified" means no official page confirms it.

1. **Teams only delivers by HTTPS POST to a public endpoint. There is no socket or long-poll mode.** "Teams, Agent 365, and other webhook callers deliver requests from their own servers, not a browser, so your endpoint must be reachable on the public internet over HTTPS" ([local tunnels](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels)). "Teams can only communicate with an agent runtime that's reachable from the public Internet" ([Teams SDK quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/agents-in-teams/quickstart-create-agent-teams-sdk)). No Microsoft page found offers a socket, pull or long-poll mode for Teams. The Bot Framework "Streaming Connections" feature is a different thing and was not carried into the Agents SDK ([migration guidance](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-guidance)).
2. **Registration no longer needs an Azure subscription.** The Teams Developer CLI (`@microsoft/teams.cli`) runs `teams app create --name <bot> --endpoint https://<tunnel-host>/api/messages`. It "provisions a Teams-managed bot by default, no Azure subscription required". It creates the client id, a Bot Framework registration and a client secret, and prints an "Install in Teams" sideload link ([core concepts](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/teams/core-concepts), [register quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/get-started/quickstart-register)). An Azure-managed bot (`--azure --subscription <id> --resource-group <rg>`) is needed only for OAuth or SSO. `teams app update <id> --endpoint ...` re-points the endpoint ([local tunnels](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels)).
3. **Azure Bot resources must be single-tenant or managed identity.** "Multi-tenant bot creation will be deprecated after July 31, 2025" ([abs-quickstart](https://learn.microsoft.com/en-us/azure/bot-service/abs-quickstart?view=azure-bot-service-4.0)). The same page says the single-tenant and managed-identity types are "not supported in ... Bot Framework Emulator, or Dev Tunnels". The newer Teams SDK quickstart pairs a CLI-registered bot with a dev tunnel. **That contradiction is unresolved.** Phase 1 tests it.
4. **Teams traffic costs nothing on Azure Bot Service.** "Standard channels include Microsoft first-party services (such as ... Microsoft Teams)", and those are unlimited on both tiers ([pricing](https://azure.microsoft.com/en-us/pricing/details/bot-services/)). No Microsoft page gives a price for Teams-managed bots. None was found.
5. **The Bot Framework SDK the adapter uses is unsupported.** "Support tickets for the Bot Framework SDK will no longer be serviced as of December 31, 2025". The SDK and the Emulator are archived on GitHub and get no product updates ([what's new](https://learn.microsoft.com/en-us/azure/bot-service/what-is-new?view=azure-bot-service-4.0)). Scout pins `Microsoft.Bot.*` 4.23.1 (`Directory.Packages.props:30`).
6. **Two SDKs are current.** The Microsoft 365 Agents SDK is "the evolution of the Azure Bot Framework SDK" and is multi-channel ([migration guidance](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-guidance)). The Teams SDK (formerly Teams AI library) is "Teams-first". Microsoft's rule of thumb is to use the Teams SDK when the bot uses most Teams-native features, and the Agents SDK when it uses few. Teams SDK 2.1 for .NET is GA with native ASP.NET Core (`AddTeamsBotApplication()` and `UseTeamsBotApplication()`, which maps `POST /api/messages`). It also has a compatibility package that runs an existing `IBot` unchanged ([SDK comparison](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/teams/sdk-comparison) and [2.0 to 2.1](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/migrations/v2-dotnet). The comparison page redirected when fetched, so its quotes come from search extracts. Reliability: medium).
7. **Inbound JWT validation is what decides whether a request is real.** The bot must check all of the following:
   - The bearer token's issuer is `https://api.botframework.com`.
   - Its audience is the bot's app id.
   - It is within its lifetime (5 min skew).
   - Its signature uses a key from `https://login.botframework.com/v1/.well-known/openidconfiguration`, with keys refreshed at least every 24 h.
   - Its `serviceUrl` claim matches the activity's `serviceUrl`.

   The doc adds that "Implementers shouldn't expose a way to disable validation of the JWT token" ([connector authentication](https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication?view=azure-bot-service-4.0)). The Bot Framework SDK did this inside its hosting stack. "The Agents SDK leaves HTTP authentication to ASP.NET": you copy `AspNetExtensions` and configure a `TokenValidation` section that has an `Enabled` flag ([.NET migration](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-dotnet)). The Teams SDK quickstarts use `skipAuth: true` for local playground testing ([Teams SDK quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/agents-in-teams/quickstart-create-agent-teams-sdk)). Each of these is a switch that turns validation off.
8. **Dev tunnels.** These are the Microsoft-recommended local tunnel.
   - **Persistence:** a persistent tunnel (`devtunnel create <id> --allow-anonymous`, `devtunnel port create <id> -p <port>`, `devtunnel host <id>`) keeps its hostname across restarts ([local tunnels](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels)).
   - **Access:** by default only the creator can connect. Teams needs `--allow-anonymous`, and then "Anyone with the URL can reach it ... It doesn't replace app-level auth: keep JWT/activity validation on" (same page).
   - **TLS and the interstitial:** TLS terminates at Microsoft's ingress. The anti-phishing interstitial is skipped for non-GET requests ([dev tunnels security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security)).
   - **Limits:** 5 GB bandwidth per user per month, 10 tunnels per user, 1500 HTTP requests/min per port ([service limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits)).
   - **Status and cost:** the feature is in public preview and "not recommended for production". No paid tier was found.
   - **Login:** `devtunnel user login` works with a Microsoft, Entra or GitHub account.
9. **ngrok.** The free plan has one dev domain, 1 GB/month out, 20,000 HTTP requests/month and 4,000 requests/min ([ngrok free limits](https://ngrok.com/docs/pricing-limits/free-plan-limits)). Microsoft's page says "ngrok's free tier assigns a new hostname every run" ([local tunnels](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/developer-tools/local-tunnels)). ngrok's own docs say the dev domain is reused on every run. We follow the vendor. The URL is public the same way an anonymous dev tunnel is.
10. **Azure Relay Hybrid Connections.** The listener opens an **outbound** WebSocket and receives HTTP request frames over it, so no inbound port and no public tunnel host are needed. A sender may put the Relay token in the query string (`https://{namespace}/{path}?sb-hc-token=...`). When it does, a separate `Authorization` header "is always passed on as-is", so the Bot Framework JWT still reaches the listener. Other limits:
    - Each request must be answered within 60 s.
    - Control-channel bodies are capped at 64 kB, and anything larger needs a rendezvous socket.

    Source: [Hybrid Connections protocol](https://learn.microsoft.com/en-us/azure/azure-relay/relay-hybrid-connections-protocol). A token in the messaging-endpoint URL would gate the URL itself. It is a long-lived SAS token stored in the bot registration, and whether the registration accepts such a URL is unverified. Relay needs an Azure subscription. **Pricing was not retrieved**: the pricing page returned 404.
11. **Sideloading is a tenant admin decision.** Custom app upload is controlled by:
    - an org-wide switch in the Teams admin center;
    - the "Upload custom apps" setting in an app setup policy;
    - a per-team setting.

    With the org-wide switch off, users can only submit apps for admin approval. A change can take up to 24 h ([custom app policies](https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings)). `teams login` reports `Sideloading: enabled|disabled` ([register quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/get-started/quickstart-register)).
12. **App manifest.** In `bots[]`, only `botId` and `scopes` (`personal`, `groupChat`, `team`, `copilot`) are required ([root.bots](https://learn.microsoft.com/en-us/microsoft-365/extensibility/schema/root-bots?view=m365-app-1.19)). Graph `POST /appCatalogs/teamsApps` uploads a zip to the tenant catalog. It works only with a delegated work account: `AppCatalog.Submit` only submits for admin review, and application permissions are "Not supported" ([publish teamsApp](https://learn.microsoft.com/en-us/graph/api/teamsapp-publish?view=graph-rest-1.0)).
13. **Streaming.** Source for this finding: [streaming-ux](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/streaming-ux).
    - **Where:** "Streaming agent messages are supported only in one-on-one chats", with one stream per chat at a time.
    - **Wire format:** `typing` activities carry a `streaminfo` entity (`informative` or `streaming`, with `streamSequence` starting at 1). The text must be cumulative. A final `message` with `streamType: final` closes the stream.
    - **Rate and time limits:** 1 request per second, and Microsoft recommends buffering tokens for 1.5 to 2 s. The stream has a hard two-minute limit.
    - **Errors:** `403 ContentStreamNotAllowed` covers several cases: the user pressed Stop, the time was exceeded, "Message size too large", or the stream already completed.
    - **Fit with the sink:** the sink already follows this shape (`src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:18`). It sends informative, then cumulative streaming, then final, with a 1 s floor (`:96`), and only in one-on-one chats (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:139`).
    - **Gaps:**
      - It treats every `ContentStreamNotAllowed` the same way (`:128`). After a timeout or size error, the answer should still go out as a plain message.
      - The informative "Working on it..." starts the two-minute clock.
      - The doc writes `streamInfo` in its table and `streaminfo` in its JSON. The sink uses `streaminfo` (`:48`).
      - Edit-in-place (`UpdateActivity`) is not needed.
14. **The worker has no HTTP server today.** `Scout.Host.Console` uses `Microsoft.NET.Sdk` and the generic host (`src/Scout.Host.Console/Scout.Host.Console.csproj:1`, `src/Scout.Host.Console/Program.cs:29`). An endpoint means adding Kestrel through a `Microsoft.AspNetCore.App` framework reference. Logs must keep going to stderr so `SCOUT_READY` stays alone on stdout (`src/Scout.Host.Console/Program.cs:39`).

## Options

| | A. Teams-managed bot + persistent dev tunnel + loopback Kestrel | B. Azure Bot + Azure Relay Hybrid Connection | C. Own relay (small cloud function forwarding over an outbound socket) |
|---|---|---|---|
| Who can reach the endpoint | Anyone with the tunnel URL. The JWT is the only gate. Local processes can reach the loopback port. | Only holders of the Relay token in the URL, and then the JWT. There is no listening port on the machine. | Whatever the relay enforces. It must validate the JWT itself or forward blindly. |
| Owner prerequisites | M365 work account with sideloading; `teams login`, `devtunnel user login` | The same, plus an Azure subscription, a Relay namespace and a single-tenant Azure Bot | The same as A, plus a cloud account and a deployment |
| Cost | No price found for Teams-managed bots; dev tunnels free within preview limits | Teams channel free ([pricing](https://azure.microsoft.com/en-us/pricing/details/bot-services/)); Relay listener-hours not retrieved | Hosting cost |
| Code in Scout | Kestrel, one route, SDK hosting, supervising `devtunnel host` | A Relay listener in place of Kestrel; token renewal | A whole service outside the repo's model; a second secret holder |
| Maturity | Dev tunnels preview, "not for production"; documented path in every Teams SDK quickstart | GA services; Bot-to-Relay URL with `sb-hc-token` unverified | Ours to maintain |
| Fit with "runs on the owner's machine" | Yes | Yes, but adds Azure | No: a public component Scout would ship |

**C is rejected.** It moves Scout's only internet-facing code off the owner's machine, and it keeps a credential in a place the owner does not run.

## Recommendation

Option A, decided in [[pages/decisions/0025-teams-inbound-endpoint]]:
- Register the bot with a Teams-managed registration through the `teams` CLI.
- Expose it through a persistent, anonymous dev tunnel the owner creates once.
- Serve exactly one route, `POST /api/messages`, from Kestrel bound to `127.0.0.1` inside the worker.
- Validate tokens with a **maintained** SDK, with no switch that turns validation off.

Keep B as the upgrade when the owner has an Azure subscription and wants the URL itself gated. The phase 1 spike picks the SDK (Teams SDK 2.1 or Agents SDK). The lean is the Teams SDK: Scout is Teams-only here, the Teams-managed bot is its documented backend, and it has a C# streaming writer. Either SDK's types stay inside `Scout.Adapter.Teams` ([[pages/invariants/pluggable-boundaries]]).

The archived `Microsoft.Bot.*` packages must not be the only validator of internet traffic: they get no fixes (finding 5).

## Security model

- **Classification:** the endpoint is ingress. Replies are descriptive writes into the originating conversation ([[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/invariants/no-executive-writes]]). Owner-initiated setup creates the registration and the tunnel. No LLM-invocable function can reach either.
- **Authenticity:** every request is checked against finding 7. The check fails closed: if the OpenID metadata or keys cannot be fetched, the request is rejected, never accepted unvalidated.
- **Tenant:** an activity whose `conversation.tenantId` differs from the configured `TenantId` is dropped. This is defence in depth on top of the audience check.
- **Who may talk to Scout:** the JWT proves Bot Framework sent the request, not who the user is. [[pages/decisions/0021-allowed-users]] still gates users by `from.id` (`29:` ids). Open question 3 asks whether connecting Teams should force the allow-list.
- **Secrets:** the client secret never lands in a repository file. The official quickstart writes it into `appsettings.json` (`teams app create --env appsettings.json`), which breaks [[pages/invariants/secrets-never-in-source]]. Scout points `--env` at a scratch file outside the repo, or the desktop captures the CLI output. The secret goes to User Secrets or the desktop vault and reaches the worker as `SCOUT__ADAPTERS__TEAMS__APPPASSWORD` (`src/Scout.Adapter.Teams/TeamsOptions.cs:20`). The CLI writes `ClientId`, `ClientSecret` and `TenantId`, while `TeamsOptions` uses `AppId`, `AppPassword` and `TenantId`. The adapter owns that mapping, so names are bound once.

## Phased plan

### Phase 1 - Throwaway spike outside the repository (smallest working path)
A console app in a scratch folder, like the ChatGPT spike. Placeholders only: no ids, tenant or tunnel names are written to the vault. It checks:
1. `teams login` reports sideloading, and `teams app create` produces a Teams-managed bot against a persistent dev tunnel.
2. Teams SDK 2.1 `UseTeamsBotApplication()` on `127.0.0.1:<port>` receives a one-on-one message. Record which component validates the JWT, and that a request with no token gets 401.
3. The outbound reply succeeds. Community reports of 401s on outbound replies from portal-only bots are unverified.
4. A 90 s streamed answer completes, and the Stop button yields `ContentStreamNotAllowed`.
5. Whether `streamInfo` and `streaminfo` are both accepted.
6. Whether the HTTP turn must return before the answer finishes: does a long turn time out or get redelivered?
7. The same through the Agents SDK only if step 2 fails.

### Phase 2 - Endpoint in the worker
- Add Kestrel to `Scout.Host.Console` with one route, bound to loopback, started only when `Scout:Adapters:Teams:AppId` is set.
- The new SDK's turn handler calls `TeamsChatAdapter.ProcessActivityAsync`. The translator and sink move to the new SDK's activity types.
- Drop `Microsoft.Bot.*` and Newtonsoft.
- Port and tenant come from configuration. There is no flag that disables validation.
- The sink falls back to a plain message on non-user `ContentStreamNotAllowed`.

### Phase 3 - Desktop setup
A Teams card in the desktop app:
- checks `teams` and `devtunnel` are installed;
- runs `devtunnel host <id>` as a second supervised process next to the worker;
- stores the secret in the vault and passes the four env vars;
- shows the install link.

### Phase 4 (optional) - Relay
Option B, behind the same route handler, if the owner wants no anonymous URL.

## What the desktop can automate, and what stays manual

- **Automatable:**
  - Driving `teams app create` and `teams app update --endpoint` as subprocesses after the owner logs in.
  - Creating, hosting and deleting the dev tunnel.
  - Building the manifest zip.
  - Storing the secret.
  - Showing the install link.
  - Restarting the worker.
- **Manual for the owner:**
  - A tenant admin enables custom app upload (finding 11), which can take up to 24 h.
  - `teams login` and `devtunnel user login` (interactive sign-in).
  - Clicking "Install in Teams", then **Add**.
  - Finding their `29:` user id for the allow-list.
- **Not proposed:** calling undocumented Developer Portal or Bot Framework registration endpoints directly. The CLI is the documented surface. Graph catalog upload needs a delegated admin-capable token and adds nothing over the sideload link for one owner.

## Test strategy

Rule B covers each edit. Rule I adds one failing-call test per new boundary. The endpoint tests run through ASP.NET Core `TestServer`, using a locally generated signing key and a stub OpenID metadata handler, with no network ([[pages/conventions/testing]]):
- **Missing, expired, wrong-audience, wrong-issuer or badly signed token:** 401, and the conversation handler is never called.
- **Metadata or keys endpoint:** on timeout or 5xx, the request is rejected (fail closed). On a later success, a valid token is accepted.
- **Activity from another tenant:** ignored, handler never called.
- **Outbound reply:** the token request fails (timeout or 5xx) or the connector returns 5xx. The asserted outcome is that the turn ends without an unhandled exception and logs one error with no secret.
- **Binding:** the endpoint binds only to a loopback address, and only `POST /api/messages` is mapped.
- **No bypass:** no configuration value disables validation. A test binds every documented switch (`TokenValidation:Enabled=false`, `skipAuth`) and still gets 401.
- **Sink:** a `403 ContentStreamNotAllowed` "exceeded streaming time" still delivers the full text as a plain message.
- **Desktop:** if `devtunnel host` exits, the card shows the error and the worker keeps running.

## Open questions for the owner

1. Which tenant: is it one where you can enable custom app upload, or a Microsoft 365 developer sandbox?
2. Is an anonymous dev tunnel URL acceptable, given that the JWT is the only gate? Or do you want option B (Azure subscription, Relay) from the start?
3. Should connecting Teams set `RequireAllowList` ([[pages/decisions/0021-allowed-users]])? Today, with only an API-key provider, anyone in the tenant who installs the app spends your credit.
4. One-on-one only at first, or also @mentions in group chats? Group chats get no streaming (finding 13).
5. Should the desktop drive the `teams` and `devtunnel` CLIs (new install prerequisites: Node for `teams`), or should you run them once by hand while Scout only stores the result?
