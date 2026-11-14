---
title: "Host.ControlPlane: Public contract: Entry points"
version: "1.1"
date: 2026-11-13
updated: 2026-11-14
changes: "v1.1 (2026-11-14): citations re-pointed."
page_type: service
status: active
description: "Read when a change touches: Entry points; gRPC services."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

## Public contract

- **Entry points:** `AddControlPlaneHost` (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:20`) and `MapControlPlaneServices` (`:49`); process entry `src/Scout.Host.ControlPlane/Program.cs:5`. The host takes and sends messages up to 16 MB (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:11`, `:39`), so a full comments view, 1000 comments of up to 2000 characters, fits above the 4 MB gRPC default; the worker's channel has the same limit ([[pages/services/host-console]]), and the tests hold them together through the real host (`tests/Scout.Host.Console.Tests/GrpcOperatorControlPlaneTests.cs:756`).

- **gRPC services:** `ProvidersGrpcService` (`src/Scout.Host.ControlPlane/Services/ProvidersGrpcService.cs:14`), `ModelsGrpcService` (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:13`), `FeaturesGrpcService` (`src/Scout.Host.ControlPlane/Services/FeaturesGrpcService.cs:38`) over `FeaturesService`, `IActionQueue`, `IWriteLog` and, for the YouTube purge, `OperatorService`, `WorkerGateGrpcService` (`src/Scout.Host.ControlPlane/Services/WorkerGateGrpcService.cs:26`) over `WriteGateService` and the worker side of `OperatorService` and `CommentService`, and `OperatorGrpcService` (`src/Scout.Host.ControlPlane/Services/OperatorGrpcService.cs:19`) over the desktop side of `OperatorService` and `CommentService`, mapped at `src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:59` and `:61`; mapping helpers `FeatureMapping` (`src/Scout.Host.ControlPlane/Services/FeatureMapping.cs:18`), `OperatorMapping` (`src/Scout.Host.ControlPlane/Services/OperatorMapping.cs:56`), `CommentMapping` (`src/Scout.Host.ControlPlane/Services/CommentMapping.cs:14`) and `ProviderMapping`. An unknown provider id on the `WatchProviderStatus` stream answers `NOT_FOUND` "Unknown provider '<id>'.", the answer the unary `Providers` methods give through `Guard` (`src/Scout.Host.ControlPlane/Services/ProvidersGrpcService.cs:78`, `:104`, `tests/Scout.ControlPlane.Tests/Integration/ProviderWatchGrpcTests.cs:18`). A feature status the mapping does not know throws `ArgumentOutOfRangeException` and never reaches the wire as `UNSPECIFIED` (`src/Scout.Host.ControlPlane/Services/FeatureMapping.cs:254`, `tests/Scout.ControlPlane.Tests/Features/FeatureMappingStatusTests.cs:12`).
