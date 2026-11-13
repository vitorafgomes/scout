---
title: "ControlPlane.Contracts"
version: "1.32"
date: 2026-10-21
updated: 2026-11-12
changes: "Scope row summary (2026-11-12): FeatureScopeRow.summary, field 4; features.proto citations re-pointed by content.."
page_type: service
status: active
description: "The gRPC contracts of the control-plane host: four proto files for the desktop UI and one for the worker, compiled to client and server stubs."
source: ["src/Scout.ControlPlane.Contracts/", "tests/Scout.ControlPlane.Tests/"]
reliability: high
---

# ControlPlane.Contracts

> The wire format: four services for the desktop, one for the worker, no logic.

## Responsibility

Holds `.proto` files and generates C# for both sides (`GrpcServices="Both"`). The project contains no hand-written `.cs` files.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/control-plane-contracts.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/control-plane-contracts/public-contract-all-five-services-share-package\|Public contract: All five services share package scout.controlplane.v1]] | All five services share package scout.controlplane.v1; Providers; Models; Features | 8154 |
| [[pages/services/control-plane-contracts/public-contract-operator\|Public contract: Operator]] | Operator | 36196 |
| [[pages/services/control-plane-contracts/public-contract-workergate\|Public contract: WorkerGate]] | WorkerGate; Build wiring; Configuration keys | 11316 |
| [[pages/services/control-plane-contracts/how-it-works\|How it works]] | How it works | 567 |
| [[pages/services/control-plane-contracts/hot-spots\|Hot spots]] | Hot spots | 476 |
| [[pages/services/control-plane-contracts/testing-footprint\|Testing footprint]] | Testing footprint | 1068 |

## Related

Direct project references: none. Referenced by [[pages/services/host-control-plane]] and, for the WorkerGate client, [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
