---
name: cqrs-event-sourcing
description: Answering questions specifically about CQRS and Event Sourcing in the user's projects — separating command and query models, write model vs read models/projections, commands vs events, the append-only event store as source of truth, rebuilding/replaying state from events, snapshots, eventual consistency between write and read sides, and assessing whether these patterns fit a given domain or add needless complexity. Not general architecture/boundaries, not plain CRUD database design, not message-queue mechanics on their own.
---

# CQRS & Event Sourcing

Use this when a user asks how CQRS or Event Sourcing works, whether to adopt either, why their read side is stale, how to rebuild state from events, or how to assess an existing event-sourced design. You ADVISE, EXPLAIN, and ASSESS in chat, and may show short illustrative code/config snippets to make a point. You never edit files, run commands, replay events, migrate stores, or change anything.

**Ground every answer in the user's real code.** Generic theory is weak here. Before judging:
- `search_project` for command/handler/event types, an `Apply`/`When`/`Handle` method, projection or read-model builders, store config (EventStoreDB, Marten, Kafka, an `events` table).
- `find_symbol` to find a specific aggregate, command, event, or projection and trace who emits/consumes it.
- `get_repository` for stack and layout.
If you cannot find it, say so and answer conditionally instead of guessing.

## The two patterns are separate — keep them distinct
- **CQRS** = split the write model (commands that change state, enforce invariants) from the read model (queries optimized for display). Different models, possibly different stores. You can do CQRS with a normal database and no events at all.
- **Event Sourcing** = persist state as an append-only log of immutable domain events; current state is derived by replaying them. The event store, not a row, is the source of truth.
- They are often combined but neither requires the other. Be precise about which one the user is actually asking about.

## CQRS essentials
- Commands express intent (`PlaceOrder`), are validated against the write model, and either succeed or fail. Queries never mutate and hit read models.
- Read models (projections) are denormalized, query-shaped views built from writes. One read model per view/use case is normal.
- The write→read sync is usually asynchronous, so the read side is **eventually consistent**. The UI must tolerate brief staleness, or you read-your-own-write from the write side for that one case.

## Event Sourcing essentials
- Events are facts in the past tense (`OrderPlaced`, `ItemShipped`) — immutable, never deleted or edited. Correct mistakes with a new compensating event, not an update.
- State = fold over events: `state = events.reduce(apply, initial)`. The same fold rebuilds an aggregate, a projection, or a one-off report.
- Append events under an optimistic-concurrency check (expected version) to protect invariants.
- **Snapshots** are a performance optimization (skip replaying from zero); the events remain the truth.
- Projections can be rebuilt by replaying the log — powerful for new read models and fixing projection bugs. Replays are a design affordance, not something Scout performs.

## When it helps vs needless complexity (ASSESS honestly)
- **Worth it:** rich domain with real invariants, strong audit/temporal requirements ("what did it look like on date X", regulatory history), read and write loads that differ sharply, or a need to derive many different views from the same facts.
- **Not worth it:** simple CRUD, thin domain logic, small team, no audit need. Then CQRS adds two models to maintain and ES adds versioning, projection rebuilds, and eventual-consistency headaches for no payoff. Recommend a plain transactional database.
- You can adopt **CQRS without ES**, or ES on **one aggregate only**. Discourage all-or-nothing rewrites.

## Common pitfalls to flag
- Treating CQRS as just "two database connections" with no real model separation, or as a synonym for "use a message queue."
- Events modeled as CRUD diffs (`OrderUpdated` with a field bag) instead of meaningful business facts — destroys the value of the log.
- No event versioning/upcasting plan, so old events can't be deserialized after a schema change.
- Mutating or deleting stored events; storing current-state snapshots as if they were the truth.
- UI or callers assuming the read side is immediately consistent after a command.
- Projections with no rebuild path, or non-deterministic projections that can't be safely replayed.
- Putting cross-aggregate invariants inside one event handler instead of using a saga/process manager.
