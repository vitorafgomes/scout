---
title: "ADR 0019 - GitHub through a read-only GitHub App and the device flow"
version: "1.1"
date: 2026-10-23
updated: 2026-10-23
changes: "Amended 2026-10-23: a pasted token as the second method, with a warning for write-capable classic tokens"
page_type: adr
status: accepted
description: "The desktop connects GitHub through a read-only GitHub App with the device flow, or with a token the owner pastes; one credential at a time, held by main."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "docs.github.com: Generating a user access token for a GitHub App; Refreshing user access tokens (read 2026-10-23)"
  - src/Scout.Plugin.GitHub/
  - ui/packages/main/src/oauth/github-auth.ts
reliability: high
---

# ADR 0019 - GitHub through a read-only GitHub App and the device flow

> The owner clicks "Connect with GitHub", types a code on github.com and approves a GitHub App that can only read; Scout never asks for a personal access token with write scope.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-23.

## Context

The owner asked for GitHub to ask permission through the browser, so that he only picks his account. The Features catalog asked for a personal access token with the `repo` scope, which GitHub treats as read and write, and advertised "Create issues", which no plugin function does ([[pages/invariants/no-executive-writes]]). A classic token cannot be narrowed to read-only on private repositories, and pasting one is the opposite of "ask through the browser".

A GitHub App user access token reaches only what both the user and the App can reach, so the App's permissions cap the token whatever the code does. The device flow needs no client secret and no loopback port: Scout shows a code, the owner types it at `https://github.com/login/device`, and Scout polls until GitHub answers.

## Decision

- **Sign-in.** The desktop main process runs the device flow (`ui/packages/main/src/oauth/github-auth.ts:163`): `POST https://github.com/login/device/code` with the App's client id, then `POST https://github.com/login/oauth/access_token` with the device code every `interval` seconds, never faster. GitHub answers errors with HTTP 200 and an `error` field: `authorization_pending` keeps polling, `slow_down` adds 5 s or takes the interval GitHub sends, `access_denied` and `expired_token` stop with a message, anything else stops with GitHub's code. The renderer sees only the user code and the verification page, which must start with `https://github.com/`.
- **Identity and installation.** After approval main reads `GET https://api.github.com/user` for the login and `GET https://api.github.com/user/installations` for whether the App is installed; when it is not, the card links to `https://github.com/apps/<slug>/installations/new`, where the owner picks the account and repositories.
- **Token custody.** The session (access token, refresh token, expiry, login) is stored in the safeStorage vault under the main-only key `github.session`. Main writes `{"access_token": ...}` to `<userData>/github-token.json` through a temporary file and a rename, mode `0600`, and deletes it on disconnect.
- **Refresh.** User tokens last 8 hours (`ghu_`), refresh tokens 6 months (`ghr_`), and a refresh rotates both. Main is the only refresher, through one serial queue: every minute and on wake from sleep it refreshes when less than 5 minutes remain (`ui/packages/main/src/oauth/github-auth.ts:292`), without a client secret. `bad_refresh_token` signs the owner out: the token file is deleted before the vault session, and if it cannot be deleted the owner stays connected with an error rather than half signed out.
- **Worker.** When connected, the worker gets `SCOUT__PLUGINS__GITHUB__TOKENFILE` (`ui/packages/main/src/worker/build-env.ts:46`), and a running worker is restarted on connect and disconnect. The plugin's `GitHubTokenFileHandler` sets the bearer header from the file on every request and re-reads it when its write time changes (`src/Scout.Plugin.GitHub/GitHubTokenFileHandler.cs:40`); a missing, empty or unreadable file fails the call before it leaves the process with "GitHub is not connected. Connect it on the Features screen." (`src/Scout.Plugin.GitHub/GitHubTokenFileHandler.cs:70`).
- **Second method: a pasted token** (amended 2026-10-23). Many users never open GitHub's settings and are handed a token, so the card also offers "Use a token". Main checks it with `GET https://api.github.com/user` (`ui/packages/main/src/oauth/github-auth.ts:379`): a 401 fails with "GitHub rejected this token.", an unreachable GitHub with a typed network error, and nothing is stored on either. A working token is kept under the main-only vault key `github.token` and reaches the worker as `SCOUT__PLUGINS__GITHUB__TOKEN`, the plugin's existing static `Token` path (`ui/packages/main/src/oauth/github-auth.ts:128`). Exactly one GitHub credential exists at a time: saving a token removes a browser session and its token file, and a browser connect removes a saved token. Disconnect clears whichever is active. The installation check applies to the browser method only.
- **Write-capable tokens.** A pasted token is capped by what it was granted, not by the App. A classic token answers with an `X-OAuth-Scopes` header (GitHub docs, "Scopes for OAuth apps"); when it lists `repo`, `public_repo`, `delete_repo`, `workflow`, or any `write:` or `admin:` scope, Scout keeps the token but the card warns "This token can write to your repositories. A read-only fine-grained token is safer." (`ui/packages/main/src/oauth/github-auth.ts:23`). Fine-grained tokens send no such header, so no warning appears for them; the helper text recommends one with read-only Metadata, Contents, Pull requests and Issues, and links to `https://github.com/settings/personal-access-tokens/new` (the page GitHub's personal access token docs link to). Read-only then rests on Scout's GET-only client for that method, not on GitHub.
- **Configuration.** The App's client id and slug are public identifiers, not secrets. Main reads `SCOUT_GITHUB_APP_CLIENT_ID` and `SCOUT_GITHUB_APP_SLUG`, then falls back to constants that are empty until the App is registered (`ui/packages/main/src/oauth/github-auth.ts:6`); with no client id the card says "The Scout GitHub App is not configured yet." and Connect is disabled.

### GitHub App registration (done once by the owner)

| Setting | Value |
|---|---|
| Repository permissions | Metadata: read-only, Contents: read-only, Pull requests: read-only, Issues: read-only |
| Account permissions | none |
| Webhook | inactive |
| Enable Device Flow | on |
| Expire user authorization tokens | on |
| Where can this App be installed | any account |

## Consequences

- **Better:** read-only is enforced by GitHub's permission model, not only by the absence of write methods in `IGitHubClient`. The owner never handles a token. The catalog no longer advertises a write.
- **Worse:** the owner must register the App once and give Scout its client id and slug before the button works. The access token sits on disk in a `0600` file while connected, readable by any process of the same user. Disconnect only deletes Scout's copy: revoking the grant on GitHub's side needs the client secret, which a desktop app cannot keep, so the owner revokes it under GitHub's Settings, Applications.
- **Neutral but noteworthy:** the control-plane Features service still holds a `pat` credential field for hosts without the desktop; the desktop GitHub card bypasses it: its badge comes from the main-owned connection and it has no toggle or credential buttons until a real activator exists ([[pages/roadmap/followups]]).

## Alternatives considered

Not re-read on docs.github.com in this change; verify before quoting.

- **Classic personal access token** - cannot be read-only on private repositories and is pasted, not approved in the browser.
- **Fine-grained personal access token** - read-only is possible, but the owner still creates and pastes it, and it expires without a refresh path.
- **OAuth App with the web flow** - needs a client secret and a loopback port; OAuth App scopes include no read-only private repository access.
- **The `gh` CLI token** - its scopes are whatever the CLI was granted, usually `repo`, and Scout would depend on another tool's login.

## Enforcement

- `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:140` - every request any GitHub function sends to `api.github.com` is a GET.
- `ui/packages/main/test/oauth-github.test.ts:455` - main sends only GETs to `api.github.com` and only POSTs to the two sign-in endpoints.
- `ui/packages/main/test/oauth-github.test.ts:481` and `ui/packages/main/test/ipc-github.test.ts:55` - no token or device code in a log line or in what the renderer receives.
- `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:19` - the catalog lists only the four read permissions.
- Invariant pages: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/plugin-github]], [[pages/services/desktop-ui]], [[pages/services/control-plane]]
- Refines nothing and supersedes nothing; it applies [[pages/decisions/0009-descriptive-vs-executive-writes]] by giving the GitHub plugin no write permission at all.
- Related: [[pages/decisions/0011-backend-frontend-grpc-uds]] (the renderer never sees a token).

## Notes

Endpoints, error codes, token lifetimes and the rotation on refresh were read from docs.github.com ("Generating a user access token for a GitHub App", "Refreshing user access tokens") on 2026-10-23.
