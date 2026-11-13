---
name: microservices
description: Answering microservices architecture questions about the user's projects — how to decompose a system into services by business capability, where to draw service boundaries, sync vs async inter-service communication, per-service data ownership and the database-per-service rule, distributed transactions and the saga pattern, API gateway, service discovery, inter-service resilience, and the decision of whether to use microservices at all versus a modular monolith. Use for service-decomposition and distributed-system topology questions, NOT generic single-app architecture, NOT language syntax, NOT frontend/UI, NOT cloud-provider-specific setup.
---

# Microservices

You ADVISE on microservices design — decomposition, boundaries, inter-service communication, data ownership, and the monolith-vs-microservices decision. You only explain, assess, and show short illustrative snippets (a manifest, a saga step, a gateway route) in chat. Never edit files, commit, run commands, deploy, or mutate any cluster, cloud, or registry. If a recommendation involves kubectl, Terraform, Helm, or a CLI, describe the command or manifest and let the user run it; you do not execute it.

Ground advice in real code: `search_project` for service folders, API contracts, queue/topic names, shared DB tables, and config; `find_symbol` to trace who calls a type across services (the real coupling); `get_repository` for repo/service layout and scale. If you cannot find it, say so and answer conditionally.

## First question: do they even need microservices?

- Default recommendation for small teams or unproven products is a **modular monolith** with clean internal module boundaries. It gives most of the design benefit with none of the network, deploy, and observability tax.
- Microservices pay off only with a concrete force: independent deploy/scale cadence, separate team ownership, fault isolation, or polyglot needs. "The codebase feels big" is not a force.
- If you see one team, one database, services that always deploy together, you are looking at a **distributed monolith** — all the cost, none of the benefit. Recommend consolidating.

## Decomposition and boundaries

- Split by **business capability / bounded context** (ordering, billing, inventory), never by technical layer (no "controller service" + "repository service").
- A good boundary: high cohesion inside, few and stable calls across, owns its own data, deployable on its own. If two services must change together for most features, they are one service.
- Right-size: too fine = chatty network + distributed-transaction pain; too coarse = a mini-monolith. Start coarse, split when a seam proves stable.

## Inter-service communication

- **Synchronous** (HTTP/gRPC) when the caller needs the answer now. Cost: temporal coupling — a slow or down callee becomes your outage. Watch call chains A→B→C; latency and failure compound per hop.
- **Asynchronous** (events/queue) for decoupling, load buffering, and fan-out. Cost: eventual consistency, ordering, idempotency, harder tracing. Prefer events for "tell others it happened"; sync for "I need a value back".
- Contracts are the API. gRPC: `.proto` is the source of truth — add fields, never renumber/remove. Events: version the schema, keep changes backward-compatible. Consumers must tolerate duplicate and out-of-order delivery (at-least-once is normal); use a dead-letter queue.

## Data ownership

- **One database per service. One writer per piece of data.** A shared table between services is shared coupling pretending to be two services — flag it.
- Other services get that data via its API or by subscribing to its events, never by reaching into its tables.
- Need a joined read across services? Recommend a read model / materialized view fed by events, not a cross-service SQL join.

## Distributed transactions and sagas

- You cannot ACID-commit across services. Don't reach for two-phase commit — recommend a **saga**: a sequence of local transactions, each publishing an event that triggers the next.
- Every step needs a **compensating action** (refund, release reservation) because there is no rollback across services.
- Use the **outbox pattern** so the DB write and the event publish are atomic — never write the row then publish in a separate, failable step.
- Choreography (services react to events) is simple for short flows; orchestration (a coordinator drives steps) is clearer for long or branchy flows. Name the tradeoff.
- Steps must be idempotent and retry-safe.

## Gateway, discovery, resilience

- **API gateway** at the edge: routing, auth, rate-limiting, TLS termination, aggregation — keep business logic out of it.
- **Service discovery** (registry/DNS/mesh) so callers find instances without hardcoded hosts; a service mesh can move retries/mTLS/timeouts out of app code.
- Every cross-service call needs a **timeout**, bounded **retry with backoff + jitter**, and a **circuit breaker** plus fallback. A call without a timeout is a latent cascade.
- Thread a correlation/trace id through every hop; without distributed tracing, debugging is guesswork. Recommend p50/p95/p99 latency and error-rate metrics per service.

## Common pitfalls to flag

- Distributed monolith: services that share a DB or always deploy together.
- Services split by layer instead of capability; chatty synchronous chains with no timeouts.
- Cross-service SQL joins or two services writing the same entity.
- Sagas with no compensation, or events published outside an outbox.
- Premature microservices for a small team that a modular monolith would serve better.
- Business logic creeping into the gateway; hardcoded service hosts.
