# What a change records, and where

Every row that applies is written, in the same commit as the code. A change that leaves one out
is not finished. These pages are part of the plan's edit list, so whoever implements writes them
while the lines are still in front of them.

| What happened | Where it is recorded |
|---|---|
| Behaviour changed | A dated entry in `context/pages/history/<project>.md`: what it was, what it is, which tests changed on purpose |
| The code a page describes changed | That topic page, with its `updated` date and its `changes` note |
| A test was added | The project's testing page, beside the tests of the same behaviour |
| A decision was amended | A dated amendment in the decision page that names the earlier text it replaces; the earlier text stays |
| A follow-up was closed | Its entry removed from `context/pages/roadmap/followups.md` |
| Any of the above | One line at the top of `context/log.md` |

A decision keeps its original text. The amendment is added below it, says which sentence of the
original it replaces, quotes the new one and names every place it now holds, so it can be read
without opening another page.

Every page touched, a history page included, gets its `version`, `updated` and `changes` fields
brought up to date. A service or topic page says how things are now; "since" and "until" belong
in the history page.

A citation of `file:line` in a page you changed is read again after the change: a line that still
exists proves the file is there, not that it still says what the page claims.

## Before the commit message

- After a change to pages, run `tools/checks/check-doc-sizes.sh` and `tools/checks/check-context-refs.sh`.
- The pull request body uses `.github/PULL_REQUEST_TEMPLATE.md` and carries the line
  `Review: applied N, rejected M`. Its two boxes about writes (every call leaving the process
  declared, granted, approved when irreversible and logged; nothing writes code or outside Scout's
  own folders) are ticked only after the `read-only` lens, or the plan's statement that no call
  leaves the process, says so.

## Spending less

- A check that passed is not run again on an unchanged diff.
- A long search goes to one `Explore` agent; a follow-up goes to the agent that has the context.
