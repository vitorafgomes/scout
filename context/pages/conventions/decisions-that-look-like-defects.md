---
title: "Decisions that look like defects"
version: "1.7"
date: 2026-11-11
changes: "v1.7 (2026-11-12): a record that fails while the worker runs leaves the function's result, one Warning and the started entry, which lists as not confirmed (issue #323); and a started write with no record is listed as may have run, never sent again.."
page_type: established-patterns
status: active
description: "Behaviour in Scout that reads as a bug and is intended: what it is, why it is right, where it lives and the test that locks it. A reviewer or an audit checks this page before reporting; a finding that matches an entry is not a finding."
source:
  - chat
  - "src/Scout.Conversation/Streaming/StreamPumpOptions.cs"
  - "src/Scout.ControlPlane/Operator/CommentRules.cs"
  - "src/Scout.ControlPlane/Operator/OperatorModels.cs"
  - "src/Scout.ControlPlane/WriteGate/WriteGateService.cs"
  - "src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs"
  - "src/Scout.Llm/Guard/ReadOnlyGuardChatCompletionService.cs"
  - "ui/packages/renderer/src/app/models/models.component.ts"
  - "src/Scout.ControlPlane/Providers/OllamaProviderService.cs"
  - "tests/Scout.ArchTests/TestSuiteHealthTests.cs"
reliability: medium
---

# Decisions that look like defects

> Behaviour in Scout that reads as a bug and is intended. Check here before reporting a finding; add an entry when a finding is answered "not a defect", in the change that answers it.

Two audits of this repository (2026-11-09 and 2026-11-10) reported 27 findings that were intended behaviour. Each cost a triage, an answer and a closed issue. An entry here costs one read. An entry is a claim about the code today: when the code under it changes, the entry is checked again, and an entry with no test behind it is weaker than one with a test.

Not on this page: a limitation that is real and accepted for now. Those are in [[pages/roadmap/followups]] and in the known exceptions of each invariant.

---

## Conversation and streaming

**An empty model reply is shown as a placeholder.** The pump turns an empty body into "(no response from model)" before it completes the sink, so a sink never gets an empty final text (`src/Scout.Conversation/Streaming/StreamPumpOptions.cs:26`). A streaming cursor left behind by an empty reply cannot happen.

**The question and the attached context are joined with no delimiter.** `LlmStreamingRouter` adds none because the delimiter is part of the context: only the local adapter sets a message's context, from the line the desktop sends, and the desktop's main process refuses any context that does not start with one of its known headers (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs`, `ui/packages/main/src/quick-ask/quick-ask.ts`).

**The interim rendering has no clamp for a cursor longer than the body cap.** The pump's options are built in code and never bound from configuration, so the cursor is always the one-character default, and no sink advertises a cap below one. Locked by `The_Registered_Pump_Draws_The_One_Character_Cursor_Whatever_The_Configuration_Says` (`tests/Scout.Conversation.Tests/ConversationServiceCollectionExtensionsTests.cs`).

**A voice reply's quote does not grow with the transcript.** The conductor cuts it at 200 characters before the prefix is built, and the smallest body cap of any sink is far above that. Locked by `A_Long_Voice_Quote_Keeps_Every_Rendered_Body_Within_The_Smallest_Sink_Cap` (`tests/Scout.Conversation.Tests/ConversationConductorVoiceTests.cs:137`).

**A citation's line number is formatted without a culture.** `Line` is an `int?` (`src/Scout.Conversation/Abstractions/Citation.cs:26`); integer formatting differs by culture only in the negative sign, and a line is never negative.

**A successful attachment download does not dispose the response itself.** The stream the downloader returns is the response's content stream; the one caller disposes it, which releases the response. Locked by `The_Attachment_Stream_A_Caller_Disposes_Releases_The_Response_Body` (`tests/Scout.Adapter.Discord.Tests/DiscordVoiceMessageTests.cs:107`).

**A cancelled stop of the Slack adapter can return while the socket is still open.** `StopAsync` cancels the adapter's lifetime token first, the token the socket-mode connection was started with (`src/Scout.Adapter.Slack/SlackChatAdapter.cs:126`), then bounds the wait for the connect gate and the disconnect by the caller's token (`:154`). A stop that is cancelled returns instead of hanging the shutdown, and the process is ending. Locked by `StopAsync_When_Socket_Mode_Disconnect_Never_Completes_Returns_On_Cancellation` (`tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs:115`).

**A Teams turn that fails is acknowledged to the webhook as handled.** The adapter logs the failure and swallows it (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:137`), on purpose: a thrown error makes the Bot Framework answer a server error and can replay the whole turn, model call and functions included, and a replayed turn costs more than a lost reply; the sink already reports a failed send. Locked by `ProcessActivity_Handler_Throw_Is_Swallowed` (`tests/Scout.Adapter.Teams.Tests/TeamsChatAdapterTests.cs:144`) and stated in [[pages/services/adapter-teams]].

**Two flows of one turn share the turn's attached blocks.** `TurnAttachments` keeps one locked list per turn on the async flow where the turn began (`src/Scout.Llm/Abstractions/TurnAttachments.cs:17`, `:23`), because a function the model calls in a child flow of the same turn has to reach that turn's list; every read and write holds the lock (`:44`, `:68`, `:83`), and the blocks are taken once, when the turn ends (`src/Scout.Conversation/Streaming/StreamPump.cs:211`). A turn started from inside another gets its own list and restores the outer one on dispose.

---

## Write gate and GitHub

**The record of a write is accepted even when the grant changed after the send.** The worker asks the gate before it sends, and an action asked before the current grant is refused there (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:126`). The record step only logs a write that already happened; refusing it would lose the log entry, which is the worse outcome ([[pages/invariants/no-executive-writes]]).

**A verdict offered again after a restart is not sent twice.** Before sending, the plugin asks GitHub whether the same signer already gave that verdict on that head commit and returns the earlier review (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:229`). There is no in-memory "sent" set to lose.

**Queued payloads carry their values as strings.** Only the GitHub functions carry a payload, and the runner reads strings, numeric strings and the raw text of the comments list (`src/Scout.Plugin.GitHub/GitHubWriteRunner.cs:101`, `:145`). String values are the contract between the gate and the runner, not a loss of type.

**The read-only guard trusts the probability it is given and lets the request through when its service fails.** The guard is advisory (`src/Scout.Llm/Guard/ReadOnlyGuardChatCompletionService.cs:123`); writes are governed by the write gate, which fails closed.

**A listing of comments that returns nothing cannot lead to a second post.** The verdict runner treats a null list as empty (`src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs:72`), and the listing returns null only for an HTTP 404 (`src/Scout.Plugin.GitHub/GitHubClient.Reads.cs:127`); every other failing status throws (`:134`). A pull request that answers 404 to the read answers 404 to the comment that would follow, so nothing is posted twice.

**The mark that a read returned no content is shared by every call that runs under one scope.** The scope is begun once per function invocation by the filter (`src/Scout.Audit/AuditBatchRunner.cs:227`) on that invocation's own async flow, and the one place that marks it is the read function itself (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:290`), which starts no parallel read inside one call; concurrent invocations each hold their own box (`src/Scout.Llm/Abstractions/ReadOutcome.cs:19`).

**A record that fails while the worker runs keeps the write's result and logs one Warning.** When the control plane does not take the record of a write that already ran, `WriteGateFilter` leaves the function's result as it is and logs one Warning naming the function and the exception type, never an argument (`src/Scout.Llm/Guard/WriteGateFilter.cs:114`). Throwing would hand the model a failure for a write that happened, so it could ask for it again. Since 2026-11-12 (issue #323) the `started` entry the gate appended before the write stays in the log, and once 30 minutes have passed it lists as `not confirmed: Scout stopped before it recorded how this ended, so it may have run` ([[pages/services/control-plane]]). Locked by `tests/Scout.Llm.Tests/Guard/WriteGateFilterTests.cs:195`, `tests/Scout.Host.Console.Tests/GrpcWriteGateTests.cs:206` and `tests/Scout.ControlPlane.Tests/WriteGate/WriteGateServiceTests.cs:1060`.

**A started write with no record reads "may have run" and is never sent again.** A reversible write is in the write log as `started` before it runs, and its record follows under the same id. A `started` entry with no record could be a write that ran or one the worker never reached; Scout cannot tell which, so after 30 minutes it says it may have run, and nothing claims, retries or reconciles it, so the write runs at most once (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:609`; `tests/Scout.ControlPlane.Tests/WriteGate/WriteGateServiceTests.cs:1047`, `:1077`). A reversible write that runs longer than 30 minutes shows not confirmed until its record arrives, and then lists with how it ended.

---

## Control plane and hosts

**The operator's comments list has no paging.** The store keeps at most 1000 comments (`src/Scout.ControlPlane/Operator/CommentRules.cs:32`), so the answer is bounded.

**The operator's project lists accept any limit from the client.** A limit of zero means 50 and none above 200 is honoured. Locked by `A_Limit_Of_Zero_Means_Fifty_And_No_More_Than_Two_Hundred_Are_Listed` (`tests/Scout.ControlPlane.Tests/Operator/OperatorServiceTests.cs:315`).

**A worker save may drop or rewrite an idea that is still proposed.** Only an idea the owner decided is protected: the merge refuses a save that changes or drops a decided idea or that decides one itself (`src/Scout.ControlPlane/Operator/CommentRules.cs`); a proposed idea is the worker's to revise ([[pages/services/control-plane]]).

**The merge of a worker's scenes does not check for a repeated id.** The validation that runs before the merge on the only path that reaches it refuses a list with a repeated scene id (`src/Scout.ControlPlane/Operator/SceneRules.cs`), so the merge never sees one.

**Worker comments and ideas are validated without null checks.** They arrive over gRPC, where an unset text field is empty text and a collection is never null; the mapping builds the domain objects from the message (`src/Scout.Host.ControlPlane/Services/CommentMapping.cs`), so the validation has no null to meet on that path.

**The socket path can be overridden by configuration.** It is the owner's own setting (`src/Scout.Host.ControlPlane/Program.cs:20`); the default directory is private to the user, the socket is hardened or the host stops (`:53`), and every call still needs the session token ([[pages/decisions/0011-backend-frontend-grpc-uds]]).

**The contracts and the control-plane host have no test project of their own** (2026-11-10, issue #227). `Scout.ControlPlane.Contracts` holds the `.proto` files the gRPC code is generated from, and `Scout.Host.ControlPlane` hosts `Scout.ControlPlane`; `Scout.ControlPlane.Tests` references all three projects and drives the generated contracts over the real host and socket in its integration tests (`tests/Scout.ControlPlane.Tests/Integration/`), so it is the suite gate 4 runs for them ([[pages/architecture/project-map]], the tests column). A change to the contracts runs every test project anyway (gate 4's row for `Scout.ControlPlane.Contracts`). Locked by `TestSuiteHealthTests.EverySourceProjectHasATestProject`, whose `CoveredElsewhere` list names exactly these two and fails for any other source project without `tests/<Name>.Tests` (`tests/Scout.ArchTests/TestSuiteHealthTests.cs:15`).

**The control-plane tokens arrive as environment variables.** The supervisor hands them to the process it starts that way on purpose ([[pages/decisions/0011-backend-frontend-grpc-uds]]); nothing is committed or logged ([[pages/invariants/secrets-never-in-source]]).

**The in-memory credential store answers that it stored a secret while it reports itself unavailable.** Decided 2026-11-11: unavailable means the secret lasts only while Scout runs, and the contract says so (`src/Scout.ControlPlane/Features/ICredentialStore.cs`). No card in the catalog saves through it. Locked by `The_Default_In_Memory_Credential_Store_Answers_Saved_While_Unavailable_Because_Setup_Then_Enable_Runs_On_It` (`tests/Scout.ControlPlane.Tests/Features/FeaturesServiceTests.cs`).

**The audit planner does not check for links leaving the snapshot.** The snapshot store writes only regular files and the directories that hold them, so the root the planner walks cannot contain a link. Locked by `Extraction_Writes_Only_Regular_Files_And_Directories_So_No_Symlink_Exists_For_The_Audit_Planner_To_Follow` (`tests/Scout.Plugin.GitHub.Tests/RepositorySnapshotStoreTests.cs`).

**The control plane downloads and deletes local models, and starts and stops the local model server, with no grant and no approval.** Decided by the owner on 2026-11-11 (issue #293): these change the owner's computer and belong to no feature, so they are a reviewed local write on the owner's click, the class of the desktop's own model download and delete, outside the feature grant and the approval queue. Only the desktop token reaches them, nothing the model can call does, a stop refuses a server Scout did not start, and a download logs one line at its start and one at its end and a delete one line saying how it ended, each naming the model, to the control plane host's log, not the write log. Recorded in [[pages/decisions/0032-per-feature-write-permissions]] (amendment of 2026-11-11) and [[pages/invariants/no-executive-writes]] (known exceptions, and Where it is held). Locked by `The_Worker_Can_Request_But_Never_Approve_Grant_Enable_Or_Read_The_Features` (`tests/Scout.ControlPlane.Tests/Integration/WorkerGateGrpcTests.cs:29`) and `A_Caller_Outside_Its_Role_Is_Denied_Naming_The_Method` (`tests/Scout.ControlPlane.Tests/Security/TokenAuthInterceptorTests.cs:72`) for the worker token's denials, and `Deleting_A_Local_Model_Logs_One_Line_Naming_The_Model_And_How_It_Ended` and `Downloading_A_Local_Model_Logs_One_Line_At_The_Start_And_One_At_The_End` (`tests/Scout.ControlPlane.Tests/Providers/OllamaProviderServiceTests.cs:326`, `:359`) for the lines.

**Two presenter versions can share an id.** The id is a hash of the version, not a stored value: the lower-hex SHA-256 of its JSON, computed on read and never written to `projects.json` (`src/Scout.ControlPlane/Operator/OperatorModels.cs:156`). Two versions share it only when every field is equal, the control plane's 100-nanosecond `MadeAt` stamp included; Use and Remove act on the first one with that id, and since the two are interchangeable the result is the same whichever is taken, as for the twin of a built-in presenter. Locked by `Removing_A_Twin_Of_The_Current_Presenter_Succeeds_And_The_Last_One_Naming_It_Is_Refused` and `A_Project_Stored_Before_Ids_Reads_The_Same_Distinct_Ids_Twice` (`tests/Scout.ControlPlane.Tests/Operator/OperatorServiceTests.Presenter.cs:671`, `:821`).

**The ChatGPT plan token policy reads the file's write time without a try.** That read does not throw for a missing or unreadable file (`src/Scout.Llm/Providers/ChatGptPlan/ChatGptPlanTokenPolicy.cs:51`); the read of the file that follows maps an I/O or access failure to the sign-in-required error.

**A scheduled job that failed is not tried again in the same slot.** The scheduler stores the slot before the job runs (`src/Scout.Operator/OperatorScheduler.cs:152`) and logs a throw (`:161`), so a failing job counts as run until its next slot; running it again every minute would spend the YouTube quota on a failure that does not change. The service page says so ([[pages/services/operator]]). Locked by `A_Job_That_Throws_Is_Not_Run_Again_In_The_Same_Slot` (`tests/Scout.Operator.Tests/OperatorSchedulerTests.cs:193`).

**A drafts-only comment run clears the sync error in the worker, not in the store.** The worker builds the saved state with no error (`src/Scout.Operator/CommentDesk.cs:339`), and the control plane keeps the stored one unless the save reports a newer sync time or its own error (`src/Scout.ControlPlane/Operator/CommentRules.cs:84`); a drafts-only run sends no sync time, so the stored error stays. The behaviour the class summary promises lives on that side.

**The projects validator does not meet a null entry.** `Items` is bound by the configuration binder (`src/Scout.Knowledge/ProjectsOptions.cs:10`), which never inserts a null element, and no code builds the list with one, so the validator's read of an entry (`src/Scout.Knowledge/ProjectsOptionsValidator.cs:34`) meets one only from hand-built options. A guard there would be hardening, not a fix.

**The first readiness gate of the local model server is completed by the first start.** The initial gate is created with the probe (`src/Scout.ControlPlane/Providers/OllamaReadinessProbe.cs:15`); moving from unknown to starting leaves it in place, and ready or failed completes that same gate (`:77`, `:85`). Only the move out of failed swaps in a fresh one (`:60`), which is the case the comment above it explains.

**A project queued from the chat is reported as started even if the hand-over is dropped.** The hand-over channel is unbounded and nothing in the operator completes it (`src/Scout.Operator/OperatorServiceCollectionExtensions.cs:43`), so the write cannot be refused (`src/Scout.Operator/YouTubeOperatorPlugin.cs:107`); and the project is saved as queued first, which the worker's own poll picks up whatever the chat did (`src/Scout.Operator/OperatorWorker.cs:38`).

**A Node range with an empty alternative or an x-range selects the newest image.** In npm's range syntax an empty range, `*` and `>=x` mean any version, and `SandboxNode.Range` reproduces that (`src/Scout.Sandbox/SandboxNode.cs:221`); text outside the range alphabet or left over after the comparators are read is refused (`:215`, `:233`), so garbage still selects nothing.

---

## Desktop

**On the Analysis and Translation rows of Models the arrow keys move the focus without checking a row.** The rows are radios in a radiogroup, and the Speech rows on the same screen check on an arrow, but an Analysis or Translation choice saves the setting and restarts Scout at once, where a Speech arrow only sets a choice that Save stores; checking on each arrow press would save and restart on every key. Space, Enter or a click chooses (`onRowKey`, `ui/packages/renderer/src/app/models/models.component.ts:1620`). Locked by the key table "key x focus position" of the Models spec (`ui/packages/renderer/src/app/models/models.component.spec.ts:3198`), every arrow row asserting no write and no restart.

**The control plane launcher lets the extra environment come before its own settings.** `extraEnv` is passed by Scout's own main process, not by the user, and no production code passes it; the socket path and both tokens are written after it (`ui/packages/main/src/control-plane/launcher.ts:143`), and the host hardens the socket and requires the token whatever the environment says ([[pages/decisions/0011-backend-frontend-grpc-uds]]).

**The collapsible list tracks its rows by their text.** Its two users pass text that is unique: the GitHub list holds full repository names (`ui/packages/renderer/src/app/features/github-connect.component.ts:306`) and the Kubernetes list builds its line from the context name (`ui/packages/renderer/src/app/features/kubernetes-connect.component.ts:332`), so the `track` expression (`ui/packages/renderer/src/app/features/collapsible-list.component.ts:109`) never meets two equal rows. A caller that could repeat a row would need a key of its own; none does.

**Closing a feature's side panel does not restore the focus in the panel.** The owner does it: `closePanel` focuses the Details button of the card the panel showed (`ui/packages/renderer/src/app/features/features.component.ts:504`). Locked by the spec "closes the panel and gives the focus back to the card's Details" (`ui/packages/renderer/src/app/features/features.component.spec.ts:1344`).

**A recording that fails while it opens is released by the caller.** `open` can throw after it holds the stream and the context (`ui/packages/renderer/src/app/quick-ask/wav-recorder.ts:122`), but every caller that starts a recording stops it when the start fails (`ui/packages/renderer/src/app/speech/conversation-mode.ts:203`, `ui/packages/renderer/src/app/speech/level-meter.ts:110`, `ui/packages/renderer/src/app/quick-ask/live-listener.ts:185`), and `stop` waits for the failed start and then releases (`:111`); `start` also releases first (`:94`).

---

## Tooling and tests

**The mutation-testing skill writes `$0` in its commands.** The file is a Claude Code skill, not a shell script: the harness replaces `$0` with the skill's first argument before the text is read (`.claude/skills/stryker-project/SKILL.md`).

**The wiki skills take the first `wiki-config.md` they find.** The search is bounded to the session's scope, which here is the repository, and the repository holds exactly one (`context/wiki-config.md`).

**A spec helper waits a fixed number of rounds.** Each round awaits a zero-delay timer, which drains the whole promise queue; it is a queue drain, not a wall-clock wait.

**One test may hold many expectations.** The testing conventions have no one-assertion-per-test rule ([[pages/conventions/testing]]); the runner names the failing line.

**The review gate hook cannot be satisfied by text that only mentions the reviewer.** The hook looks for the reviewer's name as a JSON key and value (`.claude/hooks/stop-review-gate.sh:35`, `:36`); the transcript is JSON lines, where text in a message is stored with its quotes escaped, so only a real tool call writes that shape. The hook is an advisory nudge that fails open (its header) and blocks once.

**The worker publish script detects an architecture it then refuses.** On a Linux arm64 machine it names `linux-arm64` (`ui/build/scripts/publish-worker.sh:25`), which is not in the supported list (`:45`), and stops with the unsupported-architecture error that names the list (`:58`); the release matrix builds the worker for `linux-x64` ([[pages/decisions/0029-project-site-and-releases]]). The refusal is the intended outcome; the arm64 branch is dead code that could be deleted.

**The Kubernetes skill advises limits for every pod.** The line separates a request, the scheduling guarantee, from a limit, the hard cap, and says an exceeded memory limit is OOMKilled and an exceeded CPU limit is throttled, not killed (`src/Scout.Skills/skills/kubernetes.md:21`); a pod with no memory request or limit is first in line for eviction and for the kernel's out-of-memory kill, so "can be OOMKilled or evicted" is true, and "always set" is advice in a prompt, not a claim about the cluster.

---

## Open tensions, not decisions

These were reported, are not defects as the code stands, and are not settled either. They stay open on purpose and are not to be closed by citing this page.

None at the moment: the two that stood here were ruled on 2026-11-11 and are entries under Control plane and hosts.

---

## Related

[[pages/invariants/a-guard-must-be-able-to-fire]] | [[pages/conventions/tooling]] | [[pages/conventions/testing]] | [[pages/roadmap/followups]]
