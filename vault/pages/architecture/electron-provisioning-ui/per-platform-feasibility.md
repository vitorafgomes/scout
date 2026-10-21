---
title: "Electron Provisioning UI - feasibility study: Per-platform feasibility"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Per-platform feasibility of automating bot provisioning for Discord, Slack and Microsoft Teams, with the manual steps each platform still requires."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Per-platform feasibility

### Discord

**Detection.** The Discord desktop app installs to predictable paths:

- Windows: `%LOCALAPPDATA%\Discord\` with an `Update.exe` shim and
  versioned `app-*` folders. Discord also registers `discord://` as a URI
  scheme (HKCU `Software\Classes\discord`).
- macOS: `/Applications/Discord.app` (bundle id `com.hnc.Discord`).
  Detectable via `mdfind "kMDItemCFBundleIdentifier == com.hnc.Discord"`
  or Launch Services `CFBundleCopyInfoDictionaryForURL`.
- Linux: deb/rpm installs under `/opt/Discord` or `/usr/share/discord`;
  Flatpak `com.discordapp.Discord`; Snap `discord`; `.desktop` files in
  `~/.local/share/applications/` or `/usr/share/applications/`.

**Provisioning — what is *not* possible.** There is no public Discord API
endpoint to create an Application or to create a Bot user. The
[Application Resource docs](https://docs.discord.com/developers/resources/application)
describe read and modify endpoints for an *existing* app — creation is
only via the [Developer Portal web UI](https://discord.com/developers/applications).
No OAuth flow mints an app token; developer portal actions require a
human-authenticated session and a checkbox acceptance of the Developer
Terms. `TODO: verify` whether a Discord-sanctioned partner API exists;
none is public as of the time of writing.

Additionally, [Discord's Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy)
and [Terms of Service](https://discord.com/terms) explicitly prohibit
scraping the site or automating browser-driven actions against it.
Driving the Developer Portal via hidden Playwright/Puppeteer is **out**.

**Provisioning — what *is* possible.**

| Step                                             | Automation level                                                                                                                                                                                                                                                                    |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Create Application in Developer Portal           | Manual. Electron opens the URL in an external browser.                                                                                                                                                                                                                              |
| Add Bot user, copy token                         | Manual. User pastes token into an Electron input.                                                                                                                                                                                                                                   |
| Toggle intents (Message Content, Server Members) | Manual deep link to the Bot tab.                                                                                                                                                                                                                                                    |
| Build OAuth2 install URL                         | Automated. Scout constructs the URL with `scope=bot applications.commands` and minimum permission integer.                                                                                                                                                                          |
| OAuth2 authorize → install to guild              | Fully automated via standard [OAuth2 authorization code grant](https://discord.com/developers/docs/topics/oauth2). Electron opens the URL, user picks guild, consents. Scout acts as the redirect target on `http://localhost:<port>/callback` and exchanges `code` for token info. |
| Launch Scout worker with token                   | Automated.                                                                                                                                                                                                                                                                          |

Endpoints we rely on (quoted from Discord OAuth2 docs):

- Authorize: `https://discord.com/oauth2/authorize`
- Token exchange: `https://discord.com/api/oauth2/token`
- Required scopes for a bot with slash commands: `bot applications.commands`.
  Discord's clients auto-append `applications.commands` when `bot` is
  requested.

**Auth model.** Bot token (static, long-lived) is the thing that matters
for Scout's runtime. The OAuth2 grant itself yields a short-lived user
access token we don't need — Scout keeps using the static bot token
entered in step 2. The OAuth2 install flow serves only to put the bot
into the target guild; it is not the source of the bot token.

**Running mode.** `Discord.Net` connects to the Discord Gateway
(WebSocket). No inbound HTTPS endpoint required. The Electron shell can
supervise `Scout.Host.Worker` as a local child process.

**Realistic UX.** "Wizard with deep links": Scout opens the Developer
Portal in the default browser, the user copies the token back into
Electron, Scout then drives OAuth2 guild install without the user ever
leaving the app. Total clicks: ~6. True one-click: impossible.

### Slack

**Detection.** Slack desktop:

- Windows: `%LOCALAPPDATA%\slack\` (Squirrel installer) or AppX
  `91750D7E.Slack` (Microsoft Store) under `%LOCALAPPDATA%\Packages\`.
  URI scheme `slack://`.
- macOS: `/Applications/Slack.app`, bundle id `com.tinyspeck.slackmacgap`.
- Linux: deb/rpm `/usr/bin/slack` + `/usr/lib/slack`; Snap `slack`;
  Flatpak `com.slack.Slack`.

**Provisioning — the good news.** Slack has first-class programmatic app
creation via [`apps.manifest.create`](https://docs.slack.dev/reference/methods/apps.manifest.create/).

- **Required scope:** `app_configurations:write` on an **app
  configuration token** (starts with `xoxe.xoxp-...`), *not* a regular
  user or bot token. See [app_configurations:write docs](https://docs.slack.dev/reference/scopes/app_configurations.write/).
- **How the user obtains the config token:** one-time manual step on
  [api.slack.com/apps](https://api.slack.com/apps) → "Your App
  Configuration Tokens" → "Generate Token". Each config token is unique
  to a user + workspace. Expires 12h; must be refreshed via
  `tooling.tokens.rotate`.
- **Manager-app cap:** Slack limits how many managed apps a single
  config token can create. `TODO: verify current cap` — the API
  documents an error `manager_app_max_apps` but not the numeric limit.

**End-to-end flow Scout can drive:**

1. User pastes their app configuration token into Electron (one-time).
2. Scout calls `apps.manifest.create` with a Scout-authored manifest
   (bot user, required scopes, Socket Mode enabled).
3. Slack returns `app_id`, a bot token seed, and configuration metadata.
4. Scout generates an app-level token (`xapp-...`) via the app settings
   API with `connections:write` scope — required for Socket Mode.
   `TODO: verify exact API method name for generating app-level tokens
   programmatically; historically done via the UI`.
5. User clicks "Install to workspace" → Electron drives the standard
   [OAuth install flow](https://docs.slack.dev/authentication/installing-with-oauth/)
   against `https://slack.com/oauth/v2/authorize` with redirect to
   `http://localhost:<port>/callback`. Returns a bot token (`xoxb-...`).
6. Scout stores bot token + app token, launches `Scout.Host.Worker`.

**Auth model.** Two tokens at runtime:

- Bot token (`xoxb-...`) — used for Web API calls.
- App-level token (`xapp-...`) with `connections:write` — used to open
  the Socket Mode websocket.

**Running mode.** [Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/)
is the key enabler: Slack pushes events to Scout over a WebSocket that
Scout opens outbound. **No public HTTPS endpoint needed.** This is
exactly what Scout wants for a local OSS install.

**Realistic UX.** Closest to "one-click" of the three. One manual step
(generate config token on Slack's web UI), then fully automated app
creation + install. Total user clicks: ~4.

### Microsoft Teams

**Detection.** Teams desktop:

- Windows: `%LOCALAPPDATA%\Microsoft\Teams\` (classic) or AppX
  `MSTeams_*` (new Teams) under `%LOCALAPPDATA%\Packages\`. URI scheme
  `msteams://`.
- macOS: `/Applications/Microsoft Teams.app` (classic) or
  `/Applications/Microsoft Teams (work or school).app` (new).
- Linux: new Teams has **no official Linux desktop client** as of 2026
  (Microsoft discontinued the Linux build in late 2022). This kills
  "detect local Teams" on Linux — user will be on the web client.
  `TODO: verify` current Linux status; it has not changed as of last
  check.

**Provisioning.** Three things must exist for a Teams bot to run:

1. **Azure Bot Service registration** (in an Azure subscription) — a
   Microsoft Bot Framework bot, tied to an Azure AD (Entra) app
   registration. This issues the Bot Framework credentials Scout uses.
2. **Teams app manifest** (JSON, per the
   [Teams app manifest schema](https://learn.microsoft.com/en-us/microsoftteams/platform/resources/schema/manifest-schema))
   packaged as a zip with icons.
3. **Installation into a team/tenant**, either via sideload (dev) or
   publication to the org catalog / Microsoft 365 Marketplace.

**What Graph API *does* expose:**

- [`POST /appCatalogs/teamsApps`](https://learn.microsoft.com/en-us/graph/api/teamsapp-publish?view=graph-rest-1.0)
  publishes a Teams app zip to the tenant catalog. Requires
  `AppCatalog.ReadWrite.All` — a tenant-admin-consented permission.
- [`POST /users/{id}/teamwork/installedApps`](https://learn.microsoft.com/en-us/graph/api/team-post-installedapps?view=graph-rest-1.0)
  installs a published app for a user. Requires
  `TeamsAppInstallation.ReadWriteForUser` (admin consent).

**What Graph API *does not* do:**

- It does not create the underlying Azure Bot Service resource. That is
  a separate Azure Resource Manager operation and requires an Azure
  subscription context.
- It does not register the Azure AD app. That is Entra ID admin territory.
- It does not flip the tenant-level setting "Upload custom apps" — an
  explicit Teams admin center toggle documented in
  [Manage custom app policies and settings](https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings).
  If that toggle is off, sideloading is blocked period.

**Auth model.** At runtime: Bot Framework App ID + secret/cert, used by
`Microsoft.Bot.Framework` to authenticate to the Bot Connector.
Inbound messages arrive via HTTPS webhook (Azure Bot Service forwards
Teams events) — which means **Teams requires a public HTTPS endpoint**
for Scout to receive messages. This is a structural mismatch with
Scout's "run locally" design.

**Running mode.** HTTPS webhook. Options for a local Electron-shelled
Scout: (a) run ngrok / dev tunnel (Azure Dev Tunnels) and inject the
URL into the bot registration — automatable, but fragile and gives the
user a public URL they probably don't want; (b) use the
`BotFrameworkNamedPipe` streaming extension, which is *not* supported
by the Teams channel. `TODO: verify` — streaming is Direct Line only,
not Teams.

**Realistic UX.** An Electron wizard would have to:

1. Ask the user for an Azure subscription + tenant.
2. Authenticate via MSAL with admin scopes.
3. Create the Azure AD app + Azure Bot Service resource.
4. Upload the Teams app manifest zip to the tenant catalog.
5. Install the app for the user / target team.

Step 3 alone requires an Azure subscription with billing. Steps 4-5
require the admin toggle. Even if all of that is automated, the user
must be a tenant-admin *or* the admin must have pre-authorized
sideloading.

**Conclusion: not viable for a V1 contributor install flow.** Keep
Teams support as a future ADR topic (post-V1, per
[[pages/decisions/0001-platform-priority|ADR-001]]).
