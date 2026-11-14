---
title: "Follow-ups"
version: "1.0"
date: 2026-10-21
changes: "Initial tracker page"
page_type: roadmap
status: active
description: "Deferred work found while doing something else: each entry names the blocker, the evidence and what unblocks it."
source:
  - chat
reliability: medium
---

# Follow-ups

> Deferred work found during other changes. Each entry carries its own date; close an entry by deleting it in the change that resolves it.

## Scope

- **In scope:** small, concrete deferrals with a known unblock condition.
- **Out of scope:** initiatives with phases (they get their own page under `pages/roadmap/`).

## Entries

The local-model flow has its own tracker, [[pages/roadmap/ollama-local-models]]; its findings are not repeated here.

### Four desktop rules files load together on any ui file (2026-11-13)

- **Gap:** `.claude/rules/desktop-ui.md`, `desktop-channels.md`, `desktop-operator.md` and `desktop-quick-ask.md` all load when a file under `ui/packages/` is opened, about 30,000 characters together; `desktop-ui.md` (17,724) and `operator.md` (15,833) are each over the 6,000-character ceiling.
- **Blocker:** narrower `paths` per file, written by hand; the generator could only copy the service page's folders.
- **Unblock:** split the paths per package, then run `tools/checks/check-doc-sizes.sh`.

### The wiki skills keep one index while the context folder has one per area (2026-11-13)

- **Gap:** `wiki-integrate` and `wiki-crystallize` add a new page to `context/index.md`; each area now has its own `index.md`, which they do not update.
- **Blocker:** an instruction in those skills for the area index.
- **Unblock:** the skills name the area index of the page they add.

### A build older than the review job reads a review as the repository's audit (2026-11-13)

- **Gap:** the job row's kind and pull request arrive with [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]]; a build from before them looks a job up by owner and repository only, so once a review row exists, going back to that build makes its latest-job and stop calls take the review for the audit. No review row is written until the change that starts the job.
- **Blocker:** none until that change; then the owner's word on whether going back a version is supported at all.
- **Unblocks with:** the start of the review job refusing to run under a schema it does not know, or the decision recorded that a downgrade past it is not supported.

### Defaults taken for the checks before a review, waiting for the owner's word (2026-11-13)

- **Gap:** the first part of the checks before a review ([[pages/services/audit]], Checks before a review) is built with these defaults, shown to the owner and not yet confirmed. 1. The outcomes that block: the missing grant (the gate's own sentence, unchanged), no AI Source connected (not built yet), and a review of the same pull request and commit already running or done; everything else is a warning. 2. A review already done at the same commit blocks the start with its progress; the owner may prefer a warning that lets a done review run again. 3. The size shows an upper bound only, `at most M minutes`, the smaller of the turn time limit times two tries per batch for at most 10 batches and the ADR's 60 minutes, never an expected time. 4. The context window, when it is built, compares one batch at 4 characters per token and only warns. 5. Compose files are not read for tests that need Docker. 6. A job paused by the provider means one paused now, since no pause time is kept. 7. A resume of a paused review at the same commit runs no check and keeps the checks of its own start.
- **Blocker:** the owner's word on each.
- **Unblocks with:** each answer as a dated line on [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]], and the change that alters a default with its test.

### The checks before a review that are not built yet (2026-11-13)

- **Gap:** four checks [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] lists are left from the first part: the AI Source (a block when none is connected, read from the provider's name; the context window, known for Ollama only, through a default member on the provider factory; a job the provider paused now); Docker on this computer, through the sandbox's existing `docker version` probe made reachable through `IReviewEnvironment`, a read, with a time bound so a daemon that does not answer cannot stall the start; tests that need a Docker daemon, a count of manifests that name a container testing library, read as text with symbolic links skipped, a 1 MB cap and the folders the sandbox's scan skips, where a manifest that cannot be read or is over the cap is a "Could not check", never a zero; and the stack and test command, from the sandbox's detection, whose refusal sentences for Node, Python, Go and Java must be checked first for text echoed from the repository's manifests. The head's check counts also wait for the plugin to pass them, with the third change.
- **Blocker:** none; split out because the whole set passed the size of one change.
- **Unblocks with:** one change that builds the four, each with the test whose input makes its warning or block fire and one test where its source throws.

### The list of checks before a review starts is a proposal (2026-11-13)

- **Gap:** the owner asked for a validation shown before a process starts; the eight checks [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] lists are the proposed list, not yet confirmed one by one, and whether the same checks run before an audit and before a test run is open.
- **Blocker:** the owner's confirmation of the list and of its reach.
- **Unblocks with:** the confirmed list in the change that starts the job, with one test per check and per outcome (ok, warning, block).

### The review job's open points, built with defaults (2026-11-13)

- **Gap:** [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] left seven points to the owner; the three changes build these defaults until the owner answers. 1. A review with zero findings: answered by the separate change that gives a clean review a card, so the default of one plain sentence with the verdict and no card no longer applies. 2. GitHub failing on the head re-read before a batch: the job pauses, and a start resumes it. 3. The 60 minutes: wall clock from the start, pauses included. 4. The status as its own function: yes, in the third change. 5. A fork's head commit through `snapshot_repository` is unverified: the refusal is returned as it is. 6. `kind`, derivable from `pull_request`: kept as its own column, as approved. 7. A stopped review may be started again at the same commit; a done one is refused.
- **Blocker:** the owner's answers to points 2, 3, 4, 5 and 7; point 6 follows the approved design.
- **Unblocks with:** each answer recorded on ADR 0055 as a dated line, and the change that alters a default with its test.

### The checks before a review starts, for an audit and a test run (2026-11-13)

- **Gap:** [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] checks a review job before it is created (size, the AI Source's capacity and context window, grants, Docker, tests that need a Docker daemon, stack and test command, the head commit and its checks, a review already present), each ok, a warning or a block. An audit and a pull request's test run start without such checks.
- **Blocker:** the owner's decision on whether the same checks extend to the audit and to a test run.
- **Unblocks with:** if extended, the checks of each start with a table test of ok, warning and block per check; if not, the decision recorded on ADR 0055.


### Defaults taken for the clean review card, waiting for the owner's approval (2026-11-12)

- **Gap:** a review that found nothing now comes back as a findings card ([[pages/services/desktop-quick-ask]]), drawn first as the Main artboard state "Review: no findings" ([[pages/conventions/desktop-prototype]]). Four choices were taken as defaults: Request changes is not shown on that card, the chat's verdict function remaining the way to request changes; the sentence of what Scout read is shown only on the clean card, as plain text on its own line under "No findings.", worded "What Scout says it read: <read>" so it reads as the model's report beside the checks and tests Scout read itself (the wording waits for the owner's approval, and a count measured by the code, "read N of M files", replaces it when the review job lands); the posted body says "No findings." only when the review has none; Approve follows today's rules (hidden on a pull request the signing identity opened, off without a reviewed commit), with the tests chips above it.
- **Blocker:** the owner's approval of the artboard state; open.
- **Unblocks with:** the owner's answer on each default; a different answer changes the artboard first, then the card and its spec rows.


### get_pull_request_file reads a pull request's whole file listing on every call (2026-11-12)

- **Gap:** since the files are read page after page ([[pages/services/plugin-github]], Bounds), each `get_pull_request_file` call lists every file again to find the one asked, up to 30 requests of a hundred files on a pull request of 3,000; a review that reads many left-out diffs of a large pull request repeats the whole listing each time and spends GitHub's rate limit on it (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:107`).
- **Blocker:** none; a cache was kept out of the change that added the paging.
- **Unblocks with:** the listing kept for the head commit within a turn (or GitHub's per-file read, if one fits), with a test that two `get_pull_request_file` calls on the same commit list the files once.

### A wiki skill writes to whatever root the user names (2026-11-12)

- **Gap:** the crystallize skill, and the other wiki skills that write, take the wiki root the session finds or the user names, so a write can land outside this repository's context folder (issue #235). Reading from a root the user names was answered as not a defect (issue #139); this is the write side of the same choice.
- **Blocker:** the owner's decision on whether a wiki skill's write is limited to this repository's `context/`; open.
- **Unblocks with:** if limited, a check in the skills that refuses any other root and a scanner test for it; if not, the decision recorded with its reason on [[pages/conventions/decisions-that-look-like-defects]].

### The architecture reference page lives outside the context folder (2026-11-12)

- **Gap:** `doc/index.html` and `doc/architecture.svg` are a published reference kept by hand beside the context folder (issue #247); only the page's navigation script is tested, and nothing checks its prose or diagram against the context folder.
- **Blocker:** the owner's decision; open.
- **Unblocks with:** either the page generated from context folder pages and marked as generated, or the page recorded as the one hand-kept published artefact with the context folder named as its source of truth.

### Eval cases for the write contract's other decision paths (2026-11-12)

- **Gap:** the pipeline's four eval cases cover one decision of the write contract, a one-line outbound write that must be classified as a write and get the `read-only` lens ([[pages/conventions/tooling]], `.claude/skills/scout-pipeline`). Three paths the standard names have no case: a new LLM-invocable function, which the pipeline judges by what it can reach and never by its description; a change to a credential or its configuration binding, which adds the `secrets` lens and keeps every value out of `appsettings*.json`; and a write outside Scout's own folders, which is forbidden and stops the plan before any edit. Issue #370 asked for them.
- **Blocker:** none; each case needs graders that tell the affirmative answer from its denial, which is wider than the change that found it.
- **Unblocks with:** one case per path under `.claude/skills/scout-pipeline/evals/` (a prompt naming a concrete task, and graders for the stop or the lens it must produce), each grader with an answer it must accept and one it must reject in `tests/Scout.ArchTests/PipelineEvalGraderTests.cs`, which `EveryGraderOfEveryEvalCaseHasAnAcceptedAndARejectedRowInTheTable` already demands; then one live run of the suite recorded in the PR body.

### The review card does not show the sandbox run's note (2026-11-12)

- **Gap:** the run's note, which since issue 372 says which tests the .NET default left out ("Left out the tests marked Category=Docker, found in N file(s): the container has no Docker daemon."), reaches the GitHub plugin's `ReviewTests.Note` (`src/Scout.Plugin.GitHub/ReviewFindings.cs:270`), but the desktop's `ReviewTests` (`ui/packages/shared/src/ipc-contract.ts:1636`) has no `note`, its parser drops it (`ui/packages/renderer/src/app/core/review-findings.ts:207`) and the tests line never shows it, so the owner reading the card does not see what was left out ([[pages/services/sandbox]]).
- **Blocker:** a UI change of its own (one change per version), artboard first.
- **Unblocks with:** `note` on the TypeScript `ReviewTests`, its parser and the card's tests line, with the artboard updated and a spec row per state.

### The review card keeps only the end of a failed run's output (2026-11-13)

- **Gap:** a failed sandbox run's result leads with the lines that name the failure, then the last 4,000 characters ([[pages/services/sandbox]]); `run_pull_request_tests` answers the model both, but the run kept for the review's card is cut to its last 4,000 (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Tests.cs:249`), and the desktop's parser cuts to the last 4,000 again (`ui/packages/renderer/src/app/core/review-findings.ts:227`), so a failure the model saw in the failure section can be absent from the card ([[pages/services/plugin-github]]).
- **Blocker:** the kept output counts toward the 24,000-character findings block, and the card is a UI change of its own (one change per version), artboard first.
- **Unblocks with:** the kept run carrying the failure section within a bound the findings block allows, the parser keeping it, and the card's tests line showing it, with the artboard updated and a spec row per state.

### The .NET default command does not run a repository on the Microsoft Testing Platform (2026-11-13)

- **Gap:** the .NET stack's default is `dotnet test '<target>' --no-restore` (`src/Scout.Sandbox/SandboxDotNet.cs:126`); a repository whose `global.json` sets `"test": {"runner": "Microsoft.Testing.Platform"}` needs `dotnet test --project '<target>'` on the .NET 10 SDK: on the pinned SDK 10 image the default answers "No test projects were found." and exits 1 (checked 2026-11-13), so its tests reach the sandbox only through a command the project names. The end-to-end test passes that command itself (`tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs:483`).
- **Blocker:** none; found while checking what a failing test prints on that platform.
- **Unblocks with:** the detection reading the `test.runner` of `global.json` and the default choosing `--project`, with a unit row and a Docker row.

### A pull request can hide a failing test by marking it Category=Docker (2026-11-12)

- **Gap:** the .NET default leaves out every test a repository marks `Trait("Category", "Docker")` ([[pages/decisions/0043-the-verdict-on-a-pull-request]] v1.3), so a pull request that adds the trait to a failing test runs green in the sandbox. The note states the left-out and its count on every run and the diff shows the trait; nothing compares the count with the base branch's.
- **Blocker:** the card does not show the note yet (entry above); a rule beyond the note needs the owner's word.
- **Unblocks with:** the card showing the note, then, if the owner wants it, the count of marked files compared with the snapshot of the base branch and a changed count named in the verdict.

### A Category=Docker match in a comment or a string adds a filter that excludes nothing (2026-11-12)

- **Gap:** the .NET stack finds the trait by reading `*.cs` files as text (`src/Scout.Sandbox/SandboxDotNet.cs:47`), so the words inside a comment or a string literal (a test fixture, as in `tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs:417`) count as a declaration: the run gets `--filter 'Category!=Docker'`, which excludes nothing, and a note claiming a left-out that did not happen.
- **Blocker:** none; parsing C# to tell an attribute from text is a larger read than the stack does for any other manifest.
- **Unblocks with:** a report of a wrong note on a real repository; then blanking comments and string literals before the match, as the architecture tests' scanners do.

### Nothing in CI keeps Scout green in its own sandbox (2026-11-12)

- **Gap:** issue 372 was proved by hand on 2026-11-12, running Scout's own tree in the pinned .NET image under the sandbox's flags ([[pages/services/sandbox]]); no CI step repeats it, so a new test that needs something the image lacks (a native library, a daemon, the network) turns the sandbox run red unnoticed.
- **Blocker:** a CI job that pulls the pinned image and runs the whole solution under those flags costs a long run on every push; the owner has not chosen when it runs.
- **Unblocks with:** the owner choosing a trigger (weekly, or on a label), then one job running the two commands of the proof.

### The speech runtime test's oracle names only libgomp (2026-11-12)

- **Gap:** `A_File_That_Is_Not_A_Whisper_Model_Fails_As_A_Model_Load_Error_Where_The_Runtime_Loads_And_As_Unavailable_Where_It_Does_Not` (`tests/Scout.Transcription.Whisper.Tests/WhisperAudioTranscriberTests.cs:36`) decides which outcome to expect by whether `libgomp.so.1` loads on Linux; a machine where libgomp loads but another library of the runtime does not expects `WhisperModelLoadException` and fails, and on macOS and Windows it always expects the runtime to load.
- **Blocker:** no such machine has been seen; libgomp is the one the pinned .NET image lacks.
- **Unblocks with:** a failure of that test on a machine with libgomp; then the oracle names that library too.

### The keyring banner says "until a keyring is installed" when the availability check itself failed (2026-11-12)

- **Gap:** since the fix for a failed check that hid the warning, the shell banner shows the unavailable warning when `vaultIsAvailable` rejects (`ui/packages/renderer/src/app/shell/vault-availability-banner.component.ts:53`), with the generic text "Secret storage is unavailable on this system. Scout cannot save bot tokens securely until a keyring is installed. See docs for details."; for a check that failed, rather than a keyring that is missing, the sentence is imprecise. No artboard draws the shell banner in any state ([[pages/services/desktop-ui]], What the owner reads).
- **Blocker:** the owner's wording for a failed check, and an artboard state for the banner.
- **Unblocks with:** the banner drawn in `context/prototypes/desktop/` with the failed-check state and its sentence, then the component and its spec row taken to it.

### Features.dc.html has no state for a Docker work folder that cannot be read (2026-11-12)

- **Gap:** Docker Clear is refused when the work folder exists but cannot be listed, with "Scout could not read the work folder, so nothing was deleted." (`CLEAR_UNREADABLE`, `ui/packages/main/src/docker/docker.ts:194`). The sentence is new copy inside the refused alert the artboard draws for a run in progress (`context/prototypes/desktop/Features.dc.html:518`); the artboard has no state for an unreadable folder.
- **Blocker:** the owner's approval of the sentence and an artboard state for it.
- **Unblocks with:** the state drawn in `context/prototypes/desktop/Features.dc.html`, then the sentence and the panel's alert checked against it.

### Gemini is sent no temperature and no output limit (2026-11-12)

- **Gap:** every provider is called with the OpenAI connector's settings type (`src/Scout.Llm/Providers/ProviderSettings.cs:19`), and the Gemini connector reads none of its temperature or output limit, so a turn's temperature, its output limit and the 400-token cap of a live translation (`src/Scout.Llm/Providers/ProviderSettings.cs:39`) never reach Gemini. On the agent path no provider is sent a temperature or a limit. `tests/Scout.Llm.Tests/ProviderRequestSettingsTests.cs` states what each provider is sent today. Mistral and Hugging Face moved to the OpenAI connector on 2026-11-12 and are now sent all three and their declared functions; whether they accept them is in the two entries below.
- **Blocker:** a change in what a provider is sent needs a live call with the owner's key for that provider; the move that found this was a refactor and changed nothing on the wire.
- **Unblocks with:** a change scoped to Gemini, with its request test updated to the new body and a live call. Two things keep it from being done without a key: moving Gemini to the OpenAI connector changes the URL and the way the key is sent, where the Mistral and Hugging Face moves kept both; and on a Gemini model that thinks, the output limit counts the thinking, so sending the 800-token chat cap may cut an answer short or leave it empty. Azure AI Inference has no such gap (it is offered functions and sent both values); moving it would only drop its package, and it too changes the URL.

### Mistral through the OpenAI connector is not verified against the live Mistral API (2026-11-12)

- **Gap:** Mistral now runs on the OpenAI connector ([[pages/services/llm]], Hot spots), and no key was available, so the request tests state what is sent, not what Mistral accepts. Fields the old connector never sent: `max_completion_tokens` where the Mistral connector's own request type names `max_tokens` (read from its 1.82.0-alpha assembly; every live translation and every turn with a limit), `stream_options` `{"include_usage":true}` on every streamed turn, `tools` entries with `"strict":false`, an empty `description` and `Plugin-function` names, `tool_choice` `auto`, an assistant message with `"content":""` beside its `tool_calls` and a `tool` message without `name` on the second request of a function round trip, and embeddings `encoding_format` `base64` instead of `float`. No longer sent: `safe_prompt`, the default `temperature` 0.7 and `top_p` 1. A 429, a 5xx or a reset is retried three times by the client.
- **Blocker:** a live call with the owner's Mistral key.
- **Unblocks with:** one streamed turn with a limit and a declared function through a function round trip, one live translation and one embedding against `https://api.mistral.ai/v1`, reading for each field whether Mistral rejects it, ignores it (the limit not applied, no usage on the stream) or honours it; a rejected field is then a change of its own.

### Hugging Face chat through the OpenAI connector is not verified against a live Hugging Face endpoint (2026-11-12)

- **Gap:** Hugging Face chat now runs on the OpenAI connector ([[pages/services/llm]], Hot spots), and no key was available, so the request tests state what is sent, not what a Hugging Face endpoint accepts; the serverless host, an Inference Endpoint and a self-hosted text-generation-inference server may each answer differently. Fields the old connector never sent: `max_completion_tokens` where the HuggingFace connector's own request type names `max_tokens` (read from its 1.82.0-preview assembly; every live translation and every turn with a limit), `stream_options` `{"include_usage":true}` on every streamed turn, `tools` entries with `"strict":false`, an empty `description` and `Plugin-function` names, `tool_choice` `auto`, and an assistant message with `"content":""` beside its `tool_calls` and a `tool` message without `name` on the second request of a function round trip. No longer sent: `temperature` 1 and, on a translation, `stream` false. A 429, a 5xx or a reset on chat is retried three times by the client. Unchanged and also unverified: the default host `https://api-inference.huggingface.co`, and a configured endpoint ending in `/v1` posting chat to `/v1/v1/chat/completions`.
- **Blocker:** a live call with the owner's Hugging Face key.
- **Unblocks with:** one streamed turn with a limit and a declared function through a function round trip, one live translation and one embedding against the default host and against the endpoint the owner uses, reading for each field whether the endpoint rejects it, ignores it (the limit not applied, no usage on the stream, no tool call) or honours it; a rejected field, the default host or the `/v1/v1` form is then a change of its own.

### The .NET 11 package line held at 10.0.15, Microsoft.Extensions.AI at 10.11.0 (2026-11-12)

- **Blocker:** the 2026-11-12 package update moved the .NET 10 packages to 10.0.15 and stopped short of 11.0.0. `Microsoft.AspNetCore.OpenApi`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore.Sqlite` and Entity Framework Core 11.0.0 target `net11.0` only, and every project targets `net10.0` (`Directory.Build.props:3`) under the pinned SDK 10 (`global.json`). The `Microsoft.Extensions.*` 11.0.0 packages carry a `net10.0` build but move with the rest of the line, and .NET 11 changes a behaviour Scout relies on: "Starting in .NET 11, when a `BackgroundService` throws an exception from its `ExecuteAsync` method and `HostOptions.BackgroundServiceExceptionBehavior` is set to `BackgroundServiceExceptionBehavior.StopHost`, the task returned from calling `RunAsync`, `StopAsync`, or `WaitForShutdownAsync` fails with an exception. The application typically exits with a failure exit code (non-zero)." `StopHost` is the default, Scout sets no other, thirteen classes under `src/` derive from `BackgroundService`, so a failing one would end the host with an exception out of its run call instead of a clean exit, and the desktop supervisor logs that exit code with each restart (`ui/packages/main/src/worker/supervisor.ts:218`). `Microsoft.Extensions.AI`, `.Abstractions` and `.OpenAI` 11.0.0 need `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` 11.0.0 in their `net10.0` group, which the held line pins at 10.0.15, so they stay on 10.11.0, and `Microsoft.Extensions.TimeProvider.Testing` stays on 10.11.0 with them, the same release; Semantic Kernel 1.82.0, the newest, is built against `Microsoft.Extensions.AI` 10.5.
- **Unblocks when:** the target framework moves to `net11.0` with SDK 11, a change of its own: `Directory.Build.props`, `global.json`, the CI SDK and the self-contained worker publish, then the whole line, `dotnet ef` included, at one version, with a test of the worker's exit when a `BackgroundService` fails and a read of the rest of the .NET 11 breaking-change list against the hosts.
- **Affects:** the `10.0.15` and `10.11.0` entries of `Directory.Packages.props`, `.config/dotnet-tools.json`, [[pages/decisions/0052-persistence-behind-stores-on-entity-framework]].

### xunit and the Bot Framework packages are deprecated for new package ids (2026-11-12)

- **Blocker:** none; out of scope for a version update, which adds no package. nuget.org marks `xunit` 2.9.3 deprecated (reason Legacy, alternative `xunit.v3`) and `Microsoft.Bot.Builder`, `Microsoft.Bot.Builder.Integration.AspNet.Core`, `Microsoft.Bot.Connector` and `Microsoft.Bot.Schema` 4.23.1 (reason Legacy, alternatives `Microsoft.Agents.Builder`, `Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Connector` and `Microsoft.Agents.Core`). Both are the newest of their ids and have no known vulnerability (`dotnet list package --vulnerable --include-transitive` is empty).
- **Fix:** xunit: every test project to `xunit.v3`, which runs as an executable, with its runner and the ArchTests rules that read test attributes rechecked; the 19 projects at their current totals. Bot Framework: [[pages/services/adapter-teams]] onto the Agents SDK, an adapter rewrite behind the same chat abstraction, with its own plan.

### The control plane's model delete answers a timeout of the local server as an unknown error (2026-11-11)

- **Gap:** `OllamaApiClient.DeleteModelAsync` turns a delete the local server did not answer in time into `TimeoutException` (`src/Scout.ControlPlane/Providers/OllamaApiClient.cs:191`), and `ModelsGrpcService.DeleteModel` maps `ProviderUnknownException`, `ProviderLifecycleUnsupportedException`, `ProviderNotReadyException` and `HttpRequestException` only (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:65`), so the timeout reaches the caller as gRPC `UNKNOWN` with no text that says the server timed out. Since 2026-11-11 the provider service logs the failed delete with the exception ([[pages/services/control-plane]]); the answer on the wire is unchanged. No caller meets it today: the desktop does not call `Models`.
- **Blocker:** none; outside the change that ruled and logged the control plane's local-model writes, which left `ModelsGrpcService` untouched.
- **Unblocks with:** a `TimeoutException` catch in `DeleteModel` mapped to a status that says the local server did not answer in time, with one gRPC test in which the fake API times out and the status and its text are asserted.

### The control plane's GitHub feature still requires a default repository nothing reads (2026-11-11)

- **Gap:** the feature catalog declares a required `default_repo_url` setting for GitHub (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:33`), so a GitHub setup through the control plane's credential path is refused without it and the control plane stores it, but nothing in the worker, the GitHub plugin or the desktop reads it. The desktop's "Use a token" no longer asks for it: the owner decided on 2026-11-11 that the dialog has the token only ([[pages/services/desktop-github]], Use a token). A real default repository, the one Scout would use when a question names none, does not exist; it would be a product item of its own.
- **Blocker:** none; outside the desktop dialog change that found it.
- **Unblocks with:** removing the setting from the catalog with its rows in `FeaturesServiceTests` and `JsonFileFeatureStateStoreTests`, or, if the owner wants a default repository, a product item that defines who sets it, where it is kept and which questions use it.

### A pull request verdict given again may be sent twice after a claim that was not confirmed (2026-11-11)

- **Gap:** the verdict card's once-only enqueue, `EnqueueApprovedOnceAsync` (`src/Scout.ControlPlane/Actions/JsonFileActionQueue.cs:155`, called from `src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:185`), answers an earlier action unless it was rejected or executed with an outcome starting with `failed` (`src/Scout.ControlPlane/Actions/JsonFileActionQueue.cs:178`). That queues a verdict again when its earlier action was recorded `failed: not confirmed`, or rejected by the claim path as not confirmed after a lapsed claim across a revoke or a new grant; both mean it may already be on GitHub. The findings card's issues stopped doing this in review round 2 of [[pages/roadmap/approve-together]] phase 1, where such a row answers the click ([[pages/services/control-plane]], The findings card's issues).
- **Blocker:** none; out of the scope of that review, which was the card.
- **Unblocks with:** the same rule on the verdict path, through `WriteGateService.NotConfirmedOutcome` and `WriteGateService.IsNotConfirmed`, with a test that drives the lapse through the real claim; the verdict's own check on GitHub for one already given may cover part of it, which the change should read first.

### Six name rules still accept a trailing newline (2026-11-11)

- **Gap:** a .NET regex `$` also matches before a final newline, so a value such as `main` followed by a newline passes these rules: the git reference and file extension rules of the GitHub plugin (`src/Scout.Plugin.GitHub/GitHubNames.cs:39`, `:33`), the verdict card's repository part and commit rules (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:126`, `:129`), the owner control's name rule (`src/Scout.ControlPlane/Controls/OwnerControlService.cs:65`) and the comment rules' name rule (`src/Scout.ControlPlane/Operator/CommentRules.cs:311`). The repository name rules of the plugin and of the findings card were anchored with `\z` in review round 1 of [[pages/roadmap/approve-together]] phase 1.
- **Blocker:** none; none was named by a finding of that review.
- **Unblocks with:** `\A` and `\z` in each, one row per rule with a trailing newline in its existing test, as the repository rules got.
- **Done (2026-11-12):** the verdict card's repository part and commit rules, the owner control's name rule and the comment rules' name rule are anchored with `\A` and `\z`, and so are the findings card's finding id rule (`src/Scout.Plugin.GitHub/ReviewFindings.cs:55`) and the comment desk's id rule for fetched comments (`src/Scout.Operator/CommentDesk.cs:368`), each with a trailing-newline row (`tests/Scout.Operator.Tests/CommentDeskTests.cs:90`, `tests/Scout.ControlPlane.Tests/WriteGate/PullRequestVerdictServiceTests.cs:198`, `tests/Scout.ControlPlane.Tests/Controls/OwnerControlServiceTests.cs:39`, `tests/Scout.ControlPlane.Tests/Operator/CommentServiceTests.cs:186`, `tests/Scout.Plugin.GitHub.Tests/GitHubPluginFindingsTests.cs:129`). Still open: the plugin's git reference and file extension rules, which feed only reads and a snapshot into Scout's own folder.

### The channel operator's model calls say "did not answer" for a missing model, a used-up credit or a rejected key (2026-11-11)

- **Gap:** `ModelJson.AskAsync` wraps every failure of the chat call in one `InvalidOperationException`, "The AI model did not answer; check the AI Source in Scout Desktop." (`src/Scout.Operator/ModelJson.cs:52`), so the operator's planning and writing steps never pass through `LlmFailures.Translate`: an AI Source without the model ([[pages/services/llm]], the missing-model refusal of the same day), an account out of credit or a rejected key all read as the model not answering. The chat, quick ask, live translation and the audit each say what happened ([[pages/services/conversation]], [[pages/services/audit]]).
- **Blocker:** none; out of scope of the missing-model fix, which kept to the paths the credit fix covered.
- **Unblocks with:** `ModelJson` translating the failure with `LlmFailures.Translate(ex, chat.GetModelId())` and choosing its sentence by the translated type, with one row per refusal in its tests.

### The pending list has no paging, so enough rows pass the message limit on their other fields alone (2026-11-11)

- **Gap:** `ListPendingActions` and `GetActionGroup` bound the listed title and body of one answer to 8 MiB, and nothing bounds the number of rows ([[pages/services/host-control-plane]]). With that text budget spent, the rows' other fields fill the other 8 MiB: at 394 bytes a row, an issue row with a 32-character id and group id, a 60-character title in its summary and a short repository, the answer passes the 16 MiB limit at 21,291 waiting actions; at 1,056 bytes, a comment row whose summary carries three values at the worker's 200-character bound and a 140-character repository, at 7,944. The control plane does not bound `Summary`, `Target`, `GroupId` or `Repository` at enqueue, so a worker that sent a summary of several megabytes would make even one row break the list.
- **Blocker:** none; a review found it, with no report of a queue that large.
- **Unblocks with:** paging on both calls, or a bound at enqueue on those four fields and a test at the computed row count, whichever the owner prefers.

### A project deletion does not look at a YouTube action still waiting for approval (2026-11-11)

- **Gap:** `DeleteProjectAsync` refuses a project with an open action, one approved or executing ([[pages/services/control-plane]]). An action still pending, queued through the worker's write-gate request for a project target, is not looked at; approved later, it would name a deleted project. No production path queues a project's YouTube action that way today: the operator service queues them already approved.
- **Blocker:** none; outside the fix that serialised the deletion with the three approvals.
- **Unblocks with:** the deletion refusing or rejecting a pending action that names the project, with a row in the deletion's tests, when a path that queues one appears.

### A Slack start a stop overtakes lets the worker start the next adapter during shutdown (2026-11-11)

- **Gap:** when a stop overtakes the Slack start, during the sign-in or during the connect, the start returns normally ([[pages/services/adapter-slack]]). The conversation worker then goes on to start the adapters after Slack while the host is shutting down. The race was already there for the sign-in phase.
- **Blocker:** none; it is the worker's start loop, outside the adapter fix that found it.
- **Unblocks with:** the worker's start loop looking at its stopping state before each adapter, with a test in the host's test project that a stop during one adapter's start leaves the later adapters unstarted. The same lane adds the missing test for a start token the host cancels while the Slack connect is pending.

### The audit job's batches cannot read the open issues the audit skill asks for (2026-11-11)

- **Gap:** `code-audit` says that, when `list_issues` is available, the audit reads the open issues once and reports none of them again ([[pages/services/skills]]). The job's batches read that sentence as part of their instructions but are offered only five reads and `present_audit_findings` (`src/Scout.Audit/AuditBatchRunner.cs:41`), not `GitHub.list_issues`, so an audit run as a job can still report a finding an open issue already describes. An audit run in one conversation is offered the function through the skill's functions line.
- **Unblocks with:** `GitHub.list_issues` added to the batch's offered set (a read, so no write enters the job), or the job reading the open issues once and passing their titles to each batch as it passes the findings so far, with a test in `AuditBatchRunnerTests` that a batch does not report a finding an open issue describes.

### What the started entry of a reversible write leaves open (2026-11-12)

- **Gap:** issue #323 closed the case of a reversible write that ran with no entry in the write log: the gate now appends a `started` entry before it answers allowed and the worker's record names its id ([[pages/decisions/0032-per-feature-write-permissions]], amendment of 2026-11-12; [[pages/services/control-plane]]). Left out on purpose: the desktop's own local writes, which do not pass the gate; a retry or outbox for a record that fails; a worker journal and any signal at worker start, since the state is derived when the log is read and nothing reconciles at start; any `.proto` edit. Known readings that follow from the design: a `started` entry whose write never ran, the worker having stopped between the answer and the function, reads "may have run" after 30 minutes; a reversible write that runs longer than 30 minutes shows not confirmed until its record arrives; a record sent after a revoke in the middle of a run is refused, as before, so that write reads not confirmed. Two neighbouring cases stay as they were: YouTube still hands a lapsed, unrecorded claim out again, a second send, and GitHub records a lapsed claim as not confirmed although nothing may have been sent.
- **Defaults taken for the owner:** the words "Started" and "Not confirmed: Scout stopped before it recorded how this ended, so it may have run"; the window is 30 minutes, the claim lease; the state is derived when the log is read, with no reconcile at worker start; no record is accepted after a revoke in the middle of a run. Each is one constant or one branch in `src/Scout.ControlPlane/WriteGate/WriteGateService.cs` and its listing.
- **Blocker:** none; each item above waits for the owner's word.
- **Unblocks with:** the owner's answer per item; a change to the words or the window is a constant and its tests, a record after a revoke or a reconcile at start is a new branch in the gate with a test for it.

### The shared reply styles never reach a reply's rendered Markdown (2026-11-10)

- **Gap:** `REPLY_STYLES` (`ui/packages/renderer/src/app/core/reply-styles.ts:7`) is one of the component styles of the Chat screen and the quick-ask window, which use Angular's default emulated encapsulation. Angular adds the component's content attribute to every compound selector, so `.reply p`, `.reply > :first-child`, `.reply pre`, `.reply h1` and the rest only match elements the component's own template created. A reply's Markdown is set through `[innerHTML]` and carries no such attribute, so none of those rules apply: a paragraph keeps the browser's 1em margins, which puts about 16 px between Scout's header and the first line where the artboards draw 8 px, and the code block, table and heading rules do not apply either. This has been so since the reply styles were introduced. The finding card's body has the same gap: `.body :is(p, ul, ol)` in `ui/packages/renderer/src/app/core/review-findings.component.ts` does not reach the Markdown it sets through `[innerHTML]`, which the 2026-11-10 captures show as a blank line between a finding's text and its Fix. It may also be the cause of "A long line of code makes the whole conversation scroll sideways" below, since `.reply pre` declares the block's own scroll.
- **Evidence:** seen on 2026-11-10 in the captures of version 2 of [[pages/roadmap/prototype-migration]] (quick ask, both themes). The cause is inferred from Angular's emulated encapsulation and not measured.
- **Blocker:** none; the file is shared by the Chat lane and quick ask ([[pages/services/desktop-quick-ask]]).
- **Proof:** a spec that renders a reply with two paragraphs and a fenced code block in the quick-ask window and on the Chat screen, and reads the computed `margin-top` of the reply's first `p` (0) and the computed `overflow-x` of its `pre` (`auto`) through `getComputedStyle`. It fails today. It needs a DOM that applies the component's styles, so it belongs in the end-to-end harness ([[pages/decisions/0051-desktop-end-to-end-tests]]) if jsdom does not cascade them.
- **Unblocks with:** the Markdown rules reaching the `[innerHTML]` content (rules scoped under `:host ::ng-deep .reply`, or the rules moved to the global stylesheet under a class only the reply carries), with the proof above.

### The control plane's six JSON files outside the persistence decision (2026-11-10)

- **Gap:** [[pages/decisions/0052-persistence-behind-stores-on-entity-framework]] puts every use case that keeps data behind a store on Entity Framework Core over SQLite; the control plane keeps six in JSON files (the action queue, the write log, the feature state, the operator store, the local runtime configuration and the owner's controls), and no page records why. The ADR's amendment of 2026-11-10, proposed, names them as outside the decision for now.
- **Blocker:** the owner's decision on the amendment.
- **Unblocks with:** either the six behind stores on Entity Framework, one at a time with their migrations and the write log's history kept, or the owner's reason to keep files written into the ADR and the amendment accepted.

### No written support policy for older desktop releases (2026-11-10)

- **Gap:** `.github/SECURITY.md` says security fixes land on `main` and reach the desktop app in the next release, and that the release marked Latest is the one the site and the update notice offer; nothing in the repository says whether an older release (or the `.deb`, which the updater cannot replace in place) gets a fix of its own, or for how long a release is supported.
- **Unblocks with:** the owner's decision on the supported releases (the Latest only, or a window), written in `.github/SECURITY.md`'s Supported versions section.

### A file read only in its first part does not make the audit incomplete (2026-11-10)

- **Gap:** the audit's verdict is `incomplete` when a file of an area was never read or an area failed. A file read only up to the reader's cap (its first 20,000 characters) is named as partly read in the scope lines and still leaves a clean verdict possible for code that was not all read.
- **Unblocks with:** the owner's decision on whether a truncated read counts as unread for the verdict; if so, the same rule that names unread files takes the truncated ones.

### A partly read batch's card drops a worse verdict the model gave (2026-11-10)

- **Gap:** the card of an area whose outcome names an unread file says `incomplete` whatever the model said (`src/Scout.Audit/AuditWorker.cs:171`), so a model's `needs rework` is not shown on that card; the findings and their severities stay.
- **Unblocks with:** a decision on the card's words when both apply (for example `incomplete, needs rework`), with a row in `The_card_of_an_area_read_in_part_says_incomplete_whatever_verdict_the_model_gave`.

### Project discovery checks cancellation only after the whole listing (2026-11-10)

- **Gap:** `DiscoverAsync` lists the root's folders whole before its loop checks the token (`src/Scout.Knowledge/ProjectDiscoveryService.cs:49`, `:59`), so a cancellation during a slow listing waits for the listing to end.
- **Unblocks with:** a check of the token before the listing, if a root large or slow enough to matter is reported; with a test that discovery under a cancelled token throws without listing.

### A Teams turn that read the session just before a stop can still run on the old handler (2026-11-10)

- **Gap:** a turn reads the handler and the lifetime token in one step (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:106`); a stop that lands after that read and before the handler call returns while the turn goes on to call the old handler with a cancelled token.
- **Unblocks with:** a decision whether StopAsync waits for turns in flight, as the Discord adapter does (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:169`), and a deterministic test that holds a turn between its read and its call.

### This repository's own tests in the container meet tests that need what the container lacks (2026-11-10)

- **Gap:** since the CI line choice was fixed ([[pages/history/plugin-github]]), a review of this repository with no profile command runs the .NET default: `dotnet restore 'Scout.slnx'`, then `dotnet test 'Scout.slnx' --no-restore` under `--network none` in the SDK 10 image (`global.json` 10.0.100 with `latestMinor` takes SDK 10.0.403), 2 CPUs, 4g, 512 pids, 20 minutes in all, read-only root, the owner's uid. Read from the code and the tests, not run in a container: the nine `Category=Docker` tests of `tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs` fail, because the image has no `docker` and `RequireDocker` fails them by design, never skips (`tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs:121`); CI keeps them out through `tools/checks/test-per-project.sh` and runs them in a step of its own. So the run ends exit 1 and the review says the tests failed although CI is green. No test was found that needs the network outside loopback, a keyring or a display; the tests that start `/bin/sh`, `od`, `tr`, `sleep` and `/usr/bin/env` find them in the Debian-based image; the tests that find the root by `Scout.slnx` find it in the copy. Unverified: whether a whole-solution build and test fits 20 minutes on 2 CPUs, and whether 512 pids, which counts threads, holds for parallel test hosts.
- **Options, for the owner:** a per-repository test command in the conventions profile, which already takes precedence (for example `dotnet test Scout.slnx --no-restore --filter Category!=Docker`, `--no-restore` because the restore before it has the network and the tests do not; a filtered line is eligible); recognising a CI line that calls a script (`bash tools/checks/test-per-project.sh ...` is the one CI uses here, and it names no runner Scout matches); or a way for tests to declare that they need a daemon, which the sandbox's default leaves out.
- **Unblocks with:** the owner's choice, then a container run of this repository on the owner's machine to confirm the time and the pids.

### A review reads every non-zero exit as "Tests failed" (2026-11-10)

- **Gap:** the review's tests line shows "Tests failed (exit N)" whenever `ran` is true and the exit is not 0, and never reads `reason` there (`ui/packages/renderer/src/app/core/review-findings.ts:82`). The worker already says when the tests never started: a failed restore answers `ran` true with "The restore exited with code N; the tests did not run.", and since 2026-11-10 a test command that exits 126 or 127 answers "The test command could not be started (exit N: ...)." ([[pages/services/sandbox]]). The model sees both in `run_pull_request_tests`' answer; the review card and the posted tests line do not (the Docker card already shows the run record's `reason` as written, `ui/packages/renderer/src/app/features/docker-card.component.ts:547`).
- **Unblocks with:** `testsLine` reading "Tests could not be started: <reason>" when `ran` is true and `reason` is set, with rows for a restore failure, exits 126 and 127, an exit 1 without a reason and a pass; no contract change, the field is already in the block. Exit 2 from a line `sh` cannot parse stays "Tests failed".

### Models keeps "Restart failed" after Scout restarted itself (2026-11-10)

- **Gap:** when a restart asked from Models fails by readiness timeout, main answers `failed` and the screen shows the reason, "Restart Scout in Settings, Scout worker, to use <source>." and "Restart failed" on the row. The killed worker's exit then makes the supervisor schedule a start of its own (`ui/packages/main/src/worker/supervisor.ts:375`), and when that start works Scout is running again while the screen still says the restart failed. The same holds on AI Sources and every other card that shows a failed restart.
- **Blocker:** the renderer learns of a restart only from the answer to its own request; nothing tells Models that the worker came back.
- **Unblocks with:** Models following the worker state it can already read (`scout:worker.status` and its change push) and taking the failed state off once the worker runs with the choice it was asked for, with a row for a failure by readiness timeout followed by the automatic start.

### A rate limit answer from GitHub fails the action and the rest keep being sent (2026-11-10)

- **Gap:** the executor sends approved actions one second apart (`src/Scout.Host.Console/GitHubActionExecutor.cs:36`), which is GitHub's guidance for requests that create content, not a guarantee. A 403 or 429 that names a time to wait is recorded as failed with the rate-limit sentence like any other refusal, so the owner has to ask and approve again, and the actions still approved are sent a second apart into the same limit. The tests line of a verdict is also posted right after the verdict, with no pause between the two.
- **Unblocks with:** the executor waiting the time GitHub names before it takes the next action, and a decision on the refused one (left approved for one more run, or failed as now); with a test on the fake clock where the second of three actions is answered 429 with a retry-after and the third is not sent before it has passed.

### What the audit cards still take from the model after the sent titles (2026-11-12)

- **Gap:** since 2026-11-12 a finding is sent once a done issues turn on its repository carries its title (`sentTitles`, `ui/packages/renderer/src/app/core/review-findings.store.ts:409`), no longer from the reply's English "queued for your approval". What the card knows is still what it asked, not what was queued: a done turn where the model called nothing still reads as queued; a turn that fails after queuing some issues marks none sent, so Open offers them again; whether an issue was already queued is the model's check, with no dedupe at the gate for model calls; and a reply that cites an earlier issue link of the same repository still marks the cards opened, since the queued sentence no longer wins over a link. Two findings of one audit with the same trimmed title both read as sent once one is sent, since a title is the only identity an issue has on the turn. An issue the owner later rejects in Approvals still reads "Issue queued" on the card, and a done turn whose reply says the issues could not be queued is contradicted by the card; both stay so until the card reads the queue's state.
- **Unblocks with:** the card's Open going through `ApproveCardIssues` (approve-together phase 3, [[pages/roadmap/approve-together]]), which takes the model off the path, dedupes in code and answers each issue's "Opened #N"; then a gate-level dedupe for the model's own `create_issue` calls.

### A short and a full hash of one commit are two keys (2026-11-10)

- **Gap:** one action per pull request and commit compares the commit as text, in the renderer's store, in main and in the control plane (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:327`). A review that names `ec7f951` and a later one that names the full hash of the same commit are two keys, so a second Post or verdict is accepted.
- **Unblocks with:** comparing by prefix when one side is a prefix of the other, at least seven characters, in the three places, with a row per place.

### Doc summaries on functions that are not exported (2026-11-10)

- **Gap:** `stopRunning` in main and `outcome` in the findings store carry a doc summary although neither is exported (`ui/packages/main/src/quick-ask/quick-ask.ts:1375`, `ui/packages/renderer/src/app/core/review-findings.store.ts:422`); both files already had such summaries on local functions, so the lane followed the precedent, which is wider than the rule that allows a summary on a public type or member.
- **Unblocks with:** a decision on local functions in `ui/`, then one sweep of the two files.

### No GitHub action goes through the one-minute release window in a test (2026-11-10)

- **Gap:** the claim releases an action whose approval is not yet in the write log and skips it for up to a minute (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:200`); the tests of that window use a card verdict and YouTube rows, none a GitHub row asked by the model, and none checks that a released GitHub action is not later taken for a lapsed claim.
- **Unblocks with:** one row on a GitHub row in the control plane's release tests: released, then handed out once its approval is logged, never recorded as not confirmed.

### No timeout test for a comment or a reply (2026-11-10)

- **Gap:** a request that times out after it left is tested for an issue, a review and a verdict (`tests/Scout.Host.Console.Tests/GitHubActionExecutorTests.cs:227`, `:184`); `comment_on_pull_request`, `comment_on_issue` and `reply_to_review_comment` have a 502 test only, and `comment_on_issue` shares the pull request comment's route.
- **Unblocks with:** the timeout rows for the three, asserting the "may already be there" sentence.

### A GitHub action claimed and never handed to the worker is also recorded as not confirmed (2026-11-10)

- **Gap:** the claim marks an action executing before it reads the write log (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:150`). When that read fails, or the action cannot be returned to the queue while its approval is still on its way to the log, the action stays executing without ever reaching a worker. The same happens when the claim's reply never reaches the worker, and when the worker's read of the grant just before the send throws, which sends nothing and records nothing (`src/Scout.Host.Console/GitHubActionExecutor.cs:128`). For YouTube it is handed out after the lease, as the control plane's tests pin; for GitHub, since 2026-11-10, the lapsed claim is recorded as `failed: not confirmed`, "may already be on GitHub", although nothing was sent (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:179`), and the owner has to ask again.
- **Unblocks with:** a mark on the action set only when the claim returns it to the worker, the not-confirmed rule applying only to actions that carry it; with a test where the log read fails on a GitHub claim and the action is handed out after its lease.

### A lost record of a GitHub write is reported as not confirmed although the worker knew how it ended (2026-11-10)

- **Gap:** when the control plane cannot take the record of a write the worker just sent, the worker logs it and keeps nothing (`src/Scout.Host.Console/GitHubActionExecutor.cs:177`), the action stays executing and is never handed out again ([[pages/invariants/no-executive-writes]], once means at most once): 30 minutes later it is recorded as `failed: not confirmed` (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:69`) and the owner is told to look on GitHub, although the worker, still running, knew the write succeeded and its link.
- **Unblocks with:** the worker retrying the record itself, by action id, at each tick until the control plane takes it, which needs no new claim; with a test where the record fails once and the action ends recorded with its link and no second send.

### A queued GitHub write is checked only after the owner approved it (2026-11-10)

- **Gap:** a call the gate queues never runs the function, so the function's own checks (names, the caps on a review, a comment or an issue; `src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:393`, `:416`) run only when the executor sends the approved action. The owner can approve a card that is then recorded as failed for a reason known when it was asked.
- **Unblocks with:** running the checks that need no network when the call is queued, and answering the model the refusal instead of queuing; the checks that read GitHub (the diff a review comment anchors to) stay at the send.

### A rejected action keeps its whole payload (2026-11-10)

- **Gap:** an executed action keeps only the fields that say which pull request, commit and verdict it was (`src/Scout.ControlPlane/Actions/JsonFileActionQueue.cs:536`); a rejected one keeps the whole payload, up to 200,000 characters, in `actions.json` for as long as the action is kept, and every read of the queue parses it.
- **Unblocks with:** cutting the payload on rejection as on execution, keeping what the card needs to show which action it was.

### Main polls each verdict or review it follows every 2 seconds (2026-11-10)

- **Gap:** after a click on a review card main reads the action's state from the control plane every 2 seconds until it is given or failed (`ui/packages/main/src/ipc/github-verdict.ts:26`); since 2026-11-09 a review's Post is followed the same way. Each read is a gRPC call that parses the whole queue file.
- **Unblocks with:** the control plane pushing action changes on the stream the Approvals screen already uses, main reading only on a change.

### The two GitHub runners each parse a payload with their own helpers (2026-11-10)

- **Gap:** `GitHubWriteRunner` and `GitHubVerdictRunner` each carry a `Read`, a `Text` and a `Number` over the payload's JSON (`src/Scout.Plugin.GitHub/GitHubWriteRunner.cs:124`, `src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs:83`), written twice with small differences.
- **Unblocks with:** a third reader of an action's payload; until then two copies are cheaper than a shared type (Rule C).

### The five GitHub write functions still carry a body that sends (2026-11-10)

- **Gap:** every call to `submit_pull_request_review`, `create_issue`, the two comments and the reply is queued by the gate, so the function bodies that send and answer "Opened issue #N ...: URL" (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:499` and its four siblings) never run from a model call; only the internal senders the runner uses do. The bodies and their `<returns>` text describe a result no caller gets, and a catalog row turned back to `Write` by mistake would make a body send on the grant alone, which only the tests guard today ([[pages/decisions/0032-per-feature-write-permissions]]).
- **Unblocks with:** bodies that answer the queued sentence and send nothing, the senders staying internal to the runner.

### What is left of the review turn nothing builds (2026-11-10)

- **Gap:** a review's Post goes to the control plane since 2026-11-09 and main no longer reads a `Review to post` block since 2026-11-10, but the contract still has the turn's `review` flag and the marker (`ui/packages/shared/src/ipc-contract.ts:1538`, `:1586`), and the Chat screen and the quick-ask window still have the "with the review" tag for it, with a spec each.
- **Unblocks with:** deleting the flag, the marker, the two tags and their specs in one change; the compiler names every use.

### The conductor's own failure write is not bounded (2026-11-10)

- **Gap:** the stream pump bounds the write of an error ending, but the conductor, for an error it catches outside the pump, calls the sink's failure write with no cancellation at all, so a sink that never returns there holds the turn.
- **Unblocks with:** the same bounded wait the pump uses, applied to that write, with a test on a sink that never returns.

### The pump's begin, updates and completing write still wait on the sink (2026-11-10)

- **Gap:** only the writes of an error ending are bounded by the pump itself. The begin call, the interim updates and the write that completes a successful reply are awaited with the run's token, so a sink that ignores cancellation there still holds the run past the overall timeout.
- **Unblocks with:** the same bounded wait on those three calls, with what an abandoned begin or update leaves in the channel decided first (a message with a cursor and no ending).

### A Slack reply ends in two cursors while it streams (2026-11-10)

- **Gap:** the pump renders an interim body of up to the sink's 3,000 characters with its own cursor at the end, and the Slack sink cuts what it is given to 2,999 and adds a cursor of its own without removing the pump's (`src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:140`, `:265`); the Discord sink removes it (`src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:146`). So every interim update on Slack ends in two cursors, and one the pump rendered at exactly 3,000 characters is cut again by one character's worth: it loses its newest text and shows Slack's "... (truncated)" although it was within the declared limit.
- **Evidence:** the real pump with its default options driven into the real sink through its test seam on 2026-11-10: a 100-character text arrived as the text and two cursors; a 2,999-character text arrived as its first 2,982 characters, the marker and one cursor. The sink's own test of the pump trims every trailing cursor before comparing (`tests/Scout.Adapter.Slack.Tests/Streaming/SlackSinkBoundaryTests.cs:254`), so it does not see it.
- **Blocker:** none; in [[pages/services/adapter-slack]], outside the pump change that found it ([[pages/history/conversation]]).
- **Unblocks with:** the Slack sink removing a trailing cursor from the text it is given before it adds its own, as Discord's does, with a test where the pump's interim body of exactly 3,000 characters reaches the edit unchanged and one where a short text ends in a single cursor.

### An error ending after a very long partial reply is still lost on Discord (2026-11-10)

- **Gap:** the pump keeps an error sentence whole within the limit a sink declares ([[pages/services/conversation]]), and the Discord sink declares none (`src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:116`) while it keeps the first five parts of 3,968 characters and ends the fifth with "(reply truncated)" (`:187`). The sentence is the end of the body, so once the partial text fills the five parts the rate-limit, quota, sign-in or key sentence is dropped. Driven through the real sink on 2026-11-10 with text made of seven-letter words, the sentence arrived after a partial text of 19,700 characters and was lost after one of 19,840; where the line falls depends on where the splitter cuts.
- **Blocker:** declaring the limit is not safe. `MaxBodyLength` is read by the pump for every body, not only for error endings: past the limit an interim body becomes the newest text behind a marker (`src/Scout.Conversation/Streaming/StreamPump.cs:403`), so all five messages would be rewritten on every update where today only the last one changes (`src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:243`); a long reply that ended well would lose its beginning and keep its end (`src/Scout.Conversation/Streaming/StreamPump.cs:217`), where today it keeps its beginning; and an attached block longer than the limit would be dropped (`:331`). A voice reply would only lose the quote's length (`src/Scout.Conversation/Streaming/VoiceReplySink.cs:13`). Five parts also hold less than 19,840 characters of real text, because a part ends at a paragraph, a line or a space.
- **Unblocks with:** the Discord sink making room itself when a terminal body does not fit its parts and the body ends in a sentence the pump marked as the ending, or a second declared number that the pump applies to error endings only; either with a test in `tests/Scout.Adapter.Discord.Tests` where an error ending after a 25,000-character partial text delivers the whole sentence, and one where a long reply that ended well still keeps its beginning.

### One marker on a failed reply rests on the sinks' footers (2026-11-10)

- **Gap:** a failed reply over the limit shows its first part, one marker and the sink's footer because the pump cuts the partial text from its end and the Slack and Teams sinks, which cut again to make room for their footer, cut from the same end and so remove the pump's 15-character mark (`src/Scout.Conversation/Streaming/StreamPump.cs:281`, `src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:188`, `src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:202`). That holds while a sink removes at least 15 characters, which a footer of 26 or 52 characters does; a sink with a shorter footer, or one that cuts from the start, would show two marks. `RenderCapabilities` has no way to say how much room a sink's failure footer needs, so the pump cannot leave it.
- **Blocker:** none; nothing is wrong today, and `tests/Scout.Adapter.Slack.Tests/Streaming/SlackSinkBoundaryTests.cs:263` and `tests/Scout.Adapter.Teams.Tests/Streaming/TeamsSinkStreamPumpTests.cs:111` fail if either sink changes its cut.
- **Unblocks with:** a declared footer reserve in `RenderCapabilities` that the pump subtracts before it cuts, so no sink cuts a failed reply twice; worth doing when a third sink with a length limit is written.

### A failed reply copies the whole buffer before it cuts it (2026-11-10)

- **Gap:** each ending of a failed run calls `buffer.ToString()` on the whole streamed text and then keeps at most the sink's limit (`src/Scout.Conversation/Streaming/StreamPump.cs:139`, `:153`, `:160`, `:167`, `:175`, `:191`, `:203`): one copy of the whole reply, on the large object heap above about 42,000 characters, once per failed run. The code did the same before the endings were capped.
- **Blocker:** none; measured nowhere, and it happens once per failed turn.
- **Unblocks with:** the endings reading only the range they keep, `buffer.ToString(start, length)`, when the text is over the limit, with the existing tests of the endings unchanged.

### No ceiling on a reply outside Teams (2026-11-10)

- **Gap:** `StreamPumpOptions.OverallTimeout` is the hard ceiling on a pump run, and the registered pump does not set it (`src/Scout.Conversation/ConversationServiceCollectionExtensions.cs:40`). The only ceiling in the composed worker is the 120 seconds the Teams sink declares (`src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:99`). On Discord, Slack and the desktop a model stream that stalls, or a sink write that never returns, holds the turn until the caller cancels; the bound on the terminal write of a failed reply ([[pages/services/conversation]]) only applies once a timeout exists.
- **Blocker:** a product decision, outside the change that found it. No value was set there.
- **Unblocks with:** a default for the three adapters without one, decided with these in hand:
  - the value has to sit above the longest turn that is working as intended. A turn can wait on the sandbox's test run, whose own limit is 20 minutes (`src/Scout.Sandbox/DockerSandboxRunner.cs:35`, called from `src/Scout.Plugin.GitHub/GitHubActionsPlugin.Tests.cs:52`), so an overall ceiling under about 25 minutes would cut a pull request verdict short; 30 minutes is defensible as a ceiling, and it is no protection against a stream that stalls for one minute. A stall needs a second, shorter bound on the time between two tokens or on a single sink write, which the pump does not have;
  - the pump's token is the one the model call and the functions it calls run under (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:126`), so a ceiling that fires can cancel a write in flight; what an approved write does when cancelled midway has to be checked first ([[pages/invariants/no-executive-writes]]);
  - a timed-out reply ends through `FailAsync`, whose reason is logged and never shown, so on Discord and Slack the user reads "the AI provider returned an error", which is not what happened; a ceiling users will meet needs a sentence of its own;
  - the desktop has Stop, so its case is the weakest; Discord and Slack have nothing;
  - a test per adapter on a fake clock, where the stream stalls and the reply ends at the ceiling with that sentence.

### A revoke between a claim and its release leaves the action approved (2026-11-10)

- **Gap:** inside the minute the claim waits for an approval's log entry, the action is held as executing. Clearing the grant in that moment rejects the approved and unstarted actions and skips executing ones, so the release returns this one to approved with no rejection logged. It is never handed out (no grant hands out nothing, and a later grant makes it older than the grant), but it stays approved in the list until the next revoke or a call that supersedes it.
- **Unblocks with:** the claim, when it finds no grant or an action older than the grant, rejecting and logging the approved leftovers it meets; a test with a revoke between the claim and the release.

### An approval logged a minute late reads as approved over rejected (2026-11-10)

- **Gap:** the claim rejects an approved action whose log entry is still missing after one minute, on the log it read at the start of that poll. A producer that stalls a minute or more between its queue save and its log append then writes `approved` after the `rejected` entry: the action stays rejected and never runs, and the history, newest first, shows approved on top. A channel project approved for publishing this way is left waiting for an action that will not run.
- **Unblocks with:** the producer's append refused, or followed by a correcting entry, when the action is no longer approved; the history showing an action's last state once.

### A release is matched by the action and not by the claim it gives back (2026-11-10)

- **Gap:** the release returns an executing action to approved by its id. A release delayed past the 30-minute lease could give back the claim of a later poll. It also runs on the poll's own cancellation, so a poll cancelled between the claim and the release leaves a just-approved action executing until the lease lapses.
- **Unblocks with:** the release carrying the claim time it gives back and changing nothing when it differs, and running to the end once the claim was made.

### The approval time is read from the system clock (2026-11-10)

- **Gap:** the three producers stamp an approval with the system clock, while the claim measures the one-minute bound on the injected clock. The tests set the stamp by hand; a producer on the injected clock would let them run both sides on one clock.
- **Unblocks with:** the producers taking the time from the injected provider.

### An action settled from the write log carries no result (2026-11-09)

- **Gap:** an action whose write is already in the write log is marked executed with the logged outcome and no result (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:226`), because the write-log entry keeps the outcome only. A GitHub verdict settled that way reads as given with an empty link, and a settled failure shows "not recorded" in place of the failure text (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:219`, `:220`).
- **Blocker:** a decision on where the result is kept: on the write-log entry, or on the action before it is marked.
- **Unblocks with:** the result kept where the settle can read it and passed to the mark, with a test where a verdict logged but never marked is settled and its state carries the link, and one where a settled failure carries its text.

### A publication left publish_approved with no action to run refuses every later publish (2026-11-09)

- **Gap:** when the owner revokes YouTube writes, disables YouTube or deletes its credentials while an upload is queued, the approved actions are rejected and the publication is left `publish_approved` (`src/Scout.ControlPlane/Operator/OperatorService.cs:1711`); after a new grant every publish is refused with "The video of this project is publish_approved; wait for the upload to end." (`src/Scout.ControlPlane/Operator/OperatorService.cs:1416`) and nothing resets it. It predates the change of 2026-11-09, which returns the project only when the publish's own actions cannot be queued or logged ([[pages/history/control-plane]]).
- **Blocker:** none.
- **Unblocks with:** the revoke returns such a publication to none, or to `failed` with the revoke as its error, in the same pass that ends the running experiments; with a test where, after a revoke with an upload queued and a new grant, the owner publishes again and it is queued once.

### A worker that stops between an external write and its record sends the write again (2026-11-09)

- **Gap:** the control plane settles an action only when its write is already in the write log (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:216`, [[pages/history/control-plane]]). A worker that stops, or loses the control plane, after GitHub or YouTube accepted the call and before any record was written leaves the action executing with no line in the log; once the 30-minute claim lapses it is handed out again and the write is sent a second time. The reply path already refuses a second post from the comment's own status (`src/Scout.Operator/ActionExecutor.cs:429`); the pull request verdict and the other YouTube actions were not checked for an equal guard.
- **Since 2026-11-10, GitHub only:** a GitHub action whose claim lapses with no record is no longer handed out again: the shared claim records it as `failed: not confirmed`, saying it may already be on GitHub (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:179`, [[pages/invariants/no-executive-writes]]). The gap stays open for YouTube, which does not declare `UnrecordedClaimMayHaveBeenSent`: its lapsed claim with no record is handed out again, and only the upload and the reply are guarded by the operator's own state.
- **Blocker:** a decision on the key: where it is kept and which external calls can carry one or be read back first.
- **Unblocks with:** per action kind, either a read of the target before the write (the verdict already on the pull request, the video already uploaded) so a second run records what the first one did instead of sending, or an execution key written on the action at the claim and sent with the write where the service refuses a repeat. One test per kind where the worker stops after the external call and the action's second run sends nothing.

### A full re-index drops the rows of a file that exists but cannot be read (2026-11-09)

- **Gap:** `ProjectIndexer.IndexAsync` skips a file whose read fails with a warning (`src/Scout.Knowledge/ProjectIndexer.cs:43`) and then replaces the whole project, so that file's chunks from the earlier index are gone until it can be read again; accepted on 2026-11-09 when the embedding failure stopped emptying the index ([[pages/history/knowledge]]). Since 2026-11-11 the same holds for a folder below the root that cannot be listed, which is skipped with a warning instead of aborting the index (`src/Scout.Knowledge/ProjectIndexer.cs:194`). The watcher is not affected: it keeps the rows of a file it cannot read.
- **Blocker:** `ReplaceProjectAsync` replaces by project; keeping one file's rows needs the store to take the paths to leave alone, or the indexer to read them back first.
- **Unblocks with:** a replace that carries the skipped paths and leaves their rows, vectors and text, with a test over a real SQLite file where an unreadable file's chunks survive a full re-index.

### The watcher does not retry a file whose re-index failed (2026-11-09)

- **Gap:** a changed file whose embedding call fails keeps the rows it had, which is the point, but the pending entry is dropped with the batch (`src/Scout.Knowledge/ProjectWatchService.cs:153`), so the index holds the file's old content until the file changes again or the project is indexed in full ([[pages/services/knowledge]]).
- **Unblocks with:** the failed entries put back in the pending set with a bounded number of attempts, on the watcher's fake clock in the test; `ProjectWatchService.cs` was out of scope on 2026-11-09.

### Two index runs of one project can interleave (2026-11-09)

- **Gap:** nothing serialises `IndexAsync` per project, and the path is recorded after the chunks in a second statement (`src/Scout.Knowledge/ProjectIndexer.cs:65`). Two runs for the same name at two paths, such as a snapshot registered again while the old one is still indexing and a second tool call arriving before it ends, can leave the chunks of the old path under the recorded path of the new one, when the old run's replace and its path write both land between the new run's replace and its path write; the project then answers from the old tree until its path changes again. The stale mark covers the case where the old run ends first ([[pages/services/knowledge]]); two concurrent first uses of one project also index it twice.
- **Unblocks with:** one index run per project name at a time in `ProjectResolutionService` (a second caller awaits the first), or the path written in the transaction that writes the text rows, which means `ReplaceProjectAsync` takes the path.

### search_project fails with the embedder down although the text index could answer (2026-11-09)

- **Gap:** a question is embedded before the search (`src/Scout.Knowledge/ProjectKnowledgeService.cs:148`), and only "no embeddings" or a vector of another size falls back to the text search (`:150`); a provider that is unreachable makes `search_project` answer "Could not search project" while `find_symbol` and the stored text would still answer. Since 2026-11-09 a failed re-index keeps the index and a project at an unchanged path keeps answering, so this is the remaining way an embedder outage silences search on such a project.
- **Unblocks with:** the text search as the answer when the question's embedding call fails for a transport reason, with one warning, and a failing-call test through the real connector.

### Measure the text-ranked path now that every member is a chunk (2026-11-09)

- **Gap:** one chunk per field, property and constructor took Scout's own `src/` from 3,071 chunks to 5,244, 1,560 of them one-line fields ([[pages/history/knowledge]]). The vector half is settled for fields: they are not embedded (`src/Scout.Knowledge/ProjectIndexer.cs:215`), so they never take one of `search_project`'s five vector hits. What is not measured is the text-ranked path, which answers on a provider without embeddings and when the vector search finds nothing: `bm25` favours a short row, so a one-line field can rank above the method or the type that explains it, and `unicode61` splits `_retry_count` at the underscores into words a question may match by accident (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:185`, `:26`). The 507 property chunks are still embedded and are as short.
- **Blocker:** no recorded set of real project questions with the chunk each should find.
- **Unblocks with:** a handful of questions over a real repository, each with its expected chunk, run on the text path and on the vector path; if fields or properties crowd the top five, leave fields out of `SearchTextAsync` (they stay in `FindSymbolAsync`), rank a member under its type, or stop embedding properties.

### Ollama and Gemini validate MaxBatchSize and HttpTimeoutSeconds and never use them (2026-11-09)

- **Gap:** both factories reject a `MaxBatchSize` or an `HttpTimeoutSeconds` below 1 (`src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs:82`, `:87`; `src/Scout.Llm/Providers/Gemini/GeminiLlmProviderFactory.cs:69`, `:74`) and neither value is read anywhere else, so a file's embedding inputs go out in one request whatever its size, up to 118 inputs for one file of Scout's own tree, under the HTTP client's default timeout ([[pages/services/llm]], [[pages/services/knowledge]]).
- **Unblocks with:** the generator splitting its inputs into requests of `MaxBatchSize` and the timeout applied to the embedding client, with a failing-call test for a request that times out; or the two options removed.

### A project whose index run always fails is indexed again on every tool call (2026-11-09)

- **Gap:** when one file's embedding request fails every time (an input the provider still rejects under the 16,000-character cut, a content filter), `ResolveAsync` starts a full index on each `search_project` or `find_symbol` call (`src/Scout.Knowledge/ProjectResolutionService.cs:135`) and embeds again every file enumerated before the failing one before it throws again; on a paid provider every such tool call is paid for ([[pages/services/knowledge]]).
- **Unblocks with:** per project, the time of the last failed run kept in `ProjectResolutionService` and no new run inside a fixed interval on an injected `TimeProvider`, as the skill index build does (`src/Scout.Skills/SkillSelector.cs:32`, `:162`), with tests for "not retried before the interval" and "retried after it".

### An index from before 2026-11-09 has no struct, enum, property or field chunks until it is indexed again (2026-11-09)

- **Gap:** a project indexed before every member kind had a chunk is adopted as it is, with no embedding call ([[pages/services/knowledge]], [[pages/history/knowledge]]), so `find_symbol` finds its structs, enums, delegates, constructors, properties, events and fields only after its next index: a changed path, a registration at run time, or the `index <project>` command. Nothing tells the owner that an existing install is missing them.
- **Blocker:** the owner's decision: a re-index embeds every file again, which costs money on a paid provider, so it is not started without being asked for.
- **Unblocks with:** one re-index per project offered after the upgrade and run only when the owner accepts it; or, to evaluate first because it needs no embedding call, a pass that writes only the text rows again and leaves the stored vectors, since `find_symbol` reads the text table alone (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:193`). Either way with a test over a database of the old shape where a field is found afterwards.

### A re-index that stops between the vector write and the text write is not repaired (2026-11-09)

- **Gap:** a full replace writes the vectors, then the text rows (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:131`). When a re-index of a project that already has an index stops between the two, the project keeps its old text rows beside the new vectors and its recorded path, so the next resolve takes it as indexed: `find_symbol` and the text-ranked search answer from the old rows while the vector search answers from the new chunks, until the next full index. Only a first index is repaired, because only there are the text rows missing ([[pages/services/knowledge]]). The write order, and so this gap, is older than 2026-11-09.
- **Unblocks with:** a mark that a full replace is in progress, set before the vectors and cleared in the transaction that writes the text rows, which makes the project count as not indexed while it is set; with a test that fails the text write of a re-index and then finds the new symbol after the next resolve.

### One watcher write makes a project whose first index did not finish count as indexed (2026-11-09)

- **Gap:** the existence check reads the text table, and the watcher's per-file replace writes text rows for the one file that changed (`src/Scout.Knowledge/ProjectWatchService.cs:200`, `src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:147`). With the watcher on, a file saved in a project whose first index stopped half-way, or was never run, gives the project text rows for that file alone; the next resolve finds chunks and no recorded path, adopts the configured path and answers from that one file, and the project is not indexed again ([[pages/services/knowledge]]). The same happened before 2026-11-09.
- **Unblocks with:** the watcher leaving a project alone until a full index has recorded its path, or the existence check requiring a recorded path once the project has a row in `project_source`; with a test where a first index is interrupted, the watcher re-indexes one file, and the next resolve still indexes the whole project.
### Renderer specs that wait on the clock or read source by the working directory (2026-11-09)

- **Gap:** under a loaded machine the renderer suite times out at the default 5 s per test. Three things in the wizard and shell specs still depend on real time or on where the suite is started: a real 300 ms wait in "the Launch the shell replaces at the handover" (`ui/packages/renderer/src/app/shell/shell.component.spec.ts:274`); `colourLiterals`, which reads a component's source with a path relative to the working directory, in 13 specs, 8 of them under `wizard/` and `shell/` (for example `ui/packages/renderer/src/app/wizard/token-paste/token-paste.component.spec.ts:379`); and the fixed four-pass settle loops around the confirmation dialog in the older Ready tests (`ui/packages/renderer/src/app/wizard/ready/ready.component.spec.ts:217`).
- **Blocker:** none; left out of the wizard migration because they predate it.
- **Unblocks with:** fake timers or the awaited promise in place of the 300 ms wait, a path built from `import.meta.dirname` in `colourLiterals`, and the dialog's `afterOpened` and `afterClosed` in place of the loops, as "Reset open" in the same Ready spec already does.

### Approvals' History says nothing was written while the first read failed (2026-11-09)

- **Gap:** when the first read of the approvals fails, the History tab prints "Scout has not written anything yet." under the alert (`ui/packages/renderer/src/app/review/review.component.ts:258`). The write log was never read, so the sentence states something Scout does not know. The Waiting for you tab already holds its empty sentence back while an error stands (`ui/packages/renderer/src/app/review/review.component.ts:123`); History has no such guard, and had none before the prototype migration's version 9. The artboard's Error state shows no panel at all ([[pages/roadmap/prototype-migration]], Approvals).
- **Blocker:** none; it is behaviour, so it was left out of a change of appearance.
- **Unblocks with:** the sentence shown only when a read answered with no writes, with a spec that fails the first read and opens History. The Error row of "Approvals shows each state of the prototype" in `ui/packages/renderer/src/app/review/review.component.spec.ts` lists `writes-empty` on the History tab today and changes with the fix.

### The operator window's Approvals page is still pinned to the dark canvas (2026-11-09)

- **Gap:** the Approvals screen follows the theme in the main window, and since version 9 of [[pages/roadmap/prototype-migration]] matches its artboard in both themes, but the operator window keeps the same component on the dark canvas: `/operator/approvals` is not in `THEMED_ROUTES` (`ui/packages/renderer/src/app/operator/operator-shell.component.ts:49`), and the shell's spec pins that (`ui/packages/renderer/src/app/operator/operator-shell.component.spec.ts:95`).
- **Blocker:** none; the operator shell is version 12 of the migration.
- **Unblocks with:** the route added to `THEMED_ROUTES`, the shell's spec updated, and the page captured in the light theme in the operator window.

### Meetings: Delete all stays live while a delete runs (2026-11-09)

- **Gap:** on the Meetings screen a running delete disables every row's Delete but not Delete all (`ui/packages/renderer/src/app/meetings/meetings.component.ts:52` has no `disabled`, where the rows have it at `:128`). A click on Delete all during a delete opens a second confirmation and, confirmed, sends a second `scout:local.deleteMeetings`. The first delete's `finally` (`:387`) then re-enables the rows' Delete while the second is still in flight, and an error of the first that arrives late stays on the page after the second succeeds, because the second cleared the error only when it started (`:378`). It predates the prototype migration; `meetings.component.spec.ts` pins today's behaviour in its "Delete all running" case, which this fix changes.
- **Unblocks with:** Delete all disabled while `deletingBusy` is set, or one delete at a time enforced in `confirmDelete`, with a test that a second delete cannot start before the first settles.

### A start a stop pre-empted still counts as a started adapter (2026-11-09)

- **Gap:** a Discord `StartAsync` that a stop overtakes during the login now returns normally with the gateway never started (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:108`; [[pages/services/adapter-discord]]). The host's start loop takes a start that returned as a started adapter: it goes on to the next adapter (`src/Scout.Host.Console/ConversationWorker.cs:40`, `:45`) and then signals readiness (`:66`) without looking at the stopping token, and its own `StopAsync` stops the adapters before the base class cancels that token (`:73`, `:76`), so a host that is stopping can start the remaining adapters and report itself ready.
- **Blocker:** none; outside the adapters, audit and memory lane that found it ([[pages/services/host-console]]).
- **Unblocks with:** the start loop leaving, and readiness not signalled, once a stop was asked for (the stopping token cancelled before the adapters are stopped, or a check after each start), with a test where a stop lands while the first adapter's start is pending and asserts that no later adapter starts and readiness is never signalled.

### A long line of code makes the whole conversation scroll sideways (2026-11-09)

- **Gap:** a reply holding a code block with one line wider than the window makes the turn list itself scroll sideways, in the quick-ask window and on the Chat screen, where only the code block should. The block declares its own horizontal scroll (`.reply pre`, `ui/packages/renderer/src/app/core/reply-styles.ts:178`, `overflow-x: auto` at `:184`), but nothing above it bounds its width, so the block, the reply, the row and the turn all grow to the line's length. Prose, a long path and a token with no break in it wrap as they should.
- **Evidence:** seen on 2026-11-09 through the end-to-end harness ([[pages/decisions/0051-desktop-end-to-end-tests]]) while capturing version 5 of [[pages/roadmap/prototype-migration]], with a scripted reply holding one 136-character line of code: in the 640 px quick-ask window the turn list was 625 px wide and scrolled to 1158 px, with the turn, the row, the reply and the `pre` each 1146 px wide in a 601 px box; the main window's Chat screen showed a horizontal scrollbar under the reply card. Both themes. It was not proven to predate that branch: no capture from before it exists, and the branch changed none of the files involved (it changed the page margin, the font and the Material button and badge overrides; the code block sets in the monospace stack).
- **Blocker:** none; the files belong to the Chat lane ([[pages/services/desktop-quick-ask]]).
- **Unblocks with:** the code block's ancestors bounded to the list's width so the block alone scrolls, with a spec for a reply holding one long line of code (the list's scroll width equal to its client width) in both the Chat screen and the quick-ask window, and the same case in the end-to-end suite.

### The control-plane host trusts its stat before the self-test proves it (2026-11-09)

- **Gap:** the socket pre-flight runs while the host is built and can delete a stale socket on the word of a stat that the self-test only verifies afterwards ([[pages/services/host-control-plane]]). On a platform where the layout is read wrongly, the pre-flight acts first and the refusal to start comes second.
- **Unblocks with:** the self-test moved ahead of the build of the host, with a test that a failed self-test leaves an existing socket file untouched.

### Intel macOS reads the wrong stat layout (2026-11-09)

- **Gap:** the host declares macOS x64 supported but binds the plain `stat` symbol, which on Intel macOS is the 32-bit-inode variant with the mode and the owner at other offsets. The self-test now also requires a regular file, so the host refuses to start there instead of misreading in silence. Only `osx-arm64` is released, so nobody is affected today.
- **Unblocks with:** the 64-bit-inode symbol on macOS x64 and a run on that platform, or macOS x64 removed from the supported list.

### A new feature status would fail the whole feature list (2026-11-09)

- **Gap:** an unmapped feature status now throws instead of reaching the desktop as unspecified, and the test of the mapping lists the four statuses by hand. A fifth status added without its mapping would compile, pass the tests and fail every `ListFeatures` call.
- **Unblocks with:** the mapping test driven from the enum's own values.

### Desktop unit tests that fail only under heavy machine load (2026-11-09)

- **Gap:** on a machine shared with other builds, at a load average of 40 to 121, `bun run test` in `ui/` failed in three of four runs with nothing changed under `ui/packages`. Twice the first test of five heavy renderer spec files ended in "Test timed out in 5000ms." (`ui/packages/renderer/src/app/features/features.component.spec.ts:178`, `ui/packages/renderer/src/app/features/github-connect.component.spec.ts:268`, `ui/packages/renderer/src/app/features/kubernetes-connect.component.spec.ts:193`, `ui/packages/renderer/src/app/features/youtube-connect.component.spec.ts:181`, `ui/packages/renderer/src/app/production/production.component.spec.ts:339`); the renderer sets no test timeout, so each has vitest's 5 s (`ui/packages/renderer/vitest.config.ts:9`); that it is always a file's first test suggests the first one also pays for compiling the component, which was not measured. Once the worker that should have started on the other voice model had not been spawned when the test looked (`ui/packages/main/test/whisper-model-switch.integration.test.ts:225`): its `settle` (`ui/packages/main/test/whisper-model-switch.integration.test.ts:157`) returns once the worker's state and the number of spawns have not changed for 300 ms, which under load can be before the restart begins. Each passes alone: the five files with 316 tests, the model-switch file with 9; and the fourth full run passed.
- **Unblocks with:** for the renderer, the cause confirmed by timing a file's first test against its second under load, then, if it is the compile, that cost taken out of the first test's time (a warm-up in `beforeAll` of the five files, or a longer timeout for a file's first test), measured under an artificial load; for the model switch, `settle` waiting for the spawn the test expects rather than for 300 ms of quiet; each fix shown by running the suite under load, where it fails today.

### Measure the pick by words on real questions (2026-11-08)

- **Gap:** the 0.55 threshold and the ignored English and Portuguese words of the pick by words are constants tuned on the bundled skills and a hand-written table, not on questions users asked (`src/Scout.Skills/LexicalSkillIndex.cs:18`, `:24`, the table `tests/Scout.Skills.Tests/LexicalSkillIndexTests.cs:39`); a custom skills directory or a new bundled skill changes every score, and "list open pull requests" scores 0.52 for `code-review`, just under it ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]).
- **Blocker:** no recorded set of real questions yet.
- **Unblocks with:** real questions in English and Portuguese, each with the skill it should pick or none, collected without names or repository content, run as a table test against the bundled skills; tune the threshold and the word list from the misses, and move the threshold to `SkillsOptions` only if an operator needs it.

### The Ollama pull's ssh-keygen check proves nothing on Windows (2026-11-08)

- **Gap:** the pull test puts an `ssh-keygen` shell script first on the child's PATH and asserts it never ran (`ui/packages/main/test/llm-pull.test.ts:88`); Windows finds a program on PATH only with an `.exe` or `.com` name, so there the check passes without the shim being reachable. The home isolation holds on every OS, `HOME` and `USERPROFILE` both pointing at the temporary home (`ui/packages/main/test/llm-pull.test.ts:124`), so the `~/.ollama` check still proves nothing is written in the real home ([[pages/services/desktop-ui]]).
- **Unblocks with:** a tiny `ssh-keygen.exe` that records it ran, built in the test or shipped as a fixture, first on PATH on Windows, with the same mutation (a pull that spawns `ssh-keygen`) failing the test there.

### Code identifiers are one token in the full-text index (2026-11-07)

- **Blocker:** deferred by the owner on 2026-11-07; G3 shipped with the default tokenizer ([[pages/roadmap/gap-plan]]).
- **Evidence:** `chunk_fts` uses the `unicode61` tokenizer (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:26`), which splits on non-alphanumerics only, so `ApplyDiscount` is one token and a question with "discount" does not rank the method that applies it when the provider has no embeddings ([[pages/services/knowledge]]).
- **Unblocks with:** a second indexed column holding each chunk's identifiers split at case changes and digits (`ApplyDiscount` as `apply discount`), written by `ReplaceTextRowsAsync`, with a ranking test that "discount" finds `ApplyDiscount`; measured on a real repository against today's ranking before it ships.

### A structural graph over a repository snapshot (2026-11-06)

- **Gap:** a review reads a snapshot file by file and searches it by text or by embedding ([[pages/decisions/0039-repository-snapshot-under-the-data-folder]]); "who calls this", "what breaks if this changes" and "where are the hubs" still cost many reads. The owner asked on 2026-11-06 whether a graphify-style graph (AST nodes and edges, god nodes, blast radius) should be a third toolset.
- **Unblocks with:** one real review on a snapshot first, to see which questions the reads and the index leave unanswered; then a graph built once per snapshot under its folder and three functions (callers, affected, hubs), weighed against the build cost per snapshot.

### Every local `update` carries the whole reply so far (2026-11-06)

- **Gap:** the sink contract is cumulative for Teams's sake, and the local adapter writes the full text on every `update` (`src/Scout.Adapter.Local/LocalResponseSink.cs:47`), so a long reply streaming to the desktop costs O(n²) bytes on the worker's stdout; harmless at today's sizes (32,000-character cap), waste at the pace of a code review ([[pages/services/adapter-local]], [[pages/decisions/0013-conversation-core-streaming-sink]]).
- **Unblocks with:** an `append` event on the local wire only, the sink sending what grew since its last write and main concatenating; the core's contract untouched; measured first on a 30 KB reply.

### Main re-sends the whole conversation to every window on every event (2026-11-06)

- **Gap:** each event reshapes one turn and broadcasts the whole list, up to 200 turns with their replies, to every window (`ui/packages/main/src/quick-ask/quick-ask.ts:763`); with replies now rendered from Markdown each push costs more on the renderer side ([[pages/services/desktop-quick-ask]]).
- **Unblocks with:** a per-turn push (`scout:local.turn` with the one turn that changed) that the renderer patches into its list, the full list kept for the first load and a clear; measured first with a long conversation open in both windows.

### The presenter description cannot be given from the chat (2026-11-04)

- **Gap:** `youtube_make_video` starts an `animated_character` project with an empty presenter description, so its presenter picture is drawn from the direction and the topic alone (`src/Scout.Operator/OperatorPipeline.cs:378`); only Scout Desktop's Start, Produce and Delegate take the description ([[pages/services/operator]], [[pages/services/desktop-operator]]).
- **Unblocks with:** a `presenter` parameter on `youtube_make_video` and on the owner's produce and delegate functions, owner only like the direction, at most 300 characters, carried to the control plane's start and produce requests, with a plugin test that a non-owner's presenter starts nothing.

### The animated character never blinks or turns its head (2026-11-04)

- **Gap:** the assembler lays only the mouth over the presenter picture (`src/Scout.Operator/Assembler.cs:298`), so the face is otherwise frozen for the whole scene ([[pages/services/operator]]).
- **Unblocks with:** an eye box the owner marks beside the mouth box and a closed-eyes picture laid over it for about 150 ms every 3 to 6 seconds, timed by the scene's length so the segment key stays stable, with the overlay asserted in an assembler test; head turns wait for the realistic photo animation spike of phase 8c.

### Only one mouth style (2026-11-04)

- **Gap:** every animated character speaks with the same nine cartoon mouths Scout Desktop bundles (`SCOUT__OPERATOR__MOUTHSPATH`), whatever the presenter picture's style ([[pages/services/operator]], [[pages/services/desktop-operator]]).
- **Unblocks with:** a second set of nine shapes under its own folder, chosen per project and stored like the motion, its name joining the presenter segment's identity (`src/Scout.Operator/Assembler.cs:410`), with a segment-key test that a changed style renders the scene again.

### Mouth shapes drawn to match the presenter picture (2026-11-04)

- **Gap:** the bundled mouths are drawn once for every character, so their colour and line can differ from the picture the image service made ([[pages/services/operator]]).
- **Unblocks with:** an image-edit request per shape against the presenter picture's mouth box, nine pictures saved in the media folder and counted in the 40 pictures a day, behind the readiness of an image-edit service; measured against the bundled set on a few pictures before it is the default.

### Rhubarb on macOS is not verified (2026-11-04)

- **Gap:** Rhubarb Lip Sync 1.14.0 publishes a macOS archive, but no run of Scout Desktop's macOS build has shown that the bundled program starts there, signed and outside quarantine, and answers `--version` ([[pages/services/desktop-operator]]); until then the readiness of lip sync on a Mac is unproven (`src/Scout.Operator/RhubarbLipSync.cs:62`).
- **Unblocks with:** one run of the macOS build that reports `lip_sync_available` and renders a presenter scene with its mouth moving, recorded on [[pages/services/desktop-operator]].

### Regenerate picture is offered while the project runs (2026-11-04)

- **Gap:** the control plane refuses `RegeneratePresenter` on a `producing` or `assembling` project with "A project that is producing or assembling cannot have its presenter picture regenerated; wait for it to finish or fail." (`tests/Scout.ControlPlane.Tests/Operator/OperatorServiceTests.Character.cs:334`), but the Presenter block's "Regenerate picture" is disabled only while a media action runs or the AI Source makes no pictures (`ui/packages/renderer/src/app/production/production.component.ts:698`), so the owner learns it from the refusal under the block ([[pages/services/desktop-operator]]). The same holds for "Save mouth": the control plane refuses a mouth box on a `producing`, `production_queued` or `assembling` project with its own sentence, and the button stays enabled there.
- **Unblocks with:** both buttons disabled on the running statuses with the control plane's sentence as their title, asserted in `production.component.spec.ts`.

### The presenter composite is blended at 30 frames a second though it changes once a second (2026-11-04)

- **Gap:** a presenter scene over the project's background turns the background to 30 fps before the presenter picture is laid over it (`src/Scout.Operator/Assembler.cs:322`), so the overlay of a picture that only changes with the background's one frame a second runs on every output frame ([[pages/services/operator]]).
- **Unblocks with:** the picture laid over the background before `fps=30`, measured with `ffmpeg -benchmark` on a 10 s scene against today's graph, and taken only if it is faster; it changes the presenter graphs every assembler test asserts, so the argument lists are updated in the same change.

### A failing rhubarb runs again on every assembly (2026-11-04)

- **Gap:** a rhubarb failure or timeout leaves no cues file, and only a cues file newer than the narration skips the run (`src/Scout.Operator/RhubarbLipSync.cs:95`), so a narration rhubarb cannot read is run again, up to its timeout, on every assembly of the project ([[pages/services/operator]]).
- **Unblocks with:** a failure marker beside the cues, `mouth-cues-<scene>.failed`, newer than the narration, read like the cues so the scene is shown still with its note without running rhubarb, and removed when the narration changes; with a test that a second assembly of a failing scene runs rhubarb once.

### The learning loop's thumbnail prompt repeats the no-real-person clause (2026-11-04)

- **Gap:** the thumbnail and the presenter picture end their prompts with one constant, `VisualGenerator.NoRealPerson` (`src/Scout.Operator/VisualGenerator.cs:152`), while the learning loop's thumbnail proposal writes the same words inline (`src/Scout.Operator/LearningLoop.cs:294`), and no test pins that prompt ([[pages/services/operator]]).
- **Unblocks with:** a learning-loop test asserting the prompt the image service receives ends with the clause, then the inline words replaced by the constant.

### The Production screen reads every project in full every 10 seconds (2026-11-04)

- **Gap:** the Production screen refreshes the whole project list every 10 seconds (`ui/packages/renderer/src/app/production/production.component.ts:34`), and every project comes with its script, claims and scenes; an `own_video` project with its timed transcript and scenes is about 100 to 130 KB, so a list of a few long recordings is megabytes over the socket every 10 seconds ([[pages/services/desktop-operator]], [[pages/services/operator]], phase 8b of [[pages/roadmap/youtube-channel-operator]]).
- **Unblocks with:** a list summary rpc that answers id, topic, status, format, progress and `UpdatedAt` per project, the full project read only for the one the owner opens, with a client test for the summary and a contract test for the rpc.

### A long recording holds the speech model and its samples for the whole transcription (2026-11-04)

- **Gap:** the operator hands the whole recording's WAV to the transcriber in one call, so the transcriber's single gate is held for the whole transcription and a voice message gets the busy sentence after 30 seconds, and the samples of the whole recording, about 230 MB at 60 minutes, are held beside the model ([[pages/services/transcription-whisper]], [[pages/decisions/0024-voice-messages-transcribed-locally]], [[pages/services/operator]]).
- **Unblocks with:** transcribing in 5-minute windows, each its own `TranscribeSegmentsAsync` call with its offset added to the segments' times and the gate released between windows, so a voice message waits for one window at most and only one window's samples are held; with a test that two windows give one transcript with continuous times and a voice message answered between them.

### Captions in a project of the owner's own video cannot be turned off, and drift a little from the speech (2026-11-04)

- **Gap:** in `own_video` the scene's text is always drawn as captions over the recording, and the assembler spreads them evenly over the scene rather than at each spoken segment, so a caption runs a little ahead of or behind the words; an owner whose recording already carries its own titles, or who wants none, has no way to leave them out ([[pages/services/desktop-operator]], [[pages/services/operator]], phase 8b of [[pages/roadmap/youtube-channel-operator]]).
- **Unblocks with:** a per-project caption toggle, chosen in the Produce panel and stored on the project like the motion, that the assembler reads; and captions timed from the transcript segments, which the script keeps with their start and end only until the scene plan rewrites it as one section per chapter, so the segment times would need to stay on the scenes.

### A "This is me" mark for the owner's chat ids (2026-11-04)

- **Gap:** the operator's owner-only functions from the chat answer an allowed chat user only when `Scout:Access:OwnerUserIds` lists that id ([[pages/decisions/0021-allowed-users]], amendment of 2026-11-04), and Scout Desktop sets no such list yet, so on Discord and Slack nobody is the owner and the owner acts from the quick ask only.
- **Unblocks with:** a "This is me" mark beside an id in Settings, "Who can use Scout", that main validates like the allow-list and passes as `SCOUT__ACCESS__OWNERUSERIDS__<n>`, restarting a running worker, with the desktop tests for the mark and the variable.

### The own_pictures text-card decision lives in the production loop, not in VisualGenerator (2026-11-04)

- **Gap:** `OperatorPipeline` masks `imagesNow` for `own_pictures` and builds a synthetic `VisualResult(TextCard, null, NoOwnPicture)` beside `VisualGenerator`, which already answers `TextCard` with a note for `NoImages` and `PastMaxScenes`; a third format (the AI presenter of 8c) would add another branch there ([[pages/services/operator]]).
- **Unblocks with:** passing the project format to `VisualGenerator.GenerateAsync` and answering the `own_pictures` card there, when 8c touches the generator.

### Editing the first scene of a section can drop its chapter (2026-11-04)

- **Gap:** `src/Scout.Operator/PackageChecks.cs:261-265` anchors a chapter by finding the scene's cleaned narration inside its section's narration, so an edited text on a section's first scene no longer matches and the chapter is dropped, and the whole chapter list if fewer than 3 remain ([[pages/services/operator]]). Since 2026-11-04 a scene of the owner's recording is anchored by its clip start instead, so this stays open for the narrated formats only.
- **Unblocks with:** anchoring chapters by scene id instead of narration text, with a test that edits the first scene of a section and checks the chapter survives.

### An approved upload action survives a scene edit (2026-11-04)

- **Gap:** an approved `videos.insert` stays queued after a scene edit, regenerate or attach; only `ApprovedAt` blocks it at execution, so once the owner re-approves the edited package the old approval runs ([[pages/services/control-plane]], ADR 0032). Pre-existing before 8a.
- **Unblocks with:** `AfterSceneEdit` cancelling pending upload actions of that project, with a test.

### A long meeting keeps only its newest 500 lines (2026-11-04)

- **Gap:** the transcript store holds 500 lines and the Meetings screen lists the newest 100, while the worker accepts 5,000 lines per meeting ([[pages/services/desktop-quick-ask]], [[pages/services/meetings]]).
- **Unblocks with:** a larger store while a meeting runs, or paging in the list, with tests.

### Done during a streaming reply keeps a partial answer (2026-11-04)

- **Gap:** a turn whose reply is still streaming or failed is kept with the partial text or the failure sentence as its answer ([[pages/services/desktop-quick-ask]], [[pages/services/meetings]]).
- **Unblocks with:** waiting for the reply or marking the turn unfinished, with a test.

### A meeting line over 2,000 characters gets the generic limits sentence (2026-11-04)

- **Gap:** the store refuses a line over 2,000 characters, but the local adapter's sentence for a refused `keep` names only the lines, the turns and the title ([[pages/services/meetings]], [[pages/services/adapter-local]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** a sentence naming the line length, with a test that a `keep` holding a 2,001-character line answers it.

### The meetings WAL checkpoint is a no-op while another connection reads (2026-11-04)

- **Gap:** a `meetings` request racing a `delete` can leave the deleted text in `meetings.db-wal` until the next delete, as the ADR states ([[pages/services/meetings]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** retrying the checkpoint on the busy flag, or failing the delete, with a test that holds a read open during a delete.

### A meeting at every limit can exceed the local adapter's request line (2026-11-04)

- **Gap:** 5,000 lines of 2,000 characters with their translations passes the 20 MB stdin line the local adapter accepts, so a meeting at every limit is refused as an oversized request ([[pages/services/adapter-local]], [[pages/services/meetings]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** the desktop capping what it sends, or the adapter accepting a chunked keep, with a test at the limit.

### The OCR bundle ships without the licence texts of its other bundled libraries (2026-11-04)

- **Gap:** the tesseract AppImage carries only the tesseract Apache-2.0 `LICENSE` and `AUTHORS`; glibc, libstdc++, libgomp, libarchive, libxml2, nettle and the image libraries ship with no notice, and the tree is 54 MB with six unused languages and man pages ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]], [[pages/services/desktop-operator]]). The leptonica licence is the one text that ships beside the program.
- **Unblocks with:** a first-party static build in CI, or a pruned tree with each library's notice collected at build.

### The transcript sent with a question enters the conversation memory (2026-11-04)

- **Gap:** the quick-ask window appends the last 10 minutes of the transcript (up to 24,000 characters) to the question text, and the worker stores the whole text as the turn, so every follow-up turn replays it to the provider ([[pages/services/desktop-quick-ask]], [[pages/services/adapter-local]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]]).
- **Unblocks with:** a `context` field on the ask line that the adapter passes to the model request and leaves out of the stored turn, with a test that a follow-up turn's history holds the bare question.
- **Done (2026-11-07):** the ask line carries `context` apart from `text`, and the conversation memory stores only the question (gap plan G6; [[pages/services/adapter-local]], [[pages/services/conversation]]).

### Live sessions are not stopped when the local adapter shuts down (2026-11-04)

- **Gap:** `LocalChatAdapter.StopAsync` cancels the lifetime token but leaves any session still in `_sessions` to its own end ([[pages/services/adapter-local]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]]); the desktop stops every session before it stops the worker, so nothing is lost today, but a worker stopped mid-session never writes `complete` for it. Present since phase 1 with one session; the two-session change (review of 2026-11-04) made it visible.
- **Unblocks with:** `StopAsync` stopping every open session and writing their `complete` events before the token is cancelled, with a test that stops the adapter while a session is open.

### A listen that reuses an id just stopped can share that id with the old session's last final (2026-11-04)

- **Gap:** `stop` frees the id as soon as it is removed, while the old session's last final may still be decoding; a new `listen` with the same id is accepted and both could write events under one id. The desktop never reuses an id, so this cannot happen from Scout Desktop.
- **Unblocks with:** refusing a `listen` whose id is still stopping, or the desktop contract stating that ids are never reused; one test that stops and immediately re-listens under the same id.

### A revoke and re-grant during an upload can let a second upload be queued (2026-11-03)

- **Gap:** `PublishPackageAsync` ignores an executing `videos.insert` whose claim predates the current grant (`!Stale(a, grantedAt)` in [[pages/services/control-plane]]), so after the owner revokes YouTube writes mid-upload and grants again, the publication reads failed and a second Publish queues a second upload while the first may still finish on YouTube (`/code-review medium` of 2026-11-03). Rare: it needs a revoke and a re-grant inside one upload.
- **Unblocks with:** the executing check counting a stale executing insert as still running, with a test that revokes, re-grants and publishes again.

### A retryable action failure waits for the 30-minute claim lease, not the next tick (2026-11-03)

- **Gap:** `ActionExecutor` returns without recording on a retryable failure, so the action stays executing until `ClaimLease` expires ([[pages/services/operator]], [[pages/services/control-plane]]); a `thumbnails.set` waiting on an upload recorded "succeeded, not saved" is re-handed every 30 minutes with no end, and the project cannot be published again while its publication is `uploading`.
- **Unblocks with:** a bounded wait (the thumbnail giving up after N attempts with a readable reason) and a shorter re-hand for a retryable failure, with a test on the "not saved" upload path.

### Using an audience idea starts the project before the idea is marked used (2026-11-03)

- **Gap:** `DecideIdeaAsync(use: true)` calls `StartProjectAsync` first and writes `comments.json` after ([[pages/services/control-plane]]); a comments write that fails leaves a queued project and a still-proposed idea, and a retry starts a second project on the same text.
- **Unblocks with:** marking the idea used with the project id in the same gated write, or starting the project only after the comments write succeeded and tolerating a used idea without a project.

### The write log parses each tail line twice (2026-11-03)

- **Gap:** `JsonLinesWriteLog.ListSinceAsync` deserializes every line in its stop predicate and again in `ListCoreAsync`, on every `RecordAsync` of an approved action ([[pages/services/control-plane]]). Measured nothing; estimated thousands of lines per record on a busy operator.
- **Unblocks with:** `ReadTailAsync` handing the parsed entry to the stop predicate, with the existing log tests green.

### YouTube metadata limits and the media file-name rule live in three places (2026-11-03)

- **Gap:** `PackageRules` (control plane) re-implements `PackageChecks` (worker) for the title, description, tag and chapter limits, and the media file-name rule exists in `SceneRules.ValidateFileName`, `MediaFiles.PathFor` and the desktop's `isSafeMediaName`; the comment-id regex exists in `CommentRules`, `CommentDesk` and `operator.ts` ([[pages/services/control-plane]], [[pages/services/operator]], [[pages/services/desktop-operator]]). Drift makes the worker and the control plane disagree on a package.
- **Unblocks with:** one shared static for the limits in `Scout.ControlPlane.Contracts` (the only assembly both sides reference) and contract tests pinning the desktop copy to it.

### `NextQueuedProjectAsync` reads the projects file twice per poll (2026-11-03)

- **Gap:** a pre-check read precedes `UpdateProjectsAsync`, which re-reads under the gate and already skips the write when the callback answers null ([[pages/services/control-plane]]); the worker polls every 30 s.
- **Unblocks with:** dropping the pre-check, with the queued-project tests green.


### Some comment fields have no length limit in the control plane (2026-11-03)

- **Gap:** the control plane bounds a comment's text, draft, theme and id, but not its `Author`, `VideoTitle`, `ThreadId`, `VideoId` or `Error` ([[pages/services/control-plane]]). A worker that misbehaves could grow `comments.json` up to the 16 MB message limit. The security review of phase 6 marked this as hardening only: strangers cannot reach it, and the worker already holds the owner's token.
- **Unblocks with:** limits in `CommentRules` for those five fields (YouTube's own display-name and title lengths), with a test, in the next change that touches the comment rules.

### The comment poll reads the whole comments state every minute (2026-11-03)

- **Gap:** the worker's comment poll needs only the owner's sync request and the comments marked for a draft, but `GetComments` has one full-state shape, so it reads and maps up to 1000 comments every 60 s under the operator store's single gate ([[pages/services/operator]], [[pages/services/control-plane]]). The performance lens measured nothing and estimated about 0.5 MB a minute at 1000 comments, a fraction of a percent of one core.
- **Unblocks with:** a measured cost on a busy channel; then an optional `pending_only` flag on `ListCommentsRequest` that answers the sync fields and the `draft_requested` comments only.

### The 6-hour clearing can undo an approval made in the same moment (2026-11-03)

- **Gap:** the control plane's clearing reads the live comment actions before it takes the store's gate; in the few milliseconds between `ApproveReply` writing `reply_approved` and queueing its action, the clearing moves the comment back to `drafted`, and the executor records that action failed without posting ([[pages/services/control-plane]]). Nothing is posted wrongly; the owner approves again.
- **Unblocks with:** reading the live actions inside the same gate as the comments write, which needs the action queue and the operator store to share one lock or a two-phase check.

### YouTube does not say how `order=time` sorts comment threads (2026-11-03)

- **Gap:** Google's `commentThreads.list` reference (read 2026-11-03) does not say whether `order=time` sorts by the top-level comment's publish time or by the thread's latest activity. The sync reads until a whole page is older than its window, at most 5 pages, and keeps threads by the top-level comment's time ([[pages/services/plugin-youtube]]), so a channel with more than 500 threads touched inside one window can miss the oldest of them until the next window.
- **Unblocks with:** an observation on a real channel with replies on old threads; then either a larger page budget or a per-video read for the videos the scan stopped on.

### The revoke sentence is not shown on a write recorded after a revoke (2026-11-03)

- **Gap:** when the owner revokes YouTube writes while an approved action is running, the executor saves what YouTube took (the video id or the arm timing) and records the action once as "after revoke" ([[pages/invariants/no-executive-writes]], [[pages/services/operator]]); the record consumes the action, so the executor's follow-up save of "The owner revoked YouTube writes after approving this action" is refused and only logged. The write itself is shown and logged correctly; the project just does not say why.
- **Unblocks with:** the control plane's after-revoke record path writing that sentence on the action's project in the same step ([[pages/services/control-plane]]).

### No LGPL ffmpeg is bundled for macOS (2026-11-03)

- **Blocker:** the operator's assembly needs ffmpeg (ffprobe is no longer used). Linux and Windows bundle the BtbN LGPL static builds (libopenh264, AAC, libfreetype), pinned by SHA-256; no reputable LGPL static build exists for macOS arm64 (martin-riedl, evermeet and osxexperts builds are GPL, Homebrew is GPL), so on macOS Scout uses an ffmpeg the owner installed and the readiness banner says when none is found ([[pages/decisions/0033-channel-operator-architecture]], [[pages/services/operator]]).
- **Unblocks with:** a CI job on a macos runner that builds ffmpeg from a pinned tag with an LGPL configure (no `--enable-gpl`, `--enable-videotoolbox`, libopus, libfreetype) and publishes it as a pinned release asset.
- **Also noted:** the bundled H.264 encoder is a self-compiled libopenh264; Cisco's patent licence covers only Cisco's own binary, so the owner carries the same H.264 patent position as any project that ships an H.264 encoder; VP9 with Opus is the runtime fallback.

### One-click update covers only the AppImage and Windows (2026-11-02)

- **Blocker:** Update and restart ([[pages/decisions/0031-desktop-self-update]]) needs a package the updater can replace. On macOS, electron-updater applies an update only to a signed and notarized app and reads a `zip` target the build does not produce, and the builds are unsigned (see "Desktop builds are unsigned" below). A `.deb` has no in-place updater: electron-updater's `DebUpdater` installs through `pkexec` or `sudo` and `dpkg`, which is a system package change, not Scout's own file. Both keep the Download button (`canSelfUpdate`, `ui/packages/main/src/update/update-notice.ts:66`).
- **Unblocks with:** macOS: the signing and notarization in the unsigned-builds entry, plus a `zip` mac target and keeping `latest-mac.yml` and the mac blockmaps in `.github/workflows/release.yml:211`. `.deb`: an apt repository the package manager updates from, which is outside Scout's own installation and needs its own ADR.
- **Also noted:** an AppImage whose file name carries a version is replaced under the new release's name, and the old file is deleted (electron-updater's `AppImageUpdater.doInstall`); `refreshLoginEntry` (`ui/packages/main/src/shell/login-item.ts:102`) rewrites the start-at-login entry on the next start.

### Desktop builds are unsigned (2026-11-02)

- **Blocker:** no signing identities. The release workflow packages with `CSC_IDENTITY_AUTO_DISCOVERY` false and the mac config sets `identity: null` (`ui/build/electron-builder.yml:93`), so macOS Gatekeeper refuses the dmg until the user allows it in System Settings, and Windows SmartScreen warns on the installer. The site and the README say so ([[pages/decisions/0029-project-site-and-releases]]).
- **Unblocks with:** an Apple Developer ID certificate plus notarization (an App Store Connect API key, `hardenedRuntime: true`), and a Windows code-signing certificate (an OV or EV certificate, or Azure Trusted Signing), each stored as a GitHub Actions secret and read by electron-builder in the `build` job of `.github/workflows/release.yml:32`.

### Intel Mac package is built but not released (2026-11-02)

- **Blocker:** the mac leg of the release workflow publishes only the `osx-arm64` worker, while the desktop looks for an `osx-x64` worker on an Intel Mac (`ui/packages/main/src/worker/resolve-binary.ts:17`), so the x64 `.dmg` electron-builder produces could not start Scout. The release workflow deletes it before publishing and the site offers the Apple silicon image only.
- **Unblocks with:** a second `bun run worker:publish -- osx-x64` on the mac leg and both workers carried in each dmg, or a separate x64 leg; then stop pruning `*-mac-x64.dmg` and show the Intel row the page already knows how to label.

### Project site has no screenshots yet (2026-11-02)

- **Blocker:** none; nobody has captured them. The hero of `docs/index.html` shows an example Discord conversation built in HTML instead of a screenshot of the desktop app or a real chat.
- **Unblocks with:** screenshots of the desktop app and of a Discord or Slack answer, with no real names, hosts or ids, saved under `docs/` and placed next to the example conversation or in the "How it works" section.

### X11 always-on-top unverified on COSMIC (2026-10-31)

- **Blocker:** the owner's run of the AppImage. On Linux under Wayland with XWayland, Scout relaunches once with `--ozone-platform=x11` (`ui/packages/main/src/shell/x11-relaunch.ts:12`, `ui/packages/main/src/main.ts:179`) so the floating button and the quick-ask window are placed in the chosen corner and stay on top through XWayland. The corner and display choice are measured to hold (below); what stays open is only whether the bubble stays above native Wayland windows, visible only on screen. The second start costs one extra process launch on every Wayland start.
- **Fix:** none expected; what remains open is the owner confirming on screen that the bubble stays above native Wayland windows. If it does not, record it under ADR 0028.
- **Measured 2026-10-31 with Electron 45 under `--ozone-platform=x11`:** every window COSMIC's X window manager manages, whatever its type, is placed where COSMIC chooses, ignores `setPosition` and `setBounds` before and after `show`, reports not always-on-top and gets a server-side frame. A window created with `focusable: false`, shown, then made focusable and focused stays unmanaged, keeps its position and always-on-top, and `document.hasFocus()` reported focus to its page, but that was not real X keyboard focus (see the last bullet).
- **Measured 2026-10-31 on the owner's COSMIC session** (AppImage started with `--ozone-platform=x11`, read with `xwininfo` and `xprop` on `DISPLAY=:1`): the bubble window was 64x64 at +1840+1190, the bottom-right corner of the 1920x1080 laptop display (offset +0+190) minus the 16 px margin, so the corner and the display choice hold. COSMIC's X window manager reports itself as "Smithay X WM" and lists `_NET_WM_STATE_ABOVE` and `_NET_WM_STATE_SKIP_TASKBAR` in `_NET_SUPPORTED`. The bubble carries no `WM_STATE` and is absent from `_NET_CLIENT_LIST_STACKING`: with `focusable: false` Electron creates it as a window the X window manager does not manage, which the compositor draws above managed windows. Whether that holds above native Wayland windows is only visible on screen.
- **Measured 2026-10-31 on the owner's COSMIC session:** an unmanaged window never receives keyboard focus there: `_NET_ACTIVE_WINDOW` on the root window stays 0 while it is shown, and Electron's `focus()` on X11 only asks the window manager, which ignores a window it does not manage. The owner could not type in the quick-ask window or close it. The quick-ask window is now managed on Linux (`ui/packages/main/src/quick-ask/quick-ask.ts:1097`), so on COSMIC it loses the corner and gets a frame, while the bubble keeps the corner. Keeping the corner and the keyboard there needs a window the compositor positions (a Wayland layer-shell surface or a COSMIC-specific protocol), which Electron does not offer.

### Voice transcript shown inside the reply, not as the question (2026-10-31)

- **Done (2026-11-08):** closed. "(voice message)" no longer exists anywhere in the desktop: main accepts only a text question, so a dictated question is its own heard text, and no turn carries a voice quote; the quick-ask line the blocker below cited is gone ([[pages/services/desktop-quick-ask]]).
- **Resolved 2026-11-07:** the desktop dictates instead of recording: hold to talk streams to the live transcriber and sends the heard text as the question, and main refuses an audio question, so no turn reads "(voice message)" and no desktop reply carries the quote ([[pages/services/desktop-quick-ask]], Microphone). The worker's local adapter still accepts an audio ask line, unused by the desktop.

- **Blocker:** no worker event carries the transcript. The owner's request is deferred, not done: a voice turn's question was "(voice message)", and what Scout heard reaches the owner only as the reply's opening `> (voice) ` quote, which the worker writes once the reply starts. The owner asked to see what was heard so they can check it ("it should show what I said, so I can see whether it is right").
- **Fix:** a `heard` event from the local adapter, carrying the transcript before the reply, that main sets as the turn's question; the reply then drops the quote for local turns. It touches the worker's local protocol and main's turn handling, with a test on each side.

### Floating button cannot be dragged (2026-10-31)

- **Blocker:** none. The floating button sits only in one of four corners, 16 px from the edges of the chosen display's work area (`ui/packages/main/src/quick-ask/placement.ts:18`); the owner cannot move it out of the way of something in that corner ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** pointer-drag in `FloatingButtonComponent` through an IPC that moves the bubble window from main, telling a drag from a click, and remember the offset per display under `quick-ask.bubble`; a click still opens quick ask. Wayland stays compositor-placed.

### Floating button does not follow display changes (2026-10-31)

- **Blocker:** none. The bubble is placed when it is created, when its settings change and each time the quick-ask window hides (`ui/packages/main/src/quick-ask/quick-ask.ts:623`); unplugging a display, or a resolution or scale change, can leave it off screen or away from its corner until then.
- **Fix:** reposition it on `screen`'s `display-metrics-changed` and `display-removed` events, removing the listeners on dispose, with a test that emits each event on the fake screen.

### Clear on the Chat screen keeps the worker's local memory (2026-10-30)

- **Blocker:** no channel for it. Clear empties only the conversation main holds for the quick-ask window and the Chat screen (`ui/packages/main/src/quick-ask/quick-ask.ts:875`); the worker keeps the `local` channel's turns in `IConversationMemoryStore` until they expire or the worker restarts, so a question after Clear can still draw on the cleared conversation ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** let Clear also reset the worker's memory for the local channel, for example a `reset` request on the quick-ask stdin protocol that the local adapter turns into a memory reset; it touches only Scout's own state.
- **Done (2026-11-07):** Clear writes `{"type":"reset"}` to the worker, which clears the `local` conversation's memory (gap plan G6; [[pages/services/adapter-local]]).

### Provisioning pages still describe the old Discord flow (2026-10-28)

- **Blocker:** none. `pages/architecture/electron-provisioning-ui/per-platform-feasibility.md:52` to `:53` and `pages/architecture/desktop-ui-design-system/5-screens/screen-5-discord-wizard-step-3-oauth2-install.md` still describe the manual intent toggle and the loopback OAuth install, which [[pages/decisions/0022-discord-setup-from-the-bot-token]] replaced with the bot-token setup and a plain `scope=bot` invite.
- **Fix:** mark both as superseded by ADR 0022, or refresh them to the current wizard.

### Semantic Kernel held at 1.78.0 (2026-10-21)

- **Blocker:** SK 1.79 and later require `Microsoft.Extensions.VectorData.Abstractions` >= 10.5.2. `Microsoft.SemanticKernel.Connectors.SqliteVec` 1.74.0-preview, the last version published, is built against 10.1.0. On 10.5.2 `ChunkRecord`'s `VectorStoreVector(Dimensions: ...)` no longer compiles, and after that is fixed, search throws `MissingMethodException` on `VectorSearchOptions.get_OldFilter()` (8 failures in `Scout.Knowledge.Tests`).
- **Unblocks when:** a SqliteVec build targets VectorData >= 10.5, or the vector store in [[pages/services/knowledge]] moves to another connector.
- **Affects:** the whole SK family pinned in `Directory.Packages.props`, per [[pages/decisions/0012-llm-communication-through-semantic-kernel]].
- **Update (2026-11-08):** still held at 1.78.0; the rest of the dependency sweep moved. `Microsoft.SemanticKernel.Connectors.SqliteVec` is deprecated on nuget.org (reason Legacy) and renamed to `CommunityToolkit.VectorData.SqliteVec`, whose 1.0.4-preview targets VectorData >= 10.8.2 and `Microsoft.Data.Sqlite` >= 10.0.10. A trial swap to it with SK 1.82.0 changed the package id, one `using` in the store and its test, and the `VectorStoreVector` dimensions argument; it built at 0 warnings, kept every .NET test project green at the same totals, and a vector database written by the old connector opened, searched and took new rows under the new one. The swap adds a preview package id and waits for the owner's decision.
- **Done (2026-11-08):** the swap was taken: `CommunityToolkit.VectorData.SqliteVec` 1.0.4-preview replaces the deprecated connector and the Semantic Kernel family is on 1.82.0 (the connectors on 1.82.0-alpha, -preview and -beta); a fixture database written by the old connector opens, searches with the same ranking and takes new rows ([[pages/services/knowledge]]).

### `electron-builder-squirrel-windows` peer mismatch (2026-10-21)

- **Blocker:** none. `electron-builder` is on 26.18.0, while its unpinned peer `electron-builder-squirrel-windows` stays at 26.8.1 in `ui/bun.lock`, and `bun install` warns about it. The peer is unused while the Windows target in `ui/build/electron-builder.yml:109` stays `nsis`.
- **Fix:** add the peer at the matching version only if a Squirrel target is ever configured.
- **Done (2026-11-08):** a freshly resolved `ui/bun.lock` takes the peer at 26.18.1, the same as `electron-builder` 26.18.1, and `bun install` no longer warns.

### Adapter stop has no fault isolation (2026-10-21)

- **Blocker:** none. `ConversationWorker.StopAsync` stops adapters in sequence with no per-adapter `try`/`catch` (`src/Scout.Host.Console/ConversationWorker.cs:69`), so a fault from one adapter's stop (now possible from Slack's awaited `DisconnectAsync`) leaves the adapters after it running until the process exits.
- **Fix:** isolate and log each adapter's stop in the worker, with a test where the first adapter's stop throws and the second is still stopped. A related edge: `SlackChatAdapter.DisposeAsync` calls `StopAsync(CancellationToken.None)`, so a hung disconnect blocks disposal when the host never called `StopAsync`. See [[pages/history/adapter-slack]].

### Synchronous `safeStorage` calls deprecated in Electron 45 (2026-10-21)

- **Blocker:** removal in Electron 46. `ui/packages/main/src/vault/safe-storage-vault.ts` uses the synchronous `safeStorage` encryption API, deprecated in 45.
- **Fix:** move to the async API before taking Electron 46.

### TypeScript 7 deferred (2026-10-21)

- **Blocker:** `typescript-eslint` 8.72 and `@typescript-eslint/typescript-estree` peer `typescript >=4.8.4 <6.1.0`; `@angular/compiler-cli` and `@angular/build` 22.2 peer `>=6.0 <6.1`.
- **Unblocks when:** both widen their TypeScript peer ranges.

### Packaging and native assets only exercised on Linux (2026-10-21)

- **Blocker:** none. Electron 45 packaging (`dist`) and the SQLitePCLRaw 3 native assets for Windows and macOS have not been run; the first run is the `ui-ci.yml` package step on the three-OS matrix.
- **Fix:** watch that CI run, then a packaged smoke run before a release.

### `dotnet format` check disabled in CI (2026-10-22)

- **Blocker:** `dotnet format --verify-no-changes --no-restore` times out on the MSBuild build-host pipe with SDK 10.0.106; the step is commented out in `.github/workflows/ci.yml`. Code style is still enforced at compile time by `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`. The GitHub issue that tracked this was deleted on 2026-10-22; this entry replaces it.
- **Unblocks when:** a later SDK pin in `global.json` runs the command clean; then restore the step with the exact name `Format check`. Rule B runs the same command locally, so the gap is CI-only.

### Control-plane socket directory check is not atomic (2026-10-22)

- **Blocker:** none. Found by the review of the phase 1 spike in [[pages/roadmap/control-plane-client]]. The launcher checks the socket directory with `lstat` and then calls `chmod` (`ui/packages/main/src/control-plane/launcher.ts:75`), leaving a window in which a same-uid principal could swap the directory. The environment half of the original entry was closed on 2026-10-23: the launcher now filters the inherited environment (see [[pages/services/host-control-plane]]).
- **Fix:** open the directory with `O_NOFOLLOW` and `fchmod` the descriptor, with a test that a symlink swapped in after the check is refused.

### Control-plane phase 1 test notes (2026-10-22)

- **Blocker:** none. The wrong-token integration test asserts only the error type; add `not.toContain` of the token on its message. The integration `afterAll` throws a `TypeError` when `beforeAll` fails before launching, which adds noise to an already-reported failure.
- **Fix:** both in phase 2, alongside the codegen change to the same files.

### Cloud LLM cards: what is still open (2026-10-22, updated 2026-10-23)

- **Blocker:** none. [[pages/decisions/0015-cloud-llm-providers-by-api-key]] wired the OpenAI API and Google Gemini cards and marked ChatGPT Plus unsupported, which [[pages/decisions/0018-chatgpt-plan-sign-in]] replaced with Sign in with ChatGPT; [[pages/decisions/0017-provider-connect-disconnect]] closed removing a stored key and checking a key against the provider before saving. Still open: OpenRouter (its factory needs a separate embeddings host and key) and Custom API (endpoint validation). Loose ends from ADR 0017: a legacy `llm.api-key` left while the provider is Ollama is never migrated or shown; after disconnecting the provider in use the ready screen says "No LLM model selected" (with provider `None` that is now the honest state, but the wording asks for a local model rather than a provider); and Use applies the card's default model rather than a stored `llm.cloud-model`.
- **Fix:** one change per item; OpenRouter first, since its .NET factory already exists.

### LM Studio and Custom API cards use generic icons (2026-10-23)

- **Blocker:** no freely licensed LM Studio mark exists in Simple Icons, and Custom API has no brand.
- **Fix:** add the official LM Studio artwork to the `ICONS` map in `ui/packages/renderer/src/app/sources/sources.component.ts` once the owner supplies it, with its licence noted beside the Simple Icons line.

### Read-only guard does not judge tool calls (2026-10-23)

- **Blocker:** none. The TypeSafe guard ([[pages/decisions/0016-jev-read-only-guard]]) judges the latest user message, with up to 12 messages before it, before the model runs; a function the model calls during its turn is not judged. Read-only still rests on no write function existing ([[pages/invariants/no-executive-writes]]).
- **Fix:** an `IAutoFunctionInvocationFilter` on the shared kernel that asks Jev about each function name and its arguments before invocation and terminates the call on a yes, with a test where a write-named function is blocked. Decide fail-open or fail-closed for that path separately.

### Turning a feature on does not reach the chat bot (2026-10-23)

- **Blocker:** the control plane registers `NullFeatureActivator` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:70`), whose `CanActivate` is false (`src/Scout.ControlPlane/Features/IFeatureActivator.cs:31`), so `Enable` answers `UNIMPLEMENTED` and the Features screen says turning a feature on is not available yet. Nothing tells `Scout.Host.Console` about a feature's state. GitHub and Kubernetes reach the worker through main-owned connections instead ([[pages/decisions/0019-github-app-device-flow]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]), so no catalog card calls `Enable` today.
- **Unblocks when:** an activator exists that the console host honours, decided against [[pages/invariants/no-executive-writes]]; then the second note under the Features grid and the card copy change with it. See [[pages/services/host-control-plane]].

### Feature credentials live in memory and are never validated (2026-10-23)

- **Blocker:** none. The control plane keeps credentials in `InMemoryCredentialStore` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69`), so they are lost when Scout closes, and no host registers an `IFeatureCredentialValidator` (the GitHub and Kubeconfig validators were in the retired first cut only, deleted on 2026-11-06). Neither catalog entry promises a keyring any more: the desktop GitHub card signs in through the GitHub App ([[pages/decisions/0019-github-app-device-flow]]) and the Kubernetes card reads the kubeconfig on this computer ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The Features screen says nothing about how long these credentials are kept, and no owner reaches the control-plane credential dialog: every card of the catalog has its own connection flow, so the dialog's Add and Update credentials actions are never shown. Since 2026-11-11 the store's contract says that unavailable means a stored secret lasts only while Scout runs ([[pages/services/control-plane]]).
- **Fix:** a keyring-backed `ICredentialStore` and the validators in the current tree, or catalog text that matches the in-memory store; one change in `Scout.ControlPlane`. Rule (2026-11-11): "The change that first lets an owner save a credential through this store, or adds the keyring store, makes the feature snapshot say whether the secret is kept after Scout closes and shows it in the credential dialog before Save."

### Error codes do not cross Electron IPC for most handlers (2026-10-23)

- **Blocker:** none. Electron forwards only the message of an error thrown in an `ipcMain.handle` handler; the `code` property the handlers attach never reaches the renderer (checked on Electron 41.3.0). The Features handlers now encode the code in the message (`ui/packages/shared/src/errors.ts:238`); every other handler does not, so renderer branches on `err.code` never fire in the running app, for example `ui/packages/renderer/src/app/wizard/welcome/welcome.component.ts:272`.
- **Fix:** encode in every handler and decode once in `ui/packages/preload/src/build-bridge.ts` with `readIpcError`, so the renderer receives a `ScoutError` with its code; drop the Features-only decode in `features.store.ts` then.

### Raw gRPC text for feature validation errors (2026-10-23)

- **Blocker:** none. The desktop control-plane client maps only `UNAVAILABLE`, `DEADLINE_EXCEEDED`, `UNAUTHENTICATED` and `UNIMPLEMENTED` (`ui/packages/main/src/control-plane/client.ts:459`). `INVALID_ARGUMENT` (a missing credential or setting), `FAILED_PRECONDITION` and `NOT_FOUND` reach the Features card as text like `3 INVALID_ARGUMENT: credentials.pat: ...`.
- **Fix:** typed errors for those codes and card copy per code, with a client test for each.

### Kubernetes pod logs: owner decision (2026-10-23)

- **Blocker:** an owner decision. The Kubernetes plugin has no log function ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]) because chat output is posted to a shared channel and logs routinely carry credentials and personal data. The retired plugin had `get_pod_logs` with a tail clamp (`KubernetesFunctions.cs` of the first cut, in the history before `93da0d4`).
- **Unblocks when:** the owner decides whether logs may reach a shared channel, and with what redaction or limit; then one GET function and its own consent line on the card.

### Merged multi-file `KUBECONFIG` (2026-10-23)

- **Blocker:** none. The desktop reads only the first `KUBECONFIG` entry (`ui/packages/main/src/kubernetes/kubeconfig.ts:20`, a `ponytail:` line), and the worker gets one path, so contexts defined in later files are not offered.
- **Fix:** list contexts from every entry with kubectl's merge rules, store the file per context, and give the plugin one path per context.

### Kubernetes from an in-cluster service account (2026-10-23)

- **Blocker:** none. When Scout itself runs inside a cluster it could use the pod's service account instead of a kubeconfig; the plugin builds clients only from a kubeconfig file (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:305`).
- **Fix:** an opt-in `InCluster` setting that builds one client from `KubernetesClientConfiguration.InClusterConfig()`, with the same GET-only tests.

### Kubernetes consent covers context names, not the file (2026-10-23)

- **Blocker:** none. The desktop stores only the kubeconfig path and the allowed context names; the worker re-reads the live file each time it builds a client (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:305`), so a context whose user later gains or changes an exec command runs the new command without a new disclosure ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]).
- **Fix:** store a fingerprint of each allowed context's cluster and user sections with the selection, and have main drop the selection and ask again when it changes.

### The control-plane toggle and credential dialog are unreachable on the desktop (2026-10-23)

- **Blocker:** an owner decision. Both catalog features now show a main-owned connection (`ui/packages/renderer/src/app/features/features.component.ts:512`), so the toggle, the credential dialog and the `Enable` path in `FeaturesComponent` serve no typed feature; their specs reach them only through a cast feature id (`ui/packages/renderer/src/app/features/features.component.spec.ts:73`).
- **Fix:** remove that path and its specs, or keep it for a future feature without a main-owned connection.

### The GitHub card bypasses the control-plane Features service (2026-10-23)

- **Blocker:** no activator. The desktop main process owns the GitHub sign-in and hands the worker a token file (`ui/packages/main/src/worker/build-env.ts:80`); the control plane's GitHub entry, its `pat` credential and its enabled state play no part in it, so the GitHub card shows the main-owned connection as its badge and hides the toggle and credential buttons. The Kubernetes card does the same with the kubeconfig selection ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The card also hides with the Features grid when the control plane is not running, although the sign-in does not need it.
- **Unblocks when:** a real feature activator exists; then decide whether the control plane reports the GitHub connection or the card leaves the control-plane grid.

### An API-key provider does not close the bot to other users (2026-10-23)

- **Blocker:** an owner decision. `SCOUT__ACCESS__REQUIREALLOWLIST` is set for GitHub, Kubernetes and the ChatGPT plan only (`ui/packages/main/src/worker/build-env.ts:92`); with an OpenAI or Gemini key in use and no allowed users, anyone who can message the bot spends the owner's API credit ([[pages/decisions/0021-allowed-users]]).
- **Fix:** add `llm.provider` OpenAI and Gemini to the condition, with the same card warning on those cards, if the owner wants API credit treated as an owner credential.

### Teams allowed-user ids are Bot Framework ids (2026-10-23)

- **Blocker:** none while Teams has no webhook in the shipped host. The Teams translator passes `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:133`), a `29:` id Teams does not show to users, not the Microsoft Entra object id; the Settings help says so.
- **Fix:** pass `activity.From.AadObjectId` as `UserId` when the Teams webhook lands, and change the Settings help to "the user's Microsoft Entra object ID"; check that a real `29:` or object id fits the 64-character limit of `ui/packages/main/src/ipc/access.ts:16`.

### Allowed-user ids are not platform-qualified (2026-10-23)

- **Blocker:** none; the desktop app runs Discord only. An id listed for one platform also admits the same string on another (`src/Scout.Conversation/ConversationConductor.cs:378`).
- **Fix:** store and compare `Adapter:UserId`, as [[pages/decisions/0014-per-request-identity-gating]] anticipates, when a second adapter ships in the desktop app.

### The script writer is not told how the bundled voice reads capitals (2026-11-04)

- **Blocker:** none. `PiperSpeech.Spelled` (`src/Scout.Operator/PiperSpeech.cs:125`) writes every run of 2 to 4 capital letters as dotted letters, so the bundled voice spells it; a word-like acronym ("Nasa", "Pib") is read as a word only when it arrives capitalised, and nothing in the script prompt says so.
- **Fix:** tell the script writer to write word-like acronyms capitalised ("Nasa", "Pib") and spelled ones in capitals ("ETF", "IPO").

### The desktop composes the presenter prompt with its own copy of the worker's strings (2026-11-05)

- **Blocker:** none. The Presenter block prefills the prompt from `PRESENTER_STYLE`, `PRESENTER_SAFETY` and its own composition (`ui/packages/renderer/src/app/production/production.component.ts:99`), copies of `PresenterStage.Style`, `VisualGenerator.NoRealPerson` and `PresenterStage.ComposedPrompt`; the review of phase 8c-B caught the two style strings apart (the desktop said "plain background", the worker needs the green one the chroma key removes) and made them equal, but nothing keeps them equal.
- **Fix:** carry the composed prompt and the safety tail on the wire (a `Readiness` or `Project` field the worker fills), so the desktop shows what the worker uses and composes nothing itself.

### The desktop validates presenters.json with stricter rules than the worker (2026-11-05)

- **Blocker:** none; the shipped manifest (`ui/packages/main/resources/presenters/presenters.json`) satisfies both. `presentersFolder` (`ui/packages/main/src/worker/resolve-binary.ts`) accepts only a bare array whose entries name a `file`, while `BuiltInPresenters` (`src/Scout.Operator/BuiltInPresenters.cs`) also reads an object with `presenters` and defaults the file to `<id>.png`; a manifest the worker would read can be withheld from it by the desktop with an empty `SCOUT__OPERATOR__PRESENTERSPATH`.
- **Fix:** one validator: the desktop checks the folder exists and leaves the manifest to the worker, which keeps the plain-basename rule.

### Three WorkerGate chat rpcs and one Operator rpc have no caller (2026-11-05)

- **Blocker:** none. `ChatEditScenePicturePrompt` and `ChatRegenerateScenePicture` (`src/Scout.ControlPlane.Contracts/protos/worker.proto`) exist for the owner's picture edits from the chat, but the plugin offers no function for them; `ListBuiltInPresenters` (`operator.proto`) answers the readiness list the desktop already reads. Each is tested on the control plane and reachable only under the grant.
- **Fix:** add `youtube_edit_scene_picture` and `youtube_regenerate_scene_picture` to the plugin under the owner-identity and state-read rules of phase 8d, or reserve the three rpc names in the proto and drop them.

### The face models stay resident after the first readiness round (2026-11-05)

- **Blocker:** none, unmeasured. `OnnxRunner` keeps one session per model for the life of the worker (`src/Scout.Operator/FaceMouth.cs`), and the readiness probe loads both at the first round whether or not a presenter job ever runs; the optimizer estimated tens of megabytes of RSS.
- **Fix:** measure the worker's RSS before and after the first round; if it matters, probe with `File.Exists` only and load the sessions at the first job, dropping them after an idle hour.
### Vault entries written under `basic_text` are read again once a keyring is selected (2026-11-10)

- **Blocker:** a decision. The vault refuses every read while Linux has no keyring service selected ([[pages/services/desktop-ui]], "Credential vault"), but Electron does not say under which backend an entry was written, so after a restart with a keyring the vault decrypts every entry, including one written under `basic_text`, whose protection is a publicly known password. A probe on Electron 45.2.3 saw `basic_text` ciphertext prefixed `v10` and keyring ciphertext `v11` on Linux; the prefix is Chromium's internal format, not an Electron API, and `v10` is the normal prefix on macOS and Windows. Scout never turned on plain-text encryption, and on Electron 45 `basic_text` reports encryption unavailable without it, so no such entry is known to exist.
- **Fix:** decide whether to treat a `v10` entry on Linux as unsaved (ask for the credential again and rewrite it under the keyring) or to delete `vault.enc` when Linux selected `basic_text`; either is a write to the owner's credential file and needs its own test table.
