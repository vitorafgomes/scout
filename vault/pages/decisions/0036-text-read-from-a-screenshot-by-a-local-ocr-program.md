---
title: "ADR 0036 - Text read from a screenshot by a local OCR program: the bundled tesseract on the JPEG bytes, through standard input and output"
version: "1.2"
date: 2026-11-04
changes: "Amendment, desktop bundle: the leptonica licence now ships beside the program, pinned at the 1.87.0 commit, and the AppRun rule is the resolver's. Earlier: the tesseract 5.5.4 AppImage and the tessdata_fast eng and por data pinned by hash in the media tools, run through AppRun because the bare binary does not start, the licence facts found and the real run on the bundled program; initial ADR"
page_type: adr
status: accepted
description: "The quick-ask window may ask the worker to read the text on a screenshot; the worker answers a read request by running the bundled tesseract on the JPEG bytes through standard input and output, with the languages from the app's locale, a 3 s budget and a 5,000-character cap, and registers the reader only when the program and its language data are on the configured paths; the image and the text are never stored and never logged."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Conversation/Abstractions/IScreenTextReader.cs
  - src/Scout.Operator/ScreenText/TesseractScreenReader.cs
  - src/Scout.Operator/ScreenText/ScreenTextServiceCollectionExtensions.cs
  - src/Scout.Operator/ProcessRunner.cs
  - src/Scout.Adapter.Local/LocalChatAdapter.cs
  - src/Scout.Host.Console/HostComposition.cs
  - ui/build/media-tools.json
  - ui/build/scripts/fetch-media-tools.mjs
reliability: high
---

# ADR 0036 - Text read from a screenshot by a local OCR program: the bundled tesseract on the JPEG bytes, through standard input and output

> Scout reads the text on a screenshot with the bundled tesseract on the owner's machine, the bytes in on standard input and the text out on standard output, and keeps neither.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-04.

## Context

Phase 5 of [[pages/roadmap/live-assistant]] asks that the owner, after capturing a screenshot in the quick-ask window ([[pages/decisions/0035-screenshot-attached-to-a-question]]), can click Read text, see the text on it in an editable block and send it with the question. The roadmap left two decisions open: bundle a program or link a managed library, and which languages to read. The worker already runs local programs on configured paths, `ffmpeg` and `piper`, through one process runner that passes arguments as a list and never a shell ([[pages/services/operator]]). Nothing may leave the machine for this ([[pages/invariants/local-first-default]]), the model and the chat can only ask ([[pages/invariants/no-executive-writes]]), and no program type may cross into the conversation core or the adapters ([[pages/invariants/pluggable-boundaries]]).

## Decision

- **Interface.** `IScreenTextReader.ReadAsync(ReadOnlyMemory<byte> jpeg, CancellationToken)` in the conversation abstractions answers the text, never empty, and throws on any failure (`src/Scout.Conversation/Abstractions/IScreenTextReader.cs:7`). The local adapter sees this interface only.
- **The program.** `TesseractScreenReader` runs `<TesseractPath> stdin stdout --psm 6 -l <languages> --tessdata-dir <TessdataPath>` through `IProcessRunner` (`src/Scout.Operator/ScreenText/TesseractScreenReader.cs:46`, `:50`), with the JPEG on its standard input as bytes (`src/Scout.Operator/ProcessRunner.cs:68`); page segmentation mode 6 reads the screenshot as one block of text. No file is written: the image goes in on standard input and the text comes out on standard output.
- **Languages.** `por+eng` when the configured app locale starts with `pt`, compared without case, else `eng` (`src/Scout.Operator/ScreenText/TesseractScreenReader.cs:43`). The locale is `Scout:Tools:Locale`, `en` when unset, which the desktop passes as `SCOUT__TOOLS__LOCALE`; nothing is read from the operating system.
- **Limits.** One run gets 3 s, linked to the request's token (`src/Scout.Operator/ScreenText/TesseractScreenReader.cs:36`); the process runner kills it past that. Every control character but the newline becomes a space, the text is trimmed and cut at 5,000 characters (`:59`, `:65`). A run past its budget, an exit other than zero (`:54`) or no text left after the clean-up (`:60`) throws; the exception names the program and its last error line, never the text it read.
- **Registration.** `AddScreenText` reads `Scout:Tools:TesseractPath`, `Scout:Tools:TessdataPath` and `Scout:Tools:Locale` and registers the reader only when the program file and the language data folder both exist (`src/Scout.Operator/ScreenText/ScreenTextServiceCollectionExtensions.cs:28`); otherwise it registers nothing, which is not an error: a `read` gets the not-installed sentence. It registers the process runner itself (`:33`), so a host without the YouTube operator can still read. The host calls it after the Whisper transcription (`src/Scout.Host.Console/HostComposition.cs:57`).
- **Wire.** The desktop sends `{"type":"read","id","image":{"contentType":"image/jpeg","base64"}}`, the image shape of an ask. The adapter checks the image first, by the ask's rule, `image/jpeg` of 1 byte to 3 MB decoded, else `fail` "The screenshot must be a JPEG of at most 3 MB." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:598`); then the reader, and without one `fail` "Scout cannot read text from the screen on this machine; the text reader is not installed." (`:607`). The text comes back as `{"id","event":"text","text"}` (`:621`); a reader that throws becomes `fail` "Scout could not read the text on the screen; see Scout logs." (`:631`). A read runs on its own tracked task, beside any ask or live session, and takes no slot of either (`:613`).
- **Never stored or logged.** The image lives only in the request and the program's standard input; the text only in the reply event. Log lines carry the id, the image's byte count and the text's length (`src/Scout.Adapter.Local/LocalChatAdapter.cs:612`, `:622`), never the image or the text.
- **No write.** Standard output to the desktop is the only thing written; the program is a local file on a configured path, run with an argument list and no shell, and nothing goes over the network. The write classification is none.

## Consequences

- **Better:** the owner gets the text on the screen without sending the screen anywhere, and edits it before it goes with a question; a machine without the program says so in one sentence instead of failing.
- **Worse:** reading takes one process start per click; text in a language other than English or Portuguese reads poorly until more language data is bundled; a `pt` locale needs `por.traineddata` beside `eng.traineddata`, or every read fails with the program's error.
- **Neutral but noteworthy:** the screen entry of the `desktop` feature gains its "Read the text on a screenshot" action with the desktop side; the program and its language data are fetched and pinned by the desktop's media tools, never at run time.

## Alternatives considered

- **OCR through the connected provider's vision** - rejected: it costs the owner a paid call, sends the screen out of the machine, and the owner keeps vision for the question itself ([[pages/decisions/0035-screenshot-attached-to-a-question]]).
- **A managed OCR library linked into the worker** - rejected for its licence and its size in a single-file worker; a bundled program on a configured path follows the ffmpeg and piper pattern and can be left out.
- **Write the image to a temporary file and pass its path** - rejected: the image would touch the disk, and standard input carries it as well.
- **Status quo** - rejected: the owner asked for the text to be read and shown before sending.

## Amendment - desktop bundle, 2026-11-04

- **Sources, pinned in `ui/build/media-tools.json`** (`tesseract` at `ui/build/media-tools.json:64`, its `leptonicaLicense` at `:70`, `tessdata` at `:85`), each checked against its SHA-256 at build like ffmpeg and piper ([[pages/services/desktop-operator]]):
  - the program: `tesseract-5.5.4_lept-1.87-x86_64.AppImage` of the `v5.5.4` release of the community repository AlexanderP/tesseract-appimage, 24,017,400 bytes, SHA-256 `fc2b2a3e…88587b9`, linux-x64 only; the only checksum the release publishes is the GitHub asset digest, and it matches. It reports `tesseract 5.5.4`, `leptonica-1.87.0`.
  - the language data: `eng.traineddata` (4,113,088 bytes, `7d4322bd…6427170b2`), `por.traineddata` (1,982,756 bytes, `c4932b93…ed58b7deb`) and `LICENSE` (11,358 bytes, `cfc7749b…bc523d30`) from tesseract-ocr/tessdata_fast at commit `87416418657359cb625c412a48b6e1d6d41c29bd`; the git blob ids of the three downloads match the repository's. The AppImage carries the same two language files byte for byte, plus six more; Scout names its own `tessdata` folder.
  - the leptonica licence: `leptonica-license.txt` (1,521 bytes, SHA-256 `87829abb…7d5806`) at `raw.githubusercontent.com/DanBloomberg/leptonica/13275a27…b3ab/`, the commit the `1.87.0` tag points at, the version the AppImage reports.
- **Layout.** `fetch-media-tools.mjs` runs the AppImage with `--appimage-extract` in a temporary folder (`ui/build/scripts/fetch-media-tools.mjs:131`) and copies its whole `squashfs-root`, symlinks kept, to `media-tools/linux-x64/tesseract/`, then the three data files to `tesseract/tessdata/` and the leptonica licence to `tesseract/leptonica-license.txt` (`ui/build/scripts/fetch-media-tools.mjs:289`, `:322`, `:331`), each checked against its pinned hash and fetched again only when absent or altered. Every folder is made `0755`, because the extraction leaves them `0700` and a root-owned install would not let the owner reach the program, and `AppRun`, the bundled loader and `usr/bin/tesseract` are made executable (`ui/build/scripts/fetch-media-tools.mjs:312`, `:314`). `tesseract/NOTICE.txt` records the AppImage hash and the data commit; with it unchanged and `AppRun` present nothing is extracted again, and each data file in place with its pinned hash is not fetched again. The ffmpeg step leaves the `tesseract` folder alone (`ui/build/scripts/fetch-media-tools.mjs:198`). The folder ships with the rest of `media-tools` as an extra resource outside the asar (`ui/build/electron-builder.yml:51`).
- **The program to run is `tesseract/AppRun`, not `tesseract/usr/bin/tesseract`.** The bare binary finds the bundled glibc 2.41 through its `$ORIGIN/../lib` run path but starts under the system loader; on this machine (glibc 2.39) it aborts with `*** stack smashing detected ***`, with or without `LD_LIBRARY_PATH`. `AppRun`, a `/bin/sh` script, starts it through the bundled `ld-linux-x86-64.so.2` and passes every argument on. The rule is the resolver's: `resolveMediaTools` answers `tesseract/AppRun` as the program, only when that file exists (`ui/packages/main/src/worker/resolve-binary.ts:138`), so `SCOUT__TOOLS__TESSERACTPATH` names `AppRun`; `SCOUT__TOOLS__TESSDATAPATH` names `tesseract/tessdata`.
- **Licences found.** In the extracted tree: `usr/share/doc/tesseract-ocr/LICENSE` (Apache-2.0, byte-identical to the tessdata_fast `LICENSE`) and `usr/share/doc/tesseract-ocr/AUTHORS`; no NOTICE file, no leptonica licence, and no licence text for the bundled glibc, libstdc++, libgomp, libarchive, libxml2, nettle and the image libraries. The repository that builds the AppImage has no LICENSE file and no licence statement. Upstream, tesseract is Apache-2.0 and leptonica's `leptonica-license.txt` at tag `1.87.0` is a two-clause BSD licence whose second clause asks binary redistributions to reproduce its notice. `NOTICE.txt` names the AppImage's provenance, the tesseract and leptonica sources and the data commit. Of these gaps only the leptonica licence is closed: it ships as `tesseract/leptonica-license.txt`, because its second clause asks binary redistributions to reproduce it; the notices of the other bundled libraries are still missing ([[pages/roadmap/followups]]).
- **Real run, 2026-11-04,** on a 640x120 white JPEG rendered by the bundled ffmpeg with the caption font, the worker's command line against the fetched folder:
  - `tesseract/AppRun stdin stdout --psm 6 -l eng --tessdata-dir tesseract/tessdata < en.jpg` printed `Scout reads this line`, exit 0;
  - `tesseract/AppRun stdin stdout --psm 6 -l por+eng --tessdata-dir tesseract/tessdata < pt.jpg` printed `Scout lê esta linha`, exit 0;
  - the same bytes written to the program's standard input by a Node `spawn` with the argument list, as the worker feeds them, gave the same two lines in 97 ms and 154 ms.
- **Enforcement.** `ui/packages/main/test/fetch-media-tools.test.ts:440` (the pinned entries: linux-x64 only, https, size, SHA-256, one data commit, the licence at its commit), `:752` (the whole tree, the modes, the notice, the data and the leptonica licence), `:809` (a tampered AppImage neither kept nor extracted), `:843` (a tampered leptonica licence not kept), `:876` (a licence in place with its hash not fetched again, an altered one replaced), `:908` (a tampered data file not kept), `:939` (a file that does not extract itself fails the build), `:716` (a second run fetches and extracts nothing); `ui/packages/main/test/worker-resolve-binary.test.ts:209` and `ui/packages/main/test/worker-build-env.test.ts:1067` (the program is `AppRun`).

## Enforcement

- `tests/Scout.Operator.Tests/ScreenText/TesseractScreenReaderTests.cs:32` (the command line, psm 6, the bytes on standard input, 3 s, the trimmed text), `:52` (languages by locale), `:63` (control characters and the 5,000 cut), `:78` (slow, failing and silent programs throw), `:97` (no file, no path argument but the language data folder), `:118` (registered only with both paths), `:129` (the configured locale reaches the registered reader).
- `tests/Scout.Operator.Tests/ProcessRunnerTests.cs:102` (bytes reach a real program's standard input).
- `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:557` (the text event), `:574` (no reader), `:591` (the image rule), `:606` (a reader failure), `:619` (never logged); `tests/Scout.Adapter.Local.Tests/LocalLiveSessionTests.cs:380` (a read beside an open ask and listen).
- `tests/Scout.Host.Console.Tests/HostLocalCompositionTests.cs:198` (the composed local adapter gets the reader only with both paths).
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]], [[pages/invariants/local-first-default]].

## Related

- Services affected: [[pages/services/operator]], [[pages/services/adapter-local]], [[pages/services/host-console]], [[pages/services/conversation]]
- Related decisions: [[pages/decisions/0035-screenshot-attached-to-a-question]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]], [[pages/decisions/0028-local-quick-ask-assistant]], [[pages/decisions/0032-per-feature-write-permissions]]
- Initiative: [[pages/roadmap/live-assistant]]
