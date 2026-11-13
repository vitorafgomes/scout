---
title: "Plugin.YouTube"
version: "1.3"
date: 2026-11-02
updated: 2026-11-03
changes: "The channel's comment threads for the operator's comment desk, and the reply and the hold for review the operator runs as actions the owner approved; no model function"
page_type: service
status: active
description: "YouTube plugin: three read-only Semantic Kernel functions over a typed Data API v3 client, which also searches videos and lists the channel's comment threads for the channel operator; with the desktop sign-in, also the write, Analytics and Reporting clients the channel operator uses for the actions the owner approved and for its numbers, none of them a model function; authenticates from the token file the desktop Google sign-in writes or from an API key, and self-gates off when neither is configured."
source: ["src/Scout.Plugin.YouTube/", "tests/Scout.Plugin.YouTube.Tests/"]
reliability: high
---

# Plugin.YouTube

> Lets the model read a YouTube channel's overview, its latest uploads and the statistics of named videos: the owner's own channel through the desktop's Google sign-in, or any public channel by handle with an API key ([[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in]]). With the sign-in it also gives the channel operator the clients that upload, set thumbnails and titles, reply to a comment, hold a comment for review, and read the owner's analytics and reach reports; the model never reaches them ([[pages/decisions/0032-per-feature-write-permissions]]).

## Responsibility

Owns the YouTube Data API v3 client, the kernel functions, reading the token file and appending the API key. Does not own the sign-in, the refresh or the token file itself, which the desktop main process writes ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]), nor the channel operator that searches through this client and runs the approved writes and the analytics reads ([[pages/services/operator]]). The channel operator reads the comments and runs the replies and holds the owner approved through these clients ([[pages/services/operator]]); captions are a later phase ([[pages/roadmap/youtube-creator-assistant]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/plugin-youtube.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/plugin-youtube/public-contract\|Public contract]] | Public contract | 7441 |
| [[pages/services/plugin-youtube/how-it-works\|How it works]] | How it works | 10171 |
| [[pages/services/plugin-youtube/hot-spots\|Hot spots]] | Hot spots | 1042 |
| [[pages/services/plugin-youtube/testing-footprint\|Testing footprint]] | Testing footprint | 3830 |

## Related

Direct project references: none. Referenced by [[pages/services/host-console]] and [[pages/services/operator]]. Catalog entry `youtube` in [[pages/services/control-plane]]. Map: [[pages/architecture/project-map]].
