---
title: "Operator: Cross-service dependencies"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Operator service page."
page_type: service
status: active
description: "Read when a change touches: Cross-service dependencies."
source: ["src/Scout.Operator/", "tests/Scout.Operator.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md", "chat"]
reliability: high
---

## Cross-service dependencies

- **Reads from:** [[pages/services/control-plane]] through `WorkerGate` (mandate, queued projects, a project, the latest projects, a project's media directory, the comments), on the host's gRPC client ([[pages/services/host-console]]); YouTube through [[pages/services/plugin-youtube]]; the chat model and the speech and image services through [[pages/services/llm]]; the `ffmpeg`, font, `piper`, voice, `rhubarb`, mouth shape, built-in presenter and face model files [[pages/services/desktop-ui]] supplies.
- **Written to by:** nobody; it writes its drafts, its packages, its readiness and the comments it read, sorted and drafted, with their ideas and the steps of a reply or hold, to [[pages/services/control-plane]], its media files, the video, the Shorts and the thumbnail into the project's media directory, and its notices through the adapters behind [[pages/services/conversation]].
