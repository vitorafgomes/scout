---
paths:
  - "src/Scout.Host.ControlPlane/**"
  - "tests/Scout.ControlPlane.Tests/**"
  - "ui/packages/main/src/control-plane/**"
---

# Host.ControlPlane

Owns the transport, authentication and gRPC adapters over [[pages/services/control-plane]]. Does not listen on TCP and does not run the chat loop ([[pages/services/host-console]]).

## Rules this project must keep

- **Scout performs descriptive writes only.** Here: `PullModel` and `DeleteModel` and the start and stop calls are reachable by any holder of the desktop token, never by the worker's.
- **ADR 0032 - Features declare what they write, and the owner grants each one.** Here: granting a write and approving an action happen only through this host with the desktop token.
- **ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel.** Here: the `Operator` service belongs to the desktop alone and the whole `WorkerGate` service, every method of it, to the worker alone, by service prefix.
- **Secrets never live in source.** Here: both tokens are minted by the desktop or read from the environment, never from a file in the repository, and never logged.

The full statement of each, with the code and tests that hold it: `context/pages/services/host-control-plane/how-the-rules-are-kept.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/services/control-plane|ControlPlane]]
- [[pages/services/control-plane-contracts|ControlPlane.Contracts]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry points; gRPC services | `context/pages/services/host-control-plane/public-contract-entry-points.md` |
| Write grants, queue and log over gRPC | `context/pages/services/host-control-plane/public-contract-write-grants-queue-and-log-over.md` |
| Worker gate over gRPC; Owner controls over gRPC; Operator over gRPC; Research cleanup | `context/pages/services/host-control-plane/public-contract-worker-gate-over-grpc.md` |
| Security; Configuration and environment; External boundaries | `context/pages/services/host-control-plane/public-contract-security.md` |
| How it works | `context/pages/services/host-control-plane/how-it-works.md` |
| the evidence for a rule of this project: the code and the tests that hold it | `context/pages/services/host-control-plane/how-the-rules-are-kept.md` |
| Hot spots | `context/pages/services/host-control-plane/hot-spots.md` |
| Testing footprint | `context/pages/services/host-control-plane/testing-footprint.md` |
| Consumers | `context/pages/services/host-control-plane/consumers.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
