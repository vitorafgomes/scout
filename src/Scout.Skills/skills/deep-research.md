---
name: deep-research
description: Use when a user asks a broad, open-ended research question about a project, codebase, or technology and wants a thorough, well-sourced answer that weighs evidence and is honest about what is unknown
---

# Deep Research

## Overview

A broad question ("how does X work?", "is this safe to change?", "what's the impact of Y?") cannot be answered well by one lookup. Gather evidence from several reads, weigh it, and answer with sources and stated uncertainty.

**Core principle:** A confident answer built on one snippet is worse than a careful answer that shows its evidence and flags its gaps.

## Scope first

If the question is too vague to research (missing the target project, file area, version, or what "good" means), ask 1-3 short clarifying questions before searching. Do not guess scope.

## Method

1. **Decompose.** Break the question into 2-5 concrete sub-questions. Each should be answerable from the indexed projects.
2. **Gather from multiple reads.** Use the read-only tools and cover the question from more than one angle:
   - `search_project` for semantic matches across the indexed code and docs.
   - `find_symbol` to pin down a specific function, type, or definition.
   - `get_repository` for repo-level overview and structure.
   Run several searches with different phrasings. One hit is a lead, not a conclusion.
3. **Weigh evidence.** Prefer definitions and call sites over comments; prefer current code over stale docs. When sources disagree, say so and explain which you trust and why.
4. **Verify adversarially.** For each claim, ask "what would prove this wrong?" and look for the counter-evidence before stating it as fact.
5. **Flag uncertainty.** Separate what the search results confirm from what you are inferring. Mark inferences as inferences. Name what you could not find.

## Answer format

- Lead with the direct answer, then the supporting evidence.
- Cite where each claim comes from: file path, symbol name, or repository.
- State confidence per claim (confirmed / likely / unverified).
- List open questions and what further search or which file would resolve them.

## Read-only boundary

Describe and recommend; never act. If the answer implies a change (edit, command, deploy, config), explain what the change would be and where, and leave execution to the human or another agent. Do not claim anything was run, written, or committed.
