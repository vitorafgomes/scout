---
title: "Electron Provisioning UI - feasibility study: References"
version: "1.1"
date: 2026-04-22
updated: 2026-10-21
changes: "Angular reference moved to v22.2.3, verified against the npm registry"
page_type: longform
status: active
description: "Reference links used by the Electron provisioning feasibility study."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: References

### Discord

- [Discord OAuth2 docs](https://discord.com/developers/docs/topics/oauth2)
- [Discord Application Resource docs](https://docs.discord.com/developers/resources/application)
- [Developer Portal applications page](https://discord.com/developers/applications)
- [Discord Developer Terms of Service](https://support-dev.discord.com/hc/en-us/articles/8562894815383-Discord-Developer-Terms-of-Service)
- [Discord Developer Policy](https://support-dev.discord.com/hc/en-us/articles/8563934450327-Discord-Developer-Policy)

### Slack

- [apps.manifest.create reference](https://docs.slack.dev/reference/methods/apps.manifest.create/)
- [app_configurations:write scope](https://docs.slack.dev/reference/scopes/app_configurations.write/)
- [Configuring apps with app manifests](https://api.slack.com/reference/manifests)
- [Installing with OAuth](https://docs.slack.dev/authentication/installing-with-oauth/)
- [Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/)
- [Slack token types](https://docs.slack.dev/authentication/tokens/)

### Microsoft Teams

- [Teams app manifest schema](https://learn.microsoft.com/en-us/microsoftteams/platform/resources/schema/manifest-schema)
- [Publish teamsApp (Graph)](https://learn.microsoft.com/en-us/graph/api/teamsapp-publish?view=graph-rest-1.0)
- [Add app to user (Graph)](https://learn.microsoft.com/en-us/graph/api/team-post-installedapps?view=graph-rest-1.0)
- [Manage custom app policies and settings](https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings)
- [Upload your custom app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-upload)
- [Microsoft 365 Agents Toolkit](https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/publish)

### Electron

- [`safeStorage` API](https://www.electronjs.org/docs/latest/api/safe-storage)
- [Electron `app` module](https://www.electronjs.org/docs/latest/api/app)
- [ASAR cannot spawn embedded binaries — #9459](https://github.com/electron/electron/issues/9459)
- [Replacing keytar with safeStorage — Freek Van der Herten](https://freek.dev/2103-replacing-keytar-with-electrons-safestorage-in-ray)

### .NET

- [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

### Bun (toolchain)

- [Bun homepage](https://bun.sh)
- [`bun install` docs](https://bun.sh/docs/cli/install)
- [Bun bundler docs](https://bun.sh/docs/bundler)
- [`bun test` docs](https://bun.sh/docs/cli/test)
- [Single-file executables (`bun build --compile`)](https://bun.sh/docs/bundler/executables)
- Bun + Electron compatibility (native modules, `electron-builder`,
  `electron-forge`): `TODO: verify` — link to authoritative
  electron-builder / electron-forge docs section or a tracking issue
  on `oven-sh/bun` once confirmed.

### Angular (renderer)

- [Angular homepage](https://angular.dev/)
- [Angular Signals guide](https://angular.dev/guide/signals)
- [Standalone components guide](https://angular.dev/guide/components)
- [Angular CLI](https://angular.dev/tools/cli)
- [Angular v22.2.3 release (angular/angular)](https://github.com/angular/angular/releases/tag/v22.2.3)
  — the `latest` dist-tag of `@angular/core` on npm on 2026-10-21 (published
  2026-10-14), and the version the renderer pins (`^22.2.3`). Angular ships
  frequent patch releases; the renderer's `package.json` is the current pin.

### Prior art

- [Probot — GitHub App manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest)
- [Botpress v12 repo](https://github.com/botpress/v12)
