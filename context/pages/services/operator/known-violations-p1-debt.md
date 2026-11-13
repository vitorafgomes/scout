---
title: "Operator: Known violations / P1 debt"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Operator service page."
page_type: service
status: active
description: "Read when a change touches: Known violations / P1 debt."
source: ["src/Scout.Operator/", "tests/Scout.Operator.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md", "chat"]
reliability: high
---

## Known violations / P1 debt

- The owner's own video, phase 8b: the transcription's time limit, 10 minutes plus six times the recording's length, is not measured; only `ggml-tiny` on 1.5 s was ever timed ([[pages/services/transcription-whisper]]), and a timing run of large-v3-turbo on a 10-minute recording is pending.
- A long recording holds the transcriber's one gate for its whole transcription; a voice message waits at most 30 seconds behind it and is then told "Scout is transcribing a recording right now; try the voice message again in a few minutes." The recording's samples are held in memory for the whole run, about 230 MB for 60 minutes beside the loaded model, read from the WAV through a 64 KB buffer with no copy of its bytes ([[pages/services/transcription-whisper]]). Transcribing in 5-minute windows is the upgrade path for both, in [[pages/roadmap/followups]].
- The captions of a recording scene are timed evenly across its clip, not by the words' own times, so they drift a little from the speech; a per-project caption toggle is in [[pages/roadmap/followups]].
- The chapter request sends each scene's text cut to its share of 8,000 characters, so for a long recording the model names the chapters from the first words of each scene: at 180 scenes, about 40 characters each. With the scene labels the request is about 11,400 characters at 180 scenes; a local model whose context is smaller than that still truncates it.
- An `own_video` project takes about 14 GB of disk at 60 minutes in its media folder: the copied recording, up to 8 GB for an hour of a phone's 1080p, the rendered segments and the video, about 2.7 GB each at 450 MB per 10 minutes, and the 115 MB WAV while it transcribes; deleting the project removes them all, and a replaced recording is deleted when the new one is attached ([[pages/services/control-plane]]).
- The Production screen reads the full projects every 10 seconds, and an `own_video` project with its timed transcript and scenes is about 100 to 130 KB of it; a list summary rpc is in [[pages/roadmap/followups]].

- A project is taken again when it has sat in `researching` or `scripting` for 15 minutes without a save ([[pages/services/control-plane]]). One worker never polls while it runs a project, so it cannot take its own run twice; with a second worker on the same control plane, a stage that runs longer than 15 minutes, such as a script written by a slow local model, would be taken by the other worker and run twice, the last save winning. Scout Desktop starts one worker. The same holds for a `producing` project; each scene save renews it, so only one scene longer than 15 minutes would let a second worker take it.
- No test runs a real piper: the command and its input are asserted, and the voice models and libespeak-ng are proven only by running the desktop build. A voice model whose JSON gives no `dataset` is labelled by its file stem.
- Narration durations are estimates at 150 words a minute until assembly; assembly measures the files but cannot save the measured lengths on the scenes, which the control plane holds as produced, so the desktop shows the estimates.
- No test runs a real rhubarb, and none runs ffmpeg over a presenter graph: the `chromakey` of a generated presenter, the overlay of a one-frame picture over the moving backdrop and the concat demuxer over PNG files outside the media folder with `-safe 0` are proven only by running the desktop build. How cleanly `chromakey` at similarity 0.1 and blend 0.2 removes a generated green that is not exactly 0x00FF00, and whether it eats green clothing, is not measured.
- The face models were measured on six cartoon pictures only (**FaceMouth acceptance**); a generated presenter in another style may get no face and Scout's own presenter instead. The decode runs under the 2-minute timeout of an ffmpeg command that reads no media.
- The presenter preview lasts as long as the hook's first sentence narrated, at least 2 seconds, not the 5 seconds of the amendment of 2026-11-05; a long first sentence makes a long preview.
- A presenter job that gets no picture at all, no generated one and no built-in presenter, saves no version, only the note, while the amendment says every job adds one version.
- The day's 40 pictures are counted per process, so `pictures_left_today` starts again at 40 after a worker restart.
- The owner's presenter description reaches the image service as written; the prompt's closing clause asks for no real person's likeness and no public figure, which the image service may or may not follow. The scene pictures carry no such clause in their request: the scene planner's system prompt tells the model never to ask for one (`src/Scout.Operator/ScenePlanner.cs:304`).
- No test runs a real ffmpeg: the commands are asserted argument by argument, and the filter graphs, the encoder flags and the concat of segments with copied video and re-encoded audio are proven only by running the desktop build. A broken graph fails the assembly with ffmpeg's last line.
- A media file's length is read from the last 8192 characters ffmpeg writes about it; a file whose tags fill more than that before the `Duration:` line reads as having no length.
- The probe checks the filters and demuxers by name, not the encoders and decoders the inputs need (PNG, MP3); an ffmpeg without them fails at the first segment with ffmpeg's last line.
- Without a font file assembly is unavailable; Scout Desktop must ship and name the font on every platform.
- A scene with a fallback note is `ready` with the note in `error`, the one text field the scene message has; the desktop must not read that as a failure.
- A worker restart in the middle of an upload, or between YouTube accepting it and its save, leaves the publication `uploading` with no session in the new process; that publication is failed with "The upload may already be on YouTube; check YouTube Studio before publishing again." instead of uploaded again, so the owner checks YouTube Studio and publishes again by hand when the video is not there.
- An action that keeps failing in a retryable way (YouTube down, a spent quota, the control plane refusing to save after an upload) is handed out again forever and holds the queue behind it; it is logged on every run.
- Retryable is wider than "4xx except 429": a 401 (a sign-in the desktop renews) and a spent quota (it resets daily) also leave the action, so they do not burn an approval.
- An arm's window is in hours but reach reports are daily: a switch day goes to the arm live for most of it, so the shorter part of that day is credited to the other arm; arm windows of whole days avoid it.
- The scheduler finds its state file from a project's media directory; before the first project exists the slots it runs are kept in memory only.
- A reply after a revoke is saved on the state the previous save answered; when the revoke also returned another approved reply to `drafted`, that state no longer matches the stored one and the control plane refuses the save, so the reply is on YouTube and recorded `succeeded: comment <id>, not saved`. The control plane then fails a `replying` comment that has no live action with the "may already be on YouTube" sentence, and returns a `reply_approved` one with no live action to `drafted` ([[pages/services/control-plane]]), so neither is left stuck; the worker sends nothing to undo it.
- A worker restart between YouTube taking a reply and its save leaves the comment `replying`; it is failed with the "may already be on YouTube" sentence rather than posted again, so the owner checks YouTube Studio.
- The comments job runs on the UTC hours a 4-hour period starts, like the experiments, not on the London clock the daily jobs use.
- A model that labels comments by something other than the `c1`, `c2` labels it was given leaves those comments `new` as `other`.
- Openverse's anonymous burst limit is 20 requests a minute and each stock picture takes two, 
- An Openverse `attribution` runs about 180 to 220 characters, so 25 to 30 distinct credits pass 5000 characters with the chapters; then the description is cut as a whole and the last credits are lost, which breaks the CC BY condition of crediting every picture. The built form is about 80 characters; choosing it, or a shorter line per credit, is open in [[pages/roadmap/youtube-channel-operator]].
- A text card left by an Openverse failure, and a scene produced before stock pictures existed, counts as done while the provider has no image service, so a later Produce does not ask Openverse again; only the owner's Regenerate does.
- With Openverse composed the readiness still carries the provider note that says "visuals become text cards" next to the Openverse note; the desktop words its warning from `stock_pictures`.
- The `Credits` block lists every `stock` scene with a credit, also one whose picture file is gone and that the video shows over the background or as a text card.
- The click-through rate of the reach report is read as a fraction unless a rate in the report is above 1 ([[pages/services/plugin-youtube]]); a percent report whose every rate is at most 1% would be read 100 times too high.
