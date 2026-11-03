---
title: "ControlPlane.Contracts"
version: "1.6"
date: 2026-10-21
updated: 2026-11-03
changes: "Assembly and review: AssembleProject, ConfirmMediaRights, EditPackageMetadata and ApprovePackage on Operator; the package on Project with VideoPackage, ShortClip, PackageCheck and EditPackageMetadataRequest"
page_type: service
status: active
description: "The gRPC contracts of the control-plane host: four proto files for the desktop UI and one for the worker, compiled to client and server stubs."
source: ["src/Scout.ControlPlane.Contracts/", "tests/Scout.ControlPlane.Tests/"]
reliability: high
---

# ControlPlane.Contracts

> The wire format: four services for the desktop, one for the worker, no logic.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/decisions/0032-per-feature-write-permissions]]
- [[pages/decisions/0033-channel-operator-architecture]]
- [[pages/services/host-control-plane]]
- [[pages/services/control-plane]]
- [[pages/conventions/testing]]

## Responsibility

Holds `.proto` files and generates C# for both sides (`GrpcServices="Both"`). The project contains no hand-written `.cs` files.

## Public contract

All five services share package `scout.controlplane.v1` and C# namespace `Scout.ControlPlane.Contracts`. The proto files carry no comments; what a field means is written here.

- **Providers** (`src/Scout.ControlPlane.Contracts/protos/providers.proto:8`): `ListProviders`, `GetProviderStatus`, `StartProvider`, `StopProvider`, `RestartProvider`, `SetAutostart`, `WatchProviderStatus` (server stream).
- **Models** (`src/Scout.ControlPlane.Contracts/protos/models.proto:11`): `PullModel` (server stream of progress), `DeleteModel`, `ListModels`.
- **Features** (`src/Scout.ControlPlane.Contracts/protos/features.proto:8`): `ListFeatures`, `SetCredentials`, `Setup`, `DeleteCredentials`, `Enable`, `Disable`, `SetWriteGrant`, `ListPendingActions`, `ApproveAction`, `RejectAction`, `ListWrites` ([[pages/decisions/0032-per-feature-write-permissions]]).
  - `FeatureScopeRow.access` (`:55`): 0 read, 1 write, 2 irreversible write. `Feature` (`:59`) carries `write_class` (`:75`; 0 none, 1 descriptive, 2 executive), `granted` and the optional ISO 8601 round-trip time of the owner's grant, `granted_at` (`:77`). Every addition is a new field number, so a client built on the earlier proto still reads the message.
  - `EnableRequest.confirm_writes` (`:106`): the owner confirmed the write rows; enabling a feature with write rows without it answers `FAILED_PRECONDITION`. `SetWriteGrantRequest` (`:113`, `id`, `granted`, `confirm_writes`) grants or revokes the write permission of a feature with write rows; granting needs `confirm_writes`, revoking does not.
  - `PendingAction` (`:119`): an irreversible action waiting for the owner, times in ISO 8601, `status` one of `pending`, `approved`, `rejected`, `executed` (approved and recorded by the worker), and the owner's optional `reason` on either decision; `ApproveAction` and `RejectAction` take `ActionDecisionRequest` (`:137`, `id`, optional `reason`) and return the decided action with that reason.
  - `WriteLogEntry` (`:141`): one write a feature performed, `approved_by` `owner` or unset; `ListWritesRequest.limit` (`:152`) is the number of newest entries to return, 0 or less means 50.
- **Operator** (`src/Scout.ControlPlane.Contracts/protos/operator.proto:7`): the desktop's door to the channel operator's state ([[pages/decisions/0033-channel-operator-architecture]]): `GetMandate`, `SaveMandate`, `ListProjects`, `GetProject`, `StartProject`, `DecideClaim`, `ProduceProject`, `ReorderScenes`, `SetSceneLocked`, `RegenerateScene`, `AttachSceneMedia`, `GetMediaDirectory`, `GetReadiness`, `AssembleProject`, `ConfirmMediaRights`, `EditPackageMetadata`, `ApprovePackage` (`:8` to `:24`).
  - `Mandate` (`:29`): `objective`, `audience`, `pillars` (at most 5, each at most 80 characters), `cadence`, `language`, `voice_and_style`, `blocked_topics`, `target_metric`, `target_value`, and the optional ISO 8601 round-trip `updated_at`, set by the control plane when it saves. `SaveMandate` takes and returns it; `objective` and `language` are required.
  - `ListProjectsRequest.limit` (`:42`): 0 or less means 50, at most 200; newest first. `StartProjectRequest.topic` (`:54`): 3 to 200 characters after trimming; without the owner's YouTube write grant `StartProject` answers `PERMISSION_DENIED` with the write gate's reason.
  - `DecideClaimRequest` (`:58`): `project_id`, `claim_id`, `status` one of `supported`, `unsupported`, `waived`, and the optional `note`, required for `waived`.
  - `Project` (`:65`): `id`, `topic`, `status` (`queued`, `researching`, `scripting`, `script_ready`, `production_queued`, `producing`, `media_ready`, `assembly_queued`, `assembling`, `assembled`, `approved`, `failed`), `origin` (`desktop` or `chat`), ISO 8601 `created_at` and `updated_at`, `candidates` (`TopicCandidate`, `:80`: `title`, `reason`, `fit`), the optional `script` (`Script`, `:86`: `hook`, `sections` of `ScriptSection` `:96` `heading` and `narration`, `call_to_action`, `title_variants`, `description`, `tags`, `chapters` of `Chapter` `:101` `at` and `title`), `claims`, the optional `error`, `scenes` and the optional `package` (`:77`), unset until the owner first asks for assembly. A `Project` never carries the conversation reference a chat request came from.
  - `Claim` (`:106`): `id`, `text`, `risk`, `source_urls`, `status` (`pending` or `unsupported` from the worker; `supported`, `unsupported` or `waived` from the owner) and the optional `note`.
  - `Scene` (`:115`): `id`, `order` (the owner's order starts at 1), `narration`, `visual_prompt`, `locked`, `status` (`planned`, `generating`, `ready`, `failed`), the optional `error`, `audio_source` (`generated`, `owner`, `none`), `visual_source` (`generated`, `owner`, `text_card`), the optional `audio_file` and `visual_file`, and `duration_ms`. A file is a plain name inside the project's media directory: not empty, at most 120 characters, no folder separator, no `..`, no leading dot, and an audio extension `.mp3`, `.wav`, `.m4a`, `.ogg` or a visual one `.png`, `.jpg`, `.jpeg`, `.webp`.
  - `ProduceProject` takes `GetProjectRequest` and answers the `Project` in `production_queued`. It needs the owner's live `youtube` write grant (`PERMISSION_DENIED`); a project that is not `script_ready` or `media_ready`, or that has claims still `pending`, is `INVALID_ARGUMENT` with a sentence that names how many are pending.
  - `ReorderScenes` takes `ReorderScenesRequest` (`:130`: `project_id`, `scene_ids`, every scene of the project exactly once, else `INVALID_ARGUMENT`); `SetSceneLocked` takes `SetSceneLockedRequest` (`:135`); `RegenerateScene` takes `SceneRequest` (`:141`) and is `INVALID_ARGUMENT` on a locked scene; `AttachSceneMedia` takes `AttachSceneMediaRequest` (`:146`: `kind` `audio` or `visual`, `file_name` already copied into the project's media directory). All four answer the `Project`, need no grant, and answer `NOT_FOUND` for an unknown project or scene.
  - `GetMediaDirectory` takes `GetProjectRequest` and answers `MediaDirectory` (`:153`): the absolute `path` of the project's media directory, created when missing; an id that is not a project id is `INVALID_ARGUMENT`.
  - `GetReadiness` takes the empty `GetReadinessRequest` (`:157`) and answers `Readiness` (`:159`): `speech_available`, `images_available`, `provider`, `ffmpeg_available`, `youtube_signed_in`, `notes`, and the optional ISO 8601 `reported_at` the control plane stamps when the report arrives; before the worker reports, every flag is false, `provider` empty, `reported_at` unset and the one note "The Scout worker has not reported yet."
  - `VideoPackage` (`:169`): `status` (`assembly_queued` and `approved` set by the owner; `assembling`, `assembled`, `failed` by the worker), the optional `video_file` (`.mp4`), `shorts` (`ShortClip`, `:185`: `file` `.mp4`, `start_ms`, `duration_ms`, `hook`), the optional `thumbnail_file` (`.png` or `.jpg`), `checks` (`PackageCheck`, `:192`: `name`, `result` `pass`, `warn` or `fail`, `detail`), `rights_confirmed`, the optional ISO 8601 `approved_at`, `title`, `description`, `tags`, `duration_ms`, the optional `error` and `metadata_edited`. Every file is a plain name in the project's media directory under the scene file name rule, and must exist there when the worker reports `assembled`. Only the owner sets `rights_confirmed`, `approved_at` and `metadata_edited`; once `metadata_edited` is set the worker cannot change `title`, `description` or `tags` (`FAILED_PRECONDITION`).
  - `AssembleProject` takes `GetProjectRequest` and answers the `Project` in `assembly_queued` with a new package. It needs the owner's live `youtube` write grant (`PERMISSION_DENIED`); a project that is not `media_ready`, `assembled` or `failed`, has no scenes, or has a scene not `ready` is `INVALID_ARGUMENT`. A new assembly clears the errors, the files, the checks, `rights_confirmed` and `approved_at`, and keeps the metadata only when `metadata_edited`.
  - `EditPackageMetadata` takes `EditPackageMetadataRequest` (`:198`: `project_id`, `title`, `description`, `tags`): the title 1 to 100 characters, the description at most 5000, each tag 1 to 100 and the tags at most 500 together, after trimming, else `INVALID_ARGUMENT`; on an `assembled` or `approved` project only, it sets `metadata_edited`, and an approved package goes back to `assembled` without `approved_at`. `ConfirmMediaRights` takes `GetProjectRequest` and sets `rights_confirmed` on an `assembled` project only. Neither needs the grant.
  - `ApprovePackage` takes `GetProjectRequest` and answers the `Project` in `approved` with `approved_at`. It needs the grant; a project that is not `assembled`, rights not confirmed, a check that failed, or a claim neither `supported` nor `waived` is `INVALID_ARGUMENT` with a sentence per cause that names the count. The approval is one write-log entry, outcome `approved package`, approved by `owner`.
- **WorkerGate** (`src/Scout.ControlPlane.Contracts/protos/worker.proto:9`): the worker's only door into the control plane ([[pages/decisions/0032-per-feature-write-permissions]]). It asks whether a declared write may run, records the writes it performed, and reads and saves the operator's state; it can never grant a permission, decide an action or decide a claim. It imports `operator.proto` for the shared messages.
  - `RequestWrite` (`:10`) takes `RequestWriteRequest` (`:19`: `feature_id`, `scope`, `target`, `summary`) and answers `RequestWriteResponse` (`:26`): `decision` is exactly `allowed`, `queued` or `refused`, `reason` says why (empty when allowed), and the optional `action_id` is set only when queued.
  - `RecordWrite` (`:11`) takes `RecordWriteRequest` (`:32`: `feature_id`, `scope`, `target`, `outcome`, optional `action_id`) and answers the empty `RecordWriteResponse` (`:40`), or `FAILED_PRECONDITION` when the write could not have been permitted. The semantics are on [[pages/services/control-plane]].
  - `GetOperatorMandate` (`:12`) answers the owner's `Mandate`, empty when none is saved.
  - `SaveOperatorProject` (`:13`) takes `SaveOperatorProjectRequest` (`:44`: `project`, optional `origin_reference`) and answers the saved `Project`. An empty `project.id` creates a project of origin `chat` that keeps `origin_reference`; an existing project keeps its topic, origin and reference. It needs the owner's live `youtube` write grant (`PERMISSION_DENIED` with the gate's reason); a status, claim or scene value outside the vocabulary or an unsafe scene file name is `INVALID_ARGUMENT`; marking a claim `supported` or `waived`, changing or dropping an owner-decided claim, changing the topic or origin, adding or dropping a scene once the project has scenes, changing a scene's `order` or `locked`, changing a locked scene, or replacing or claiming the owner's media is `FAILED_PRECONDITION`; a package outside its vocabulary, with an unsafe file name, or reported `assembled` without its files is `INVALID_ARGUMENT`, and setting what only the owner sets on a package, or an assembly save on a project that is not `assembling`, is `FAILED_PRECONDITION`; a project without `package` keeps its stored one; an unknown id is `NOT_FOUND`. Every successful save is one entry in the write log, action "Scout's data folder", outcome `saved <status>`.
  - `NextQueuedProject` (`:14`) takes the empty `NextQueuedProjectRequest` (`:49`) and answers `NextQueuedProjectResponse` (`:51`): the oldest `queued` project moved to `researching`; else the oldest `production_queued` project moved to `producing`; else the oldest project left in `researching`, `scripting` or `producing` with no save for 15 minutes, moved back to `researching` or `producing`; with its optional `origin_reference`, or neither when nothing waits. It needs the same grant.
  - `GetOperatorProject` (`:15`) takes `GetProjectRequest` and answers the `Project`, owner-decided claims with their status and note, never the origin reference; `ListOperatorProjects` (`:16`) takes `ListProjectsRequest` and answers `ListProjectsResponse`, newest first, with the desktop's limit rules. Both need the same grant; an unknown id is `NOT_FOUND`.
  - `GetOperatorMediaDirectory` (`:17`) takes `GetProjectRequest` and answers the same `MediaDirectory` as the desktop's, under the same grant. `ReportReadiness` (`:18`) takes the worker's `Readiness` and answers it as stored, `reported_at` replaced by the control plane's time; it needs no grant.
- **Build wiring:** `src/Scout.ControlPlane.Contracts/Scout.ControlPlane.Contracts.csproj:21` to `:25` list the five `Protobuf` items, all `GrpcServices="Both"`; packages are `Google.Protobuf`, `Grpc.AspNetCore`, `Grpc.Tools`. The desktop client loads `providers.proto`, `features.proto` and `operator.proto` with `@grpc/proto-loader` (the proto list in `createControlPlaneClient`, `ui/packages/main/src/control-plane/client.ts`); `Scout.Host.Console` uses the generated `WorkerGate` client ([[pages/services/host-console]]).
- **Configuration keys:** none. **External boundaries:** none; this is the contract.

## How it works

```mermaid
flowchart LR
    p["protos/*.proto"] --> gen["Grpc.Tools codegen"]
    gen --> srv["server stubs (Host.ControlPlane)"]
    gen --> cli["client stubs (tests, Host.Console's WorkerGate client)"]
```

## Invariants

- [[pages/invariants/no-executive-writes]]: `StartProvider`, `StopProvider`, `PullModel` and `DeleteModel` are mutating calls on the host; adding a method here widens what the UI can make Scout do.
- [[pages/decisions/0032-per-feature-write-permissions]]: `Enable` with `confirm_writes`, `SetWriteGrant`, `ApproveAction` and `RejectAction` are the only ways to grant a write or approve an action, and only the desktop's token reaches them; the worker's token reaches `WorkerGate` alone, which can request and record but never grant or decide ([[pages/services/host-control-plane]]). No chat path calls the desktop's services.
- [[pages/decisions/0033-channel-operator-architecture]]: `SaveMandate`, `StartProject`, `DecideClaim`, `ProduceProject`, the scene edits, `AssembleProject`, `EditPackageMetadata`, `ConfirmMediaRights` and `ApprovePackage` are the desktop's alone; the worker changes operator state only through `SaveOperatorProject` and `NextQueuedProject`, and reads projects and their media directory only through `GetOperatorProject`, `ListOperatorProjects` and `GetOperatorMediaDirectory`, all under the grant; its `ReportReadiness` writes only `readiness.json`.

## Hot spots

- `src/Scout.ControlPlane.Contracts/protos/models.proto:13`: `DeleteModel` is the RPC that removes model files from the host.

## Testing footprint

No test project of its own. `tests/Scout.ControlPlane.Tests` references it and exercises the stubs through `Integration/ControlPlaneUdsIntegrationTests.cs`, `Integration/ControlPlaneBoundaryTests.cs`, `Integration/FeatureWritesGrpcTests.cs` (the grant, the queue and the log over the socket), `Integration/OperatorGrpcTests.cs` (the operator's two callers, its round trips and status codes) and `Integration/WorkerGateGrpcTests.cs` (the two callers and the exact `decision` and `reason` strings over the socket, `tests/Scout.ControlPlane.Tests/Integration/WorkerGateGrpcTests.cs:93`).

## Related

Direct project references: none. Referenced by [[pages/services/host-control-plane]] and, for the WorkerGate client, [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
