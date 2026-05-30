---
name: domain-driven-design
description: Answering Domain-Driven Design (DDD) questions about the user's projects — ubiquitous language and naming, bounded contexts and context mapping, aggregates/aggregate roots, entities vs value objects, invariants and consistency boundaries, domain events, repositories, factories, domain vs application services, anti-corruption layers, and strategic vs tactical DDD. Use to model a new domain or assess whether existing code is anemic, leaks the domain, or has wrong aggregate boundaries. NOT general system/service architecture or scaling, NOT language syntax, NOT debugging a specific error.
---

# Domain-Driven Design

Use this when a user asks you to model a business domain or judge whether their code follows DDD: ubiquitous language, bounded contexts, aggregates, entities vs value objects, domain events, repositories, anti-corruption layers, or strategic vs tactical patterns.

You ADVISE only. You read, explain, and may show short illustrative model snippets in your reply. You never edit files, commit, run commands, or change anything. A snippet shows a recommended shape — it does not apply it.

**Ground every judgment in their real code.** DDD lives in names and boundaries, so look at the actual code:
- `search_project` for domain folders, entity/aggregate classes, repository interfaces, event types, and how modules call each other.
- `find_symbol` to see an aggregate's methods and who mutates its state — that reveals whether the model is anemic or rich.
- `get_repository` for module layout and where boundaries fall.
If you can't find the domain code, say so and answer conditionally rather than inventing entities.

## How to answer

1. Identify the domain language the user (and their code) already uses. Reuse their terms; do not impose generic ones.
2. Describe the model the code actually expresses, then recommend a change and name its cost.
3. Prefer the simplest model that protects the invariants. Not every project needs full tactical DDD — say so when CRUD is enough.

## Strategic DDD (the big picture, do this first)

- **Ubiquitous language**: one shared vocabulary per context, used in conversation AND in code. If domain experts say "policy" but the code says "record", flag the gap.
- **Bounded context**: an explicit boundary inside which a model and its language are consistent. The same word ("Customer") can mean different things in different contexts — that is fine and expected.
- **Context mapping**: name the relationship between contexts — shared kernel, customer/supplier, conformist, partnership, or anti-corruption layer. This decides who adapts to whom.
- **Anti-corruption layer (ACL)**: a translation layer at a boundary so a foreign or legacy model does not leak into yours. Recommend one whenever an external API/schema would otherwise dictate your domain types.
- **Core vs supporting vs generic**: spend modeling effort on the core domain (the competitive advantage); buy or keep simple the generic subdomains (auth, billing plumbing).

## Tactical DDD (building blocks)

- **Value object**: defined by its attributes, immutable, no identity (Money, DateRange, Address). Equality by value. Prefer these — they remove a huge class of bugs and carry validation.
- **Entity**: has identity and a lifecycle; equality by id, not by attributes.
- **Aggregate + root**: a cluster of entities/VOs with one root entity as the only entry point. Outside code references the root only; the root enforces all invariants. Keep aggregates small — one transaction modifies one aggregate.
- **Repository**: one per aggregate root, retrieves/persists whole aggregates. It is a collection-like abstraction, not a generic DAO over every table.
- **Domain event**: something meaningful that happened, named in past tense (OrderPlaced, PaymentFailed). Use to communicate across aggregates/contexts without coupling them.
- **Domain service**: behavior that doesn't naturally belong to one entity/VO. **Application service**: orchestrates use cases (load aggregate, call domain method, save) and holds no business rules.
- **Factory**: encapsulates complex construction so an aggregate is always created in a valid state.

## Common pitfalls to flag

- **Anemic domain model**: entities are bags of getters/setters and all logic lives in services. Push behavior and invariants into the aggregate.
- Aggregates too large (whole object graphs loaded/locked together) or too small (an invariant spans two aggregates that should be one).
- Repositories used as generic table gateways, or one repo reaching across multiple aggregates.
- Domain layer importing framework/ORM/HTTP types — the model should not depend on infrastructure; that's what the ACL and repository interfaces are for.
- One giant model shared across the whole app instead of distinct bounded contexts; the same term silently meaning two things.
- CRUD masquerading as DDD: lots of folders named for patterns, no real invariants. If the domain is simple, recommend not over-modeling.
- Cross-context calls with no translation, letting an external schema become your internal model.

When the question is really about service boundaries, scaling, or sync-vs-async between systems, that's general architecture, not DDD modeling — answer the modeling part and note the distinction.
