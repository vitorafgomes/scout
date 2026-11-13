---
title: "Versions decisions"
version: "1.0"
date: 2026-11-13
changes: "Version notes moved out of page headers."
page_type: knowledge
status: active
description: "Version notes that were kept in page headers."
source: ["context/pages/"]
---

## Version notes: `pages/decisions/0008-local-models-real-downloads.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Third amendment of 2026-11-12: an installed Ollama model whose capabilities lack tools is marked No tools and the model in use says it cannot call tools, without blocking the choice (the owner's design); by default unknown capabilities say nothing and an empty list counts as known. Second amendment of 2026-11-12: Pull by name takes any model of the Ollama library by its name and Browse the Ollama library opens the library in the owner's browser (the owner's design); by default a name with a registry host is refused, an installed name is pulled again, and a pull by name never puts the model in use. Amendment of 2026-11-12: every model the local Ollama lists is listed and can be chosen and the five catalog models stay as Recommended (the owner's design); by default an embeddings-only model is never a chat choice and the model in use cannot be removed. Second amendment of 2026-11-11: the hint to start Ollama once before the first download is gone with the server controls. Amendment of 2026-11-11: a Download on an Ollama chat model also pulls the embedding model nomic-embed-text while Ollama lacks it, a Skill matching row shows it, and the worker is told the same name. Amendment of 2026-11-08: Scout no longer creates the Ollama identity key with ssh-keygen; a pull ends only on Ollama's success line; Cancel and a stall window on downloads. Earlier: migrated from the owner's notes on 2026-10-21

## Version notes: `pages/decisions/0013-conversation-core-streaming-sink.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.2 (2026-11-10): amendment, bounded error endings: the terminal write of a reply that ended on an error gets what remains of the overall timeout or the terminal write grace, a rejected or unfinished write is logged once and the run returns, and error bodies fit the declared maximum body length.
- v1.1 (2026-11-06): amendment, progress before the text: IResponseSink.ProgressAsync with a default that ignores it, TurnProgress and the ActivityFilter in Scout.Llm, the local adapter's progress event.
- v1.0 migrated from the owner's notes on 2026-10-21

## Version notes: `pages/decisions/0017-provider-connect-disconnect.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-08 (Discord application id): discord.client-id may be read back by the renderer; every other discord.* entry stays refused. Earlier: Amended 2026-11-08: the card in use offers Change key (owner decision B), which checks the key, stores it and restarts a running worker without switching the provider. Earlier: Amended 2026-11-07 (OpenRouter): OpenRouter joins the keyed providers; its key check is GET /api/v1/key and its model list GET /api/v1/models; the optional Admin key reads of ADR 0046 listed. Earlier: Amended 2026-11-07: Anthropic (the Claude API card, ADR 0048) joins the keyed providers; its key check and model list are a GET of the Anthropic models list. Earlier: Merge of the channel operator phase 8c-A and the live assistant: references re-pointed by content. Amended 2026-10-23: connect auto-uses, disconnect never falls back to Ollama, provider None; model choice moved to Models, Analysis

## Version notes: `pages/decisions/0019-github-app-device-flow.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-11: main also reads how many repositories each installation of the App covers, one GET per installation, for the count after Connected as. Amended 2026-11-06: ADR 0038 widens the App to Pull requests and Issues read & write when its phase 2 lands; read-only then rests on the write gate. Amended 2026-10-23: a pasted token as the second method, with a warning for write-capable classic tokens

## Version notes: `pages/decisions/0021-allowed-users.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amendment 2026-11-10: Settings and the empty-list refusal both ask for pair followed by the code. Merge of the channel operator phase 8c-A and the live assistant: references re-pointed by content. Amendment 2026-11-04: Scout:Access:OwnerUserIds names the owner among the allowed users, for the operator's owner-only functions from chat; Amendment: the owner pairs from chat with a one-time code sent in a direct message, bare or as pair <code>; the code burns on exposure or after five wrong codes; the empty-list and pairing refusals say how to pair

## Version notes: `pages/decisions/0022-discord-setup-from-the-bot-token.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amendment 2026-11-08, revised: a stored step resumes only while nothing can run; a token kept without a server reads as Needs a server with Add to server. Amendment 2026-11-08: Discord counts as set up only once a server is chosen, checked against the vault; the open step and the application id are remembered so a reopen resumes; a first token restarts a running worker; step 2 stores the token only once the intent is on

## Version notes: `pages/decisions/0028-local-quick-ask-assistant.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.10 (2026-11-10) amendment: the floating button display is one row in Settings, and Settings records the shortcut while main lets it go.
- v1.9 (2026-11-07) the diagnostics export is no longer named, its unused channel removed.
- v1.7 (2026-11-07) cancel comes only from Stop; a running turn refuses a new ask with local/busy. Merge of the channel operator and the live assistant. Channel operator: Phase 8c-A: references re-pointed by content. The LocalTurn reference re-pointed by content. Live assistant: Live transcription requests and events on the same transport (ADR 0034).

## Version notes: `pages/decisions/0029-project-site-and-releases.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-09: the site knows an arm64 visitor, so the big button never offers Linux on arm64 the x86_64 package, and two packages of one kind are labelled by architecture. Earlier: Amended 2026-11-08: build waits for the CI and UI CI workflows; an iPad with the desktop user agent gets no platform. Earlier amended 2026-11-08: a release is also cut by running the workflow from main with a kind; scripts/next-version.sh computes the version from the commits since the last tag, and publish creates the tag and the release, a prerelease when asked. Earlier: the build keeps latest*.yml and the Windows blockmap for the updater (ADR 0031)

## Version notes: `pages/decisions/0032-per-feature-write-permissions.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-12 (issue #323): a reversible write is in the write log as started before it runs, its allowed answer carries that entry's id, the record names it, and a started entry with no record 30 minutes later lists as not confirmed and is never sent again. Earlier: Amended 2026-11-12: the desktop's unload of Scout's own Ollama models when the owner's click moves the provider in use away from Ollama is a reviewed local write of the class the amendment of 2026-11-11 rules: no grant, only the models Scout uses and Ollama has loaded, the server left running, each model logged by name in main's log. Earlier: Amended 2026-11-11 (issue #293): the control plane's download and delete of a local model, and the start and stop of a server Scout started, are a reviewed local write on the owner's click outside the feature grant, on the desktop token only, logged to the host's log. Earlier: Amended 2026-11-11: the owner accepted one decision for a set of actions he was shown, at most 50, a changed group keeping Reject all. Amended 2026-11-10: an approved GitHub action is sent at most once: the shared claim, after settling a write already in the log, records a lapsed claim with no record as failed, not confirmed, and never hands it out again, while YouTube's is still handed out again; the payload and the card's summary carry only the function's own parameters. Earlier: Amended 2026-11-08 and 2026-11-09 (issues and comments wait for the owner): GitHub's New issues, Pull request comments and Issue comments rows are irreversible, so each review, issue, comment or reply the model asks for is queued with its arguments and waits for the owner's approval of that one action, and the worker's executor sends it after the approval and records it; a review is irreversible too, its card's Post being the owner's approval and a review asked by the model waiting on a card. Earlier: Amended 2026-11-09 (bot-findings lane): a write already in the write log is never handed out again or rejected as never finished; an approval that cannot be logged rejects its action; an approved action is handed to a worker only when its approval is in the write log, and is rejected for a missing entry only a minute after its approval. Earlier: Amended 2026-11-08 (Approvals lane): an approval needs the feature's grant, and an action asked before the current grant is rejected; the grant is read once more just before an irreversible action is sent; every decision, the owner's and the rejections a cleared grant causes, is logged; a claimed action from before the current grant is rejected once its claim lapses. Earlier amended 2026-11-08: clearing a grant rejects the waiting and approved-unclaimed actions with writes were revoked; a revoke main could not send is kept and sent again before grants reach a worker; only the main window grants; a credential change or loss revokes the grant, GitHub included. Earlier: amended 2026-11-07: an irreversible action can also be approved from its card in the chat or the quick-ask window, still the owner's click in the desktop. Earlier: the function gate is the enforcement point; the guard is a pre-filter

## Version notes: `pages/decisions/0033-channel-operator-architecture.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amendment of 2026-11-11: a presenter version is named by an id the control plane derives from its content, and UsePresenterVersion and RemovePresenterVersion take that id instead of a position; a version that is no longer there answers NOT_FOUND and nothing changes. Field 2 of both requests is reserved. The desktop, the worker and the control plane ship in one package, so no earlier client calls this contract. Amendment of 2026-11-05: the owner removes a presenter version made by mistake with RemovePresenterVersion. Amendment of 2026-11-05: the Writes bullet names exactly what the control plane and the worker delete, and RegeneratePresenter became a presenter request. Amendment of 2026-11-05 (phase 8c-B): the automatic presenter, the mandate's default format, the first presenter asked for when the script is ready and made as a job of its own before production, presenter versions the owner improves with feedback or a prompt, at most 10 kept, a picture per scene in the animated character with the 8c-A refusals lifted, the mouth found by a local face landmark model; rejected a heuristic mouth position and a vision model's box as the only detector. Amendment of 2026-11-04 (phase 8c-A): the animated character, a cartoon presenter made once per project whose mouth moves with the narration through a local lip sync program, with no video model and no paid call per video; the realistic photo animation rejected after spike B found no CPU talking-head model under commercial-use licences, the video model a later option; RegeneratePresenter deletes the picture it forgets and is refused on a running project. Phase 8b amendment restated: the 60-second bound is on a scene's speech and a pause is carried into the clip before it; a caption edit on a delegated own video goes back to assembly. Amendments of 2026-11-03 and 2026-11-04 (phase 7): bundled voices, delegation, stock pictures, visual brief and motion, delete project and voice samples; amendment of 2026-11-04 (phase 8a): the project format, the direction and the owner's scene text; amendment of 2026-11-04 (phase 8d): the operator from the chat, for the owner only, through the worker token's chat calls under the grant, logged via chat when the project changed; the trust model, the state read in the same conversation within 10 minutes, the conversation the memory keys, the voices, the 240-character cut and youtube_scene_text; amendment of 2026-11-04 (phase 8b): own video, the recording transcribed on this machine as the script, scenes as clips with captions, the transcript text to the AI Source for the metadata and chapters, the 8 GiB and 60-minute caps

## Version notes: `pages/decisions/0034-live-transcription-on-the-local-adapter.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Dated line (2026-11-12): the failed-load live tests run on the model seam and a speech runtime that cannot load is TranscriptionUnavailableException. Earlier: The transcript is not persisted by the desktop; a kept meeting is stored by the worker. Earlier: The live translation amendment's Enforcement lists the desktop tests. Earlier: Amendment: live translation (listen takes translate, each final carries its seq, a translation event per final from the connected provider, outside the read-only guard, through a per-session queue with one in flight, coalescing, two context lines, 20 a minute, 20 s a call and a 15 s drain before complete; rejected: Whisper translate, persisting translations, translating partials). Earlier: the rejected second consent states what consent is on Wayland and off it. Earlier: Amendment: the include toggle turns on with the first Others line and the turn keeps only the question; desktop lanes and system audio (the me and others lanes, the armed loopback answer, the window kept up as the indicator, the half-duplex gate and the in-memory transcript in the renderer). Earlier: two live sessions at once on the local adapter, each under its own id, over the shared decode gate

## Version notes: `pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amendment, desktop bundle: the leptonica licence now ships beside the program, pinned at the 1.87.0 commit, and the AppRun rule is the resolver's. Earlier: the tesseract 5.5.4 AppImage and the tessdata_fast eng and por data pinned by hash in the media tools, run through AppRun because the bare binary does not start, the licence facts found and the real run on the bundled program; initial ADR

## Version notes: `pages/decisions/0037-meetings-kept-locally-in-sqlite.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amendment: the store on Entity Framework Core (ADR 0052): the schema from a migration, a file an older build wrote created again, the pragmas and the delete scrub kept as SQL in the store; keep-key citations re-pointed (2026-11-08). Earlier: Amendment: a keep carries a keep key, the hash of the meeting's start, and the store keeps once per key, so a retried keep does not store a second copy; Enforcement citations re-pointed (2026-11-08). Earlier: Delete scrub is uncancellable and runs on a retry. Earlier: Amendment: desktop side (what the desktop sends on keep, the context a meeting question carries, the export through the save dialog, the Meetings screen, SCOUT__MEETINGS__DBPATH; rejected: automatic prompts on a timer, keeping by default). Earlier: Initial ADR

## Version notes: `pages/decisions/0038-code-quality-assistant-on-the-write-gate.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.4 (2026-11-09) dated line: an issue is irreversible again and waits for the owner per issue (ADR 0032 amended); the review unchanged.
- v1.3 (2026-11-06): the review and the issue reclassified as reversible writes under the grant, posted from chat at once; the draft, the queue and the executor removed (owner decision).
- v1.2 (2026-11-06): phase 4, a whole-project audit from chat with the tree and code-search reads and a conventions profile kept as a reversible write.
- v1.1: phases 2 and 3 implemented the same day; the draft-and-executor shape, the feature-scoped claim on the worker gate, and Scout commenting only are recorded.
- v1.0 initial ADR

## Version notes: `pages/decisions/0040-review-findings-as-cards-in-chat.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.6 (2026-11-13) dated line: a review's block may be posted by the review job, its verdict and read line computed by code (ADR 0055).
- v1.5 (2026-11-12) dated line: a review that finds nothing still produces a findings card with Approve alone; an audit still needs a finding.
- v1.4 (2026-11-09) dated lines: Post is the owner's approval of the review, sent as one approved action and never through the model; the issues chosen on the cards are queued for the owner's approval one by one, and the cards show them queued (ADR 0032 amended).
- v1.3 (2026-11-07) dated line: the artboards of 2026-11-07 do not draw the GitHub preview, so it keeps GitHub's light greys in either theme.
- v1.2 (2026-11-06) amendment: an audit comes back as the same cards, through present_audit_findings, with kind audit, no number, the known limitations and a rule per finding, and the cards open one issue per chosen finding through create_issue.
- v1.1 (2026-11-06) amendment: the block comes from present_review_findings, a function the model calls, and Scout appends it to the reply; the renderer is unchanged.
- v1.0 initial ADR: the findings block in the reply, the cards on both chat surfaces, the posting question with its marker, the posted box

## Version notes: `pages/decisions/0041-angular-material-in-the-renderer.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.1 (2026-11-09) amendment: Inter ships inside the app, from a pinned package, where the decision said the system stack.
- v1.0 Initial ADR: Angular Material and the CDK in the renderer, the global theme from the approved palette, the sc aliases, the snackbar service, screens migrating one per version

## Version notes: `pages/decisions/0042-audit-as-a-job-with-saved-progress.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.3 (2026-11-13) dated line: the job engine also runs pull request reviews, picked before an audit (ADR 0055).
- v1.2 (2026-11-09) dated line: one approval per issue is enforced by the gate (ADR 0032 amended).
- v1.1 (2026-11-07) provider limits pause the job; batches of 60,000 characters; partly read files named; a time limit per turn; posts split under 30,000 characters; the snapshot found through the plugin.
- v1.0 initial draft

## Version notes: `pages/decisions/0043-the-verdict-on-a-pull-request.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.3 (2026-11-12) amendment: the .NET default test command leaves out the tests the repository marks Category=Docker and the run's note says so; a caller's command is never altered.
- v1.2 (2026-11-09) dated lines: the card's Post goes in as the verdict comment, the click being its approval; the three comment writes are irreversible and wait for the owner per comment (ADR 0032 amended).
- v1.1 (2026-11-07) the card click is the approval: a desktop-only call enqueues an approved action with its payload, a host executor runs the verdict pinned to the reviewed commit; give_pull_request_verdict separate from the review.
- v1.0 initial draft

## Version notes: `pages/decisions/0044-scout-acts-on-github-as-its-own-app.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.3 (2026-11-08) amended: the App's name and slug are read again with the App's JSON Web Token at start and when the card reads the bot status, so a rename on GitHub is followed; the card shows the name and handle and opens the App's settings page to change the name.
- v1.2 (2026-11-08) amended: the write log records the login that signed a verdict, and Approvals shows it under Signed as.
- v1.1 (2026-11-07) amended with what the implementation found against GitHub's docs

## Version notes: `pages/decisions/0045-theme-and-notification-center.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.6 (2026-11-12) a read job entry turns unread again only on a state change or a note, not at each area's end; an audit running when Scout closed comes back in progress, only a test run as failed.
- v1.5 (2026-11-11) Pause, Continue and Stop on a running audit and Stop on a running test run, from the bell, through the control plane; the entry shows only what the worker reported.
- v1.4 (2026-11-08) a comment or review that mentions the owner's Scout bot becomes a mention entry: one phrase search more per read inside the 20-a-minute budget, each hit confirmed from the pull request's body, comments and reviews, one entry per comment or review with Review it, the reads named on the Notifications row.
- v1.3 (2026-11-08) what becomes an entry: only error notices from the window, background work, a pending approval and a worker that crashed or gave up; GitHub entries leave on Disconnect; the GitHub read follows every page at no more than 20 searches a minute.
- v1.2 (2026-11-07) an entry may offer actions; the voice message without a Whisper model is the first, with the base model's download run by main and Open Models, Transcription.
- v1.1 (2026-11-07) GitHub activity through the search API, since /notifications accepts only classic tokens.
- v1.0 initial

## Version notes: `pages/decisions/0046-ai-source-usage-on-the-cards.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-07: OpenRouter is connectable on its card (key checked with GET /api/v1/key, models from GET /api/v1/models), so its balance shows; an optional Admin key on the OpenAI API and Claude API cards shows the organization's spend this month from the providers' cost reports. Earlier: initial ADR

## Version notes: `pages/decisions/0047-the-docker-card.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.6 (2026-11-08) the GitHub card's \"Set up Docker\" opens the Docker side panel (the Features side panel), no longer a scroll to the Docker card, since every Features card is a summary of one size and its sections live in the side panel its Details opens.
- v1.5 (2026-11-07) Download images fetches the one image each recorded repository needs and Docker lacks, and a repository's Download pulls only its image (main's pullImage); the open item of v1.4 closed.
- v1.4 (2026-11-07) part 2: each repository's stack is recorded beside the snapshots when one is taken or reused, and every run writes a record beside its log; the card lists the repositories and the last run.
- v1.3 (2026-11-07) the pinned list grows to 33 images, one per supported version of every stack (.NET SDK 8, 9, 10; Node 18 to 26; Python 3.10 to 3.14; Go 1.23 to 1.26), in the same groups.
- v1.2 (2026-11-07) the switch is Scout's own on/off for Docker runs (the public vault key `sandbox.enabled`, passed to the worker as `Scout:Sandbox:Enabled` and checked by the host's sandbox adapter), because the feature grant is per feature and a per-row switch does not exist; the work folder follows .NET's LocalApplicationData per OS; the card's sections collapse and remember the owner's choice; the images are grouped by stack with a Download per stack, Download images waits for the repositories' list, and Docker Hub's 429 has its sentence; Repositories and Last run ship as their empty states.
- v1.1 (2026-11-07) Java (Maven and Gradle, one image per supported JDK) in the pinned-image list; the stack and versions are detected when Scout reads a repository and shown per repository on the card, with the image that run needs; no setting where the owner types a stack.
- v1.0 initial ADR

## Version notes: `pages/decisions/0050-read-aloud-and-conversation-mode.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Amended 2026-11-08 (the microphone and the speaker): the owner chooses both in Settings, Audio, kept in renderer storage like the read-aloud switch, an unplugged one falling back to the default with one notice; a Test meter shows the room and the worker's speech threshold; a refused, missing or busy microphone each has its own sentence. Amended 2026-11-08 (conversation in a noisy room): a frame is speech only when it is three times the room, the conversation microphone has no automatic gain, a click keeps what was said and Esc drops it, the partial shows, a replaced or refused listen ends the conversation with its reason, and the worker logs each turn end. Amended 2026-11-08: the owner chooses the voice in Models, Speech; main reads the saved choice from the vault for every sentence, so nothing restarts; the worker lists its voices and refuses one it does not have. Earlier: initial ADR

## Version notes: `pages/decisions/0051-desktop-end-to-end-tests.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.4 (2026-11-11): the fake worker answers a delete request, for the Meetings notices flow.
- v1.3 (2026-11-09): amendment, the real control plane host started on a seeded Scout data folder, the fake worker listing meetings, five specs for the loaded states and a control plane that dies; every test gets its own runtime and temporary folder, runs at UTC, takes no scout variable from the invoking shell, and checks all of it inside main at launch; the host build written last is the one run; the e2e job builds the host; the CI line citations of the Decision re-pointed; fixture citations re-pointed by content.
- v1.2 (2026-11-08): pointer to ADR 0053, the driven copy and the fifth smoke test.
- v1.1 (2026-11-08): amendment, the packaged smoke test: bun run test:smoke opens the extracted AppImage in network and PID namespaces and checks the window, the shipped worker and control plane, and a clean startup.
- v1.0: initial ADR, with the Chat flows built

## Version notes: `pages/decisions/0052-persistence-behind-stores-on-entity-framework.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.3 (2026-11-13) dated line: the audit job's repository index gains kind and pull_request in a second migration (ADR 0055).
- v1.2 (2026-11-12) Amendment: Entity Framework Core, `Microsoft.Data.Sqlite` and `dotnet ef` on 10.0.15, the 11.0 line held back.
- v1.1 (2026-11-10) Amendment, proposed and waiting for the owner: the control plane's six JSON files are named as outside this decision, pinned by a test, with a follow-up to decide whether they move behind stores.
- v1.0: Initial ADR: the audit and meetings stores on Entity Framework Core, measured first; the meetings store warmed after readiness, measured; dotnet-ef pinned by the local tool manifest; indexes; an older-build file deleted only with known tables; keep measured

## Version notes: `pages/decisions/0053-electron-fuses-and-ipc-sender-check.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.5 (2026-11-11): bringing the Scout window forward answers only the operator window (SHOW_MAIN_GUARDS).
- v1.4 (2026-11-10): letting the quick-ask shortcut go while Settings records one answers only the main window (SHORTCUT_GUARDS); three citations re-pointed by content.
- v1.3 (2026-11-10): the floating button card channel, answered only by the floating button.
- v1.2 (2026-11-08): the verdict on a pull request joins the decision family (main and quick ask); the twenty-three credential and access channels outside scout:features.* answer only the main window through one guarded ipcMain; where credentials are typed made precise.
- v1.1 (2026-11-08): setCredentials and setup answer only the main window; the deciding scout:operator.* channels answer only the operator and main windows; the sender check moved to its own module.
- v1.0: initial ADR, phase S1 of the security hardening initiative, built in the same change

## Version notes: `pages/decisions/0054-functions-scoped-per-skill-and-job.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.4 (2026-11-13): amendment line: decision 5 gains the review batch's job set, and code-review declares start_pull_request_review (ADR 0055).
- v1.3 (2026-11-11): amendment line: code-audit also declares the read list_issues, so an audit in a conversation does not report again what an open issue describes; the audit job's batches are not offered it.
- v1.2 (2026-11-10): amendment line: code-review declares two comment functions, no longer comment_on_issue, and a review keeps the audit only when the message names it or asks for the whole repository.
- v1.1 (2026-11-08): a write skill is selected only from the message's own text, never the context; the carry expires with the memory's idle time.
- v1.0: initial ADR, phase S2 of the security hardening initiative, built in the same change

## Version notes: `pages/decisions/0055-pull-request-review-as-a-job-in-batches.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.2 (2026-11-13) dated line: the first part of the checks before the start is built (size, Docker runs, head, another review, grants), with its blocking outcomes and its tests in Enforcement; the AI Source, Docker on this computer, tests that need a Docker daemon and the stack are left.
- v1.1 (2026-11-13) the second change landed: the start and the plan of a review in the service only, with its tests in Enforcement; the checks before the start move to a change of their own.
- v1.0 (2026-11-13) Initial ADR, accepted by the owner: a review that does not fit one turn is a job of kind review on the audit's engine, pinned to the head commit, read in batches that never write, bounded at 10 batches and 60 minutes, checked before it starts, ending in one card whose verdict and read line Scout computes; the first of its three changes, the planner over a list and the job's kind and pull request, built with it
