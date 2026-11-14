---
paths:
  - "src/Scout.Operator/**"
  - "tests/Scout.Operator.Tests/**"
---

# Operator

Owns phases 2 to 4 of [[pages/roadmap/youtube-channel-operator]] on the worker side: topic research, the script with its factual claims, the run of one project from researching to `script_ready`, the production stage from `producing` to `media_ready` (scenes, narration, pictures, the files in the project's media directory), the assembly stage from `assembling` to `assembled` (the video, the Shorts, the thumbnail, the checks and the first metadata, rendered with `ffmpeg`), the background worker that takes projects, the readiness report, the search and picture counters and the model functions `youtube_make_video` and `youtube_video_status`, and, phase 8d, the owner's model functions that list, read and change projects from the chat through the control plane. Does not own the operator's state, which the control plane holds and checks against the owner's YouTube write grant ([[pages/services/control-plane]]); nor the YouTube client ([[pages/services/plugin-youtube]]); nor the write gate ([[pages/services/llm]]); nor posting into a chat, which the adapters do behind `IOriginNotifier` ([[pages/services/conversation]]); nor the Production and Assembly pages ([[pages/services/desktop-operator]]). Nor the speech and image services, which [[pages/services/llm]] registers for the connected provider; nor the `ffmpeg`, font, `piper`, voice, `rhubarb`, mouth shape, built-in presenter and face model files, which Scout Desktop supplies and names in configuration ([[pages/services/desktop-operator]]); nor the owner's review of the package (rights, metadata edits, approval), which the control plane holds. Phase 5 on the worker side: running the YouTube actions the owner approved, measuring published videos, proposing changes, measuring and deciding experiments and the schedule of those jobs; the 30-day clearing of other channels' data is the control plane's own timer ([[pages/services/control-plane]]); the approvals themselves, the publish settings and the adoption of a winner are the owner's, through the control plane. Phase 6 on the worker side: reading the comments on the owner's channel, their kinds, themes, reply drafts and ideas, answering the owner's sync and draft requests, and running the replies and holds the owner approved; the comments, every decision on them and the 30-day clearing are the control plane's ([[pages/services/control-plane]]), and the Comments page is the desktop's ([[pages/services/desktop-operator]]). Phase 7 on the worker side: the narration voices, the bundled ones and the provider's, offered in the readiness and used as the project names, and the credited stock pictures from Openverse for scenes the provider cannot draw. Phase 8c on the worker side: the animated character, every scene with its own picture and the presenter laid over it, its mouth timed to the narration by the bundled `rhubarb` and drawn from the bundled mouth shapes, and, since 8c-B, the presenter job that makes each presenter version with its preview, the mouth found on the picture by the bundled face models, and Scout's own built-in presenters standing in; the presenter description, the attached picture, the mouth box the owner marks, the presenter requests, the current version and the pruning of old versions are the owner's and the control plane's, through the desktop and the control plane ([[pages/services/desktop-operator]], [[pages/services/control-plane]]). Phase 8b on the worker side: the owner's own video, from the recording the owner attached to the transcript as the script, its scenes as clips of the recording and the assembled video; the upload of the recording and its file rules are the desktop's and the control plane's ([[pages/services/desktop-operator]], [[pages/services/control-plane]]), the speech-to-text model [[pages/services/transcription-whisper]]'s. Beside the channel operator, and composed without it, the project also owns the screenshot text reader of [[pages/roadmap/live-assistant]] phase 5, because it runs a bundled program the way piper does; the `read` request that calls it belongs to [[pages/services/adapter-local]].

## Rules this project must keep

- **Scout performs descriptive writes only.** Here: the operator writes nothing outside the control plane's state, the project's media directory and the operator media root's `voice-samples` folder, the last only through `MediaFiles` and only for the bundled voices' samples, and refuses a file name that would leave it or starts with a dot.
- Every write goes through the gate: a composed chat turn that calls `youtube_make_video` asks the gate with `youtube | Scout's data folder | youtube_make_video(topic=...)` before the project is saved, a save the control plane refuses is recorded as failed, never succeeded.
- The owner's direction reaches the model only as data in the one script request and the one scene request, which offer no function, and never an ffmpeg command; the format and the direction are the owner's, chosen on the desktop or, since phase 8d, given by the owner to `youtube_make_video` from the chat.
- Owner only from the chat: who the owner is never comes from a function parameter, only from the turn the conductor entered; every owner-only function refuses anyone else before any control-plane call; a change runs only after a state read of the same project in the same conversation within 10 minutes.
- Progress only to the origin: the only posts are the start, ready and failure notices, to the reference the turn carried; a project without one posts nothing.
- The owner's claim decisions are never changed or dropped by the worker.
- A locked scene and the owner's own media are never regenerated and go back exactly as read; the production statuses and every scene value the operator sends pass the control plane's worker validation, and a scene save from a read older than the owner's reorder keeps the owner's order.
- Media only from the connected provider's Semantic Kernel services, never a link fetched from the provider; the one link the operator fetches is the `url` of an Openverse result of an allowed licence, a GET whose answer must be an image of at most 20 MB, written only through `MediaFiles` into the media directory.
- The mandate's blocked topics hold from the first step: `youtube_make_video` and the pipeline refuse a blocked topic before any search.
- The owner's own video stays on the machine: the recording is read and its sound written only inside the media directory, through `MediaFiles`, and the WAV is removed when the transcription ends, also when it fails; the transcript is sent only as data in the one chapter request.
- The screenshot text reader writes no file and passes no path but the language data folder; the image goes in on standard input and the text comes out on standard output.
- Assembly writes only inside the media directory, through the same confinement, and reads only scene files that resolve inside it; ffmpeg gets an argument list, never a shell string, and no standard input, and piper only the narration; piper runs with exactly its argument list.
- The owner's package fields (`rights_confirmed(_at)`, `approved_at`, `metadata_edited`, `metadata_saved_at`, the metadata) go back as read; a last-resort failed save keeps them.
- The first metadata always fits YouTube's limits, whatever length the model gives the title or the owner gives the topic, and both sides count tags the same way.
- Writes to YouTube only as actions the owner approved ([[pages/decisions/0032-per-feature-write-permissions]]): the model's `youtube_publish_video` and `youtube_delete_project` are no declared writes and queue nothing; only `youtube_make_video` and the owner's eight changes, `youtube_change_presenter` among them.
- A video is uploaded once: a project with a video id is only recorded, an upload whose save failed is saved on the next run without a second upload, an action handed out again after a crash between the upload and the record is recorded without a second upload.
- The owner's publish settings and decisions go back as read: the upload carries the owner's privacy, schedule and declarations, and proposals the owner decided are kept next to new ones; no experiment is ever adopted by the worker.
- Comments are data written by strangers: the sort and draft requests and the research offer the model no function and call the comments data, never instructions; a sync never writes to YouTube, against a write client that throws on any call; a reply posts the owner's approved text once and never the draft.
- The owner's comment decisions are never changed: a decision made while the model drafts is kept, and an idea the mandate blocks is never proposed.
- The presenter job writes only inside the media directory, through `MediaFiles`: the generated or copied picture, the preview's narration and video; it deletes only the preview narration of presenter versions the control plane no longer keeps.
- The face models are an external boundary of their own (Rule I): a runtime that throws, a model that is missing or will not load, and an ffmpeg decode that fails each answer null and are logged once, and the presenter job goes on with Scout's own presenter, never a failed project or scene.
- Reply speech writes only its one WAV per sentence in Scout's own speech folder and deletes it once read back, also when Piper fails, and only in an absolute folder: a relative `Scout:Speech:Directory` composes no local voice and Piper never runs; the provider's speech, Piper and no voice are chosen in that order.
- **Adapters, LLM providers and plugins are swapped by configuration.** Here: no gRPC type in the project, and no vendor type outside `src/Scout.Operator/FaceMouth.cs`, where `Microsoft.ML.OnnxRuntime` is used only by `OnnxRunner` behind the internal `IOnnxRunner` and `IFaceMouth`, so no ONNX type reaches the seam records, `Scout.Conversation`, `Scout.Skills` or the contracts.

The full statement of each, with the code and tests that hold it: `context/pages/services/operator/how-the-rules-are-kept-pages-invariants-no-execu.md`, `context/pages/services/operator/how-the-rules-are-kept-reply-speech-writes-only.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]]
- [[pages/decisions/0012-llm-communication-through-semantic-kernel|All LLM communication routes through Semantic Kernel (Scout.Llm gateway)]]
- [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program|ADR 0036 - Text read from a screenshot by a local OCR program: the bundled tesseract on the JPEG bytes, through standard input and output]] - the screenshot text reader in `ScreenText/`
- [[pages/decisions/0050-read-aloud-and-conversation-mode|ADR 0050 - Read aloud and conversation mode]] - the voice that reads a reply aloud, in `Speech/`
- [[pages/roadmap/youtube-channel-operator|YouTube channel operator]] - phases 2 to 4, and the phases still to come
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry point; Control-plane seam; Face seam; Model tools; Publishing entry point; Publishing seam; Hosted services | `context/pages/services/operator/public-contract-entry-point.md` |
| External boundaries; Screen text entry point; Reply speech entry point | `context/pages/services/operator/public-contract-external-boundaries.md` |
| mermaid; A project the owner delegated (pages/services/control-plane); youtube_make_video reads the turn's origin from; The operator from the chat; OperatorWorker runs one project at a | `context/pages/services/operator/how-it-works-mermaid.md` |
| OperatorPipeline.RunAsync (src/Scout.Operator/OperatorPipeline.cs:97) runs the production stage; TopicResearcher (src/Scout.Operator/TopicResearcher.cs:28) reads the owner's channel; ScriptWriter... | `context/pages/services/operator/how-it-works-operatorpipeline-runasync-runs-the.md` |
| The owner's own video; Production of an own_video project makes; The animated character; The automatic presenter (phase 8c-B); builtin:<id> takes that built-in presenter with; first on a project whose picture | `context/pages/services/operator/how-it-works-the-owner-s-own-video.md` |
| Anything else asks the image service; Without an image service, with the; Built-in presenters; The mouth; The preview; The save | `context/pages/services/operator/how-it-works-anything-else-asks-the-image-servic.md` |
| Assembly; AssemblyStage.RenderAsync measures every scene's audio file; Assembler (src/Scout.Operator/Assembler.cs:44) renders one 1920x1080 segment; Every file ffmpeg writes goes through | `context/pages/services/operator/how-it-works-assembly.md` |
| A scene that shows the recording; The animated character in the assembler; The same scene with a built-in; RhubarbLipSync (src/Scout.Operator/RhubarbLipSync.cs:23) times the mouth with; The character is shown still,... | `context/pages/services/operator/how-it-works-a-scene-that-shows-the-recording.md` |
| ThumbnailMaker (src/Scout.Operator/ThumbnailMaker.cs:12) asks VisualGenerator.ThumbnailAsync (src/Scout.Operator/VisualGenerator.cs:114) for; PackageChecks (src/Scout.Operator/PackageChecks.cs:14) builds the first... | `context/pages/services/operator/how-it-works-thumbnailmaker-asks-visualgenerator.md` |
| Bundled voices; Voice samples; Screenshot text; Reply speech; VisualGenerator (src/Scout.Operator/VisualGenerator.cs:22) sends the scene's prompt | `context/pages/services/operator/how-it-works-bundled-voices.md` |
| Stock pictures; MediaFiles (src/Scout.Operator/MediaFiles.cs:9) is the only writer; ReadinessReporter (src/Scout.Operator/ReadinessReporter.cs:21) reports, when the worker | `context/pages/services/operator/how-it-works-stock-pictures.md` |
| youtube_video_status shows <topic>: producing (<ready> of; Publishing; ActionExecutor (src/Scout.Operator/ActionExecutor.cs:37) asks for the next; After a run that succeeded the; PublishedCheck... | `context/pages/services/operator/how-it-works-youtube-video-status-shows-topic-pr.md` |
| Experiments; Schedule; Comments; CommentRequests (src/Scout.Operator/CommentRequests.cs:16) reads the comments every; Replies and holds | `context/pages/services/operator/how-it-works-experiments.md` |
| evidence for: Scout performs descriptive writes only | `context/pages/services/operator/how-the-rules-are-kept-pages-invariants-no-execu.md` |
| evidence for: Adapters, LLM providers and plugins are swapped by configuration | `context/pages/services/operator/how-the-rules-are-kept-reply-speech-writes-only.md` |
| Hot spots | `context/pages/services/operator/hot-spots.md` |
| tests/Scout.Operator.Tests (654 cases on 2026-11-05, 38 | `context/pages/services/operator/testing-footprint-tests-scout-operator-tests-654.md` |
| FaceMouth acceptance | `context/pages/services/operator/facemouth-acceptance.md` |
| Cache footprint | `context/pages/services/operator/cache-footprint.md` |
| Cross-service dependencies | `context/pages/services/operator/cross-service-dependencies.md` |
| Known violations / P1 debt | `context/pages/services/operator/known-violations-p1-debt.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
