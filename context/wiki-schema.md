---
# Wiki Page Schema v1.0
# Read by all wiki skills on boot to know what fields pages use.
# Managed by wiki-config - run /wiki-config to view, edit, reset, or repair.

schema_version: "1.0"

# Mandatory fields - every wiki page must have these
mandatory_fields:
  title: string
  version: string
  date: date          # YYYY-MM-DD
  changes: string     # quoted, <80 chars, no paths
  page_type: enum     # see enums below

# Conditional fields - written when context applies
conditional_fields:
  updated: date                    # any skill touch, YYYY-MM-DD
  status: enum                     # default: active
  description: string              # ~200 chars, quoted
  crystallize_count: integer       # wiki-crystallize only, starts at 1
  source: list                     # always a list, even for single origin
  reliability: enum                # only meaningful when source present
  # Scout additions, explained under "Scout's additions" below
  supersedes: string               # on an ADR: slug of the ADR it replaces, "" when none
  superseded_by: string            # on an ADR: slug of the ADR that replaced it, "" until then
  enforced_by: list                # on an invariant: quoted "path:line" of each enforcement point
  verified_by: list                # on an invariant: quoted "path:line" of each test that proves it

# Valid enum values
enums:
  page_type:
    - knowledge          # default; synthesised knowledge, concept, source note
    - reference          # lookup accumulator; mostly static once mature
    - survey             # comparative overview across many items
    - domain-home        # domain navigation hub; agent bootstrap target
    - overview           # folder-level meta home; crystallize target
    - log                # append-only chronological record
    - index              # graph lookup table; agent-facing catalogue
    - config             # configuration document
    - longform           # linear authored document; spec, architecture, design brief
    - profile            # structured record about an entity (person, company, product)
    - established-patterns  # memory record; decisions, what worked, anti-patterns
    - note               # ephemeral capture; daily, scratchpad, todo
    # Scout additions
    - service            # how one Scout project works today, pages/services/
    - adr                # a numbered decision, pages/decisions/
    - invariant          # a rule the code keeps, with its enforcement and its tests, pages/invariants/
    - roadmap            # an initiative, a spike or a tracker of deferrals, pages/roadmap/
    - runbook            # a procedure the owner or an agent runs, pages/runbooks/
    - convention         # a coding, testing or tooling rule, pages/conventions/
    - incident           # a running instance that was affected, pages/incidents/
  status:
    - active           # current, canonical
    - stub             # placeholder, needs expansion
    - artefact         # kept as historical record
    - archived         # superseded but retained
    - snapshot         # point-in-time capture
    # Scout additions
    - proposed         # written and waiting for the owner's word
    - accepted         # approved by the owner and in force
    - deprecated       # no longer in force, kept for what it explains
  reliability:
    - high             # primary source or well-established
    - medium           # secondary source or partial confidence
    - low              # speculative, hearsay, or unverified
---

# Wiki Schema

Apart from the entries marked `# Scout additions` and the section "Scout's additions", this file is
the published default schema of the wiki skills, as published; where it comes from is recorded in
`THIRD-PARTY-NOTICES.md` at the root of the repository.

## How skills use this

When writing any page, skills consult this schema to know:
- Which fields are mandatory
- Which conditional fields apply given context
- Which enum values are valid for constrained fields

Skills that write `page_type` choose the appropriate enum value based on what the page is for, and ask the user when unclear.

## Field semantics

### Mandatory fields

Every page a skill writes must include all five:

| Field | Rule |
|---|---|
| `title:` | Must match the H1 heading exactly. Quote if the value contains a colon. |
| `version:` | Start at 1.0. Increment minor (1.1) for additions, major (2.0) for structural rewrites. |
| `date:` | Creation date. Set once at page creation, never modified by skills on subsequent writes. YYYY-MM-DD only, no timestamp. |
| `changes:` | One sentence. Never a file path. Never an em-dash. Always quoted. |
| `page_type:` | Controls how skills handle the page. Use values from the `page_type` enum above. |

### Conditional fields

Written by skills when context applies. Not required on every page.

| Field | Written by | When | Key rules |
|---|---|---|---|
| `updated:` | Any wiki skill on touch | Every skill write | YYYY-MM-DD; captures last skill contact date. On page creation, equals `date:` - this is expected and correct. Writing `updated:` alone does not require a version bump. wiki-lint staleness threshold: 90 days; exempt status artefact, snapshot, archived. |
| `status:` | wiki-ingest on create; any skill on state change | Every page | Default `active`; write `stub` if body is minimal at create; skills never downgrade existing value |
| `description:` | wiki-ingest, wiki-crystallize | Every page | ~200 chars, quoted; LLM bookkeeping - not human territory |
| `crystallize_count:` | wiki-crystallize only | Every crystallize write | Integer. Read current value and add 1; if field is absent, write 1. Counts deliberate crystallize events only - not general edits. |
| `source:` | wiki-ingest on create | When page has an ingested origin | Always a list, even for single origin: `["ingested/clippings/foo.md"]` |
| `reliability:` | wiki-ingest on create | Only when `source:` is present | Use minimum value when multiple sources contributed; omit entirely when no source |

### Write discipline

**Frontmatter is skill territory.** Skills overwrite known fields without asking. Humans who edit these values should expect them to be overwritten on the next skill touch.

**Body is human territory.** Skills append and add sections; they never delete human-written prose. Unrecognised frontmatter fields are preserved unchanged.

### Key anti-patterns

| Anti-pattern | Fix |
|---|---|
| `changes: key: value` (colon-space unquoted) | Quote the `changes:` value |
| Em-dash (`—`) in any field value or title | Use ` - ` (space-hyphen-space) |
| File path in `changes:` | Put paths in body `## Sources` section; `changes:` is a short description only |

## Scout's additions

Scout documents a software project, so its pages need kinds, states and fields that a general notes
collection does not. Everything below is what Scout's pages actually carry; nothing here is reserved for
a page that might exist one day, with the single exception named under "Kinds".

### Kinds

Seven kinds were added, each tied to one folder:

| `page_type` | Folder | What the page is |
|---|---|---|
| `service` | `pages/services/` | One Scout project as it works now: what it owns, its contract, its drawing, its rules, its fragile spots and its tests |
| `adr` | `pages/decisions/` | One decision, filed as `NNNN-<slug>.md` with the next free number, amended by dated sections |
| `invariant` | `pages/invariants/` | A rule the code keeps, with the lines that hold it and the tests that prove it |
| `roadmap` | `pages/roadmap/` | An initiative or a spike with phases, or a tracker such as `followups.md` |
| `runbook` | `pages/runbooks/` | A procedure with its commands and the result each one should give |
| `convention` | `pages/conventions/` | A rule for code, tests or tooling that every change follows |
| `incident` | `pages/incidents/` | A running instance that was affected; no page has needed it yet, and it stays because the diagnose lane of the pipeline files one when that happens |

Scout also gives a fixed job to some of the base kinds. A project's history page is a `reference`
page under `pages/history/`. The pages under `pages/domain/` are `knowledge`. Architecture briefs
and specs are `longform`, and a comparison of options is a `survey`. The record of behaviour that
looks wrong and is intended is an `established-patterns` page. The vision page is the one
`overview`, and `index.md` is the one `index`.

### States

ADRs move through three states of their own: `proposed` while the owner has not ruled,
`accepted` once the owner approves, `deprecated` when another decision takes its place. A survey
that recommends something nobody has approved yet is `proposed` too. Of the base states, Scout uses
`active` for living pages, `stub` for pages still thin, `artefact` for history pages (frozen by
nature) and `archived` for pages about code that no longer exists. `tools/checks/check-context-refs.sh`
skips the citations on `artefact`, `archived` and `deprecated` pages, since they describe a past
state of the code.

### Fields

- `supersedes` and `superseded_by` belong to ADRs only; the ADR scaffold writes both, as the slug of
  the other ADR or as an empty string, and the older ADRs that predate it may lack them. The
  replacing ADR fills `supersedes`; the replaced one gets `superseded_by` and its status changes, in
  the same change.
- `enforced_by` and `verified_by` exist only on invariant pages. Each is a list of quoted
  `"path:line"` strings into `src/`, `ui/` or `tests/`, and each repeats, in machine form, what the
  page's own sections on enforcement and tests say in prose. A citation of this kind elsewhere
  belongs in the body.
- `source` is on every Scout page, not only on ingested ones: `chat` for what the owner said in a
  session, repository paths for what was read in the code, or another page.
- `reliability` is on every page as well: `high` for a page written from the code, `medium` for one
  carried over from earlier notes and not yet checked against the code, or for a forward-looking
  roadmap page.

### Trackers

A page under `pages/roadmap/` that is a list of entries changes `version` only when its shape
changes: a section added, split or removed. Its `changes` field names that last change of shape,
and each entry carries its own date in its heading. Adding, editing or moving an entry touches
neither field.
