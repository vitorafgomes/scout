---
title: "Secrets never live in source"
version: "1.1"
date: 2026-10-21
updated: 2026-10-22
changes: "Desktop cloud API keys: keychain storage and environment mapping"
page_type: invariant
status: active
description: "Tokens and API keys come from user-secrets in development and environment variables in production; committed appsettings ship empty values. Only .gitignore and a convention hold this; no code or test does."
enforced_by:
  - "src/Scout.Host.Console/appsettings.json:10"
  - "src/Scout.Host.Console/appsettings.json:31"
  - "src/Scout.Host.Console/Scout.Host.Console.csproj:7"
  - "src/Scout.Host.Console/Program.cs:14"
verified_by: []
source:
  - "src/Scout.Host.Console/appsettings.json"
  - "src/Scout.Host.Console/Scout.Host.Console.csproj"
  - "src/Scout.Host.Console/Program.cs"
  - "src/Scout.Adapter.Discord/"
  - "src/Scout.Plugin.GitHub/"
reliability: high
---

# Secrets never live in source

> Every credential (LLM API keys, bot tokens, the GitHub token) is supplied at run time through user-secrets or environment variables; the committed `appsettings.json` carries empty strings for all of them.

## Why this exists

The repository is public. A committed token is a leaked token, and Git history keeps it after a later commit removes it.

## Scope

- **Applies to:** every `Scout:` credential key: `Scout:Llm:<Provider>:ApiKey`, `Scout:Adapters:Discord:BotToken`, `Scout:Adapters:Slack:BotToken`, `Scout:Adapters:Slack:AppLevelToken`, `Scout:Adapters:Teams:AppPassword`, `Scout:Plugins:GitHub:Token`.
- **Does not cover:** credentials of repositories Scout indexes (see Known gaps).

## Where it is enforced

- The committed file sets `"Provider": "Ollama"` at `src/Scout.Host.Console/appsettings.json:10` and leaves the Discord token empty at `:31`. Line numbers refer to the committed content (`git show HEAD:`), not to any local edit.
- The host loads configuration with the .NET default builder (`src/Scout.Host.Console/Program.cs:14`): appsettings, then user-secrets in the Development environment, then environment variables, then command line. `src/Scout.Host.Console/Scout.Host.Console.csproj:7` sets the user-secrets id.
- Environment variable names use the double-underscore form (`Scout__Adapters__Discord__BotToken`). `src/Scout.Host.Console/ConversationWorker.cs:28` points users to that route when no adapter is configured.
- Each credential consumer treats an empty value as "off", so an empty committed file starts a working host: `src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:30`, `src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:32`, `src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:37`, `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:38`.
- Adapter constructors reject an empty credential and name the config route: `src/Scout.Adapter.Discord/DiscordChatAdapter.cs:38`, `src/Scout.Adapter.Slack/SlackChatAdapter.cs:43`, `src/Scout.Adapter.Teams/TeamsChatAdapter.cs:58`.
- `.gitignore:23` ignores `appsettings*.bkp` copies.
- A cloud LLM key entered on the desktop AI Sources screen is stored in the OS keychain under vault key `llm.api-key` and reaches the worker only as an environment variable, mapped per provider in `ui/packages/main/src/worker/build-env.ts:40` and `:44`; the renderer can store it but never read it back ([[pages/decisions/0015-cloud-llm-providers-by-api-key]]).

## How it is verified

Not verified by any test. No test reads `appsettings.json`, and no CI step scans for secrets. Tests that touch credentials use in-memory dummy values (`tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:58`).

## Known gaps

- **Convention only.** The sole guards are `.gitignore` and the habit of keeping a local edit out of commits. Git history keeps any token that was ever committed; rotate such a token, because deleting it in a later commit does not revoke it.
- **User-secrets do not load in the shipped launch path.** User-secrets are added only in the Development environment, and the desktop supervisor starts the worker with `DOTNET_ENVIRONMENT` set to Production (`ui/packages/main/src/worker/build-env.ts:27`). There is no `launchSettings.json` for `dotnet run`. Outside an explicit Development setting, credentials must come from environment variables.
- **A tracked file invites local edits.** `appsettings.json` is tracked and also the file developers fill in locally. Prefer `dotnet user-secrets` or environment variables so the tracked file never changes.
- **No scanner in CI.** A secret-scanning step (for example gitleaks) on pull requests would turn the convention into a check.
- **Indexed repositories can leak secrets.** `src/Scout.Knowledge/ProjectFileFilter.cs:12` indexes `.json`, `.yml` and `.yaml` files, so another project's settings file is embedded into the vector database and can be returned through `search_project` (`src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26`).
- **Control-plane credential store is in memory.** `src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:68` falls back to `InMemoryCredentialStore`; no OS keyring store exists in the .NET code. The feature text promises a keyring (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:23`).

## Related

- Services: [[pages/services/host-console]], [[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]], [[pages/services/plugin-github]], [[pages/services/llm]], [[pages/services/control-plane]], [[pages/services/knowledge]]
- Similar: [[pages/invariants/local-first-default]]

## Change protocol

To add a credential key: give it an empty default, read it through the same self-gating pattern, and list it under Scope here.
