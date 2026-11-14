---
title: "Voice and meetings spike"
version: "1.0"
date: 2026-11-07
changes: "Initial research page"
page_type: roadmap
status: active
description: "Research: voice and meeting mechanisms from an open-source dictation app (mechanisms only), mapped to the quick-ask window and the Chat screen, with the owner's machine findings, items 0 to 11, sizes and a phase order."
source:
  - chat
  - Directory.Packages.props
  - src/Scout.ControlPlane/Features/FeatureCatalog.cs
  - src/Scout.ControlPlane/Features/FeatureFields.cs
  - src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs
  - src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs
  - src/Scout.Adapter.Local/LocalChatAdapter.cs
  - src/Scout.Conversation/Abstractions/IMeetingStore.cs
  - src/Scout.Llm/ChatGateway.cs
  - src/Scout.Operator/TranscriptionStage.cs
  - ui/packages/main/src/quick-ask/quick-ask.ts
  - ui/packages/renderer/src/app/quick-ask/live-listener.ts
  - ui/packages/renderer/src/app/quick-ask/screen-grab.ts
  - ui/packages/main/src/worker/whisper-model.ts
reliability: medium
---

# Voice and meetings spike

> Research of 2026-11-07 behind F11 to F18 in [[pages/roadmap/future-improvements]]: what an open-source dictation app (Electron, MIT) does with voice and meetings, what of it fits Scout's two chat surfaces, and in what order. Mechanisms only; no code, identifier or name from that project enters Scout.

## The problem in the owner's words

"it is an open-source project that can add a lot to our project", then "we can do all the research needed to add this to our chats". The survey read the app's sources and checked every Scout `path:line` below on `main` at `59043e4`. Numbers attributed to the surveyed app are starting points to retune on Scout's 16 kHz pipeline, not facts about Scout.

## Ground rules

- **No subprocess for transcription** ([[pages/roadmap/live-assistant]], out of scope; [[pages/decisions/0034-live-transcription-on-the-local-adapter]] rejected a whisper.cpp `stream` process). The surveyed app runs its engines and diarization as helper binaries; Scout takes the in-process route: Whisper.net runtimes, or ONNX Runtime, which `Scout.Operator` already references (`Directory.Packages.props:29`, `src/Scout.Operator/FaceMouth.cs:365`).
- **No write outside Scout's own folders** ([[pages/decisions/0032-per-feature-write-permissions]], "What stays forbidden", `context/pages/decisions/0032-per-feature-write-permissions.md:48`). This rules out editing desktop settings (gsettings keybindings, KDE global-accel, Hyprland binds, COSMIC's shortcut file) and typing into other apps.
- **No capture that starts by itself, no stealth.** Detection may only prompt; Scout's windows get no content protection, which the surveyed app uses to hide its overlays from screen shares.
- **Audio and screen never on disk** unless the owner keeps a meeting ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **One chosen provider; the model and the chat can only ask** (ADR 0032).
- **Every call that leaves the process is declared** (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:304`, the `desktop` feature with Screen, Microphone and System audio rows, all `ScopeAccess.Read`).

## The owner's machine (checked live)

- COSMIC on Wayland. The desktop portal exposes ScreenCast, Screenshot, RemoteDesktop and others, **but no `org.freedesktop.portal.GlobalShortcuts`** (checked with `busctl`); backends cosmic, gtk, gnome-keyring.
- PulseAudio on PipeWire 1.6.8; `pactl` 16.1, `pw-cli`, `pw-dump` on PATH; `pactl --format=json list source-outputs` works.
- GPU: NVIDIA GeForce GTX 1660 Ti (Turing, 6 GB), so CUDA and Vulkan are both candidates. Whisper.net 1.9.1's `RuntimeLibrary` lists `Cpu`, `CpuNoAvx`, `Cuda`, `Cuda12`, `Vulkan`, `CoreML`, `OpenVino`; Scout references only `Whisper.net.Runtime` (`Directory.Packages.props:31`).

## The two surfaces

The quick-ask window holds every capture tool ([[pages/services/desktop-quick-ask]]). The Chat screen shares the conversation held in main (`ui/packages/renderer/src/app/core/local-conversation.ts:16`) but has only a hold-to-record microphone (`ui/packages/renderer/src/app/chat/chat.component.ts:300`, `stopRecording` at `:881`); on COSMIC it is what the owner reaches when the global shortcut does not fire.

| Item | Quick-ask | Chat screen |
|---|---|---|
| 1 Meeting prompt | The prompt's action opens quick ask and calls `startMeeting` | The prompt can show with the main window focused; same action; no meeting UI added |
| 2 Echo and duplicates | Transcript panel, kept meetings | Nothing (no transcript); the Meetings screen benefits |
| 3 Speaker labels | Transcript panel | Meetings screen (kept lines) |
| 4 GPU / transducer | Faster partials and voice asks | Faster voice asks (same worker) |
| 5 `--talk` | Yes | No (main-window focus is not a global trigger) |
| 6 Ladder, image framing, Esc | All three | Image framing applies to any turn with an image; Attach on the Chat screen is a separate owner choice |
| 7, 8, 9, 10 | n/a | n/a (Models screen for 9) |
| 11 Import | No | An Import action on the Meetings screen (recommended) or a file dropped on the Chat screen |

## 0. Audio time on finals

The enabler for 2, 3 and 11. Each `heard` final gains `start` and `end` in milliseconds of session audio time (the segmenter knows the first and last sample); main stamps each session's wall-clock start so both lanes share one time base; a `retract { id, seq }` event is added. Today `LocalHeardEvent` carries `text`, `final` and `seq` and no time (`ui/packages/shared/src/ipc-contract.ts:1739`). Owners: `Scout.Transcription.Whisper`, `Scout.Adapter.Local`, `ui/packages/shared`. Size S, goes first.

## 1. Meeting detection and the start prompt

- **Mechanism.** Microphone in use is the only signal that prompts alone: a long-lived `pactl subscribe` child; `source-output` `new`/`remove` lines trigger a reconcile, coalesced at least 1 s apart, running `pactl --format=json list source-outputs` (5 s timeout). Streams with `media.role=production` (recorders) or `stream.capture.sink=true` (monitor captures, which is Scout's own `others` lane) and the app's own pids are ignored; activity must hold about 2 s; respawn with backoff 5 s to 60 s. A meeting-app process poll (every 30 s) only corroborates; a calendar reminder needs a synced calendar. One prompt at a time, a newer one replaces it; nothing prompts while a recording runs; a dismissal starts a 5-minute cooldown; switches per source, detectors off when off.
- **In Scout.** Nothing detects a meeting; `startMeeting` (`ui/packages/renderer/src/app/quick-ask/quick-ask.component.ts:1719`) opens `me` and, on Linux, `others`. A new `ui/packages/main/src/meeting-detect/` module in main emits an IPC event (for example `scout:local.meetingPrompt`); the prompt's one action calls `startMeeting`, it never starts capture. No stdin change. ADR 0032: a `desktop` Microphone row "See when another app starts using the microphone, to offer a meeting" (Read). Process detection reads `/proc/<pid>/comm`, no package.
- **Linux.** `pactl subscribe` works on PulseAudio and pipewire-pulse, on any compositor. Windows (WASAPI) and macOS (CoreAudio, TCC) need native helpers: deferred. The `others` lane is Linux-only anyway (`ui/packages/main/src/quick-ask/quick-ask.ts:1446`, `SYSTEM_AUDIO_UNAVAILABLE` at `:65`).
- **Dependencies.** None new; `pactl` used only if on PATH, Settings says so otherwise.
- **Risks and tests.** False prompts (voice notes, permission probes), a browser tab attributed to the browser; metadata only, never audio. A fake `spawn` with canned `pactl` lines and JSON captured once on the owner's machine (Zoom, a browser call, OBS, Scout's `others` lane); `vi.useFakeTimers()` for sustain, spacing, cooldown and suppression; Rule I with `pactl` absent, exiting 1 or printing non-JSON.
- **Size.** M for the mic detector and prompt; S for process corroboration; calendar L, deferred.

## 2. Echo and duplicates between the microphone and system audio

- **Mechanism.** Two pure, separately tested layers. *Audio:* per mic chunk RMS and peak; silent (RMS < 0.0015, peak < 0.05) is skipped; quiet (RMS < 0.018, peak < 0.07) while the system speaks is dropped as bleed; louder is sent, keeping double-talk. Then an echo-leak correlation over 6 s of system audio and 12 s of mic analyses, lags 0 to 500 ms in 5 ms steps, giving correlation, least-squares gain, residual ratio, explained ratio and mic/system RMS: suspected bleed (correlation ≥ 0.6, residual ≤ 0.45, explained ≥ 0.5, mic/system ≤ 1.3), double-talk (correlation ≥ 0.45, explained ≥ 0.3, and residual ≥ 0.2 or mic/system ≥ 1.16), else local speech; a short-window verdict for partials and a per-utterance verdict for finals. An AEC3 helper exists, off by default. *Text:* a risky mic final is held back briefly and dropped only if its text matches recent system text; a committed one is retracted when a later system final matches. Audio evidence alone may delay a line, never delete it. The renderer applies `partial`, `final`, `retract` through a pure reducer.
- **In Scout.** `isLoud` (`ui/packages/renderer/src/app/quick-ask/live-listener.ts:22`) checks the last 20 ms of the system chunk against `LOUD_RMS = 0.015` (`:17`) and zero-fills every mic chunk while it holds (`:181`, `:182`); the mic opens with Chromium's echo cancellation (`ui/packages/renderer/src/app/quick-ask/wav-recorder.ts:51`); the segmenter has its own RMS check (`src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs:23` to `:31`). The gap: no double-talk, bleed passes when the far end's last frame is quiet, no dedupe, no retract, no audio time. The correlation verdict replaces the zero-fill in `live-listener.ts`, off the audio callback (about 0.4 M multiply-adds per 256 ms chunk); the text layer goes in `LocalChatAdapter`, which owns both sessions (`src/Scout.Adapter.Local/LocalChatAdapter.cs:55`, `MaxLiveSessions = 2`), so a kept meeting stores deduplicated lines. No feature row. AEC helper not adopted (a subprocess).
- **Linux.** Platform-neutral code; matters where `others` exists. Verify with a fixture whether Chromium's AEC cancels a native app's playback.
- **Dependencies.** None.
- **Risks and tests.** Thresholds came from 24 kHz; retune on 16 kHz recordings from the owner's devices (lags become 0 to 8,000 samples in 80-sample steps). A wrong retract hides real speech: only text matches delete, shown struck through until the meeting ends. A TS PCM16 generator (sine, seeded noise, mix, delay, gain) over the verdict; a C# xUnit table for holdback, drop and retract with `FakeTimeProvider`; a mic line without a text match is never dropped; the state × action matrix.
- **Size.** Text layer M, correlation gate M, after item 0.

## 3. Speaker labels and voice profiles

- **Mechanism.** Offline: three ONNX models (Silero VAD, pyannote segmentation 3.0, 3D-Speaker CAM++ at 16 kHz, 512-dimension vectors); embeddings per turn clustered at distance 0.55 (default 2 speakers, max 8), renumbered from a per-meeting base, one centroid per cluster from its 3 longest segments. Live on the system stream: VAD over 512-sample windows, an embedding after 1.6 s then every 1 s, a match needs cosine ≥ 0.65 and a 0.03 margin over the second best. Across meetings: a speaker the user names becomes a profile matched by the same rule.
- **In Scout.** `me` and `others` are already a two-way split; `IMeetingStore` lines have no speaker field (`src/Scout.Conversation/Abstractions/IMeetingStore.cs:13` to `:22`, [[pages/services/meetings]]); ONNX Runtime 1.31.1 is already a dependency (`Directory.Packages.props:29`, `src/Scout.Operator/FaceMouth.cs:365`). A folder in `Scout.Transcription.Whisper` first (Rule C), behind a core seam `ISpeakerLabeler` in `Scout.Conversation` (Rule F), in-process; spike a hand-rolled 80-bin log-mel front end against the sherpa-onnx .NET binding. Live labels on `others` only; offline labels need audio in memory during the meeting, never on disk. `speaker` on lines and a `voice_profiles` table in the meetings SQLite file, with delete parity. A `desktop` row "Recognise voices you have named, on this computer" (Read) and a new ADR. Models pinned by size and SHA-256 like Whisper's (`ui/packages/main/src/worker/whisper-model.ts:17`, `ui/packages/main/src/ipc/whisper.ts:288`).
- **Linux.** ONNX Runtime CPU ships for linux-x64, win-x64, osx-arm64; live labels follow the Linux-only `others` lane.
- **Dependencies.** About 6 MB, 28 MB and 2 MB of models (sizes and licences MIT, MIT, Apache-2.0 to verify at pin time); run at one thread beside the live decode.
- **Risks and tests.** A voice fingerprint is special-category biometric data under UK GDPR: profiles only on an explicit "Remember this voice as <name>", off by default, deletable, the vector never sent to a provider, nothing kept unless the meeting is. Loopback accuracy is lower: "Speaker 1/2" until the owner confirms a name. Openly licensed fixtures (LibriSpeech CC BY 4.0 or LibriVox), a golden boundary test, a cosine and margin table, renumbering, store delete parity, Rule I for a missing or corrupt model.
- **Size.** Live labels L; profiles M after the ADR. Depends on item 0.

## 4. Faster local transcription

- **Mechanism.** GPU Whisper builds (CUDA, Vulkan, Metal) fetched on demand, pinned by SHA-256, with a disk check, and a CPU fallback that **says why**. A second family: Parakeet and Nemotron transducers through sherpa-onnx INT8 (`parakeet-tdt-0.6b-v3` 25 languages about 680 MB, `parakeet-tdt-110m-en` about 136 MB), whose streaming models emit partials and finals without re-decoding.
- **In Scout.** Whisper.net 1.9.1 CPU only (`Directory.Packages.props:30`, `:31`); half the processors (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:22`), one `WhisperFactory` per model (`:221`); cumulative re-decode, first window 0.75 s then every 0.6 s, beam-5 final (`src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs:26`, `:27`), so cost grows with the square of the utterance. Measured on the Ryzen 9 3900 with `ggml-base`: a partial takes 0.94 to 1.00 s against a 0.6 s hop, first partial at 2.0 to 2.3 s, final at 6.9 to 7.5 s ([[pages/services/transcription-whisper]], Measured). Plan: the Vulkan runtime package first, in-process, `RuntimeOptions` load order with CPU last; the loose-natives publish target (`src/Scout.Host.Console/Scout.Host.Console.csproj:40`) keeps its `runtimes/<rid>/`; the loaded runtime or the fallback reason reaches Settings through the ready line; CUDA optional and second. A streaming transducer is a new `ILiveTranscriber` in `Scout.Transcription.Transducer`, selected by configuration; the local protocol does not change. Measure CPU, Vulkan, CUDA and one transducer with the existing harness before choosing.
- **Linux.** Vulkan through the NVIDIA, Mesa or AMD driver with only the system loader, which suits an AppImage; CUDA NVIDIA only. macOS through CoreML or Metal.
- **Dependencies.** Vulkan runtime tens of MB per RID, CUDA much larger and maybe cuBLAS (verify); Parakeet weights CC-BY-4.0 with attribution in About; Brazilian Portuguese quality tested on the owner's speech.
- **Risks and tests.** A driver crash kills the worker: remember "GPU failed last start, use CPU" to avoid a crash loop. A fake-loader test (Vulkan fails, CPU loads, reason reported); `WhisperLiveTranscriberTests` unchanged; a benchmark in a table in the context folder; Rule I for a model or runtime that fails to load.
- **Size.** Vulkan S to M; CUDA M; transducer L after a spike.

## 5. A talk trigger from anywhere on Wayland

- **Mechanism.** Per desktop: GNOME custom keybindings through gsettings and the GlobalShortcuts portal (which reports key-up), KDE KGlobalAccel, Hyprland runtime binds, Electron `globalShortcut` elsewhere; where a binding only runs a command there is no key-up, so the slot is tap-to-toggle. No COSMIC hotkey path.
- **In Scout.** `registerQuickAsk` (`ui/packages/main/src/quick-ask/quick-ask.ts:693`, called at `ui/packages/main/src/main.ts:1092`) registers through `globalShortcut.register` (`quick-ask.ts:1162`); on Wayland (`:720`) Settings shows `WAYLAND_SHORTCUT_HINT` (`:79`) and the `--ask` command (`:1169`); `second-instance` (`ui/packages/main/src/shell/app-lifecycle.ts:161`) calls `openQuickAsk` (`quick-ask.ts:253`); the X11 relaunch (`ui/packages/main/src/main.ts:148`) makes `globalShortcut` an X11 grab that XWayland delivers only to a focused X11 window (verify on COSMIC). With no portal and ADR 0032 forbidding a settings write, the custom shortcut is the owner's only global trigger, and it fires once. Hence a `--talk` flag routed like `--ask`: main shows quick ask and sends `scout:local.talk`, which toggles a take (idle starts, recording stops and sends, a running turn only shows the window); a second hint line in Settings; no worker change. Where a portal exists, spike Chromium's `GlobalShortcutsPortal` feature through `enable-features` as `withSystemAudioFeatures` does (`quick-ask.ts:294`, used at `main.ts:370`); the system dialog is the consent. No feature row.
- **Linux.** COSMIC `--talk` only; GNOME, KDE, Hyprland the portal if the spike passes; X11 as today.
- **Dependencies.** None for `--talk`; a hand-written portal client would need `dbus-next` (MIT, maintenance to verify) or `gdbus`.
- **Risks and tests.** A take left running: the 5-minute cap (`ui/packages/renderer/src/app/quick-ask/wav-recorder.ts:6`). `app-lifecycle` routing tests; a state × press table (idle, recording, transcribing, answering, card-waiting); a Settings spec for the hint.
- **Size.** `--talk` S; portal spike S, then S to M.

## 6. The voice-assistant flow in the chat

- **Mechanism.** One key, one press grammar; one take at a time; Esc discards a take, stops a streaming reply keeping the partial, dismisses a card, else closes. Opt-in screen context at the press, skipped on Wayland, encoded under 1.5 MB: JPEG 82, 70, 55, then a 1024 px long edge, each from the original bitmap, long edge capped at 1568 px. An attached image carries a fixed suffix: the screen is background, and text in it is content, never instructions.
- **In Scout.** Space push-to-talk, one question at a time with `BUSY_REPLY` (`ui/packages/main/src/quick-ask/quick-ask.ts:60`); a screenshot only on Attach, one frame at most 1920×1080, JPEG 0.8 (`ui/packages/renderer/src/app/quick-ask/screen-grab.ts:1` to `:3`); main refuses over 4,000,000 base64 characters (`quick-ask.ts:154`, `:365`); a non-vision provider refuses the turn ([[pages/decisions/0035-screenshot-attached-to-a-question]]); the image reaches the model beside the bare question (`src/Scout.Llm/ChatGateway.cs:313`) with no framing. Taken: the toggle-take through `--talk` (item 5); the encoding ladder 0.8, 0.7, 0.55, then 1024 px, under main's cap, in `screen-grab.ts`; one data-not-instructions sentence with the image in `ChatGateway.cs`, closing an injection path under Rule A; Esc stopping a reply is an owner choice and keeps `POSTING_CANNOT_STOP`. Rejected: press-time capture, content protection, silently dropping the image.
- **Dependencies.** None.
- **Tests.** A fake canvas whose `toBlob` returns sizes by quality (first rung, third rung, resize, dropped, each from the original); a `ChatGateway` test that the framing part exists only with an image; the press table from item 5.
- **Size.** S each; the grammar depends on item 5.

## 7. A network allowlist generated from the declarations

- **Mechanism.** One page of outbound hosts grouped by when each is contacted, with protocol, port, purpose and what blocking costs; written by hand there.
- **In Scout.** `FeatureScopeRow(Scope, Actions, Access)` has no host (`src/Scout.ControlPlane/Features/FeatureFields.cs:45`); providers, adapters and main's downloads (`ui/packages/main/src/ipc/whisper.ts`, `ui/packages/main/src/update/update-notice.ts`, `ui/packages/main/src/oauth/*`) are not in the catalog; host literals sit in 19 runtime files; Ollama defaults to `http://localhost:11434` (`src/Scout.ControlPlane/Providers/OllamaProviderOptions.cs:12`). Add a host list (pattern, protocol, port, declarer, when, read or write), a TS twin in `ui/packages/shared`, a generator to `context/pages/runbooks/network-allowlist.md`, a golden test, and a source scan of `http(s)://` literals in `tests/Scout.ArchTests` and a vitest; SDK-default hosts (Octokit, Discord.Net, SlackNet) declared by hand.
- **Risks.** The page is public: vendor hosts only, never the owner's names.
- **Size.** S for page and golden test; M with the scan. Before 9 and 10.

## 8. Scheduled provider canaries

Weekly `schedule` plus `workflow_dispatch` in a new `.github/workflows/canary.yml`, never on a pull request: Whisper (`ggml-tiny.bin` checked against its pin, an openly licensed WAV, key words asserted), Ollama with a tiny model, OpenRouter with a low-credit key bound as environment variables, GitHub with the workflow's read-only token. The ChatGPT plan is excluded ([[pages/decisions/0018-chatgpt-plan-sign-in]]). On failure one labelled issue, after the owner says yes to `issues: write`; `::add-mask::`, one retry. Today: `ci.yml`, `ui-ci.yml`, `release.yml` (`.github/workflows/release.yml:201`), no scheduled job. Size S.

## 9. Model lifecycle and a signed remote catalog

Lifecycle first: `retired?: { replacement?: string }` on `LocalModelCatalogEntry` (`ui/packages/renderer/src/app/core/local-models.catalog.ts:7`) and the Whisper pins (`ui/packages/main/src/worker/whisper-model.ts:17`), a pure resolver that follows the chain and checks language coverage, a Settings sentence, a migration of the stored selection (S). A remote catalog (an Ed25519 envelope, monotonic version with a jump limit, bundled fallback, `If-None-Match`, a download-host allowlist) only if releases become too slow a channel (M to L, after item 7); the signing key is the real risk. See [[pages/decisions/0006-models-screen-task-based]].

## 10. Scout as an MCP server

The surveyed app's MCP is hosted and paid; only the idea carries over. A `stdio` server, a `--mcp` mode of `Scout.Host.Console` (Rule C), the official C# SDK. Read-only tools first: `list_projects`, `search_project`, `find_symbol` ([[pages/services/knowledge]]); then `list_meetings` and `get_meeting` (`src/Scout.Conversation/Abstractions/IMeetingStore.cs:16`, `:19`) behind a separate grant, off by default. Never a write tool (Rule A); an `mcp` feature row per tool group; SQLite opened read-only beside the worker's WAL. Risk: whatever model the client uses receives the data, which the grant text says. Tests: an in-memory client over a pipe, a read-only connection test, Rule I for a missing database. Size M, then S for meetings.

## 11. Audio import into a kept meeting

The operator already transcribes any media: ffmpeg writes a temporary WAV (`src/Scout.Operator/TranscriptionStage.cs:98`, `:148`), timed segments up to 60 minutes (`:19`, `:106`), the WAV deleted after (`:141`); `IMeetingStore.KeepAsync` stores lines (`src/Scout.Conversation/Abstractions/IMeetingStore.cs:13`). A stdin `{"type":"import","id","path"}` with a path from main's file picker only; ffmpeg to stdout (`-f s16le -`) so audio never touches disk; refuse while a lane listens or release the Whisper gate in 5-minute windows ([[pages/roadmap/followups]]); store as `others` or an `import` lane. A `desktop` row "Transcribe an audio or video file you choose" (Read). Direct URLs into memory up to a cap later; streaming-site URLs out. An hour may take about 20 minutes on CPU (measure). Tests: a fake ffmpeg runner, Rule I (exit 1, hang, URL 500), refusal while listening, path validation. Size M, S more for URLs.

## Outside Scout's purpose (for the owner)

Scout creates information in repositories, never code. These serve dictation; the first three inject input into another app, a write ADR 0032 forbids:

- Dictation into any app with auto-paste (on Wayland `wl-copy` plus `wtype`, `ydotool` or uinput). Would need a superseding ADR; not recommended.
- A dictation translation hotkey (same paste path); Scout already translates live lines in its panel.
- Editing selected text in another app in place. Not recommended.
- Notes with team spaces and cloud sync; Scout keeps meetings locally only. Out.

## Summary

| # | Item | Size | Depends on | Value |
|---|---|---|---|---|
| 0 | Audio time on `heard` finals | S | none | Enabler for 2, 3, 11 |
| 1 | Meeting detection on Linux, prompt to `startMeeting` | M (+S) | none | High |
| 2 | Text-layer holdback and retract; correlation gate | M + M | 0 | High: keeps double-talk |
| 3 | Live labels on `others`; named profiles | L; M | 0, an ADR | Medium, sensitive |
| 4 | Vulkan Whisper, then CUDA; transducer spike | S-M; M; L | the harness | High on the 1660 Ti |
| 5 | `--talk`; portal spike | S; S-M | none | High on COSMIC |
| 6 | Ladder; framing sentence; toggle grammar; Esc | S each | 5 (grammar) | Medium |
| 7 | Generated allowlist with golden and scan tests | S-M | none | Medium |
| 8 | Weekly canaries | S | none | Medium |
| 9 | Lifecycle fields; remote catalog | S; M-L | 7 (remote) | Low |
| 10 | MCP server, knowledge read-only; meetings gated | M; S | 7 (page) | Medium |
| 11 | Audio import, files then URLs | M (+S) | 0; better with 3, 4 | Medium |

## Proposed phase order

Mapped to [[pages/roadmap/future-improvements]] F11 to F18.

1. Quick wins (F11): the ladder and the framing sentence (6), canaries (8), the allowlist page and golden test (7), lifecycle fields (9). No protocol change.
2. A trigger from anywhere (F12): `--talk` (5) and the toggle grammar (6); the portal spike in a GNOME and KDE VM.
3. Meeting start (F13): the Linux mic detector and one prompt (1), suppressed while a lane, take or meeting runs.
4. Transcript quality (F14): audio time (0), then the text layer, then the correlation gate (2).
5. Speed (F15): measure CPU, Vulkan, CUDA; ship Vulkan with a reported fallback; spike a streaming transducer (4).
6. Import (F16): files through ffmpeg to stdout with the gate released, then direct URLs (11).
7. Speakers (F17): live labels on `others` (3); named profiles after an ADR on biometric data and the owner's word.
8. MCP (F18): knowledge tools (10); meetings only on the owner's decision; a remote catalog only if releases become too slow.

## Open: verify on nuget.org before choosing

- The sherpa-onnx .NET binding: package id, native payload per RID, licence (items 3 and 4).
- The Whisper.net Vulkan runtime package's size per RID; the CUDA runtime's size and whether it needs CUDA or cuBLAS installed (item 4).
- `ModelContextProtocol`: id, licence (expected MIT), still preview (item 10).
- Outside NuGet: `dbus-next` maintenance on npm (item 5); the sizes and licences of the VAD, segmentation, CAM++ and Parakeet models at pin time (items 3, 4).

Drift found while verifying: [[pages/decisions/0034-live-transcription-on-the-local-adapter]] cites `quick-ask.ts:905` for the system-audio refusal outside Linux; it is now at `:1149`.