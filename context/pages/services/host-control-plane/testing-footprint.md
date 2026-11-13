---
title: "Host.ControlPlane: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Host.ControlPlane service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

## Testing footprint

Covered by `tests/Scout.ControlPlane.Tests` (611 test attributes in total, 42 files, counted 2026-11-09): `Security/SessionTokenTests.cs`, `Security/TokenAuthInterceptorTests.cs` (the role table per method), `Security/CallerTokensTests.cs` (equal tokens, a missing worker token and its one warning), and `Integration/` tests that start the host on a Unix socket (`ControlPlaneUdsIntegrationTests.cs`), check the gRPC boundary (`ControlPlaneBoundaryTests.cs`), and drive the write confirmation, `SetWriteGrant`, the queue and the log over the socket (`FeatureWritesGrpcTests.cs`), the cap on `ListWrites` (`WriteLogLimitGrpcTests.cs`), the status watch of an unknown provider (`ProviderWatchGrpcTests.cs`), the two callers of the worker gate (`WorkerGateGrpcTests.cs`) and of the operator (`OperatorGrpcTests.cs`; the test host pins a desktop token and, unless a test asks otherwise, a worker token, opens a channel with either, and exposes its services so a test can seed the queue and the log). `Transport/` covers the native stat on a real socket, a real file and a missing path (`NativeUnixTests.cs`), its offsets per architecture, the self-test's file-type check and a stat that fails under a regular file (`NativeUnixStatLayoutTests.cs`), the socket path and its pre-flight (`ControlPlaneEndpointTests.cs`), and the hardening of the socket and the stop when it fails (`SocketHardeningTests.cs`). `Features/FeatureMappingStatusTests.cs` covers the status mapping.
