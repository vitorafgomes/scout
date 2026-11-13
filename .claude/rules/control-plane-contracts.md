---
paths:
  - "src/Scout.ControlPlane.Contracts/**"
  - "tests/Scout.ControlPlane.Tests/**"
---

# ControlPlane.Contracts

Holds `.proto` files and generates C# for both sides (`GrpcServices="Both"`). The project contains no hand-written `.cs` files.

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: `StartProvider`, `StopProvider`, `PullModel` and `DeleteModel` are mutating calls on the host; adding a method here widens what the UI can make Scout do.
- [[pages/decisions/0032-per-feature-write-permissions]]: `Enable` with `confirm_writes`, `SetWriteGrant`, `ApproveAction` and `RejectAction` are the only ways to grant a write or approve an action, and only the desktop's token reaches them; the worker's token reaches `WorkerGate` alone, which can request and record but never grant or decide ([[pages/services/host-control-plane]]). No chat path calls the desktop's services.
- [[pages/decisions/0033-channel-operator-architecture]]: `SaveMandate`, `StartProject`, `DecideClaim`, `ProduceProject`, `DelegateProject`, the scene edits, `EditSceneText`, `RegenerateNarration`, `EditScenePicturePrompt` and `RegenerateScenePicture` among them, the presenter's `RequestPresenter`, `UsePresenterVersion` and `RemovePresenterVersion`, `AssembleProject`, `EditPackageMetadata`, `ConfirmMediaRights`, `ApprovePackage`, `PublishPackage`, `DecideProposal`, `StartExperiment`, `AdoptExperimentWinner`, `RequestCommentSync`, `DecideComment`, `ApproveReply` and `DecideIdea` are the desktop's alone; the worker changes operator state only through `SaveOperatorProject`, `NextQueuedProject`, `SaveComments` and, for the owner's requests in a chat, the ten `Chat` rpcs, each under the grant and logged via chat when it changed the project, and reads projects and their media directory only through `GetOperatorProject`, `ListOperatorProjects` and `GetOperatorMediaDirectory`, and the comments only through `GetComments`, all under the grant, and the voice samples directory only through `GetVoiceSamplesDirectory`, with no grant, because the samples are Scout's own data; its `ReportReadiness` writes only `readiness.json`, and `NextApprovedAction` hands it only actions the owner approved in the desktop.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]]
- [[pages/services/host-control-plane|Host.ControlPlane]]
- [[pages/services/control-plane|ControlPlane]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| All five services share package scout.controlplane.v1; Providers; Models; Features | `context/pages/services/control-plane-contracts/public-contract-all-five-services-share-package.md` |
| Operator | `context/pages/services/control-plane-contracts/public-contract-operator.md` |
| WorkerGate; Build wiring; Configuration keys | `context/pages/services/control-plane-contracts/public-contract-workergate.md` |
| How it works | `context/pages/services/control-plane-contracts/how-it-works.md` |
| Hot spots | `context/pages/services/control-plane-contracts/hot-spots.md` |
| Testing footprint | `context/pages/services/control-plane-contracts/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
