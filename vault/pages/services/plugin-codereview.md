---
title: "Plugin.CodeReview"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Model-backed code review of snippets, diffs and (with a GitHub token) a repository's key files."
source: ["src/Scout.Plugin.CodeReview/", "tests/Scout.Plugin.CodeReview.Tests/"]
reliability: high
---

# Plugin.CodeReview

> Model-backed code review of snippets, diffs and (with a GitHub token) a repository's key files.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/architecture/plugin-catalogue]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddCodeReviewPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:37`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:80` under the name `CodeReview`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:84`.
- **Gate:** GitHub token read at `src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:55` (snippet functions are always registered; the repository fetcher only when `Scout:Plugins:GitHub:Token` is set).
- **Model tools:**
  - `analyze_code_quality` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:18`): model review of a pasted snippet.
  - `detect_vulnerabilities` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:29`): model security review of a snippet.
  - `suggest_improvements` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:40`): model refactoring suggestions.
  - `generate_tests` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:52`): model-written test text returned in chat; nothing is written to disk.
  - `explain_code` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:66`): model explanation of a snippet.
  - `review_pull_request` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Snippets.cs:79`): review of a pasted unified diff; the description says it does not call GitHub.
  - `analyze_repository` (`src/Scout.Plugin.CodeReview/CodeReviewPlugin.Repository.cs:24`): GETs a bounded set of key files (`src/Scout.Plugin.CodeReview/GitHubRepositoryFetcher.cs:41,54,150`) then asks the model to review them.
- **Configuration keys:** section `Scout:Plugins:CodeReview` (`src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:22`), keys `DefaultRepoUrl` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:15`), `Temperature` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:18`), `MaxTokens` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:21`), `MaxFileBytes` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:24`), `MaxFiles` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:27`), `CorpusCharBudget` (`src/Scout.Plugin.CodeReview/CodeReviewOptions.cs:34`), `Scout:Plugins:GitHub:Token` (read at `src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:25`; owned by Plugin.GitHub). Names only; values live in the operator's configuration.
- **Data source:** The configured chat model (`IChatCompletionService`, resolved lazily at `src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:50`) and, for `analyze_repository`, HTTPS GET to the GitHub REST API (`:27`). Code and file contents leave the host for the configured LLM provider.

## Write behaviour

Every function is read-only (GET only to GitHub; results are text in chat). No function writes to an external system or to the host filesystem outside Scout's own store.

## Coupling by configuration key

It shares the GitHub token by reading `Scout:Plugins:GitHub` directly (`src/Scout.Plugin.CodeReview/CodeReviewServiceCollectionExtensions.cs:25,55`) instead of referencing Plugin.GitHub; the feature catalogue entry for it says the same ([[pages/services/control-plane]]). There is no enable flag: the snippet functions are always on.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddCodeReviewPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.CodeReview.Tests` (75 test attributes, 11 files): CapturingChatCompletionService, CodeReviewPluginMetadataTests, CodeReviewPluginTests, CodeReviewPromptTemplateTests, CodeReviewServiceCollectionExtensionsTests, CodeReviewerTests, GitHubRepositoryFetcherTests, KeyFileSelectorTests, RepositoryUrlParserTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
