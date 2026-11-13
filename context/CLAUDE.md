# Keeping Scout's context folder

This file says how Scout's documentation is kept: what goes where, in what order things are read,
and what may never happen to a page. The root `CLAUDE.md` imports it, so every session in this
repository, every subagent and every person pairing here works to it. The wiki skills under
`.claude/skills/wiki-*` read `wiki-config.md` and `wiki-schema.md` beside it; this file adds what
Scout asks on top of them.

## Reach

The context folder documents all of Scout in one place: the .NET worker and its plugins under `src/`, the
hosts, the gRPC control plane, the desktop app under `ui/`, the tests, the scripts and the agent
harness. There is no second context folder for one part of the project.

The repository is public. The owner keeps private material (plans, handoffs, session notes, audits)
in a separate vault outside this project; none of it is copied in here, not even a quote, unless the
owner says so for that piece.

## Layout

| Path | What it holds | Who changes it |
|---|---|---|
| `pages/<kind>/` | The pages themselves, one folder per kind (see the table further down) | Agents, reviewed by the owner |
| `index.md` | One catalogue line per page, grouped by kind | `/wiki-integrate` and the manager |
| `log.md` | The dated record of every context folder operation, newest first | Whoever did the operation |
| `wiki-config.md`, `wiki-schema.md` | Folder rules and the frontmatter every page carries | Rarely, and only on purpose |
| `templates/<page_type>.md` | The scaffold a skill or a person starts a page from | Changed with the pages of that kind |
| `archive/` | Lint reports and pages taken out of the live catalogue | `/wiki-lint`, the manager |
| `prototypes/` | The desktop artboards, the spec of every screen | The desktop lane, by hand |
| `raw/` | Sources the owner drops to be ingested; read, never edited | The owner only |
| `ingested/<subdir>/` | Sources already ingested, moved out of `raw/` | `/wiki-ingest` |

## Reading order

1. `index.md` comes first in every session, before any code grep or file read, so that a question
   the context folder already answers is never answered again from the code.
2. Name the task, then open the pages that govern it:

   | When the task is to | Open | And before finishing |
   |---|---|---|
   | build something new | the project page under `pages/services/`, every invariant it links, the ADRs behind the area, the conventions | file every new page through `/wiki-integrate` |
   | repair a fault | the project page, the invariants near the fault, its history page, any incident page | if an invariant was broken, that rule is what the fix restores |
   | reshape code without changing behaviour | the project page, the ADRs that gave the code its shape, the conventions | an ADR the new shape contradicts gets a superseding ADR first |
   | explain how something works | `pages/overview/`, the index, then the pages it points to | answer with `[[pages/...]]` citations |
   | check the context folder itself | everything under `pages/`, `index.md`, `log.md` | run `/wiki-lint` |

3. Read those pages in full before proposing anything. Code is the last stop: only after the context folder
   is exhausted, grep source, and what the grep taught you goes back into a page.

## Kinds of page

Every page carries the frontmatter `wiki-schema.md` defines, with a `source:` list naming where its
content came from (`chat`, a source path, another page).

| What is being recorded | Folder | `page_type` | Start from |
|---|---|---|---|
| How one project works today | `pages/services/<project>.md` | `service` | `templates/service.md` |
| What changed in that project, dated | `pages/history/<project>.md` | `reference` | `templates/reference.md` |
| A decision, numbered | `pages/decisions/NNNN-<slug>.md` | `adr` | `templates/adr.md` |
| A rule the code must keep | `pages/invariants/<slug>.md` | `invariant` | `templates/invariant.md` |
| An initiative, a spike or a list of deferrals | `pages/roadmap/<slug>.md` | `roadmap` | `templates/roadmap.md` |
| A procedure someone runs | `pages/runbooks/<slug>.md` | `runbook` | `templates/runbook.md` |
| A running instance that was affected | `pages/incidents/<slug>.md` | `incident` | `templates/incident.md` |
| A term or a component described for reference | `pages/domain/<slug>.md` | `knowledge` | `templates/knowledge.md` |
| What was settled, and what was turned down | `pages/conventions/<slug>.md` | `established-patterns` | `templates/established-patterns.md` |
| A coding, testing or tooling rule | `pages/conventions/<slug>.md` | `convention` | none yet |
| An architecture brief, a design or a spec | `pages/architecture/<slug>.md` | `longform`, or `survey` for a comparison | none yet |

Two splits matter most. A project page describes the present only; what changed and when goes on
that project's history page, newest entry first. Work put off for later goes to a page under
`pages/roadmap/`: a small deferral is an entry on `pages/roadmap/followups.md`, an initiative with
phases gets a page of its own.

## Writing a page

- **Back the first sentence.** Nothing goes on a page without a source, and the opening sentence
  must be one a citation supports. A link into the code is not the only evidence for a claim: link
  the invariant, ADR or convention that governs that code as well.
- **Citations are checked, never guessed.** A `path:line` is written only after the line was read.
  When a location cannot be confirmed, the page says it is unconfirmed. `tools/checks/check-context-refs.sh`
  resolves the citations on every page that still describes the current code.
- **Keep to one subject.** A page that grows past roughly 300 lines is split by subject. A tracker
  under `pages/roadmap/` is the exception: splitting one ordered list ruins it, so closed entries
  are pruned instead.
- **Version a tracker by its shape.** On a tracker, `version` moves only when a section is added,
  split or removed, and `changes` names that last change of shape. Adding, editing or moving an
  entry leaves both alone; each entry carries its own date. A bump per entry put every open branch
  in conflict on the same frontmatter line.
- **Link where the idea is.** A wikilink goes in the sentence that needs it; a "see also" list at the
  bottom is not a substitute.
- **Supersede, do not overwrite.** A decision that changes gets a dated amendment section or a new ADR
  that names the old one in `supersedes:`, and the old page records `superseded_by:`. What a page
  said is never silently replaced.
- **Fields and prose have different owners.** The skills rewrite the frontmatter fields they know;
  they add to the body but never delete prose a person wrote.

## Invariants come before code

Before any code change is proposed, the invariants linked from the affected project page are read
and the change is checked against each one. A change that would break an invariant is either
reworked until it does not, or preceded by a superseding ADR that retires or narrows the rule.

## Joining a page to the rest

A new page under `pages/`, or one rewritten in substance, goes through `/wiki-integrate` in the same
change: the skill adds the page's line to `index.md`, adds wikilinks to it from the pages that
should point at it, and writes its own log entry. It leaves pages in blacklisted folders alone.

## Recording what a session found

When a session turned up something worth keeping (a fact, a decision, a rule, a surprise), it ends
with `/wiki-crystallize`, which proposes the page changes, writes them once confirmed with
`source: [chat]` or the source path, and counts each pass in `crystallize_count`. The session also
puts one entry at the top of `log.md`.

`/wiki-query` answers from the index, the pages a question names and the pages two links beyond
them, cites each page it used and logs the question. A question the context folder could not answer is
reported as such, answered from the source, and the answer is crystallized before the session
closes. A synthesis worth keeping, a comparison or the reasons behind a decision, is offered as a
page of its own.

## Sources from outside

The owner drops a source file straight into `raw/` and asks for `/wiki-ingest`. The skill looks at
the files at the top of `raw/` only, never inside its subfolders. It reads the source, picks the
`ingested_subdirs` entry that fits, writes or updates the pages it touches, adds them to the index,
moves the source to `ingested/<subdir>/` and logs the run. A page made this way names the
`ingested/` path in `source:`, never a `raw/` path.

A `knowledge` or `survey` page that rests on a single source of `medium` or `low` reliability keeps a
`## Pending Review` section until a stronger source arrives; that section is a signal, not a
leftover. Pages of the engineering kinds never get one; their `reliability:` field says it all.

Nothing in `raw/` is ever edited, renamed or deleted by an agent; the only thing that happens to a
raw source is the move into `ingested/`.

## Lint

`/wiki-lint` looks for wikilinks that lead nowhere, pages nothing links to, pages untouched for more
than 90 days that cite files, concepts mentioned without a link to their own page, frontmatter that
breaks the schema, and `file:line` citations that no longer resolve. It files a dated report under
`archive/` and one log entry, and changes no page; fixes are made afterwards, with the owner's
agreement.

## The log

`log.md` only grows, and the newest entry sits at the top. An entry is a heading in the form
`## [YYYY-MM-DD] <op> | <title>` followed by at least one line of body; a heading with no body fails
lint. `.gitattributes` gives `log.md` the `merge=union` driver, so branches that each added
entries merge without a conflict. Entries are never reordered or deduplicated by hand during a merge.

## Questions for the owner

- **Two pages disagree.** Neither is picked by the agent: both statements go to the owner, the owner
  decides, and the ruling is recorded in an ADR.
- **A change reaches ten or more pages.** The full set is shown first as a dry run and applied only
  after the owner confirms it.
- **A new skill, hook or tool.** It is documented in `pages/conventions/tooling.md` in the change
  that adds it.
