---
name: project-improvements
description: Finding what to improve in a software project and how - "what could we improve in this repo", "what is missing in this project", "audit the codebase", "where is the technical debt", "o que dá para melhorar no projeto", "o que falta neste repositório", "abre uma issue com as melhorias". Reads the project's code and documentation, in any language, and answers with a prioritised list of improvements, each with what, why, how and where. Not for reviewing one pull request, not for a question about one function.
---

# Project improvements

Use this when a user asks what a project could do better, what it lacks, or wants the improvements written up as work items. The list is grounded in the project's real files, never in what projects like it usually need.

## Read first

1. If `list_projects` is available and names the project, use `search_project(project, question)` and `find_symbol(project, name)`: that index covers the whole repository on this machine. Otherwise, or for a repository on GitHub, use `list_repository_files(owner, repo)` for the shape (folders, languages, where the tests live), `get_repository(owner, repo)` for the README, `search_code(owner, repo, term)` to find where something is done, and `get_file_contents(owner, repo, path)` for the files that decide the shape of a project: the build or package manifest, the CI workflow, the test folder, the entry point, the configuration, the largest source folder. For a full audit across bugs, security, conventions and performance, the `code-audit` skill is the one to follow.
2. `list_issues(owner, repo)` and, for the ones that look related, `get_issue(owner, repo, number)`: an improvement that is already tracked is reported as tracked, with its number, not as new.
3. Load the language skill with `load_skill` for the project's main languages when one exists (csharp, typescript, javascript, python, go, java, kotlin, rust, ruby, php, swift, scala, dart, c, cpp, sql, bash, powershell, elixir, clojure, haskell, lua, perl, r), and `clean-architecture`, `clean-code`, `refactoring` or `qa-tester` when the question is about structure, readability, debt or tests.

Read enough to be specific: an improvement that names no file is a guess. Say which files were read.

## What to look for, in any language

- **Tests and quality gates.** No tests, tests that never run in CI, a CI that builds but does not test, no linter or formatter, warnings tolerated, no test for the money or permission paths.
- **Correctness risks.** Error handling that swallows failures, retries without limits, time or culture assumed, concurrency without protection, resources not released.
- **Security.** Secrets or tokens committed, input reaching queries, shells, paths or URLs unvalidated, dependencies with known advisories, permissions broader than the task.
- **Structure.** A module reaching into another's internals, a vendor SDK type leaking into the core, duplicated logic that drifts, a file or function far larger than its neighbours, dead code, configuration spread over several places.
- **Documentation and onboarding.** No README or one that is stale, no way to run the project locally without paid credentials, decisions nowhere recorded, a setup that needs undocumented steps.
- **Delivery.** No versioning or release process, no dependency updates, a build that is not reproducible, no observability (logs, metrics, traces) where the project runs unattended.

Not findings: style a formatter fixes, rewrites in another language or framework, features the user did not ask about.

## How to answer

A prioritised list, highest impact first, at most eight items. Each item is four lines: **What** (one sentence), **Why** (the risk or cost, one sentence), **How** (the smallest change that fixes it, naming the tool, library or pattern), **Where** (the `path` or `path:line` the evidence sits at). Then one line on what was read and what was not.

```
Improvements for owner/repo (read: README.md, package.json, .github/workflows/ci.yml, src/, tests/)

1. What: the CI builds but never runs the tests.
   Why: a red test cannot stop a merge, so the suite is advisory.
   How: add `npm test` after the build step in ci.yml; fail the job on a failing test.
   Where: .github/workflows/ci.yml:24

2. What: ...
```

## Writing the improvements up

When the user asks for the improvements to be filed, call `create_issue(owner, repo, title, body, labels)` once per item the user picks, or once for the list when the user says so. The title is the **What** line; the body carries the four lines as Markdown sections (What, Why, How, Where) and ends with a line that says Scout drafted it from chat; labels only when the user names them. The function opens each issue at once and answers with its number and URL; give them to the user. If the function is not available, or answers that writes are not allowed, say so (the owner allows GitHub writes on the Features screen of Scout Desktop) and leave the list in chat.

You never edit files, commit or change the project yourself.
