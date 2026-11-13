---
title: "Versions runbooks"
version: "1.0"
date: 2026-11-13
changes: "Version notes moved out of page headers."
page_type: knowledge
status: active
description: "Version notes that were kept in page headers."
source: ["context/pages/"]
---

## Version notes: `pages/runbooks/local-models.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- The missing Ollama key is fixed by starting Ollama from its app or a terminal: the LLM Server panel has no Start since 2026-11-11. Earlier: AI Sources has no Local Models card any more, so its Connected row is gone (2026-11-11). Earlier: A chat, a live translation, an audit or the skill index while the model is not installed or still downloading: what each says, and the skill index log text (2026-11-11). Earlier: the card states read in sentence case, as the status chip draws them: Not connected (2026-11-09). Earlier: Scout no longer creates the Ollama identity key: start Ollama once (ADR 0008 amendment of 2026-11-08). Earlier: artifact name Scout-Desktop-<version>-<os>-<arch>

## Version notes: `pages/runbooks/release.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- The smoke test checks the launcher entry against the running app, and the verification checks the dock shows Scout's icon for the running window. Earlier: the Release workflow runs the CI and UI CI workflows before it packages. Earlier: a release is cut by running the Release workflow from main with a kind (release or prerelease); it computes the version from the commits since the last tag with scripts/next-version.sh and creates the tag and the release once every build leg passes; bun run dist:linux:test gives a local test build its own version. Earlier: a local AppImage passes bun run test:smoke before it is staged (ADR 0051 amendment). Earlier: the Package step bundles the Google OAuth client from the SCOUT_GOOGLE_CLIENT_SECRET repository secret; local builds take it from the environment
