---
blacklist:
  - prototypes/

index_excludes:
  - raw/
  - archive/
  - ingested/
  - templates/

ingested_folder: ingested

ingested_subdirs:
  - vendor-docs
  - specs
  - reports
  - threads
  - notes

templates_folder: templates/

log_format: "## [YYYY-MM-DD] {type} | {subject}"
---

## Scout's settings for the wiki skills

The wiki skills find this context folder by looking for this file: the folder that holds it, `context/` in the
Scout repository, is the root every path below is measured from. Paths use forward slashes.

### Folders no skill writes into

`blacklist` names one folder under `context/`: `prototypes/`, the desktop artboards, which the desktop
lane edits by hand and which are the spec for every screen; a skill neither scans them nor files a
page there. The code, the tests and the desktop app sit outside `context/` altogether, so no entry is
needed to keep a skill out of them.

### Folders left out of the catalogue

`index_excludes` keeps four folders out of `index.md`, and out of what lint counts as a page, while
the skills can still read them. `raw/` and `ingested/` hold source material that pages are written
from. `archive/` keeps the lint reports and any page retired from the live set; `/wiki-lint` still
writes its reports there. `templates/` holds the page scaffolds, whose `{{TITLE}}`-style markers
would read as broken frontmatter if they were taken for pages.

### Where an ingested source ends up

After `/wiki-ingest` has used a file from `raw/`, it moves the file into `ingested_folder`, under
the `ingested_subdirs` entry that fits it. That folder sits in `index_excludes` and stays out of
`blacklist`, because the move needs to write there. The kinds of source Scout's pages are built
from:

- `vendor-docs` - documentation of a chat platform, an LLM provider, a cloud API or a library.
- `specs` - design briefs, proposals and screen specifications brought in from outside the context folder.
- `reports` - audit, review and measurement output kept as evidence.
- `threads` - issue and pull request discussions worth keeping beyond the hosting site.
- `notes` - anything that fits none of the above.

Files the skill could not read go to `ingested/assets/`, which it creates on its own.

### Page scaffolds

`templates_folder` is where a skill looks for `<page_type>.md` when it creates a page of that type,
replacing `{{TITLE}}`, `{{DATE}}`, `{{PAGE_TYPE}}` and `{{DESCRIPTION}}` and filling in the
sections. A page type with no file there is written from the skill's own default layout. A person
writing a page by hand starts from the same file. Changing a scaffold never rewrites existing pages.

### Log heading

`log_format` is the heading of every entry in `log.md`. All five skills write it, so it stays as it
is unless every skill changes with it.

### Field names

A notes app that guesses a type for each frontmatter field can clash with a field of the same name
used elsewhere. If that happens, rename the field in `wiki-schema.md` and on every page that
carries it, in one change, and say so in the log.
