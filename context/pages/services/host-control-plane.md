---
title: "Host.ControlPlane"
version: "1.33"
date: 2026-10-21
updated: 2026-11-12
changes: "Started entries (2026-11-12, issue #323): RequestWrite goes through RequestToRunAsync, so a reversible write is in the write log as started before the worker runs it and the allowed answer carries that entry's id, a request whose entry cannot be written failing; ListWrites reads twice the limit and."
page_type: service
status: active
description: "ASP.NET gRPC host that serves the control-plane domain and the channel operator's state to the desktop UI, and the write gate and the operator's worker methods to the worker, over a Unix socket or named pipe, guarded by one role-scoped token per caller."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

# Host.ControlPlane

> The local door the desktop app and the worker knock on: gRPC over a user-only socket, each with its own token that opens only its own rooms.

## Responsibility

Owns the transport, authentication and gRPC adapters over [[pages/services/control-plane]]. Does not listen on TCP and does not run the chat loop ([[pages/services/host-console]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/host-control-plane.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/host-control-plane/public-contract-entry-points\|Public contract: Entry points]] | Entry points; gRPC services | 2732 |
| [[pages/services/host-control-plane/public-contract-write-grants-queue-and-log-over\|Public contract: Write grants, queue and log over gRPC]] | Write grants, queue and log over gRPC | 12190 |
| [[pages/services/host-control-plane/public-contract-worker-gate-over-grpc\|Public contract: Worker gate over gRPC]] | Worker gate over gRPC; Owner controls over gRPC; Operator over gRPC; Research cleanup | 11792 |
| [[pages/services/host-control-plane/public-contract-security\|Public contract: Security]] | Security; Configuration and environment; External boundaries | 3009 |
| [[pages/services/host-control-plane/how-it-works\|How it works]] | How it works | 3061 |
| [[pages/services/host-control-plane/how-the-rules-are-kept\|How the rules are kept]] | the evidence for a rule of this project: the code and the tests that hold it | 10273 |
| [[pages/services/host-control-plane/hot-spots\|Hot spots]] | Hot spots | 838 |
| [[pages/services/host-control-plane/testing-footprint\|Testing footprint]] | Testing footprint | 1866 |
| [[pages/services/host-control-plane/consumers\|Consumers]] | Consumers | 8984 |

## Related

Direct project references: [[pages/services/control-plane-contracts]], [[pages/services/control-plane]]. Map: [[pages/architecture/project-map]]. Consumer: the desktop launcher and client in the Consumers section above, phases 1 and 3 of [[pages/roadmap/control-plane-client]]; see [[pages/services/desktop-ui]]. Dated changes: [[pages/history/host-control-plane]].
