---
name: project-vault
description: The documentation a software project keeps beside its code, and the ruler the audit judges that documentation by - "where do we record decisions", "how should this project be documented", "adopt a vault", "set up ADRs", "o projeto não tem documentação", "onde ficam as decisões", "adotar um vault", "documentar o projeto". A small set of Markdown pages in the repository: an index, a log, decisions, conventions, invariants and runbooks, every claim about the code citing a file and line. What code-audit and project-improvements measure the documentation dimension against, and the one "Adopt a vault" finding, with the skeleton as its fix, that those skills carry to the user. Not for writing the pages into the repository: the project's people write the vault from the skeleton.
functions: GitHub.list_repository_files, GitHub.search_code, GitHubActions.get_project_conventions, Skills.load_skill
---

# Project vault

Use this when a user asks how a project should be documented or where its decisions should live, and whenever `code-audit` or `project-improvements` reach the documentation dimension of a project with no decisions, conventions or runbooks written down; those two skills load this one with `load_skill`. The shape below is the ruler, under whatever names the project uses. Judge by function, never by folder name.

## The shape

```
vault/                 docs/ or wiki/ serve as well
  index.md             the catalogue: every page once, one line each, what it answers
  log.md               append-only, newest first: one dated entry per change to the pages
  pages/
    decisions/         one record per decision, NNNN-slug.md, never edited once accepted
    conventions/       the rules the code follows, one line per rule, each naming a file that shows it
    invariants/        what must always hold, each with the code that enforces it and the test that proves it
    runbooks/          how to run, release and recover, step by step with the expected result
```

A README does install, run, test, and links to the vault; it is not the vault. `docs/adr/`, `doc/decisions/`, `ADR-*.md` at the root, a `CHANGELOG.md` in place of the log, a wiki the README links to: the same functions under other names, so none of them is a finding.

## The pages

- **A decision record.** Title, status (proposed, accepted, superseded by), date, the context that forced it, the decision in one paragraph, the consequences (what gets better, what gets worse), the alternatives weighed. A decision that changes gets a new record that supersedes the old one; the old one is not rewritten.
- **A conventions page.** One line per rule, grouped by area (structure and what may depend on what, naming, errors, dependency injection, logging, async, data access, configuration and secrets, tests, formatting the build enforces, where documentation lives), each naming a file that shows it as `path:line`; a rule the code itself contradicts is marked "mixed" with both forms. The profile `code-audit` writes in its pass 1 is this page.
- **An invariant.** The rule in one sentence, the code that enforces it (`path:line`) and the test that proves it. A rule nothing enforces is a convention, not an invariant.
- **A runbook.** When to run it, the preconditions, numbered steps each with the command and the expected result, how to verify it worked, how to roll it back.
- **Every page.** A short header (title, date, status, one-sentence description, the source paths it was written from), and `path:line` on every claim about the code, so a reader can check it and a reviewer can see when it went stale.
- **The log and the index.** One log entry per change to the pages, dated, saying what changed and why, never rewritten; every page listed once in the index, because a page the index does not name is a page nobody finds.

## The ruler, for the audit

In the documentation and delivery dimension, check each function of the shape and report what is missing, by function. The Rule of each finding is the documentation and delivery requirement of the audit, since no project writes a rule demanding the documentation it lacks; the severity follows the audit's scale.

- **Most of the shape missing** (no decisions and no conventions written down, or neither of them and no runbook): one finding, not one per function, titled "Adopt a vault: decisions, conventions and runbooks beside the code", Medium, whose Fix is the issue body below. The per-function findings that follow are for a project that has the rest.
- **Decisions.** No record of decisions anywhere (no decisions folder, no ADRs, no decision log, no wiki the README links to): Medium, "No record of decisions", Fix: a decisions folder with the first record. Decisions kept outside the repository and versioned apart from the code: Low at most, and only when the README does not link to them.
- **Conventions.** Nothing says how the code is written (no `CONTRIBUTING`, `CLAUDE.md`, `AGENTS.md`, `ARCHITECTURE.md` or conventions page; lint and format configuration alone is enforcement, not documentation): Low, Fix: the pass 1 profile as the first conventions page.
- **Runbooks.** A README that runs the project covers "run"; no release path and no recovery steps where the project runs unattended are the findings the dimension already names.
- **Stale pages.** A page that names a file, folder or command the repository no longer has (`list_repository_files` and `search_code` prove it): Low, "Documentation names what is gone", with the page and the line.
- **Not findings.** Anything present and working under another name or another shape; the form of the pages (a page without `path:line` citations, the language, the volume); a limitation the project documents itself.

## The issue body, for the finding's fix

The "Adopt a vault" finding is opened the way its skill opens every other finding: through the cards in `code-audit`, through the list in `project-improvements`, never from this skill. Its Fix, which becomes the issue body, has four parts. **What:** the shape above as the skeleton, as a fenced tree, with the first decision record being the adoption itself. **Why:** what the dimension found missing, each with its `path:line` or the folder that does not exist. **How:** the skeleton created under the name the project prefers, the README linking to it, the first conventions page pasted in full: the profile `get_project_conventions(owner, repo)` returns when one exists, otherwise the rules the files read in this conversation show, one line per rule, each naming its file. **Where:** the README's path and the documentation folders that do exist.

The project's people, or the agent they run, write the pages from this skeleton. Scout never writes them into the repository: that is a change to the project's files, outside what Scout does.
