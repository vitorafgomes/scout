---
title: "ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel"
version: "1.2"
date: 2026-11-03
changes: "Amended: which operator calls the YouTube grant gates; the mandate read is not gated, the desktop's start is"
page_type: adr
status: accepted
description: "The YouTube channel operator keeps its state (mandate, video projects, scenes, packages) in the control plane as the single writer, runs its pipeline in a new C# library Scout.Operator inside the worker, generates speech and images only through Semantic Kernel services of the connected provider (local fallbacks otherwise), and reports progress later into the channel the request came from."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "vault/pages/roadmap/youtube-channel-operator.md"
  - "https://developers.google.com/youtube/v3/determine_quota_cost (read 2026-11-03)"
  - "https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol (read 2026-11-03)"
  - "https://github.com/darkzOGx/youtube-automation-agent (source read through the GitHub API on 2026-11-03)"
reliability: high
---

# ADR 0033 - Channel operator architecture

> The operator's state lives in the control plane, its pipeline runs in a new C# library `Scout.Operator` inside the worker, speech and images come only from Semantic Kernel services of the AI Source already connected, and progress is posted later into the channel the request came from.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-03. Accepted on the owner's instruction to build phases 2 to 6 of [[pages/roadmap/youtube-channel-operator]] in one branch, one commit per phase: "pode seguir com todas as proximas fases tudo em um unico PR".

## Context

Phase 1 ([[pages/decisions/0032-per-feature-write-permissions]]) put the write contract in the product: the `youtube` feature declares its rows, the owner grants its writes on the YouTube card, irreversible actions wait on the Review screen, and a function gate in the worker asks the control plane's `WorkerGate` before any declared write. Phases 2 to 6 build the operator itself: mandate, research, script, narration, visuals, assembly, review, publish, learning loop, experiments and comments, matching the reference project module for module except for media accounts ([[pages/roadmap/youtube-channel-operator]], parity table).

Four questions block the first line of phase 2 code, because every later phase builds on the answers.

- **Who writes the operator's state.** The desktop reaches only the control plane ([[pages/decisions/0011-backend-frontend-grpc-uds]]); the worker reaches it through `WorkerGate` with its own token. Two processes writing one file already cost the approval queue a fix (`actions.json`).
- **Where the pipeline code lives.** The owner's rule: "a maior parte tem que ser em C#".
- **How speech and images are made.** Semantic Kernel is the one LLM path ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]), and the owner's rule is that media comes only from the AI Sources already connected, never a separate account. In the installed Semantic Kernel 1.78 packages only the OpenAI and Azure OpenAI connectors carry text-to-audio and text-to-image services (`AddOpenAITextToAudio`, `AddOpenAITextToImage`, `AddAzureOpenAITextToAudio`, `AddAzureOpenAITextToImage`); the Google connector (Gemini, the owner's current provider), Ollama, Mistral, HuggingFace and Azure AI Inference have none, and the ChatGPT plan path has no audio or image service.
- **How progress reaches the chat.** Today every outbound message goes through an `IResponseSink` created for one inbound message and closed at the end of the turn; nothing can post later, and no transport reference outlives the turn (`src/Scout.Conversation/Abstractions/IResponseSink.cs`, `IChatAdapter.cs`). Teams has no live hosting at all.

## Decision

- **State: the control plane is the single writer.** A new `Operator` area in `Scout.ControlPlane` owns the mandate, the video projects (topic, research, script, claims, scene list, package metadata, status, the originating chat reference) and their history, stored as JSON next to `features.json` with the same atomic-write style. The desktop reads and edits through a new `Operator` gRPC service on the desktop token; the worker reads the mandate and saves its drafts through new `WorkerGate` methods on the worker token, each checked against the `youtube` write grant like any `Write` row (amended 2026-11-03: the worker's mandate read is a read of the owner's brief and is not grant-gated; its project reads, saves and the take of the next queued project are, and so is the desktop's start of a project), and the role tests prove the worker token cannot edit the mandate, approve a package or reach the desktop's `Operator` service. Media bytes (narration, images, the MP4, Shorts, thumbnails) stay on disk under Scout's data folder in one directory per project; the control plane records their paths and checksums only. The desktop previews them through a main-process route confined to that directory; the renderer never receives an arbitrary file path.
- **Code: a new library `Scout.Operator`.** Research, script, claims, scenes, media generation, the `ffmpeg` driver, the checks, publishing, the learning loop, experiments, comments and the scheduler live in `src/Scout.Operator` with `tests/Scout.Operator.Tests`, referenced by `Scout.Host.Console`. It reaches YouTube through `Scout.Plugin.YouTube`'s client (extended with the write calls the catalog declares), the model through Semantic Kernel only, and the control plane through its own seam, `IOperatorControlPlane`, implemented in the console host over the same `WorkerGate` channel and worker token as the write gate (amended 2026-11-03: a separate seam keeps the write gate's contract to requesting and recording writes). No generic pipeline engine: concrete stages, one class each where a class is needed.
- **Media: the connected provider's Semantic Kernel services, or a local fallback.** The operator resolves `ITextToAudioService` and `ITextToImageService` from the kernel; `Scout.Llm` registers them only for providers whose installed connector offers them (OpenAI, Azure OpenAI), with the same credential the owner connected and the provider's configured models. With any other provider the operator falls back without asking for an account: narration from an audio file the owner supplies or none (captions carry the script), and visuals from the owner's images and text cards `ffmpeg` renders. The capability matrix lives on [[pages/services/llm]]. A direct HTTP call to a provider's speech or image endpoint is a second LLM path and is not part of this decision; the owner decides that separately (Gemini media is the open question).
- **Progress: a proactive post into the originating channel.** When a request starts a long job, the adapter captures an opaque reference to the originating conversation (Discord channel or thread and the message replied to; Slack channel and thread timestamp; the local quick-ask session); the operator stores it with the project, and a new seam in `Scout.Conversation` lets the operator post a short progress or result message into that same conversation later. It is a descriptive write to the originating channel only, as [[pages/invariants/no-executive-writes]] already allows; Teams, which has no live hosting, gets no proactive post.
- **YouTube quota is budgeted per day.** Since 2026-06-01 `search.list` and `videos.insert` have their own buckets of 100 calls a day each; everything else shares 10,000 units a day, with `videos.update`, `thumbnails.set`, `playlistItems.insert`, `comments.insert` and `comments.setModerationStatus` at 50 units and `captions.insert` at 400. The scheduler caps research searches and uploads per day below those buckets, and burns captions into the video instead of calling `captions.insert` by default.
- **Uploads go up private.** A video is uploaded with `privacyStatus: private` and, when scheduled, `status.publishAt`; whether an unverified Google project may publish publicly is unsettled in Google's current documentation, so public visibility is a later, separately approved `videos.update`.
- **Experiments follow the reference's statistics.** A title or thumbnail experiment rotates its arms in sequence through `videos.update` and `thumbnails.set`, each arm an approved action, and declares a winner only on a pooled two-proportion z-test on click-through rate at z >= 1.96 with at least 1,000 impressions and 10 clicks per arm, with guardrails on average view percentage and traffic-source shift; adopting the winner is one more approved action. Impressions and click-through rate are read from the Analytics API in phase 5 and verified against the live API before the experiment code depends on them.

## Consequences

- **Better:** one writer for every piece of operator state; the worker can never approve its own work; the pipeline is C# end to end; no account or key beyond what the owner connected; the owner sees progress in the chat they asked from.
- **Worse:** a new project and a new gRPC service to maintain; with the owner's current provider (Gemini) the operator makes no narration or generated images until the owner either connects OpenAI or Azure OpenAI or decides on a direct Gemini media path; Teams users get no progress posts.
- **Neutral but noteworthy:** `ffmpeg` is the one external tool, fetched at build time and pinned by checksum (owner's decision of 2026-11-02); the operator never writes outside its project directories under Scout's data folder.

## Alternatives considered

- **State in the worker, files on disk.** Rejected: two processes would write the same files, and the desktop cannot reach the worker (ADR 0011).
- **Pipeline in the desktop main process.** Rejected by the owner's rule that most of it is C#.
- **Grow `Scout.Plugin.YouTube` into the pipeline.** Rejected: the plugin is the YouTube client and its model functions; the pipeline orchestrates the model, media, `ffmpeg`, the control plane and the chat, which is a different responsibility with its own tests.
- **Direct HTTP to every provider's media endpoint.** Not decided here: it is a second LLM path under ADR 0012 and needs the owner's decision.

## Enforcement

- `tests/Scout.ControlPlane.Tests`: role tests that the worker token cannot reach the `Operator` service or decide a package, and that operator writes through `WorkerGate` refuse without the grant.
- `tests/Scout.Operator.Tests`: every stage with its failing-call test (Rule I) for the model, YouTube, `ffmpeg` and the control plane.
- `tests/Scout.Llm.Tests`: the media services are registered only for providers that offer them, and the write gate is proven on the Gemini and ChatGPT plan paths as well as OpenAI.
- [[pages/services/operator]] and [[pages/architecture/project-map]] describe the new project; the review gate's `boundaries` lens covers `Scout.Operator` and the new contracts.
