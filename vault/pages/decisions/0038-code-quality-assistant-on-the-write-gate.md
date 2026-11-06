---
title: "ADR 0038 - The code quality assistant: GitHub reads and writes on the write gate, CodeQA superseded by Knowledge"
version: "1.2"
date: 2026-11-06
updated: 2026-11-06
changes: "v1.2 (2026-11-06): phase 4, a whole-project audit from chat with the tree and code-search reads and a conventions profile kept as a reversible write. v1.1: phases 2 and 3 implemented the same day; the draft-and-executor shape, the feature-scoped claim on the worker gate, and Scout commenting only are recorded. v1.0 initial ADR"
page_type: adr
status: proposed
description: "Code review and project improvements come from two skills over the GitHub plugin: reads of one pull request, its comments, one issue and one file now; a posted review and an opened issue later as irreversible writes under the github feature's grant, with the GitHub App widened to Pull requests and Issues read & write. The retired Scout.Plugin.CodeQA is superseded by Scout.Knowledge and stays under src/old."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "src/Scout.Plugin.GitHub/"
  - "src/Scout.Skills/skills/code-review.md"
  - "src/Scout.Skills/skills/project-improvements.md"
  - "src/old/Scout.Plugin.CodeQA/ (read for history)"
reliability: high
---

# ADR 0038 - The code quality assistant: GitHub reads and writes on the write gate, CodeQA superseded by Knowledge

> Reviewing a pull request and finding a project's improvements are two skills the model follows over the GitHub plugin's reads; posting the review and opening the issues are two declared irreversible writes of the `github` feature, approved one by one in Scout Desktop, with the GitHub App widened to write pull requests and issues; the retired CodeQA plugin is not rebuilt.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-06 (phases 2 and 3 implemented on `feat/code-quality-assistant-writes`). Proposed on the owner's request of 2026-11-06 ("se for um code review ele deve saber que deve criar os comentarios dentro do PR, e caso for melhorias de projeto tem que saber que tem que abrir um Issue", "nao deve ser algo apenas para uma linguagem especifica de programacao mais sim para todos") and the owner's "Estruture tudo entao pode iniciar" on the proposal below. Merging the phase 1 pull request accepts it.

## Context

Scout's GitHub feature reads: four listing functions over a typed REST client, a GitHub App registered with read-only permissions ([[pages/decisions/0019-github-app-device-flow]]), a test that fails on any request that is not a GET, and a catalog entry with four read rows. The owner wants Scout to help with code quality from chat: review a pull request and leave the comments on it, or look at a project, say what could improve and open the issues, for every language.

Three things in the repository already carry most of it. The skill selector ([[pages/services/skills]]) picks the skill whose description matches the message and the model calls functions on its own, so a new intention is a skill file, not routing code. The bundled skills cover 24 languages, so a review skill can load the language skill for each extension in the diff. The write contract ([[pages/decisions/0032-per-feature-write-permissions]]) declares writes per function, asks the control plane on every call and queues irreversible ones for the owner.

The retired first cut has `Scout.Plugin.CodeQA` under `src/old`: a RAG over one GitHub repository (Octokit tree reads, a token chunker, an in-memory vector store, an LLM composer, a keyword router), on the retired `Scout.Core`. [[pages/services/knowledge]] does the same over local repositories with a SQLite vector store, a Roslyn chunker with a line-window fallback for every other language, and the `search_project`, `find_symbol` and `list_projects` functions. The restructure backlog asked for the retired plugins' fate ([[pages/architecture/restructure-architecture]]).

## Decision

- **Two skills carry the intention.** `code-review` and `project-improvements` in `src/Scout.Skills/skills/` describe the requests they answer in the words users type (English and Portuguese phrases in the description, so the embedding match finds them), name the functions to call in order, carry a language-agnostic rubric, tell the model to `load_skill` the language skill for each extension present, fix the answer shape (findings anchored to `path:line` that exist in the diff; improvements as what, why, how, where), and say what happens when the user asks to post or to file: call the write function if it exists and relay that it waits for the owner; otherwise say posting is not enabled. The skills never promise what the functions cannot do.
- **The GitHub plugin gains the single-item reads, all GETs, in phase 1:** `get_pull_request` (metadata, extensions, file list, diffs within a 12,000-character budget, the rest marked), `get_pull_request_file` (one diff up to 20,000 characters), `list_pull_request_comments` (review and conversation comments, oldest first), `get_issue`, `get_file_contents` (one file up to 20,000 characters, or one directory, at a reference). Every owner, repository, path and reference the model passes is checked against a name grammar before a URL is built, so a value cannot climb out of the repository's endpoints (`src/Scout.Plugin.GitHub/GitHubNames.cs`), the same rule the Kubernetes plugin applies to pod names.
- **Two irreversible writes follow, in phases 2 and 3,** on the `github` feature under `WriteClass.Executive`: a scope row "Post a review with comments on a pull request" behind `submit_pull_request_review`, and "Open an issue" behind `create_issue`, each declared with `AddWriteFunction`, each one action in the owner's queue (a review with all its comments is one action), each logged. They are irreversible because a third party reads them the moment they exist, the class [[pages/decisions/0032-per-feature-write-permissions]] gives a published comment reply. Before a review is queued, each comment is mapped to a line of the diff; one that maps to nothing is dropped and the model is told.
- **The GitHub App is widened to Pull requests: read & write and Issues: read & write** (Metadata and Contents stay read-only), so the device-flow token can post. GitHub's permission model no longer proves Scout reads only; the write gate does: the only non-GET requests leave from declared write functions, to the two endpoints they name, after the control plane allowed them, and the GET-only test becomes that statement. A pasted fine-grained token keeps working for a host without the desktop, with the same two permissions when the owner grants the writes. This amends [[pages/decisions/0019-github-app-device-flow]].
- **`Scout.Plugin.CodeQA` is superseded by `Scout.Knowledge`** and is not rebuilt; `Scout.Plugin.Agent`, `Scout.Plugin.Chat` and `Scout.Plugin.Help` are superseded by the conversation core's function calling and skills. The tree under `src/old` stays as the history the root `CLAUDE.md` says it is; deleting it is a separate decision of the owner.
- **A write from chat is a draft, an action and an executor** (phases 2 and 3, 2026-11-06). The two functions, `submit_pull_request_review` and `create_issue` on the `GitHubActions` plugin, never reach GitHub: each is declared as a write into Scout's data folder, so the write gate refuses it before anything is saved while the owner has not allowed GitHub writes; under the grant it checks the request (names, caps, and for a review every comment against the right side of the diff, `DiffLines`), saves a draft file under `Scout/github-drafts` with the chat it came from, and asks the control plane for the irreversible row, which queues the action for the Review screen. The queue holds only the summary the owner reads; the text stays in the draft. The `GitHubActionExecutor`, a hosted service in the worker, claims the next approved `github` action every minute through `IApprovedActions`, finds the draft by the action id, posts it with `IGitHubWriteClient`, records the write against the action, deletes the draft and tells the chat; an action without a draft is recorded failed so the queue moves on, a failure GitHub may get past (a transport fault, a 5xx, a 401 during a token refresh, a 403 rate limit, a 429) leaves the claim to lapse and retry, any other GitHub refusal is recorded failed and the chat told why; the draft is marked posting before the POST and a draft another run left posting is never posted again, so a crash between the post and its record cannot post twice; drafts older than a week are purged. Scout posts `COMMENT` reviews only, never an approval or a request for changes. This is the operator's shape ([[pages/decisions/0033-channel-operator-architecture]]) without a project: the draft is the state the approved action acts on.
- **The worker gate hands out another feature's approved actions by name.** `NextApprovedActionRequest.feature_id`, empty for the YouTube operator's queue, names the feature whose oldest approved action the worker claims, through `WriteGateService.ClaimNextApprovedAsync`: only under the feature's grant, only actions requested under the current grant, the same 30-minute lease. The origin reference for such an action is not the control plane's to know; the draft keeps it.
- **A whole project is audited against its own conventions, with no pull request** (phase 4, 2026-11-06). Two more reads: `list_repository_files`, GitHub's recursive tree in one call with counts by folder and extension and a 300-path cap, and `search_code`, GitHub's code search on the default branch with the highlighted fragments; a query may not carry a `repo:`, `org:` or `user:` qualifier, because the repository is the function's argument and a search must not leave it. The `code-audit` skill makes the model learn the house rules first, from the manifests, the lint and format configuration, the CI, the README and the architecture notes and a sample per layer, and keep them as a **conventions profile** under `Scout/github-profiles`: `remember_project_conventions` is a reversible write declared on the `github` feature's `Scout's data folder` row, so it runs only under the owner's GitHub write grant and is logged, and `get_project_conventions` reads it back; the owner corrects a rule in chat and the correction is kept. Findings are then departures from the profile or defects the code shows, never taste. The Knowledge index covers the languages the skills cover and the project manifests, and never a credentials file.
- **No new project.** The orchestration is the model's, directed by the skills; the code lives where the logic is: the GitHub clients and plugins, the diff anchoring, the draft and profile stores and the executor.

## Consequences

- **Better:** code review and improvement planning for every language with two markdown files and five functions, on the routing and the write contract Scout already has; a review that cites lines the diff contains; one place (the catalog) that says what the GitHub feature may do, and a queue the owner sees before anything reaches GitHub.
- **Worse:** read-only GitHub is no longer guaranteed by GitHub once the App is widened; it rests on the write gate and the tests that pin it. A pull request over a hundred files or with very large diffs is reviewed in parts, and the model must say which files it read.
- **Neutral but noteworthy:** the first cut's RAG over a remote repository is not replaced; a project not indexed locally is read through `get_repository` and `get_file_contents`, file by file, which is enough for an improvement list and not for a semantic search.

## Alternatives considered

- **Move `Scout.Plugin.CodeQA` from `src/old` into `src/`** - it depends on `Scout.Core`, Octokit and an in-memory store, and everything it did is in `Scout.Knowledge`; porting it would be a second index of the same repositories.
- **A `Scout.CodeReview` project with its own pipeline** (fetch, select, review, post) like `Scout.Operator` - the review is one model turn over a diff, not a multi-stage production; a project for it is the premature abstraction Rule C forbids. It becomes right if a review ever needs stages (per-file passes with state, a second model pass), and the two skills would not change.
- **A fine-grained token with write access instead of widening the App** - keeps the App read-only but puts the owner back to creating and pasting tokens, which [[pages/decisions/0019-github-app-device-flow]] exists to avoid; kept as the second method only.
- **Reviews posted as individual comments, one action each** - ten approvals for one review; the owner approves the review once and sees every comment in its summary.
- **Status quo (reviews in chat only)** - what phase 1 delivers; the owner asked for the comments on the pull request and the issues on the repository.

## Enforcement

- `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:140` - every request any GitHub function sends through the real composition is a GET (phase 1); phase 2 rewrites it as "every non-GET comes from a declared write function, to the endpoint it names".
- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:38` - exactly the nine read functions exist, so a write function cannot appear undeclared.
- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:165` and `tests/Scout.Plugin.GitHub.Tests/GitHubNamesTests.cs` - a name that could leave the repository is refused before any request.
- `tests/Scout.Plugin.GitHub.Tests/GitHubClientReadsTests.cs:99` and `:111` - a 5xx becomes the unavailable message and a timeout bubbles to the plugin's clean line (Rule I).
- `tests/Scout.Skills.Tests/BundledSkillsTests.cs:25` and `:39` - the two skills load, describe the requests in the users' words, and name only functions and language skills that exist.
- `src/Scout.ControlPlane/Features/FeatureCatalog.cs:19` - the `github` rows name every read the functions perform, the five new ones included (the review of 2026-11-06 found them undeclared; Rule A).
- `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:20` - the `github` rows: four reads, the data folder as `Write`, "Pull request reviews" and "New issues" as `IrreversibleWrite`, under `WriteClass.Executive`; `tests/Scout.Host.Console.Tests/WriteDeclarationCatalogTests.cs:13` - the two declared functions name a write row of their feature.
- `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginTests.cs:29` - a review is saved as a draft, queued, and keeps only the comments on the diff; `:55` - a refusal leaves no draft; `:67` - an `Allowed` answer for an irreversible row runs nothing; `:79` - nothing to post, malformed comments and a missing pull request are refused before the gate; `:110` - a control plane that does not answer leaves no draft.
- `tests/Scout.Plugin.GitHub.Tests/GitHubActionExecutorTests.cs:34` to `:186` - an approved review or issue is posted, recorded against the action, deleted and told to the chat; no draft records failed; a recoverable failure retries from a clean draft; a draft left posting is not posted again; a post not recorded is recorded next run without a second post; an unexpected exception never leaves the run; a rejection records failed and tells why; stale drafts are purged.
- `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:194` - the only requests that are not a GET come from the write client to its two endpoints; `:216` - the two write functions themselves send only reads and queue.
- `tests/Scout.Plugin.GitHub.Tests/GitHubWriteClientTests.cs:22` - the review is posted with event `COMMENT` and every comment on side `RIGHT`; `:64` - a 422 names what GitHub rejected; `:78` - a 5xx maps as the reads do and a timeout bubbles (Rule I).
- `tests/Scout.Plugin.GitHub.Tests/DiffLinesTests.cs:23` - added and context lines anchor, removed lines do not; `:42` - a trailing newline adds no phantom anchor.
- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:278` - a search query with a scope qualifier is refused before any request; `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:149` - the tree and the search are GETs like every other read; `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginTests.cs:126` - the profile is absent, saved, read back with its date; `tests/Scout.Knowledge.Tests/ProjectFileFilterTests.cs:55` - `.env`, `secrets.json`, `.pem` and `.key` never enter the index; `tests/Scout.Skills.Tests/BundledSkillsTests.cs:26` - the `code-audit` skill describes the requests in the users' words and names only functions that exist.
- `tests/Scout.ControlPlane.Tests/WriteGate/WriteGateServiceTests.cs:359` - the feature-scoped claim: oldest approved first, nothing before the grant, nothing without it, another feature's queue untouched; `tests/Scout.Host.Console.Tests/GrpcWriteGateTests.cs:58` - the claim over the socket carries the feature id.
- Convention page: [[pages/conventions/testing]]. Invariant page: [[pages/invariants/no-executive-writes]].

## Related

- Services affected: [[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/control-plane]] (phase 2), [[pages/services/desktop-ui]] (phase 2).
- Amends: [[pages/decisions/0019-github-app-device-flow]] (App permissions), [[pages/decisions/0032-per-feature-write-permissions]] (the GitHub feature joins the write-capable ones).
- Supersedes the design in [[pages/domain/code-qa]] with [[pages/services/knowledge]].
- The desktop grants GitHub writes on the Features screen like any write-capable feature ([[pages/services/desktop-ui]]); the GitHub App's permissions are widened once by the owner on github.com, the only step this ADR leaves to a person.
- Initiative: [[pages/roadmap/code-quality-assistant]].
