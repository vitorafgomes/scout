---
title: "ADR 0039 - A repository snapshot under Scout's data folder, read locally and searched by the Knowledge index"
version: "1.0"
date: 2026-11-06
changes: "Initial ADR"
page_type: adr
status: accepted
description: "A review or an audit takes the repository at one commit from GitHub's tarball into Scout/github-snapshots, a declared write of the github feature; the three repository reads then serve from it and the Knowledge index searches it as the project owner/repo."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs
  - src/Scout.Knowledge/ProjectResolutionService.cs
reliability: high
---

# ADR 0039 - A repository snapshot under Scout's data folder, read locally and searched by the Knowledge index

> `snapshot_repository(owner, repo, reference)` downloads the repository at one commit into `Scout/github-snapshots/<owner>__<repo>/<sha>`, as a write of the `github` feature's data-folder row; from then on `get_file_contents`, `list_repository_files` and `search_code` answer from that tree and `search_project` and `find_symbol` search it as the project `owner/repo`.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-06.

## Context

ADR 0038 gave the model GitHub reads of one file, one tree and one code search, and the `code-audit` skill reads a whole repository through them. On 2026-11-06 the owner asked, after the first real review from the desktop: "wouldn't it be better, when a code review starts, to clone the project so the code and the PR are local and we are not querying GitHub directly all the time?", and later chose the order of the remaining work: "yes, it would be better to create a WorkThree where the project is cloned to validate everything".

The reads have limits the skills had to talk around: one request per file, 100 changed files per page, 300 paths in a tree, GitHub's code search on the default branch only, about ten searches a minute, and no semantic search at all unless the repository is a configured project of the Knowledge index ([[pages/services/knowledge]]), which the owner sets in configuration, never from chat. Phase 4 of the initiative recorded cloning as out of scope: a disk write and a large download that needs its own decision ([[pages/roadmap/code-quality-assistant]]).

A git clone needs a git binary and credentials on the command line; GitHub's tarball of a commit needs neither and comes through the same token the plugin already holds.

## Decision

- **One function, declared as a write.** `snapshot_repository(owner, repo, reference)` on the `GitHubActions` plugin (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:234`) is declared with `AddWriteFunction` on the `github` feature's `Scout's data folder` row, beside the conventions profile (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:99`, `src/Scout.ControlPlane/Features/FeatureCatalog.cs:88`): it runs under the owner's GitHub write grant, the `WriteGateFilter` refuses it otherwise and records every run, and a refusal or a failure is a `WriteFailedException` the model reads as a sentence.
- **The commit, then the tarball.** The client resolves the reference to a commit (`repos/{o}/{r}/commits/{ref}`, the default branch from `repos/{o}/{r}` when the reference is empty; `src/Scout.Plugin.GitHub/GitHubClient.Reads.cs:253`) and streams `repos/{o}/{r}/tarball/{sha}` into the store (`:274`); both are GETs, so the token-file test that every read is a GET still holds.
- **Extraction with guards.** `RepositorySnapshotStore.ExtractAsync` (`src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs:90`) reads the gzip tar with `System.Formats.Tar`, keeps regular files only, strips the tarball's top folder and refuses an entry outside it, under `.git`, or with a `.`, `..`, a drive letter or a NUL in its path (`:329`); it stops at 20,000 files or 200 MB (`Scout:Plugins:GitHub:SnapshotMaxBytes`; `:133`), extracts into a staging folder moved into place at the end so a failed snapshot leaves nothing, describes each snapshot in a `<sha>.json` beside its folder so the tree holds only the repository's files, and purges the repository's other snapshots older than seven days, never one still being extracted (`:304`). Same commit again, through any reference: nothing is downloaded, the description is relabelled.
- **The reads prefer the snapshot.** `get_file_contents`, `list_repository_files` and `search_code` answer from a snapshot of the owner/repo at the reference asked for (the default branch's when none; the newest snapshot for the search, which names no reference), and say so with the short SHA (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:253`, `:332`, `:389`); a file is cut at the same 20,000 characters, a binary or a file over 1 MB is named as such, the local search is a case-insensitive substring over every text file with the matching lines as fragments (`src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs:231`). Another reference still asks GitHub.
- **The Knowledge index learns it at run time.** The plugin registers the snapshot through `IRepositoryProjects` (`src/Scout.Plugin.GitHub/Abstractions/RepositorySnapshot.cs:13`), an abstraction the host implements over `ProjectResolutionService.Register` (`src/Scout.Host.Console/KnowledgeRepositoryProjects.cs:7`, `src/Scout.Knowledge/ProjectResolutionService.cs:83`), because the plugin may not reference Knowledge ([[pages/invariants/pluggable-boundaries]]); a registered project is indexed on its first use and again after a new snapshot replaces it (`:123`). The `ProjectKnowledge` plugin therefore rides the kernel always, with or without configured projects (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:72`); the Project Expert agent and the watcher stay gated on configuration.
- **The skills start with it.** `code-audit` takes the snapshot before reading anything (`src/Scout.Skills/skills/code-audit.md:17`), `code-review` takes it at the head branch when the pull request is larger than one listing or a judgement needs the code around a hunk (`src/Scout.Skills/skills/code-review.md:14`), `project-improvements` when the project is not indexed (`src/Scout.Skills/skills/project-improvements.md:13`); each says what to do when the write is not granted or the repository is over the cap: say so once and go on with the GitHub reads.

## Consequences

- **Better:** a review or an audit reads the whole repository at one commit, with no per-file requests, no 100-file or 300-path cut and no search quota; `find_symbol` and `search_project` work on any repository the owner can read, from chat; the same commit is never downloaded twice.
- **Worse:** up to 200 MB per snapshot on disk under Scout's data folder, kept for a week; the first `search_project` on a fresh snapshot pays the indexing (embeddings over every file); a repository over the cap gets no snapshot at all, not a partial one.
- **Neutral but noteworthy:** the snapshot is a tree, not a clone: no history, no `.git`, no diff between commits (the pull request's diff still comes from GitHub); a snapshot of a private repository lives on the owner's disk under the same folder as the rest of Scout's data.

## Alternatives considered

- **`git clone` into a worktree** - needs a git binary and credentials on the command line, brings the history nobody reads and the `.git` folder the index must skip; the tarball gives the tree through the token the plugin already holds.
- **A partial snapshot when over the cap** - an audit that silently reads half a repository anchors findings nowhere; a refusal that says the cap and suggests narrowing is honest.
- **Keeping the reads on GitHub and adding a semantic search over the API** - no API offers one; the Knowledge index exists and needed only a way to learn a project at run time.
- **Status quo (do nothing)** - the reads' limits stay the skills' problem, and the Knowledge index stays out of reach of a review.

## Enforcement

- **Tests** - `tests/Scout.Plugin.GitHub.Tests/RepositorySnapshotStoreTests.cs:29` regular files only, never `.git`, never a path that climbs out or sits outside the top folder; `:125` over the cap is refused and leaves nothing; `:137` a new snapshot replaces the same commit and purges the week-old ones; `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginTests.cs:214` the snapshot is taken once, registered as a project and found again without a download; `:233` a missing repository, a tree over the cap or no tarball refuse and register nothing; `:248` GitHub unreachable before the download and `:261` a stream that breaks mid-download are the sentence for the model and nothing is kept (Rule I); `:282` the same commit reached through another reference is relabelled, not downloaded again; `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:467` the three reads serve from the snapshot and say so, another reference still asks GitHub; `tests/Scout.Plugin.GitHub.Tests/GitHubClientReadsTests.cs:367`, `:386` the two client reads; `tests/Scout.Knowledge.Tests/ProjectResolutionServiceTests.cs:573` a registered project is listed, found and indexed again after it is replaced; `tests/Scout.Knowledge.Tests/KnowledgeServiceCollectionExtensionsTests.cs:107` the plugin rides the kernel with no project configured; `tests/Scout.Host.Console.Tests/KnowledgeRepositoryProjectsTests.cs:13` the host wires the registry to the index.
- **Catalog** - the data-folder row names the snapshot (`tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:49`); the declaration is one of the four the composition test pins (`tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:102`).
- **Invariant page** - [[pages/invariants/no-executive-writes]]: a write into Scout's own folder, declared and granted.

## Related

- Services affected: [[pages/services/plugin-github]], [[pages/services/knowledge]], [[pages/services/host-console]], [[pages/services/skills]], [[pages/services/control-plane]].
- Builds on: [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]], [[pages/decisions/0032-per-feature-write-permissions]].
- Initiative: [[pages/roadmap/code-quality-assistant]] (Phase 5).

## Notes

A graphify-style structural graph (callers, hubs, blast radius) over the snapshot would be a third toolset beside the reads and the index; the owner raised it on 2026-11-06 and it is recorded in [[pages/roadmap/followups]], to be weighed once the snapshot has been used on a real review.
