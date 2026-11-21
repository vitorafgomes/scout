---
title: "ADR 0071 - A file is read by lines, and code is searched at a reference"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "get_file_contents answers whole lines from a first line, as many as one answer holds, with a header that says which lines and how to read on; search_code at a reference searches Scout's local snapshot there and asks GitHub nothing; neither reaches a repository, reference or path it did not reach before."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs
  - src/Scout.Audit/AuditBatchRunner.cs
reliability: high
---

# ADR 0071 - A file is read by lines, and code is searched at a reference

> A model turn can read any part of a file and search the pull request's head, with no wider reach than before.

## Status

Accepted on 2026-11-21, built at the owner's request after reading the record of review turns. It shapes [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]]'s reads, whose text stays as decided.

## The problem

The record of review turns showed two faults. `get_file_contents` answered the first 20,000 characters of a file and nothing more, so a turn asked to verify code past that point read the same first part again and presented nothing. `search_code` had no reference: it searched the newest local snapshot or GitHub's default branch, so for an open pull request it searched code the pull request did not have.

## The decision

- **The file read** (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:231`) takes `firstLine` (1 unless given) and `lineCount` (0 for as many as fit). Its answer, from the API and from a local snapshot alike (`Lines`, `:671`), holds whole lines from the first line asked, as many as fit the existing bound of 20,000 characters (`:22`) and at most the count asked, exactly as the file holds them, so the parts read in turn join into the file. The header after the file's name says which lines, how many the file has and how to read on: "lines 1 to 609 of 3,000; to read on, call again with firstLine 610", or "the file ends there". No line number is added inside the text, so a quote of it stays exact. A single line longer than the bound is cut there and says so; a first line below 1 or a negative count is refused before any request (`:659`); a first line past the end answers the file's line count.
- **The search** (`:377`) takes `reference`. With one, it searches only Scout's local snapshot at that reference and never asks GitHub; with none, the newest local snapshot, or GitHub's default branch when there is none. Its first line says what was searched: "(the local snapshot of main at abc1234, every file searched; first 1 shown)". A reference with no snapshot answers that nothing was searched (`:428`); a snapshot that cannot be read says so.
- **The review's request** tells a batch that a long file comes in parts, that the lines around a patch line can be asked for directly, and that `search_code` with the head commit searches the pull request's head (`src/Scout.Audit/AuditBatchRunner.cs:351`).
- **Reach unchanged.** Both functions keep their name checks, read the same repository, reference and path as before, and pass through the same filters; a search at a reference reaches less than before, since it never calls GitHub's code search.

## Calls that leave the process

Unchanged or fewer: the file read makes the same one GET per call; the search at a reference makes none.

## What follows

- **Gained:** a turn reads a file to its end and searches the code it reviews.
- **Paid:** a long file costs one call per part.
- **To watch:** a reference with no local snapshot cannot be searched; the turn reads files instead.

## Rejected options

- **Raise the bound:** one answer would grow past what a turn should hold, and a file can always be longer.
- **Add line numbers to each line:** a number glued to a line would be copied into evidence quotes and break their match.

## How it is held

- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:535` (read to the end by following each answer, the parts joined are the file), `:570` (the lines around a line), `:581` (the same repository, reference and path), `:599` (edge values), `:623` (a failed read with lines asked), `:646` (a search at a reference, nothing asked of GitHub), `:674` (a snapshot that cannot be read).
- `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:142` (every request is a GET).

## Links

- Projects it shapes: [[pages/services/plugin-github]], [[pages/services/audit]]
- Invariant it keeps: [[pages/invariants/no-executive-writes]]

## Amendments

None yet.
