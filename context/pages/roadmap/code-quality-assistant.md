---
title: "Code quality assistant"
version: "1.0"
date: 2026-11-06
changes: "Initial initiative page: the analysis against the first cut, the decisions the owner took and the four phases"
page_type: roadmap
status: active
description: "Initiative: from a chat message, Scout reviews a pull request and posts the review on it, or finds what a project could improve and opens the issues, in any language, under the owner's write grant and per-action approval."
source:
  - chat
  - "src/old/Scout.Plugin.CodeQA/ (read 2026-11-06 for history; not extended)"
  - "src/Scout.Plugin.GitHub/"
  - "src/Scout.Skills/skills/"
reliability: medium
---

# Code quality assistant

> A developer writes "review PR 42" or "what could we improve in this repo" in Discord, Slack, Teams or the quick-ask window; Scout reads the pull request or the project, answers with findings anchored to file and line in whatever language the code is written in, and, when asked, posts the review on the pull request or opens the issues, each write waiting for the owner's approval in Scout Desktop.

## Why now

The owner asked on 2026-11-06 for Scout to help with code quality from chat: know on its own what to look at, post review comments inside the pull request when the request is a review, open an issue saying what to implement and how when the request is an improvement, and do it for every programming language, not one. The question was whether the retired `Scout.Plugin.CodeQA` under `src/old` should come back for it.

## What the analysis found (2026-11-06)

- **"Knowing what to do" is already how Scout works.** The conversation router selects the skill whose description is closest to the message and injects it into the system prompt ([[pages/services/skills]], `SkillSelector`, top 1 at similarity 0.55), and the model calls functions on its own. A new intention is a new skill markdown with a description that matches what users type, plus the functions it needs. No routing code.
- **Language-agnostic has a lever already in the box.** `src/Scout.Skills/skills/` holds skills for 24 languages (csharp, typescript, python, go, java, kotlin, rust and the rest) next to clean-code, refactoring, software-architect and qa-tester. A review skill tells the model to read the extensions of the files in the diff and `load_skill` the matching language skill; a general rubric covers the rest.
- **The write contract is ready for this.** [[pages/decisions/0032-per-feature-write-permissions]] gives a feature a scope matrix, `AddWriteFunction` declares a function as a write, the `WriteGateFilter` asks the control plane on every call, and an irreversible write waits on the Review screen. Posting a review and opening an issue are exactly the case it was built for.
- **`Scout.Plugin.CodeQA` does not come back.** It depends on the retired `Scout.Core`, on Octokit, on an in-memory vector store and on a keyword router; [[pages/services/knowledge]] already indexes repositories into SQLite with a better chunker and answers `search_project` and `find_symbol`. Of the first cut only shapes were taken: the pull request detail and issue read of the old GitHub plugin, reimplemented on the typed REST client. Nothing under `src/old` moved, and the tree was deleted on 2026-11-06 on the owner's word; the decision is recorded in [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]].
- **What was missing** was the single-item reads (one pull request with its diffs, its comments, one issue, one file), the two writes, a write-capable GitHub credential, and the two skills.

## Scope

- **In scope:** review of a pull request on GitHub from chat, in any language; a prioritised improvement list for a repository, indexed locally or read from GitHub; an audit of a whole repository with no pull request, judged against the project's own conventions, across bugs, security, conventions, performance, tests and documentation (phase 4); posting the review as review comments on the pull request; opening issues for the improvements; every write declared and granted, posted the moment the user asks in chat (v1.3 of ADR 0038; the per-action approval of phases 2 and 3 was removed the same day).
- **Out of scope:** a review triggered by a webhook when a pull request opens (needs a public endpoint, the same blocker as [[pages/roadmap/teams-adapter-spike]]); replying inside an existing review thread; GitLab and Azure DevOps (another plugin behind the same two skills); editing code or pushing fixes (Scout never changes a repository).

## Target outcome

By 2026-11-30, from any connected chat: "review PR 42 in owner/repo" returns findings with `path:line` that exist in the diff and, on request, a review the owner approved appears on the pull request; "what could we improve in owner/repo" returns a prioritised list and, on request, the issues the owner approved exist on the repository.

## Status

- **As of 2026-11-06:** phases 0 to 4 merged into `main` (#91, #93, #95); the same evening the owner's first real review (Acme #5) sat in the Review queue unseen, and the review and the issue became direct writes under the grant on `feat/github-writes-under-the-grant` (ADR 0038 v1.3): no draft, no queue, no executor.
- **Next milestone:** merge, then the owner widens the GitHub App to Pull requests and Issues read & write and grants GitHub writes on the Features screen; first review posted end to end.
- **Blocker:** the App permissions on github.com are the owner's step; until then the executor's POST answers 403 and the chat is told the write was not performed.

## Workstreams / phases

### Phase 0 - The decision
- **Status:** done 2026-11-06 (ADR proposed; the owner's merge accepts it)
- **Scope:** [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]]: GitHub writes through the write gate, the App widened to Pull requests and Issues read & write, CodeQA superseded by Knowledge.
- **Success criteria:** the ADR names the enforcement that replaces "GitHub is GET-only".

### Phase 1 - Read a pull request, review in chat
- **Status:** done 2026-11-06
- **Scope:** `get_pull_request`, `get_pull_request_file`, `list_pull_request_comments`, `get_issue`, `get_file_contents` in [[pages/services/plugin-github]]; the `code-review` and `project-improvements` skills; names, paths and references the model passes checked before a URL is built.
- **Success criteria:** every request any GitHub function sends is still a GET; a review in chat cites lines that exist in the diff.

### Phase 2 - Post the review
- **Status:** done 2026-11-06 (shape: draft, queued action, executor; see the ADR)
- **Scope:** `submit_pull_request_review(owner, repo, number, body, comments)` declared with `AddWriteFunction` for the `github` feature, scope row "Post a review with comments on a pull request", `IrreversibleWrite`; one review is one action in the queue; each comment is mapped to a line of the diff on the right side before the action is queued, and a comment that maps to no line is dropped with a note; a failing-call test for the POST (Rule I); the GET-only tests become "every non-GET comes from a declared write function and goes to that endpoint"; the catalog's GitHub entry becomes `WriteClass.Executive`; the desktop's `prepareWriteGrant` for `github` explains the App permissions to approve.
- **Success criteria:** the owner sees "repo, PR, N comments, first lines" on the Review screen; approving posts the review; the model says "waiting for the owner" until then.

### Phase 3 - Improvements as issues
- **Status:** done 2026-11-06
- **Scope:** `create_issue(owner, repo, title, body, labels)` declared for the `github` feature, scope row "Open an issue", `IrreversibleWrite`; the body follows Problem, Evidence (`path:line`), Proposed change, Acceptance; the `project-improvements` skill files one issue per item the user picks.
- **Success criteria:** an issue the owner approved exists with that body; an improvement already tracked is reported with its number instead.

### Phase 4 - Audit a whole project from chat, against its own conventions
- **Status:** done 2026-11-06 (on `feat/code-audit-from-chat`)
- **Why:** the owner's limit of 2026-11-06: "I cannot ask the agent to analyse the code directly if there is no open PR ... it should allow analysing the whole project ... first try to identify the patterns used". The reads stopped at one file and the root folder, the Knowledge index covered ten extensions, and nothing judged the code against the house rules.
- **Scope:** two reads on the GitHub plugin, `list_repository_files` (the recursive tree in one call with counts by folder and extension, filters and a 300-path cap; `src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:307`) and `search_code` (GitHub's code search on the default branch, with highlighted fragments; `:359`), the query refusing any `repo:`, `org:` or `user:` qualifier so a search cannot leave the repository (`src/Scout.Plugin.GitHub/GitHubNames.cs:58`); a **conventions profile** per repository, written by the model from the project's own files and from the owner's corrections in chat, kept under `Scout/github-profiles` as a reversible write of the `github` feature (`remember_project_conventions`, `get_project_conventions`; `src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:369`, `:334`); the Knowledge file filter widened to the languages the skills cover, the manifests and the extensionless build files, with credentials files excluded (`src/Scout.Knowledge/ProjectFileFilter.cs:50`, `:83`); and the `code-audit` skill (`src/Scout.Skills/skills/code-audit.md`): pass 1 reads or discovers the profile, pass 2 audits one dimension at a time and anchors every finding to `path:line` and to the profile line it departs from.
- **Success criteria:** "analyse the code of owner/repo" with no pull request open ends in findings with `path:line`, each tied to a rule of the project or named a defect, and the profile the user can correct; the search cannot reach another repository (`tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:447`); a `.env` or a `.pem` never enters the index (`tests/Scout.Knowledge.Tests/ProjectFileFilterTests.cs:55`).
- **Out of scope, still:** cloning a GitHub repository into the Knowledge index from chat (done as Phase 5, ADR 0039), running analysers on the project's code (Scout reads, it does not execute third-party code), Dependabot alerts (one more App permission; a later phase if wanted).

### Phase 5 - A repository snapshot, read locally and searched by the Knowledge index
- **Status:** done 2026-11-06 (on `feat/repository-snapshot`)
- **Why:** the owner, 2026-11-06: "wouldn't it be better, when a code review starts, to clone the project so the code and the PR are local and we are not querying GitHub directly all the time?"; the reads cost one request per file, cut at 100 files, 300 paths and ten searches a minute, and the Knowledge index could only search projects set in configuration.
- **Scope:** `snapshot_repository(owner, repo, reference)` on the `GitHubActions` plugin, a write of the `github` feature's data-folder row (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:234`): the commit resolved and the tarball streamed through the client (`src/Scout.Plugin.GitHub/GitHubClient.Reads.cs:253`, `:274`), extracted with guards into `Scout/github-snapshots/<owner>__<repo>/<sha>` (`src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs:90`); `get_file_contents`, `list_repository_files` and `search_code` served from the snapshot (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:253`, `:332`, `:389`); the snapshot registered as the Knowledge project `owner/repo` through `IRepositoryProjects` (`src/Scout.Host.Console/KnowledgeRepositoryProjects.cs:7`, `src/Scout.Knowledge/ProjectResolutionService.cs:83`); the three skills taking it first. Decision: [[pages/decisions/0039-repository-snapshot-under-the-data-folder]].
- **Success criteria:** "analyse the code of owner/repo" takes one download and then reads every file locally; `find_symbol(owner/repo, name)` answers from the snapshot; a tree over 200 MB or 20,000 files is refused with nothing left on disk (`tests/Scout.Plugin.GitHub.Tests/RepositorySnapshotStoreTests.cs:125`); an entry that climbs out of the tree is never written (`:29`).
- **Phase 6, 2026-11-06, built on `feat/chat-review-findings`:** the interactive findings in chat ([[pages/decisions/0040-review-findings-as-cards-in-chat]]): the `code-review` skill ends the review with a `scout-findings` block, the Chat screen and the floating-button chat render it as cards with Yes, No and Edit as icon buttons, "Yes to all", "Post n on PR #n", the GitHub-styled preview and the posted box; Post is a question to the worker with the chosen comments after the `Review to post` marker, which the skill posts unchanged through `submit_pull_request_review`. The microphone button became the prototype's icon in the same change.
- **Phase 7, 2026-11-07, built on `feat/project-vault-skill`:** the ruler for the audit's documentation dimension, as data the model loads rather than code: the `project-vault` skill (`src/Scout.Skills/skills/project-vault.md`) describes the shape a project's documentation serves (index, log, decisions, conventions, invariants, runbooks, `path:line` on every claim), how `code-audit` grades what is missing by function under any folder names, and the one "Adopt a vault" finding, Medium, whose Fix is the issue body with the skeleton and the pass 1 conventions profile as the first page; it travels through `code-audit`'s cards or `project-improvements`' list like any other finding, and Scout never writes the pages into the repository ([[pages/services/skills]]).
- **2026-11-09, built on `gp/fix-stop`:** issues, comments and replies wait for the owner ([[pages/decisions/0032-per-feature-write-permissions]]): `create_issue`, `comment_on_pull_request`, `comment_on_issue` and `reply_to_review_comment` are irreversible writes, queued per call with their arguments and sent by the worker's GitHub executor only after the owner approves each one; the skills answer that they are queued, never a URL, and the audit cards show the issues queued. Posting a review from chat stays a write under the grant.
- **Next:** the same cards for the improvements skill; the status line counting the files read.

## Where the spec lives

- **Decision:** [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]].
- **Contract and rules after each phase ships:** [[pages/services/plugin-github]] (functions, endpoints, tests), [[pages/services/skills]] (the two skills), [[pages/invariants/no-executive-writes]] (how GET-only becomes declared-writes-only).
- **This page** keeps the problem, scope, phases and open questions.

## Open questions

- Whether the owner widens the App's permissions or prefers a fine-grained token with write access per host (ADR 0038 chose the App; the token path stays for hosts without the desktop).
- Resolved 2026-11-06: Scout posts commenting reviews only; a review with no finding and no body is refused as nothing to post, and the verdict stays in chat.
