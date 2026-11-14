---
title: "Host.ControlPlane - history"
version: "1.1"
date: 2026-11-09
updated: 2026-11-14
changes: "v1.1 (2026-11-14): the package's assembly, save and confirmation times and the thumbnail description."
page_type: reference
status: artefact
description: "What changed in the control-plane host and when."
source:
  - chat
  - src/Scout.Host.ControlPlane/Transport/NativeUnix.cs
  - src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs
  - src/Scout.Host.ControlPlane/Program.cs
  - src/Scout.Host.ControlPlane/Services/ProvidersGrpcService.cs
  - src/Scout.Host.ControlPlane/Services/FeaturesGrpcService.cs
  - src/Scout.Host.ControlPlane/Services/FeatureMapping.cs
  - tests/Scout.ControlPlane.Tests/Transport/NativeUnixStatLayoutTests.cs
  - tests/Scout.ControlPlane.Tests/Transport/SocketHardeningTests.cs
  - tests/Scout.ControlPlane.Tests/Integration/ProviderWatchGrpcTests.cs
  - tests/Scout.ControlPlane.Tests/Integration/WriteLogLimitGrpcTests.cs
  - tests/Scout.ControlPlane.Tests/Features/FeatureMappingStatusTests.cs
reliability: high
---

# Host.ControlPlane - history

> Dated changes to [[pages/services/host-control-plane]]; the service page states how it works now.

## 2026-11-14 - Assembly: when the package was assembled, saved and confirmed, and what its thumbnail shows

- **Asked for:** the four owed features of the Assembly artboard ([[pages/roadmap/prototype-migration]], Operator pages): "Assembled today at 14:12", "Saved today at 14:20", "Confirmed today at 14:31" and the thumbnail described in its alt text. The description is what was asked of the picture, never its prompt (the owner's decision of 2026-11-14).
- **What changed:** `OperatorMapping` carries the package's three times as ISO 8601 and its thumbnail description both ways, each left unset on the wire when null and read as null when absent.
- **Tests:** `OperatorGrpcTests.A_Package_Maps_To_The_Wire_And_Back_Field_For_Field_With_Unset_Optionals_For_Absent_Values`.

## 2026-11-09 - Five findings of Scout's own audit, on the socket and the gRPC adapters

- **Asked for:** the findings Scout's audit of its own repository filed as issues #182 and #184 to #187, each confirmed against the code before the fix; #183, the limit of the operator's project list, was refuted, because `OperatorService` already defaults it to 50 and caps it at 200.
- **What changed:**
  - **Stat offsets per architecture (#185).** The Linux stat read `st_mode` at 24 and `st_uid` at 28 on every architecture, which is the x86_64 layout; arm64 has them at 16 and 24, so there the host read the owner as the mode and the group as the owner, and refused its own stale socket as "not a socket" on every restart. The offsets now follow the process architecture, and any other architecture is refused. The startup self-test also requires its probe to read as a regular file: with the owner check alone, a machine where uid and gid are equal passed the self-test with the wrong offsets.
  - **A socket that cannot be hardened stops the host (#187).** A failed chmod of the socket was caught, logged as a warning, and the host kept serving. `HardenSocket` also skipped the chmod without a word when the stat did not find the socket. Now `HardenSocket` throws in that case, and any failure to harden is logged as Critical, sets exit code 1 and stops the application. Throwing from the `ApplicationStarted` callback would not have stopped the host, so the callback calls `HardenSocketOrStop`, which takes the lifetime. The socket sits in a directory only its owner can enter and every call needs a token, so the old behaviour lost one layer of protection, never the only one.
  - **An unknown provider on the status watch is NOT_FOUND (#186).** `WatchProviderStatus` mapped the provider id before its `try`, so an unknown id reached the caller as `UNKNOWN` "Exception was thrown by handler." The mapping moved inside the `try`, and the answer is the one the unary methods give.
  - **ListWrites answers at most 200 entries (#182).** Any positive `limit` went to the write log as it came. The caller is the owner, with the desktop token, and the 16 MB message limit already bounded the reply, so this was an inconsistency with the project list, never an exposure.
  - **An unknown feature status throws (#184).** The status mapping answered `UNSPECIFIED` for a status it did not know, while the mapping of a queued action's status throws. Both throw now. The four statuses that exist are all mapped, so nothing reaches the new arm today.
- **Tests:** `NativeUnixStatLayoutTests` (the two layouts from a hand-built `struct stat`, an architecture without offsets, the self-test on a probe read at the wrong offsets, and a stat under a regular file, which is `Unavailable` and makes the pre-flight refuse to bind), `SocketHardeningTests` (Critical, exit code and `ApplicationStopping` on a failed hardening, nothing on a successful one, mode 0600 on a real bound socket, and the throw on a missing one), `ProviderWatchGrpcTests` and `WriteLogLimitGrpcTests` over the real socket host, `FeatureMappingStatusTests`. `OperatorServiceTests.A_Limit_Of_Zero_Means_Fifty_And_No_More_Than_Two_Hundred_Are_Listed` is the test that already held the refuted finding.
- **Not covered:** no test runs the arm64 layout against a real arm64 kernel; the layout test builds the buffer by hand, and CI has no Linux arm64 runner. No test starts the real host process with a socket it cannot harden; the stop is tested on `HardenSocketOrStop` with a real `ApplicationLifetime`.

## Version notes: `pages/services/host-control-plane.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Started entries (2026-11-12, issue #323): RequestWrite goes through RequestToRunAsync, so a reversible write is in the write log as started before the worker runs it and the allowed answer carries that entry's id, a request whose entry cannot be written failing; ListWrites reads twice the limit and lists through AsListed; citations re-pointed by content. Earlier: Scope row summary (2026-11-12): FeatureScopeRow gains summary as field 4, copied from the catalog by the mapping; citations re-pointed by content. Earlier: Presenter versions by id (2026-11-11): UsePresenterVersion and RemovePresenterVersion pass version_id, ToWire sets each version's id, and the NOT_FOUND of a version that is no longer there is pinned over the socket; citations into the client re-pointed by content. Earlier: Local models (2026-11-11): Models and the start and stop are ruled a reviewed local write on the desktop token by ADR 0032's amendment, logged to this host's log, which the desktop discards today. Earlier: Performance review of approve together (2026-11-11): ListPendingActions and GetActionGroup bound the listed text of one answer to 8 MiB, a row past it listed without its text and with text_clipped; citations into FeatureMapping and FeaturesGrpcService re-pointed by content. Earlier: Approve together, phase 1, commit C (2026-11-11): the desktop's GetActionText, the full text of one action with its digest, and ApproveCardIssues, a findings card's issues queued approved through CardIssuesService; the list's body a preview of 4,000 characters; citations into FeaturesGrpcService re-pointed. Earlier: Approve together, phase 1 (2026-11-11): the worker's RequestWrite with a group and CloseWriteGroup, the listed action's text, digest, group state and text_clipped, and the desktop's ApproveSet, RejectSet and GetActionGroup; citations into FeaturesGrpcService, FeatureMapping, ActionDecisionService and WorkerGateGrpcService re-pointed by content. Earlier: ControlJob for the desktop and NextJobControl and SettleJobControl for the worker, the owner's controls on a running audit or test run (2026-11-10). Earlier: Audit findings (2026-11-09): the Linux stat offsets per architecture and a self-test that also checks the file type; a socket that cannot be hardened stops the host with exit code 1; ListWrites answers at most 200 entries; an unknown provider on the status watch answers NOT_FOUND; a feature status the mapping does not know throws; FeaturesGrpcService, ControlPlaneEndpoint and Program citations re-pointed by content; the test footprint recounted. Earlier: Approvals lane (2026-11-08): approve and reject go through ActionDecisionService, which refuses an approval without the grant, rejects one asked before it and logs every decision; the grant-clearing rejections are logged; ListPendingActions lists approved unclaimed actions; CheckActionGrant and signed_as on the worker gate; citations re-pointed by content. Earlier: every call that clears a write grant (revoke, Disable, DeleteCredentials, a failed Enable) rejects the feature's actions waiting in Approvals or approved and unclaimed with writes were revoked; FeaturesGrpcService citations re-pointed by content (2026-11-08). Earlier: the chat and quick-ask approval cards call ListPendingActions, ApproveAction and RejectAction through main (2026-11-07). Earlier: RemovePresenterVersion on the Operator enumeration, the worker's denied Operator methods counted as the thirty-two the two tests list, and its socket round trip; OperatorGrpcService.cs and OperatorGrpcTests.cs references re-pointed by content. Presenter rpcs: the five Operator rpcs of the presenter and the scene pictures and the three Chat rpcs named with their lines, the Chat rpcs counted as ten and the WorkerGate methods as twenty-three. Merge of the channel operator and the live assistant. Channel operator: Phase 8c-A: references into OperatorGrpcService.cs, OperatorMapping.cs and OperatorGrpcTests.cs re-pointed by content. Phase 8d review: the whole WorkerGate service is the worker's by prefix, the role table's seven operator methods named, and the test footprint recounted. Phase 8d: the Chat rpcs of WorkerGate, worker token only, under the grant and logged via chat on the control plane, denied to the desktop token; WorkerGateGrpcService and OperatorGrpcTests references re-pointed by content. Live assistant: ListFeatures order lists youtube and desktop.
