---
title: "Ollama and local models: make the whole flow work"
version: "1.0"
date: 2026-11-11
changes: "Initial tracker page"
page_type: roadmap
status: active
description: "The survey of the whole local-model flow in the desktop (connect, install, choose, chat, switch away), the decisions taken, the defaults in force, every finding with its evidence and the order of the lanes that fix them."
source:
  - chat
  - ui/packages/renderer/src/app/sources/local-models.component.ts
  - ui/packages/renderer/src/app/sources/sources.component.ts
  - ui/packages/renderer/src/app/core/local-models.store.ts
  - ui/packages/renderer/src/app/core/llm-config.store.ts
  - ui/packages/main/src/ipc/llm.ts
  - ui/packages/main/src/ipc/llm-pull.ts
  - src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs
  - src/Scout.Llm/ChatGateway.cs
reliability: medium
---

# Ollama and local models: make the whole flow work

> When this is done, the owner connects a local model server from the desktop, sees every model it holds, chooses one, chats with it with the whole prompt read, and stops using it without leaving a model in memory, and each of those steps is covered by a test that runs against a fake local server.

This page is the tracker for that work: the decisions, the defaults in force, every finding of the survey with its evidence, the context-length spike, and the order of the lanes. It covers the Ollama flow; LM Studio appears only where a defect is shared. The screens and their data flow are in [[pages/services/desktop-ui]] and [[pages/architecture/desktop-models-system]]; the model catalog and the real downloads are in [[pages/decisions/0008-local-models-real-downloads]]; the chat path is in [[pages/services/llm]] and [[pages/decisions/0012-llm-communication-through-semantic-kernel]]; the operations notes are in [[pages/runbooks/local-models]].

## Why now

On 2026-11-11 the owner used the desktop with the local model server and, in one session, met four problems:

- A chat failed while the model was still downloading.
- The skill index failed every minute because an embedding model was missing.
- The server panel said Stopped while the server answered, its Autostart stayed on Checking, and Start, Stop and Restart did nothing.
- The model list offered five fixed models.

He asked for a survey of the whole flow, and for the whole flow to be tested before anything else is changed. The survey was a read-only audit of `origin/main` at b8f03747, followed by three design plans (the server panel, the free model list, the context length) and a spike for the context length. This page is their result.

## Decisions taken

All dated 2026-11-11, by the owner.

- The Local Models card leaves AI Sources (declined; lane A).
- Every installed model is listed and selectable, with Pull by name and a link to the library; there is no remote catalog and no scraping.
- The five current catalog models stay, under a Recommended heading.
- The embedding model is downloaded together with the chat model, on the owner's click, said with its size, never in the background.
- The server controls (Start, Stop, Restart, Autostart) leave the application. They stay drawn in the prototype, disabled, under a "Not supported yet" notice and each marked as design, not built. The prototype tracker records them as deferred by the owner, not as owed. The hint "start the server once before the first download" leaves with them.
- When the provider in use changes away from the local server, Scout asks the server to unload the models Scout uses (chat, translation, skill matching). It never stops a server that other programs may use.
- The tests of the whole flow come first: a fake local server in the end-to-end suite, and each known defect written as an expected-failure test that its fix turns green.

## Defaults in force unless the owner says otherwise

These are the recommended defaults the work proceeds with; each is a product choice the owner may change.

- A model that cannot call tools is warned about, not blocked (built 2026-11-12: a "No tools" chip and a sentence in the Ollama dialog and in Models, Analysis).
- Only capabilities the server reported warn: unknown ones say nothing, and an empty list counts as known and warns (2026-11-12).
- An embeddings-only model is listed with a chip and cannot be chosen for chat.
- Remove is blocked on the model in use ("In use. Choose another model before removing it.").
- A name that carries a registry host is refused in Pull by name; a model from another registry is pulled with the server's own command line and then appears under Installed.
- A name already installed is pulled again by Pull by name, which updates it, with its progress shown (2026-11-12).
- Pull by name never puts the model in use or writes `llm.model`; the owner chooses it with Use this (2026-11-12).
- Loopback only: the stored endpoint setting, which no screen writes, is removed or pinned, and the desktop and the worker share one endpoint constant.
- Pictures are accepted only when the chosen model reports vision.
- Disconnect on the Ollama card is as the artboard draws it, plus the unload: the provider goes to None when the local server was in use, the models leave Models, the loaded models are unloaded, the installed models stay.
- Context length 16384, capped at the model's own limit (see "The context length"). Built 2026-11-12 without the cap: the worker sends the configured value as it is, waiting for the owner (see "What landed").

## The findings

Recorded 2026-11-11 from the audit of `origin/main` at b8f03747. The evidence names the file; the audit read it at that commit, and each lane re-reads the file before it changes it, because every lane below moves lines in the same few files. This is the list the work is checked against.

Severity words: **blocks use**, **wrong information**, **rough edge**, **missing test**, **stale doc**. "Verified" means the code path was read; where a finding was read in code but not run, or not verified at all, the defect text says so. Lane: the letter of the lane that carries it (see "Lanes and order"); "new: X" means the audit found it after the lanes were cut and proposes lane X.

### Step 1: connect the server (AI Sources, the Ollama card, the wizard)

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 1.1 | wrong information | A stopped server and a missing one look identical ("Not connected", Connect, Download): the "installed but not answering" note, Start and Autostart never render because the status read uses a transport that is dead. | `ui/packages/renderer/src/app/sources/sources.component.ts`; `ui/packages/main/src/worker/http-client.ts` | G |
| 1.2 | wrong information | "Start Scout to start Ollama from here." can never show: it tests for the code `worker/not-running`, which no production code emits (the dead transport gives `worker/http-unavailable`); only the spec fixture invents it. | `ui/packages/renderer/src/app/sources/sources.component.ts`; `ui/packages/shared/src/errors.ts`; `ui/packages/renderer/src/app/sources/sources.component.spec.ts` | new: G |
| 1.3 | wrong information | The Ollama card has no Disconnect; the artboard draws it with a confirm saying Scout stops using the server and its models leave Models and Analysis, and the models stay installed. | `context/prototypes/desktop/Sources.dc.html`; `ui/packages/renderer/src/app/sources/sources.component.ts`; `ui/packages/renderer/src/app/sources/sources.component.spec.ts` | G (the button itself is in G) |
| 1.4 | blocks use | Use on the card saves only `llm.provider` and restarts; it never checks that a model is chosen or installed, so the worker runs the shipped default model or a stale `llm.model`, present or not. | `ui/packages/renderer/src/app/core/llm-config.store.ts`; `ui/packages/renderer/src/app/sources/sources.component.ts`; `src/Scout.Host.Console/appsettings.json` | new: D |
| 1.5 | rough edge | "Connected" is one `GET /api/tags` to a hard-coded `http://localhost:11434` with a 5 s timeout at screen open; it is stored nowhere, never rescanned on focus, and a slow server or another host or port reads "Not connected". | `ui/packages/renderer/src/app/sources/sources.component.ts`; `ui/packages/main/src/ipc/llm.ts` | new: I |
| 1.6 | wrong information | With the local server in use, the Ready screen always says "No LLM model selected": `isConfigured` needs a model in the store, `useOllama` sets it null, and the only writer of a local model there (`setSelection`) has no caller. | `ui/packages/renderer/src/app/core/llm-config.store.ts`; `ui/packages/renderer/src/app/wizard/ready/ready.component.ts` | new: D |
| 1.7 | rough edge | The wizard's launch step never checks the AI Source; the artboard has an "AI Source did not answer within 5 s" failure, "Fix the AI Source..." and a "Change in AI Sources" link, none of which exist in code. | `ui/packages/renderer/src/app/wizard/worker-launch/worker-launch.component.ts`; `context/prototypes/desktop/Wizard.dc.html` | new: J |

### Step 2: the server panel (Local models dialog, "LLM Server")

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 2.1 | wrong information | The panel chip reads "Stopped" while the server runs (the status is always null). | `ui/packages/renderer/src/app/sources/local-models.component.ts` | G |
| 2.2 | wrong information | AUTOSTART reads "Checking" forever and its switch is disabled forever. | `ui/packages/renderer/src/app/sources/local-models.component.ts` | G |
| 2.3 | wrong information | Start is enabled and always fails ("Worker HTTP endpoint not available" or "Worker is not running. Restart the app.") while the worker is running; Stop and Restart are permanently disabled, Restart with an empty tooltip. | `ui/packages/renderer/src/app/sources/local-models.component.ts`; `ui/packages/shared/src/errors.ts` | G |
| 2.4 | wrong information | "Starts automatically when needed" and "If enabled, the server starts when a model is requested" describe behaviour that exists nowhere: the autostart flag is stored and read back, and nothing consumes it. | `ui/packages/renderer/src/app/sources/local-models.component.ts`; `src/Scout.ControlPlane/Providers/OllamaProviderService.cs` | G |
| 2.5 | wrong information | SELECTED, DOWNLOADED and STORAGE come from localStorage and the catalog's nominal sizes, not from the server. | `ui/packages/renderer/src/app/sources/local-models.component.ts` | D, G |
| 2.6 | rough edge | The open dialog polls the dead transport every 4 s and main logs a warning each time. | `ui/packages/renderer/src/app/sources/local-models.component.ts`; `ui/packages/main/src/ipc/providers.ts` | G |
| 2.7 | rough edge | "Start Ollama once before the first download..." shows always, including on a server that has run for months. | `ui/packages/renderer/src/app/sources/local-models.component.ts` | G |
| 2.8 | missing test | The panel and card specs mock the status and start calls as succeeding, so they prove behaviour production cannot reach; the main test covers only the null endpoint. | `ui/packages/renderer/src/app/sources/local-models.component.spec.ts`; `ui/packages/renderer/src/app/sources/sources.component.spec.ts`; `ui/packages/main/test/ipc-providers.test.ts` | G |
| 2.9 | wrong information | Facts the server-lifecycle code on the .NET side rests on: Stop and Restart refuse a daemon Scout did not start (so they never work with a system-managed server); Start spawns `ollama serve` as the desktop user, whose model store differs from a system service's; "installed" means a binary on `PATH` (the graphical-session `PATH` on macOS is not verified). | `src/Scout.ControlPlane/Providers/OllamaProviderService.cs`; `src/Scout.ControlPlane/Providers/PathBinaryLocator.cs`; `context/pages/runbooks/local-models.md` | G (recorded as the reason the controls leave the application) |

Callers of the dead transport, as recorded for the deletion in lane G. All of them are gone: the renderer callers left with the panel on 2026-11-11, and the bridge, contract and main parts were deleted on 2026-11-12, with the six channels, their request and status types and the provider lifecycle error classes; `scout:providers.validateKey` stays. The .NET side and the desktop client's `listProviders` below are untouched.

- Renderer: `ui/packages/renderer/src/app/core/local-models.store.ts` (status); `ui/packages/renderer/src/app/sources/local-models.component.ts` (status),  (set autostart),  (start),  (stop),  (restart); `ui/packages/renderer/src/app/sources/sources.component.ts` (start),  (set autostart),  (status).
- Bridge and contract: `ui/packages/preload/src/build-bridge.ts`; `ui/packages/renderer/src/app/core/scout-bridge.ts`; `ui/packages/shared/src/ipc-contract.ts`.
- Main: six handlers in `ui/packages/main/src/ipc/providers.ts`, registered at `ui/packages/main/src/main.ts`; `ui/packages/main/src/worker/http-client.ts`; the marker parse at `ui/packages/main/src/worker/log-stream.ts`, consumed at `ui/packages/main/src/worker/supervisor.ts`. No `SCOUT_HTTP` exists under `src/`.
- Unused: the providers list call has no renderer caller; no caller passes `lm-studio`.
- What already exists: the .NET control plane implements list, status, start, stop, restart, set-autostart and watch (`src/Scout.ControlPlane.Contracts/protos/providers.proto`) plus model pull and delete; the desktop's client exposes only `listProviders` (`ui/packages/main/src/control-plane/client.ts`) and no production code calls it. That code stays untouched by this work ([[pages/roadmap/control-plane-client]], phase 3).

### Step 3: install and remove models (the dialog)

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 3.1 | wrong information | "Installed" is localStorage: a model removed outside Scout stays installed and selectable, Download silently does nothing for it, a model pulled outside never appears, and the card's "Models installed N" (from the server) can contradict the dialog. Done 2026-11-12 for the dialog and Models: Installed and the Local LLM rows are what the server lists on each read, a model pulled outside appears and one removed outside leaves (SELECTED then reads the dash and nothing is written). | `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/renderer/src/app/sources/sources.component.ts` | D |
| 3.2 | rough edge | A fixed five-model catalog; one catalog id carries another model's title and maps to a different tag. Done 2026-11-12: every installed model is listed and can be chosen, the five stay under "Recommended"; the artboards' mismatched row is fixed; the legacy id `qwen-3-vl-2b` stays (its rename needs a stored-selection migration). | `ui/packages/renderer/src/app/core/local-models.catalog.ts` | D, E |
| 3.3 | rough edge | Progress is per layer, not per model: each stream line overwrites completed and total, so the bar falls back between layers, and the "x of y GB" text multiplies the catalog size by that percent (read in code, not run). | `ui/packages/main/src/ipc/llm-pull.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts` | new: E |
| 3.4 | wrong information | A window reload mid-pull loses the row's progress while main keeps pulling, and success is never recorded; after an app quit the pull is aborted and nothing says so or resumes (read in code, not run). | `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/main/src/ipc/llm-pull.ts` | new: D |
| 3.5 | wrong information | The first download auto-selects the model and writes `llm.model` with no restart: with the local server in use the UI says "In use" for it while the running worker keeps the previous model. | `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts` | new: D |
| 3.6 | blocks use | Removing the model in use gives no warning, restarts nothing, and with no other model downloaded leaves `llm.model` naming the deleted tag. Partly done 2026-11-12: while Ollama is the provider in use, or none is saved, Remove on the model in use is disabled with "In use. Choose another model before removing it."; under a cloud provider Remove of the stored selection still moves it, and `llm.model`, to another installed chat model (owner decision pending). | `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts` | new: D |
| 3.7 | missing test | The main-process delete handler has no test and no timeout; no pull test names a refused connection or a non-2xx answer (title scan only); two pulls at once work by construction but are untested. Partly done 2026-11-12: a pull that Ollama does not answer, a non-2xx answer with and without Ollama's reason, a not-found line and another manifest error each have a test that reads the code the dialog gets; the delete handler and two pulls at once are still untested. | `ui/packages/main/src/ipc/llm-pull.ts`; `ui/packages/main/test/llm-pull.test.ts` | new: E |

### Step 4: choose a model (Models, Analysis and Translation)

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 4.1 | wrong information | The In use strip, the local group and the translation list read the localStorage selection, not `llm.model`: an empty or different window store says "No model in use" or "No models downloaded yet" while the worker answers with a model. | `ui/packages/renderer/src/app/models/models.component.ts` | D |
| 4.2 | wrong information | A failed `llm.model` save on auto-select or after a removal is swallowed with a console warning, so the UI's "In use" and the vault diverge. | `ui/packages/renderer/src/app/core/local-models.store.ts` | new: D |
| 4.3 | wrong information | LM Studio answering alone makes the local server's models listed and selectable and hides "No source", though Scout cannot answer with LM Studio. | `ui/packages/renderer/src/app/models/models.component.ts` | new: D |
| 4.4 | rough edge | The group is titled "Local LLM" in code and "Ollama" in the artboard and in the dialog's own sentence. | `ui/packages/renderer/src/app/models/models.component.ts`; `context/prototypes/desktop/Models.dc.html`; `ui/packages/renderer/src/app/sources/ollama-models-dialog.component.ts` | new: D |
| 4.5 | rough edge | Three defaults disagree: the options class names one model, the shipped settings another, and the desktop never offers the first; the env tests use tags the catalog does not have. | `src/Scout.Llm/Configuration/LlmOptions.cs`; `src/Scout.Host.Console/appsettings.json`; `ui/packages/main/test/worker-build-env.test.ts` | new: C |
| 4.6 | wrong information | `llm.endpoint` is read for the worker but written by nothing; the desktop always probes, pulls and deletes on localhost:11434, so a stored value would send the worker to another server than the one the UI manages. | `ui/packages/main/src/worker/build-env.ts`; `ui/packages/renderer/src/app/core/llm-config.store.ts`; `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts` | new: I |

### Step 5: chat with the model (the worker)

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 5.1 | blocks use | Embeddings need an embedding model that the desktop never downloads. | `src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs`; `src/Scout.Llm/Configuration/LlmOptions.cs` | B |
| 5.2 | wrong information | With that model missing, the skill index logs "running with no skills" and retries every minute, though skills are still picked by words. | `src/Scout.Skills/SkillSelector.cs` | C |
| 5.3 | blocks use | Scout never sets a context length (`num_ctx` and `keep_alive` occur nowhere in `src/` or `ui/`), and chat uses the OpenAI-compatible path, which has no field for it. The system prompt is the base prompt plus one whole skill body (2.4 to 17.8 KB; code-review and code-audit alone are about 4k tokens), plus function schemas, up to 12 history entries and tool results; audit batches default to 60,000 characters. Any truncation is done by the server, silently. The 4,096 load that the audit reports was read from a server log, not re-read. Done 2026-11-12: chat goes to the native chat endpoint and every request asks for `Scout:Llm:Ollama:Chat:ContextLength`, 16384 by default (see "What landed"). | `src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs`; `src/Scout.Llm/ChatGateway.cs`; `src/Scout.Conversation/Routing/LlmStreamingRouter.cs`; `src/Scout.Skills/SkillsOptions.cs`; `context/pages/services/audit.md` | new: H |
| 5.4 | wrong information | Every failure except 429, 402 and 401 ends as "Scout could not answer that; see Scout logs." (or the chat app's error footer): server down, model missing, "does not support tools" (400), out of memory (500), timeout. | `src/Scout.Llm/ChatGateway.cs`; `src/Scout.Conversation/Streaming/StreamPump.cs`; `src/Scout.Adapter.Local/LocalResponseSink.cs` | C (model missing); new: C (the rest) |
| 5.5 | wrong information | Functions are offered whenever the turn's skill declares any, with no capability check; an undeclared or malformed call only logs a warning, and an empty answer reads "(no response from model)" (the explanatory sentence exists for one cloud provider only). Since 2026-11-12 the server's 400 for a model that cannot call tools reaches the gateway as an HTTP 400 failure, with no Ollama client type, and still ends as the generic failure sentence, as on the `/v1` path; a sentence of its own is another lane. Partly done 2026-11-12 for the warning: an installed model whose reported capabilities lack tools carries "No tools" and a sentence in the Ollama dialog and in Models, Analysis, and the model in use says it in the LLM Server panel and the In use strip; the worker still offers functions without a capability check. | `src/Scout.Llm/ChatGateway.cs`; `src/Scout.Conversation/Streaming/StreamPumpOptions.cs` | F (the warning); new: C (the chat sentence) |
| 5.6 | wrong information | Every picture is refused on the local server ("The connected AI source cannot look at pictures..."), because the provider never declares vision, while the catalog sells one model as "Text + Image". | `src/Scout.Llm/Abstractions/ILlmProviderFactory.cs`; `src/Scout.Llm/ChatGateway.cs`; `ui/packages/renderer/src/app/core/local-models.catalog.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts` | new: F |
| 5.7 | rough edge | The conversation pump has no overall timeout, and the embedding `HttpTimeoutSeconds`, `MaxBatchSize` and `ExpectedDimensions` are validated but never applied for the local server; a slow first model load relies on the client library's default (that default is not verified). Since 2026-11-12 chat uses the Ollama client. Neither it nor the `/v1` path before it retries (the Semantic Kernel OpenAI connector sets zero retries when handed an `HttpClient`, which that registration always is), and both use the .NET default 100 s `HttpClient` timeout: on a one-reply call (an audit batch round, the operator's JSON, a translation) it bounds the whole answer of each model call, which a slow CPU-only host can exceed with a long prompt; on a streamed call it bounds only the wait for the response headers. Open owner decision: give the Ollama client an `HttpClient` with no timeout of its own, so each caller's bound governs (the audit turn's 10 minutes, the live translation's 20 s, the pump's overall timeout), or keep the 100 s. Since 2026-11-12 a stream cancelled by a Stop or the pump's timeout throws instead of ending like a finished answer (the Ollama client's reader stopped silently on a cancelled token), and an answer the server stops after a 200 (an `{"error":...}` line, which the client ignores, then no done line) fails as an HTTP 502 instead of being reported as finished; it still ends as the generic failure sentence. | `src/Scout.Conversation/ConversationServiceCollectionExtensions.cs`; `src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs` | new: H |

### Step 6: stop using the server (switching the provider)

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 6.1 | rough edge | Switching provider changes `llm.provider` and restarts the worker; `llm.model`, `llm.endpoint` and the localStorage selection stay (by design); nothing asks the server to unload, so the model stays in memory for the server's own keep-alive; nothing stops the server (and by decision nothing will). Done 2026-11-12: the switch asks the server to unload Scout's own loaded models (chat, translation on the local server, skill matching) and the card says whether it did. | `ui/packages/renderer/src/app/core/llm-config.store.ts`; `ui/packages/renderer/src/app/sources/sources.component.ts` | G (the unload) |

### Step 7: other platforms and hosts

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 7.1 | rough edge | The port and host are literals in six places and `OLLAMA_HOST` is ignored; no local-server UI code has a platform branch, so the only platform risk is the lifecycle code (2.9). | `ui/packages/renderer/src/app/sources/sources.component.ts`; `ui/packages/renderer/src/app/sources/local-models.component.ts`; `ui/packages/renderer/src/app/core/local-models.store.ts`; `ui/packages/renderer/src/app/core/llm-config.store.ts`; `src/Scout.Llm/Configuration/LlmOptions.cs`; `src/Scout.ControlPlane/Providers/OllamaProviderOptions.cs` | new: I (with 4.6) |

### Step 8: tests and documentation

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 8.1 | missing test | The e2e suite rejects every fetch from main, loopback included, so the local server is always "Not connected" there: detection, the dialog, pull, delete, choosing a model, Use and any chat on the local server have no e2e; the one assertion is the top-bar label. Partly done 2026-11-12: the fetch guard answers `/api/tags` and `/api/show` from a per-test fixture (`SCOUT_E2E_OLLAMA`), and `ui/e2e/tests/ollama-installed-models.spec.ts` opens the dialog, lists a model installed outside Scout and puts it In use; since the Pull by name change of 2026-11-12 the guard also answers `POST /api/pull` with a short stream, recording what was pulled for the next list, or the not-found line for a name the fixture marks missing, and `ui/e2e/tests/ollama-pull-by-name.spec.ts` pulls a typed name and sees it under Installed; delete and chat on the local server still have no e2e. | `ui/e2e/fixtures/main-fetch-guard.cjs`; `ui/e2e/tests/no-ai-source.spec.ts` | T |
| 8.2 | stale doc | The desktop page describes Start, Autostart and the status read as working (lines 105, 189) and says the transport is dead (line 169); its citations into both components have drifted (six of them have moved by between 51 and 414 lines). | `context/pages/services/desktop-ui.md` | G (rewrite at integration), K |
| 8.3 | stale doc | The runbook is stale: cloud cards "always Not connected (placeholder)", "Scan for X", the tag mapping "in local-models.component.ts", a chat reply "LLM provider unavailable" (no such string in `src/` or `ui/`), "Worker-side inference: not built yet", "Start on the panel" as a fix, and a speech-model folder name that is not the app's data folder. | `context/pages/runbooks/local-models.md` | new: K |
| 8.4 | stale doc | The invariant page cites wrong lines (`LlmOptions.cs/264/272`, real 260/263/274/282; `build-env.ts/134`, real 71/156) and says the control plane pulls models "from the desktop UI", which the desktop never calls; the audit page says "Works on Ollama" on the strength of a fake server. | `context/pages/invariants/local-first-default.md`; `context/pages/services/audit.md` | new: K |

### Step 9: what leaves the process, and who may set the endpoint

| id | severity | defect | evidence | lane |
|---|---|---|---|---|
| 9.1 | rough edge | Main accepts any http(s) `endpoint` from the renderer for the tags read, the pull and the delete (LM Studio's list too), though the renderer only ever passes one constant; a test asserts that openness. Partly done 2026-11-12 for the pull: it asks only `localhost`, `127.0.0.1` or `[::1]` and refuses any other endpoint before a request; the tags read, the delete and LM Studio's list are unchanged. | `ui/packages/main/src/ipc/llm.ts`; `ui/packages/main/src/ipc/llm-pull.ts`; `ui/packages/main/src/ipc/lm-studio.ts`; `ui/packages/main/test/ipc-llm.test.ts` | new: E |
| 9.2 | rough edge | The worker accepts any absolute http or https endpoint with no loopback check; if `llm.endpoint` were set, prompts, history, tool results (repository content) and embedding text would go there in clear. No UI sets it today. | `src/Scout.Llm/Providers/Ollama/OllamaLlmProviderFactory.cs`; `ui/packages/main/src/worker/build-env.ts` | new: I (with 4.6) |
| 9.3 | rough edge | `hydrateFromWorker` is dormant: once the transport works it would add models and auto-select and save `llm.model` without a click. It is replaced by a read from the server before it can wake; decide in D and G, before G lands. Closed 2026-11-11 by lane G's panel change: `refreshOllama` replaced it, reflects only what the server lists and never selects a model or writes `llm.model`; the owner's Use this and Download still select. | `ui/packages/renderer/src/app/core/local-models.store.ts` | D, G |
| 9.4 | rough edge | LM Studio failures are raised as "Could not reach Ollama at ..." (logs only). | `ui/packages/main/src/ipc/lm-studio.ts` | new: E (trivial) |

### Calls to the local server, by class

The desktop writes happen only on the owner's click. Settings touches the local server nowhere.

| caller | call | class | when |
|---|---|---|---|
| desktop main | `GET /api/tags` (`ui/packages/main/src/ipc/llm.ts`) | read | on opening AI Sources and Models, no click; Connect and Scan again |
| desktop main | `POST /api/show {model}` per listed model, 2 s each, kept by digest once read (`ui/packages/main/src/ipc/llm.ts`) | read | with every `GET /api/tags` above, for a model whose capabilities are not known yet (2026-11-12) |
| desktop main | LM Studio `GET :1234/v1/models` (`ui/packages/main/src/ipc/lm-studio.ts`) | read | same |
| desktop main | `POST /api/pull` (`ui/packages/main/src/ipc/llm-pull.ts`) | write (network, disk) | only the row's Download click, or Pull (or Enter) in Pull by name for a name main checks again, on this computer's Ollama only (2026-11-12) |
| desktop main | `DELETE /api/delete` (`ui/packages/main/src/ipc/llm-pull.ts`) | irreversible write | only after the in-row Remove confirm; not in the write log; listed as a local write in `ui/packages/shared/test/ipc-contract.test.ts` |
| worker | `POST {endpoint}/api/chat` with `options.num_ctx` (since 2026-11-12; `/v1/chat/completions` before) | read (inference; loads the model into memory at that context length) | any allowed chat user's message, live translation, audit batches |
| worker | native embeddings on `{endpoint}` | read | first message (skill index), project indexing |
| desktop main | `GET /api/ps`, then `POST /api/generate` with a zero keep-alive per model (`ui/packages/main/src/ipc/llm.ts`) | read, then a reversible write (unload) of loaded models under Scout's tags only, whoever loaded them; the endpoint configured in Scout, localhost by default | only after the owner's click saves a provider other than the local server while it was in use (2026-11-12); logged by name in main's log, not in the write log; listed as a local write in `ui/packages/shared/test/ipc-contract.test.ts` |
| control plane (.NET) | spawn and kill the server, pull, delete | writes: a reviewed local write on the owner's click, outside the feature grant ([[pages/decisions/0032-per-feature-write-permissions]], amendment of 2026-11-11) | implemented, desktop token only, never called by the desktop; a download logs its start and end and a delete its outcome to the control plane host's log (2026-11-11) |

The two new calls this work adds ([[pages/invariants/no-executive-writes]]): `POST /api/show` per listed model, a read for capabilities that runs with every list read, no click needed (2026-11-12), and, on the owner's click only and classed with the existing local write on the owner's own server, `POST /api/generate` with a zero keep-alive (the unload; reversible, the next request loads the model again).

## The context length

Finding 5.3 is the one that makes an answer wrong without any sign. Decision 1 of the audit asked for a spike before choosing a route; the spike ran on 2026-11-11.

**What was measured.**

- A prompt of about 10,020 tokens was processed as 2,050 tokens with the context loaded at 4,096, and the model invented the answer. The reported usage shows 2,050, so nothing in it signals truncation.
- Four ways of passing a context length through the OpenAI-compatible path were ignored: a request option, a top-level field, a request header, and the extension data of the Semantic Kernel connector. The server kept the 4,096 context each time. A request of that kind arriving while the model was loaded at 16,384 even reloaded it at 4,096.
- Through the native API with a context of 16,384 the same prompt was processed whole (10,020 input tokens) and the marker in the first line was answered correctly. Streaming worked (26 text chunks, then a final metadata chunk), function calling worked (the model called the function, the kernel invoked it once, the reply continued), and a kernel function-invocation filter fired, which is the interface the write gate and the activity filter implement. Usage arrived on the final chunk of each round and `MaxTokens` was honoured.
- Without the context option, the native path behaves like the OpenAI-compatible one.

**The chosen route.** Chat moves from the OpenAI-compatible path to the native API, through the same Semantic Kernel Ollama connector and the chat-client pipeline that the other chat factories already use; the context length is set once in the provider factory so that every caller of the chat service (the gateway, the live translator, the project expert agent) sends the same value. The consequences for the code:

- One new setting on the local chat options, validated in the factory, default 16384, clamped to the model's own limit read from the server.
- A short dated amendment to [[pages/decisions/0012-llm-communication-through-semantic-kernel]] covering chat clients built behind the chat-completion service, since the factory now constructs a native client.
- One more arm in the failure translation of `src/Scout.Llm/ChatGateway.cs`: on the native path a missing model or a server that is down arrives as a plain HTTP exception, not the client-result exception the OpenAI path raises, so without the arm 401, 402 and 429 from a proxy in front of the server would stop mapping. The server's own "model not found" text is lost on this path.
- Tests: a factory test that the request goes to the native chat endpoint with the configured context for both ways the factory is used; a gateway streaming test over a canned tool-call round; failing-call tests (404, 500, reset) through the translation; the options validation.

**Its costs.** The first evaluation of a 10k-token prompt took about 43 s (0.02 s once cached). The small model occupied about 2.7 GB at 16,384 (the size at 4,096 was not captured). Anything else that uses the same model without the same context forces a reload. Tool calls arrive whole, not as deltas. The Semantic Kernel Ollama connector is still an alpha release.

**What is unverified.** The agent path (the chat-completion agent over the native service), pictures on the native path, several tool calls in one turn, a real failure in the middle of a stream, the read-only guard wrapper over the native service, whether the factory's HTTP timeout suits a cold prompt evaluation of 40 s or more, and other server versions and models.

Alternatives not run: a derived model with a context parameter (a write on the owner's server per model), and the server-wide setting (owner-side, all models, not adjustable per model by Scout). Measuring and warning stays as a complement and for the clamp, not as the fix.

**What landed (2026-11-12).** Chat goes through the Ollama client's chat client to `POST {endpoint}/api/chat`, handed to the kernel as in the other chat-client factories ([[pages/decisions/0012-llm-communication-through-semantic-kernel]], amendment of 2026-11-12), so the gateway, the live translator, the project expert agent, audit batches and the operator all send the same `num_ctx`. The value is `Scout:Llm:Ollama:Chat:ContextLength` (environment `SCOUT__LLM__OLLAMA__CHAT__CONTEXTLENGTH`), 16384 by default, refused at start below 4096, with no upper bound; the desktop sets nothing, so the worker sends 16384. The failure translation is not an arm in the gateway: a provider-scoped chat client turns the Ollama client's HTTP status into the exception the gateway already maps, so a missing model (404) is still said by name and a 429 or a 401 from a proxy is still a rate limit or a rejected key; the server's own sentence is not kept. Every request also carries an empty `CustomHeaders` object, which the client serialises and the server is expected to ignore (not verified against a server). Tests run against a fake server only ([[pages/services/llm]]).

**Departure from the default, waiting for the owner.** The default above says the context length is capped at the model's own limit. This change does not cap it: reading the limit means a `POST /api/show` from the worker for the chat model, a new call, so it was left out of the lane. A model whose own limit is under 16384 is therefore asked for more than it was trained for; what the server does then is not verified.

One context length for every call to a model is deliberate: a request with a different `num_ctx` makes the server reload the model. A translation model other than the chat model also loads at 16384, so the two may not fit in memory together and the server would then reload between chat and translation (not measured). After three consecutive failing function rounds the function-invoking client stops passing errors back to the model (`MaximumConsecutiveErrorsPerRequest`, default 3), as on the Anthropic and ChatGPT plan paths.

The earlier minimum (a log line and a chat sentence when the prompt estimate exceeds the loaded context) was not built; the context length replaced it.

## Lanes and order

State on 2026-11-11.

| lane | what it carries | state |
|---|---|---|
| A | Remove the Local Models card. | pull request open |
| B | The embedding model is downloaded together with the chat model, on the click, said with its size (5.1). | built, in review |
| C | A model the AI Source does not have is said by name; the truthful skill-index log (5.2); with the audit's additions 4.5, the three failure sentences of 5.4 and the empty-answer sentence of 5.5. | built, in review (the additions are planned) |
| T | The fake local server in the end-to-end suite and the flow tests, each known defect an expected-failure test (8.1). | built, in review |
| D | Every installed model listed and selectable, with the model rows read from the server (3.1, 3.2, 4.1 to 4.4, 9.3). Also: Use on the card checking a model (1.4), removing the model in use (3.6), the Ready screen's model check (1.6), the selection read from the vault, one "model in use changed" path that saves `llm.model`, surfaces a failed save and restarts a running worker (3.5, 4.2), the pull re-attaching on dialog open (3.4), and the local group depending on this server only and renamed Ollama (4.3, 4.4). | first step done 2026-11-12: every installed model listed and selectable from the server's list, embeddings-only models never a chat choice, the model in use not removable while Ollama is in use (3.1 for the dialog and Models, 3.2, 3.6 in part); the rest planned |
| E | Pull by name and a link to the library. Also: per-model progress (3.3), delete and pull failing-call tests (3.7), main ignoring the renderer's endpoint (9.1), the LM Studio error name (9.4). | Pull by name and the Browse link done 2026-11-12, with the pull's failing-call tests (part of 3.7) and its loopback-only endpoint (part of 9.1); 3.3, the delete tests, the other endpoints and 9.4 planned |
| F | The no-tools warning and vision from capabilities (5.5 warning, 5.6). | the no-tools warning done 2026-11-12 (5.5 warning, in the desktop only); vision from capabilities (5.6) planned |
| G | The server panel tells the truth, in three changes: the panel (2.1 to 2.8, 1.1, 1.2, the Disconnect button of 1.3, the hint removal), deleting the dead worker-HTTP plumbing, and the unload (6.1). | the panel part done 2026-11-11 (2.1 to 2.8, 1.1, 1.2, the hint removal: chip, metrics and line from the list read, the not-answering line on the card; 9.3 closed: reading the list never chooses a model or writes `llm.model`); Autostart, Start, Stop and Restart deferred by the owner (drawn in the artboard, not built); the deletion of the dead worker-HTTP plumbing done 2026-11-12 (the six `scout:providers.*` lifecycle channels, their bridge members and main handlers, the worker HTTP client and the `SCOUT_HTTP=` marker parse); the unload done 2026-11-12 (6.1: on the owner's click that moves the provider in use away from the local server, `scout:llm.unloadOllamaModels` unloads the loaded models whose tag is one of Scout's on the endpoint configured in Scout, whoever loaded them, since the server keeps no record of that, and the card says freed or could not ask); the Disconnect button of 1.3 planned |
| H | The context length (5.3) with the pump timeout and the embedding timeout (5.7). | the context length done 2026-11-12 (5.3, without the cap at the model's limit, which waits for the owner); the timeouts of 5.7 planned, with the owner's decision on the client's own 100 s timeout open |
| I | Loopback only: one endpoint constant for desktop and worker, `llm.endpoint` removed or pinned (1.5, 4.6, 7.1, 9.2). | planned |
| J | The wizard's launch check of the AI Source (1.7). | planned |
| K | The docs sweep: the runbook and the stale invariant citations (8.2 to 8.4). | planned, once at integration |

**Which lanes edit the same files, and the order that avoids two lanes in one file.**

- A touches `sources.component.ts` only. C is worker-only (`ChatGateway`, `StreamPump`, `SkillSelector`, `LlmOptions`). They run in parallel.
- D owns `local-models.store.ts`, the catalog, the list part of `local-models.component.ts`, `models.component.ts`, `llm.ts` and `llm-config.store.ts`; it runs after A and C.
- B and E both edit `llm-pull.ts` and the store's download, so they are serial: B, then E. F touches the capabilities in main's list and the rows in the dialog and Models, so it follows D and E.
- The panel change of G and D both need one `refreshOllama()` in the store, replacing the dormant `hydrateFromWorker`. Whichever lands first creates it; the second only adds to it. D widens it to models outside the catalog and must not add a second read.
- I edits `build-env.ts`, the factory and the constants; it follows D and E and comes before G.
- G is last on the UI: the panel, then the deletion of the dead plumbing (any time after the panel), then the unload, which follows B so the embedding model's tag is among the models unloaded.
- H is worker-only and shares the factory and the gateway with C, so it follows C.
- J touches `worker-launch` only and can go at any time.
- T was built first so that the other lanes land against it; its expected-failure tests are turned green by the lane that fixes each defect.
- K runs once, at integration.

Order: A with C; D; B, then E; F; I; G; H (any time after C); J (any time); K last.

## Not in scope

- LM Studio, beyond the defects it shares with the local server (4.3, 9.1, 9.4).
- Running chat models inside the application.
- Controlling a system-managed service (Start, Stop, Restart, Autostart); the .NET lifecycle code and its RPCs stay as they are and are not wired to the desktop.
- A remote model catalog, or scraping the library.

## Related

- Screens and flows: [[pages/services/desktop-ui]], [[pages/architecture/desktop-models-system]], [[pages/runbooks/local-models]]
- Chat path and skills: [[pages/services/llm]], [[pages/services/skills]], [[pages/decisions/0012-llm-communication-through-semantic-kernel]]
- Rules this work keeps: [[pages/invariants/local-first-default]], [[pages/invariants/no-executive-writes]], [[pages/conventions/testing]]
- Decisions: [[pages/decisions/0008-local-models-real-downloads]]
- Sister trackers: [[pages/roadmap/prototype-migration]], [[pages/roadmap/control-plane-client]]
