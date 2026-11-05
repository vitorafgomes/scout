---
title: "Secrets never live in source"
version: "1.6"
date: 2026-10-21
updated: 2026-11-02
changes: "YouTube credentials: Google session, API key and the OAuth client id and secret (ADR 0030)"
page_type: invariant
status: active
description: "Tokens and API keys come from user-secrets in development and environment variables in production; committed appsettings ship empty values. For appsettings only .gitignore and a convention hold this; the desktop GitHub token and kubeconfig custody is test-covered."
enforced_by:
  - "src/Scout.Host.Console/appsettings.json:10"
  - "src/Scout.Host.Console/appsettings.json:31"
  - "src/Scout.Host.Console/Scout.Host.Console.csproj:7"
  - "src/Scout.Host.Console/Program.cs:14"
verified_by:
  - "tests/Scout.Llm.Tests/Guard/TypeSafeClientTests.cs:55"
  - "ui/packages/main/test/worker-build-env.test.ts:344"
  - "ui/packages/main/test/ipc-provider-keys.test.ts"
  - "ui/packages/main/test/ipc-vault.test.ts"
  - "ui/packages/main/test/oauth-github-token.test.ts:133"
  - "ui/packages/main/test/oauth-github.test.ts:481"
  - "ui/packages/main/test/ipc-github.test.ts:55"
  - "ui/packages/main/test/ipc-kubernetes.test.ts:222"
  - "ui/packages/main/test/kubeconfig.test.ts:114"
  - "ui/packages/main/test/ipc-slack.test.ts:132"
  - "ui/packages/main/test/ipc-slack.test.ts:148"
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

- **Applies to:** every `Scout:` credential key: `Scout:Llm:<Provider>:ApiKey`, `Scout:Llm:ChatGptPlan:TokenFile` (a path to the 0600 file holding the current ChatGPT access token, written by the desktop and deleted on sign-out; [[pages/decisions/0018-chatgpt-plan-sign-in]]), `Scout:Adapters:Discord:BotToken`, `Scout:Adapters:Slack:BotToken`, `Scout:Adapters:Slack:AppLevelToken`, `Scout:Adapters:Teams:AppPassword`, `Scout:Plugins:GitHub:Token`, `Scout:Plugins:YouTube:ApiKey` and `Scout:Plugins:YouTube:TokenFile` (a Data API key, or the path to the 0600 file holding the owner's Google access token, written by the desktop; [[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in]]), `Scout:Guard:TypeSafe:ApiKey`. `Scout:Plugins:Kubernetes:KubeconfigPath` and `Scout:Plugins:Kubernetes:Contexts` are not credentials: they name a file and contexts on the owner's computer.
- **Whose credentials the bot uses.** Every credential above is the owner's, and the worker uses it for whoever messages the bot. Keeping the value out of source does not limit who can make Scout use it; the allowed-users list does: once GitHub, Kubernetes, YouTube (either credential) or the ChatGPT plan is connected from the desktop, the worker answers only the platform user ids the owner listed, and nobody while the list is empty ([[pages/decisions/0021-allowed-users]], `src/Scout.Conversation/ConversationConductor.cs:342`). The ids themselves are not credentials; they reach the worker as `SCOUT__ACCESS__ALLOWEDUSERIDS__<n>`, and a refusal logs the channel id only, never the message text (`tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:122`). An OpenAI or Gemini API key does not close the bot (see Known gaps). The quick-ask window is the one caller outside the list: a message the local adapter reads from the worker's stdin is the owner's own and uses these credentials without an allow-list entry, because only the desktop app that spawned the worker holds that pipe ([[pages/decisions/0028-local-quick-ask-assistant]]). Quick ask adds no credential: `Scout:Adapters:Local:Enabled` and the stored shortcut (main-only vault key `quick-ask.shortcut`) are settings, recordings stay in memory, and reply lines never enter the log ring or the diagnostics export.
- **Does not cover:** credentials of repositories Scout indexes (see Known gaps).

## Where it is enforced

- The committed file sets `"Provider": "Ollama"` at `src/Scout.Host.Console/appsettings.json:10` and leaves the Discord token empty at `:31`. Line numbers refer to the committed content (`git show HEAD:`), not to any local edit.
- The host loads configuration with the .NET default builder (`src/Scout.Host.Console/Program.cs:14`): appsettings, then user-secrets in the Development environment, then environment variables, then command line. `src/Scout.Host.Console/Scout.Host.Console.csproj:7` sets the user-secrets id.
- Environment variable names use the double-underscore form (`Scout__Adapters__Discord__BotToken`). `src/Scout.Host.Console/ConversationWorker.cs:28` points users to that route when no adapter is configured.
- Each credential consumer treats an empty value as "off", so an empty committed file starts a working host: `src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:32`, `src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:32`, `src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:37`, `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:44`.
- Adapter constructors reject an empty credential and name the config route: `src/Scout.Adapter.Discord/DiscordChatAdapter.cs:54`, `src/Scout.Adapter.Slack/SlackChatAdapter.cs:43`, `src/Scout.Adapter.Teams/TeamsChatAdapter.cs:58`.
- `.gitignore:23` ignores `appsettings*.bkp` copies.
- A cloud LLM key entered on the desktop AI Sources screen is stored in the OS keychain under its provider's own vault key (`llm.openai.api-key` or `llm.gemini.api-key`) and reaches the worker only as an environment variable, mapped per provider in `ui/packages/main/src/worker/build-env.ts:116` and `:120`; the renderer can store and delete it but never read it back: its only vault read, `scout:vault.getPublic` (`ui/packages/main/src/ipc/vault.ts:89`), serves `llm.provider` and `llm.cloud-model` from the allow-list `PUBLIC_VAULT_KEYS` (`ui/packages/shared/src/client-kinds.ts:39`) and refuses every other key before reading the vault ([[pages/decisions/0015-cloud-llm-providers-by-api-key]], [[pages/decisions/0017-provider-connect-disconnect]]).
- Before it is stored, the key leaves the machine once, in the header of a `GET` to the provider's model list (`ui/packages/main/src/ipc/provider-keys.ts:70`); the handler logs only the provider, the status or the error name, never the key (`:87`), and `ui/packages/main/test/ipc-provider-keys.test.ts` asserts that no log line or response carries it. Once stored, main reads it again only to list that provider's chat models for Models → Analysis, again in the header of a `GET` (`ui/packages/main/src/ipc/provider-keys.ts:188`), never in a URL, a log line, a response or an error message (`ui/packages/main/test/ipc-provider-keys.test.ts`, `registerProviderModelHandlers` block).
- The TypeSafe guard key follows the same route: vault key `guard.typesafe-api-key`, mapped to `SCOUT__GUARD__TYPESAFE__APIKEY` for every provider at `ui/packages/main/src/worker/build-env.ts:103`. In the worker, `TypeSafeException` messages carry the status and validation messages only, never the key or the raw body (`src/Scout.Llm/Guard/TypeSafeClient.cs:85`) ([[pages/decisions/0016-jev-read-only-guard]]).
- The GitHub user token from "Connect with GitHub" lives in the vault under the main-only key `github.session` and in `<userData>/github-token.json`, written with mode `0600` (`ui/packages/main/src/oauth/github-auth.ts:498`) and deleted on disconnect; the worker receives only the file's path as `SCOUT__PLUGINS__GITHUB__TOKENFILE` (`ui/packages/main/src/worker/build-env.ts:68`). No log line or renderer payload carries the token (`ui/packages/main/test/oauth-github.test.ts:481`), and the plugin's not-connected message never quotes the file ([[pages/decisions/0019-github-app-device-flow]]).
- The Google session from "Sign in with Google" lives in the vault under the main-only key `youtube.session` and in `<userData>/youtube-token.json`, written with mode `0600` through a temporary file and removed before the vault entry on sign-out (`ui/packages/main/src/oauth/google-auth.ts`); a pasted Data API key lives under `youtube.api-key` and reaches the worker only as `SCOUT__PLUGINS__YOUTUBE__APIKEY`. The OAuth client's id and secret come from `SCOUT_GOOGLE_CLIENT_ID` and `SCOUT_GOOGLE_CLIENT_SECRET` or from `<userData>/google-oauth-client.json`, are read when they are needed (sign-in, refresh and the status check) and never logged, stored in the vault or sent to the renderer (`ui/packages/main/test/oauth-google.test.ts`, "never logs the client secret, the code, a token or the key"). The key travels in the query string, as Google requires, so the plugin's error messages and logs never carry a request URL (`tests/Scout.Plugin.YouTube.Tests/YouTubeClientTests.cs`, `Error_Messages_And_Logs_Never_Contain_The_Api_Key`).
- A GitHub token the owner pastes on the Features screen travels renderer to main once, is checked against GitHub, and is kept only under the main-only vault key `github.token`; the renderer never receives it back and no log line carries it (`ui/packages/main/test/oauth-github-token.test.ts:133`). It reaches the worker as the environment variable `SCOUT__PLUGINS__GITHUB__TOKEN`, and is removed on disconnect or when the owner connects through the browser instead.
- The kubeconfig is never copied. The desktop reads it in place to list contexts, never returns or logs a token, `client-key-data`, `client-certificate-data`, password or exec env value (`ui/packages/main/src/kubernetes/kubeconfig.ts:32`), shows the value of a secret-looking exec argument as `***` (`ui/packages/main/src/kubernetes/kubeconfig.ts:116`), and stores only the file's path and the context names the owner allowed under the main-only vault key `kubernetes.selection` (`ui/packages/main/src/ipc/kubernetes.ts:17`). The worker receives the path and the names as `SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH` and `SCOUT__PLUGINS__KUBERNETES__CONTEXTS__<n>` (`ui/packages/main/src/worker/build-env.ts:69`), and the plugin's failure messages never quote the cluster's answer or the library's error text ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]).
- The Slack bot token and app-level token the owner pastes in the desktop's Slack setup travel renderer to main once, in `scout:slack.save`, and are kept only in the main-only vault entry `slack.connection` (`ui/packages/main/src/ipc/slack.ts:21`), both tokens in one write, so the pair the worker gets always belongs together. Before they are stored each leaves the machine once, in the `Authorization` header of a `POST` to `auth.test` or `apps.connections.open` (`ui/packages/main/src/slack/slack-setup.ts:58`, `:74`); the Socket Mode URL the second call returns is dropped. Error messages are built from the error code alone, never from the pasted value. The worker receives the tokens as `SCOUT__ADAPTERS__SLACK__BOTTOKEN` and `SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN` (`ui/packages/main/src/worker/build-env.ts:68`); disconnect deletes the entry ([[pages/decisions/0023-slack-setup-from-a-manifest]]).

## How it is verified

The committed settings are not verified by any test: no test reads `appsettings.json`, and no CI step scans for secrets. The desktop and guard credentials are: `ui/packages/main/test/ipc-provider-keys.test.ts` asserts that no log line, response or error carries a cloud API key; `ui/packages/main/test/ipc-vault.test.ts` asserts that the public read refuses API keys and the Discord token without reading the vault; `tests/Scout.Llm.Tests/Guard/TypeSafeClientTests.cs:55` and `:73` assert that a 422, 500 or 401 whose body echoes the TypeSafe key never puts it in the exception message; and `ui/packages/main/test/worker-build-env.test.ts:344` asserts `buildEnv` writes nothing containing that key to the console. The desktop GitHub token custody is tested: no log line or renderer status carries a pasted token (`ui/packages/main/test/oauth-github-token.test.ts:133`), a device-flow token (`ui/packages/main/test/oauth-github.test.ts:481`) or an IPC payload token (`ui/packages/main/test/ipc-github.test.ts:55`). The kubeconfig custody is tested the same way: no detect, use, status or disconnect payload and no log line carries a token, key, certificate, password or exec env value (`ui/packages/main/test/ipc-kubernetes.test.ts:222`), and a malformed kubeconfig is logged by path only (`ui/packages/main/test/kubeconfig.test.ts:114`). The appsettings convention is not verified by any test: no test reads `appsettings.json`, and no CI step scans for secrets. Tests that touch credentials use in-memory dummy values (`tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:59`).

## Known gaps

- **Convention only.** The sole guards are `.gitignore` and the habit of keeping a local edit out of commits. Git history keeps any token that was ever committed; rotate such a token, because deleting it in a later commit does not revoke it.
- **User-secrets do not load in the shipped launch path.** User-secrets are added only in the Development environment, and the desktop supervisor starts the worker with `DOTNET_ENVIRONMENT` set to Production (`ui/packages/main/src/worker/build-env.ts:61`). There is no `launchSettings.json` for `dotnet run`. Outside an explicit Development setting, credentials must come from environment variables.
- **A tracked file invites local edits.** `appsettings.json` is tracked and also the file developers fill in locally. Prefer `dotnet user-secrets` or environment variables so the tracked file never changes.
- **No scanner in CI.** A secret-scanning step (for example gitleaks) on pull requests would turn the convention into a check.
- **Indexed repositories can leak secrets.** `src/Scout.Knowledge/ProjectFileFilter.cs:12` indexes `.json`, `.yml` and `.yaml` files, so another project's settings file is embedded into the vector database and can be returned through `search_project` (`src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26`).
- **Control-plane credential store is in memory.** `src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69` falls back to `InMemoryCredentialStore`; no OS keyring store exists in the .NET code. The catalog no longer promises one for GitHub or Kubernetes (`tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:68`).
- **An API-key provider leaves the bot open.** With only an OpenAI or Gemini key connected and no allowed users, anyone who can message the bot spends the owner's API credit; `RequireAllowList` is set only for GitHub, Kubernetes, YouTube (a session or a pasted key, `ui/packages/main/src/worker/build-env.ts:82`) and the ChatGPT plan (`ui/packages/main/src/worker/build-env.ts:84`, [[pages/decisions/0021-allowed-users]]).
- **The pairing code is a short-lived credential.** Main draws it, keeps it in memory only and never in the vault, and returns it to the renderer once for display (`ui/packages/main/src/ipc/access.ts:114`). The worker gets it only through `SCOUT__ACCESS__PAIRINGCODE` while it is pending and unexpired (`ui/packages/main/src/worker/build-env.ts:87`). No main or worker log line carries it, nor the message that carried it (`ui/packages/main/test/ipc-access.test.ts:400`, `tests/Scout.Conversation.Tests/ConversationConductorPairingTests.cs:273`). It works once, for 10 minutes. It is burned when it is sent outside a direct message or after five wrong codes, and main then drops it (`src/Scout.Conversation/ConversationConductor.cs:231`, [[pages/decisions/0021-allowed-users]]).
- **The kubeconfig stays readable by the worker.** The worker reads the owner's kubeconfig with the owner's file permissions, and an exec context runs the owner's own credential command; Scout stores no copy, but anything the kubeconfig grants is available to the plugin's GET calls.

## Related

- Services: [[pages/services/host-console]], [[pages/services/adapter-local]], [[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]], [[pages/services/plugin-github]], [[pages/services/plugin-kubernetes]], [[pages/services/desktop-ui]], [[pages/services/llm]], [[pages/services/control-plane]], [[pages/services/knowledge]]
- Similar: [[pages/invariants/local-first-default]]

## Change protocol

To add a credential key: give it an empty default, read it through the same self-gating pattern, and list it under Scope here.
