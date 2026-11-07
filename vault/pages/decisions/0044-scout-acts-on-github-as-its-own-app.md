---
title: "ADR 0044 - Scout acts on GitHub as its own App, when the owner chooses it"
version: "1.1"
date: 2026-11-07
changes: "v1.1 (2026-11-07) amended with what the implementation found against GitHub's docs"
page_type: adr
status: proposed
description: "An optional identity: the owner creates a GitHub App of their own from the desktop with GitHub's manifest flow, installs it, and Scout's reviews, verdicts and issues then show as that App's bot instead of the owner; without it, Scout keeps acting through the owner's sign-in as today."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/main/src/oauth/github-auth.ts
  - src/Scout.Plugin.GitHub/GitHubTokenFileHandler.cs
reliability: high
---

# ADR 0044 - Scout acts on GitHub as its own App, when the owner chooses it

> Optional: the owner creates a GitHub App of their own from the desktop in one click, installs it on the repositories they pick, and from then on Scout's reviews, verdicts and issues are signed by that App's bot; the owner who does not want it, or whose organisation does not allow it, keeps today's sign-in and nothing changes.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-07. Part of gap plan G8 with [[pages/decisions/0043-the-verdict-on-a-pull-request]] ([[pages/roadmap/gap-plan]]). Waits for the owner's approval before any code.

## Context

The owner on 2026-11-07: "temos que ter uma forma de registrar o Scout no GitHub como um agente, assim todas as ações dele não mostram como fui eu que fiz", then "isso tem que ser opcional, nem sempre alguém vai permitir isso ou vai querer isso", and that it ships with the next deliverable.

Today Scout reaches GitHub through a GitHub App's user access token from the device flow, or a pasted token ([[pages/decisions/0019-github-app-device-flow]]). A user access token acts as the user: a review or an issue shows the owner as its author, with the App's badge. The worker reads the token from a file main writes (`ui/packages/main/src/oauth/github-auth.ts:130`, the handler at `src/Scout.Plugin.GitHub/GitHubTokenFileHandler.cs:24`).

To act as itself, a GitHub App uses an installation access token, which needs the App's private key to sign a short JSON Web Token. Scout has no server, and it is open source: one shared Scout App cannot work, because its private key would have to ship inside the desktop, where anyone could take it. So each owner who wants a bot owns their own App. GitHub's manifest flow creates an App from a JSON description in one confirmation page and hands the new App's credentials back to the caller.

Some owners will not want it, and some organisations forbid members to create or install Apps. The identity is therefore a choice, never a requirement.

## Decision

- **Two identities, the owner's sign-in by default.** The GitHub card keeps "Connect" (today's device flow or pasted token, acting as the owner) and gains, below it and only after Connect, "Use a Scout bot on GitHub (optional)". Without the bot nothing changes, ADR 0043 included: a verdict on the owner's own pull request then offers Request changes and Comment only, because GitHub refuses an approval by the pull request's author.
- **Creating the App: GitHub's manifest flow, from the desktop.** Main opens a local page in the browser that posts the manifest to `https://github.com/settings/apps/new` (or the organisation's `organizations/<org>/settings/apps/new` when the owner picks one), with `redirect_url` on a loopback port main listens on for one request, a random `state` checked on return, and a five-minute timeout. The manifest: name `Scout (<login>)` (GitHub requires a unique name; the owner may edit it on the page), not public, webhook inactive, permissions Metadata read, Contents read, Pull requests read and write, Issues read and write, Checks read, Commit statuses read. On return main exchanges the code with `POST /app-manifests/{code}/conversions` once, keeps the App id, slug, client id and private key, and discards the client secret and the webhook secret it does not use.
- **Custody.** The private key goes into the desktop's safeStorage vault under a main-only key, never on disk in clear, never to the renderer, never in a log; the App id and slug are not secrets. "Remove the Scout bot" deletes the key from the vault and links to the App's settings page so the owner can delete the App on GitHub too; Scout cannot delete it.
- **Installing.** After creation the card links to `https://github.com/apps/<slug>/installations/new`; main reads the installations with the App's JSON Web Token and shows which repositories the bot can act on.
- **Tokens.** Main signs a JSON Web Token (RS256, issued 60 seconds in the past, valid 9 minutes) and asks `POST /app/installations/{id}/access_tokens` for the installation that covers the repository; the token lasts one hour, is refreshed when less than 5 minutes remain, by the same serial queue as today's refresh, and is written to a second token file the worker reads with the same handler. Reads keep the owner's token; the bot's token is used for the writes the owner switched to the bot.
- **Which actions the bot signs.** The card has one switch per declared write of the `github` feature: post a review, give a verdict, open an issue. Each is the owner's or the bot's; the default when the bot is created is the bot for all three. A repository the bot is not installed on falls back to the owner's identity and the card says so before the action runs, never silently.
- **Unchanged rules.** ADR 0032 still governs every write: the grant, the per-action approval of an irreversible one, the write log, which now records which identity signed it.

### Amended 2026-11-07: what the implementation found

Checked against docs.github.com on 2026-11-07 ("Registering a GitHub App from a manifest", REST "GitHub Apps" and "GitHub App installations", "Generating a JSON Web Token (JWT) for a GitHub App"): the form targets, `state`, the conversion endpoint and its fields (`id`, `slug`, `client_id`, `client_secret`, `webhook_secret`, `pem`, `owner`), the six permission keys and the JWT claims are as decided. Differences:

- The manifest's `url` is required, and `hook_attributes` needs a `url` even when inactive; both are Scout's repository page.
- The JWT's `iss` is the App's client id, which GitHub recommends over the App id.
- Four switches, not three: post a review, give a verdict, comment, open an issue, matching ADR 0043's declared writes.
- One installation token for the first installation, not one per repository: the worker reads one bot token file.
- The bot's refresh runs on its own serial queue in `GitHubBot`, beside `GitHubAuth`'s, not on the same one.
- Not stated by GitHub and unverified until the owner's first real creation: that a loopback `http://127.0.0.1:<port>` `redirect_url` is accepted (Probot's local setup uses `http://localhost:<port>`), and that the bot's login is `<slug>[bot]`.

## Consequences

- **Better:** the owner's reviews and issues stop looking like the owner wrote them; the bot can approve the owner's own pull requests, which completes ADR 0043's verdict on a solo repository.
- **Better:** each owner holds their own key; nothing in the open-source tree or the release can sign as anyone else's bot.
- **Worse:** one more secret to keep (the private key) and one more refresh loop; both reuse the existing vault and queue.
- **Worse:** an organisation that forbids Apps leaves its members on the owner identity; the card says why when creation or installation fails.
- **Neutral:** whether a bot's approval counts toward a branch protection's required approvals depends on the repository's rules; the card shows the verdict as given, and the spike before code confirms GitHub's current behaviour.

## Alternatives considered

- **One shared Scout App with its key in the release.** Rejected: the key would be public, and anyone could sign as every user's bot.
- **A hosted Scout service holding the key.** Rejected: Scout has no server and runs on the owner's machine by design.
- **A machine user (a second GitHub account) with a token.** Rejected as the recommended path: it costs a seat in organisations, violates many organisations' policies, and mixes a person's account with a robot; the pasted-token path still allows it for owners who already have one.
- **Mandatory bot identity.** Rejected on the owner's word: optional.

## Enforcement

Tests to write with the change, in the words of the requirement:

- `ui/packages/main/test`: the manifest posted carries exactly the permissions above and an inactive webhook; a return with a wrong or missing `state`, or after the timeout, is refused and stores nothing; the conversion is called once; the private key reaches the vault and never a file, a log line or the renderer; the JSON Web Token's claims and lifetime; the installation token refreshed before expiry; "Remove" deletes the key; the bot switch per action and the fall back to the owner on an uninstalled repository, announced on the card; the failing-call tests (Rule I) for the conversion, the installation token and the refresh (timeout, 401, 5xx).
- `tests/Scout.Plugin.GitHub.Tests`: a write switched to the bot uses the bot's token file and a read never does; with no bot configured, every call uses the owner's token exactly as before (behaviour lock).
- `ui/packages/renderer`: the card's optional section, hidden until Connect, the creation states and the per-action switches, in a matrix spec.

## Related

- [[pages/decisions/0019-github-app-device-flow]] (the owner identity, unchanged), [[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0032-per-feature-write-permissions]]
- [[pages/roadmap/gap-plan]] G8; [[pages/services/plugin-github]], [[pages/services/desktop-ui]]
