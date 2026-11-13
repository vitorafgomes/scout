---
title: "Approve together: one decision for a set of listed actions"
version: "1.1"
date: 2026-11-10
changes: "v1.1 (2026-11-11): the owner accepted the amendment; Decisions pending became Decisions taken, and a changed group keeps Reject all."
page_type: roadmap
status: active
description: "Accepted, phase 1 started: the owner approves the issues or comments one request queued with one decision on a listed, fixed set, in the Chat's approval strip and on Approvals, and a findings card's Open issues is itself the approval; each action stays one row, one log entry and one send."
source:
  - chat
  - "context/pages/decisions/0032-per-feature-write-permissions.md"
  - "context/prototypes/desktop/Main.dc.html"
  - "context/prototypes/desktop/Review.dc.html"
reliability: medium
---

# Approve together: one decision for a set of listed actions

> The owner approves the ten issues or ten comments one request queued with one decision, after seeing the list, and each of the ten is still sent once, logged once and shown as sent or failed on its own.

**Accepted; phase 1 started on 2026-11-11.** The rule is the amendment of 2026-11-10 to [[pages/decisions/0032-per-feature-write-permissions]] (one decision for a set of actions the owner was shown), which the owner accepted on 2026-11-11 with the answers under Decisions taken. The design is drawn in `context/prototypes/desktop/Main.dc.html` (the approval strip, Approval states Group waiting to Group changed, and the findings card, Findings card states) and `context/prototypes/desktop/Review.dc.html` (Approvals, the Group states).

## Why now

The owner, 2026-11-10, translated: "we are creating 10 issues or adding 10 comments on a pull request; there should be a way to approve everything at once. Even more so if the person is sent the list beforehand of everything that will be sent: there is no reason to approve one by one afterwards." Since 2026-11-09 every GitHub issue, comment and reply waits for the owner's approval of that one action ([[pages/invariants/no-executive-writes]]), so an audit's ten chosen findings become ten identical cards in the strip and ten rows in Approvals, after the owner already chose them on the card. The cards do not show the text either: the summary carries the title of an issue and nothing of a comment (ADR 0032, the amendment's "What happens today").

## Scope

- **In scope:** a group id on a pending action and on its write-log entries; a set decision on the control plane bound to ids and payload digests; the payload shown to the desktop; the approval strip grouping a request's actions, then Approvals; a findings card's Open issues sending its issues to the control plane already approved, without the model; the tests below.
- **Out of scope:** YouTube actions (the operator's publish and comment approvals have their own screens and already approve a set of switches per click); a set across features or repositories; Discord, Slack or Teams approving anything; changing the claim, the at-most-once rule, the pacing or the failure handling of a single action.

## Target outcome

When it ships: a turn that queues ten comments on one pull request leaves one group in the strip, "10 comments on acme/shop#5", that one click approves; the ten are sent one second apart and each shows sent or failed; an audit card's "Open 6 issues on acme/shop" opens six issues with no card in Approvals; a set whose content changed after it was listed is never approved by a click made before the change.

## Status

- **As of 2026-11-10:** planned. The rule is drafted as a proposed amendment to ADR 0032 and drawn on two artboards; no code.
- **As of 2026-11-11:** the owner accepted the amendment and answered the five questions; the artboards' changed group gained Reject all. No code.
- **As of 2026-11-11, later:** phase 1 started. Commit A of three: groups exist and are listed (a group id and repository on each action and log entry, the worker's `CloseWriteGroup`, the desktop list with each waiting action's text, digest and group state); nothing decides a set yet ([[pages/history/control-plane]]).
- **As of 2026-11-11, commit B:** a set is decided. `ApproveSet`, `RejectSet` and `GetActionGroup` on the desktop token: the set is fixed by the listed ids and digests, Approve set needs a closed group of at most 50, Reject set is taken on an open, changed or over-bound group, a mismatch decides none and says which, a second identical call appends nothing, and a log that fails part way is never answered as sent; each action keeps its own claim, send and record ([[pages/history/control-plane]]). The quiet close stays at two minutes: the conversation turn has no default time limit to set it above (open question on [[pages/services/control-plane]]).
- **As of 2026-11-11, commit C:** phase 1 code complete, waiting for review. `ApproveCardIssues` queues a findings card's issues already approved on `New issues`, one group, one `approved` entry each, a second click with the same card and set answering the same actions by a key kept on each row, so an issue opens once across two clicks; the list carries a preview of each text, 4,000 characters, so any number of waiting actions fits the desktop's limit, and `GetActionText` answers the full text of one action with its digest; the quiet close is ten minutes; an Approve set covers an action whose listed text was a preview, as answer 1 decided ([[pages/history/control-plane]]).
- **As of 2026-11-11, after the review:** the sentence above that any number of waiting actions fits the desktop's limit was wrong: each action's text was bounded, the number of actions was not. One answer now carries at most 8 MiB of listed text; a row past that is listed without its title and body, says so with `text_clipped`, and its full text is one `GetActionText` call away, so the owner can always list and decide ([[pages/services/control-plane]]). For phase 3: the strip and Approvals must offer the full text for a row listed without one.
- **As of 2026-11-11, review round 1:** ten findings, each reproduced by a failing test before its fix. **A card's issue is keyed on its own**: the card's id plus the digest of its repository and trimmed title and body, on its row, replacing the key of the whole set. A later click answers an issue whose row is waiting, approved, being sent, sent or not confirmed, and queues again only one whose rows all failed or were rejected, so a repeat click retries exactly the failed ones and an issue sent is never sent again whatever the set; ten issues with one 502, a second click sends only that one and a third nothing, through the real executor. **The report stamp rule**: a group with the worker's report is closed unless an action was asked after the report, whatever quiet spells came before, and the quiet spell closes only a group without a report. Also: the card trims once and then checks, stores and sends the trimmed text; the repository rule ends at the end of the text, here and in the GitHub plugin; clicks run one at a time; a blank group id or repository counts as absent; a repeated Approve set still checks each whole digest, and decided means applied, not sent; the list reads a state only for a group with a waiting action ([[pages/history/control-plane]]).
- **Next milestone:** review of phase 1, then phase 2, the worker.
- **Blocker:** none.

## Workstreams / phases

### Phase 1: the control plane

- **Status:** started 2026-11-11; commits A (groups exist and are listed), B (deciding a set) and C (the findings card's issues, the full text of one action) done, waiting for review (2026-11-11)
- **Scope:**
  - `PendingAction` gains an optional `GroupId` and the action's `Repository`, set at enqueue; `WriteLogEntry` gains the optional `GroupId`. A group is one request, one feature and one repository: the enqueue path takes the group id the worker sends, and the queue keys a group by (group id, feature, repository).
  - The worker's write-gate request (`RequestWrite` on `WorkerGate`, `src/Scout.ControlPlane.Contracts/protos/worker.proto:11`, its message at `:40`) carries the group id; the control plane never takes one from a function argument.
  - The control plane closes a group when the worker reports the request ended (the turn's end), and the desktop's list says whether a group is closed; Approve all is offered only on a closed group. A group still open grows as its actions arrive, which is normal; a closed group takes no new action, and one that changes after the close (an action joins, a digest no longer matches) is marked changed and offers per-action decisions only.
  - `ListActions` returns, per pending action, its group id, its repository, the payload's display text (the issue's title and body, the comment's body) and the payload's SHA-256 digest; the payload itself still never reaches the summary or the log.
  - A new desktop call, `ApproveSet(group id, [(action id, digest)], reason)`, and `RejectSet(group id, [action id], reason)`: under the queue's gate, every id must be pending, in that group, with that digest, the group closed and at most 50; then each is approved with the checks of `ActionDecisionService.ApproveAsync` (the grant, asked before the grant) and one `approved` entry is appended per action, with the group id. Any mismatch approves none and answers the ids that did not match and why. A second identical call answers the same result.
  - A new desktop call for the findings card, `ApproveCardIssues(card id, repository, [(title, body)])`, modelled on `PullRequestVerdictService` (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:178`): needs the grant, queues each issue already approved on the `New issues` row with one new group id, appends one `approved` entry per issue, and answers the same actions to a second click with the same card id and set digest (`EnqueueApprovedOnceAsync`, `src/Scout.ControlPlane/Actions/IActionQueue.cs:31`, extended to a set). Since review round 1 the key is per issue, not per set: a later click answers each issue's row and queues again only an issue whose rows all failed or were rejected (`EnqueueApprovedOnceEachAsync`, `src/Scout.ControlPlane/Actions/IActionQueue.cs:47`).
  - The claim, the one-minute approval window, at-most-once and the executor are not changed.
- **Success criteria:** the control-plane tests below are green.

### Phase 2: the worker

- **Status:** not started
- **Scope:**
  - `WriteGateFilter` (`src/Scout.Llm/Guard/WriteGateFilter.cs:56`) sends the turn's group id with each queued call: the id comes from the turn context the host sets (the same ambient origin the conversation memory keys), never from the model's arguments; the repository comes from the declared `owner` and `repo` arguments.
  - The host reports the end of a turn that queued actions, so the control plane closes its group.
  - The `code-audit` skill's paragraph for the issues marker (`src/Scout.Skills/skills/code-audit.md:56`) is removed with the marker path once the card no longer asks the model; a chat request for issues keeps queuing them one call each, grouped.
- **Success criteria:** the worker tests below are green.

### Phase 3: the desktop, the approval strip first

- **Status:** not started
- **Scope:**
  - Main: `scout:actions.approveSet` and `scout:actions.rejectSet`, answered only from the main and quick-ask windows, Scout's own page, main frame (the sender check of `ui/packages/main/src/ipc/sender-check.ts:42`); `scout:github.openIssues` for the card, the same senders as the verdict channel (`ui/packages/main/src/ipc/github-verdict.ts:147`).
  - `approval-strip.component.ts` (the primary surface): two or more pending actions of one group render as one group, as the Main artboard draws it: a header with the count, the place, the feature and "Cannot be undone"; one line per action (kind, target, first line), each opening in place to the full text with its own Approve and Reject; "Show every full text"; a foot with Reject all and Approve all N, then "the other N" once some are decided; header and foot sticky inside the strip's bounded height (`ui/packages/renderer/src/app/core/approval-strip.component.ts:170`), so Approve all is never below the fold; the lines not opened marked; a group still queuing with its count so far and no Approve all; a group changed after its close, or one over 50, with per-action decisions only and the reason; after the decision each line's progress, sent, waiting or failed. A single action renders exactly as today. `PendingApprovals` groups by group id.
  - The findings card (`review-findings.component.ts`): Open issues sends the chosen issues on `scout:github.openIssues` instead of `askAs` (`ui/packages/renderer/src/app/core/review-findings.component.ts:1222`); the footer reads "Open N issues on owner/repo" and "This sends them now. Nothing else to approve.", and while sending each chosen issue shows sending, sent with its link, or failed with the reason.
  - Then Approvals (`review.component.ts`): the same group on the Waiting tab, and in History a group's per-action outcomes under one header.
  - The artboards are already drawn; the screens follow them.
  - Since phase 1 (2026-11-11): a line of the strip, or of a group on Approvals, opens to its full text by calling `GetActionText` with the action's id, since the list carries only a preview of 4,000 characters; and the `New issues` row's catalog text still says an issue is opened "when you ask for it in chat" (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:121`), which the findings card's click makes incomplete; reword it with the card.
- **Success criteria:** the desktop tests below are green and the strip matches its artboard.

## Tests that must exist

Control plane:

- A table over state x action: a group pending, partly decided, closed or still open, changed, over the bound, without the grant, with an action asked before the grant; each crossed with Approve set, Reject set, a single Approve, a single Reject; the expected queue rows and log entries per cell.
- The digest mismatch: one payload changed after the listing leaves the whole set pending, approves none, and answers that id.
- An action added to the group after the listing is not approved by the set and stays pending.
- A group still open (its turn running) refuses Approve set and accepts single decisions; once closed it accepts Approve set; an action that joins a closed group (a close bug forced in the test) marks it changed, Approve set is refused and Reject set is accepted.
- At most once: an approved set of ten, a worker that sends three and stops before recording the third, a second worker: seven sent, the third recorded as not confirmed, none sent twice.
- A set over the bound: 51 ids are refused whole and nothing is logged.
- One failure in the middle: ten approved, the fifth answered 502: nine sent, the fifth failed with the reason, the sixth to tenth still sent one second apart on the fake clock.
- A revoke in the middle of a set: the actions not yet sent are rejected with "writes were revoked" and logged; the ones sent stay sent.
- The approval log failing part way: the entries appended are handed out, the others are rejected after the one-minute window and logged.
- A double click on Approve all answers the same result and appends no second entry.
- The card's set: queued already approved on `New issues`, one entry per issue, a second click answers the same actions, nothing is queued as pending.

Worker:

- The model trying to approve: no kernel function in any turn's set decides an action (the declaration catalog), and a model call with a `group` or `groupId` argument queues the action under the turn's own group, never the one it names.
- Two repositories in one turn make two groups.

Desktop:

- Main refuses `approveSet`, `rejectSet` and `openIssues` from the operator window, a sub-frame, another page and an unknown sender before any control-plane call.
- The strip: one group for one request, a single action unchanged, Approve all absent on an open or changed group and over the bound, Reject all present on a changed group, "the other N" after a single decision, the foot visible at the strip's bounded height with 50 lines, the digests sent are those of the payloads listed.
- The card: Open issues never calls `askAs`, sends exactly the titles and bodies the preview renders, and shows each issue's outcome.

## Decisions taken

- [[pages/decisions/0032-per-feature-write-permissions]]: the amendment of 2026-11-10, one decision for a set of actions the owner was shown, *status: accepted 2026-11-11*.

The owner's answers of 2026-11-11:

1. **Listed, with the full text one click away, is enough.** Approve all does not wait for every line to be opened; the lines not opened stay marked.
2. **The bound is 50, and a group over it is refused, not split.** Over the bound the group offers per-action decisions with the reason; splitting would hide how much one click approves.
3. **The findings card's Open issues is the only approval.** The issues are not shown again in Approvals; each one is still logged.
4. **No second confirmation for a large set.** The button carries the count, is never focused by default and has Reject all beside it.
5. **A group that changed after it was listed keeps Reject all.** A rejection writes nothing; only Approve all is withdrawn, and per-action decisions stay. The artboards were changed to draw it.

## Services affected

- [[pages/services/control-plane]]
- [[pages/services/desktop-ui]]
- [[pages/services/plugin-github]]
- [[pages/services/llm]]

## Risks

- **Risk:** one click causes up to 50 irreversible writes, so a mistaken click reaches further. **Mitigation:** the bound, the count on the button, Approve all never focused by default, Reject all beside it, and an open or changed group never offering Approve all.
- **Risk:** a prompt injection hides a link below the first line of a comment the model wrote, and the owner approves without opening it. **Mitigation:** the lines not opened are marked and the full text is one click away; the owner may choose the stronger variant.
- **Risk:** showing the payload to the desktop widens where it travels. **Mitigation:** it goes only to the owner's own windows through main's sender check, never to the summary, the log or a chat adapter.
- **Risk:** the group id is taken from something the model controls. **Mitigation:** the id comes from the turn context the host sets; the test above passes a model-supplied id and checks it is ignored.
- **Risk:** a group grows while its turn still runs and the owner approves a partial list. **Mitigation:** Approve all only on a closed group, the group saying Scout is still queuing until then; the ids and digests fix the set either way.
- **Risk:** the close comes late or never (a worker that stops mid-turn), so a group never offers Approve all. **Mitigation:** the control plane closes a group whose turn reported nothing for a set time, and single decisions are always offered.

## Related

- ADRs: [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]
- Initiatives: [[pages/roadmap/code-quality-assistant]], [[pages/roadmap/prototype-migration]]
- Invariant: [[pages/invariants/no-executive-writes]]
- Convention: [[pages/conventions/desktop-prototype]]
