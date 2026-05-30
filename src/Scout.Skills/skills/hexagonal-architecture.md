---
name: hexagonal-architecture
description: Answering questions about hexagonal architecture (ports and adapters) in the user's projects — isolating a pure domain core from infrastructure, defining ports as interfaces, the difference between driving/primary adapters (HTTP, CLI, UI) and driven/secondary adapters (DB, queue, HTTP client), dependency inversion at the boundary so the core depends on no framework, swapping real adapters for fakes/in-memory in tests, and assessing whether an existing ports-and-adapters design keeps the core clean. Use only for ports/adapters/hexagonal questions — not general architecture, not layered/clean-architecture-only, not microservice splitting, not a specific bug.
---

# Hexagonal Architecture (Ports and Adapters)

Use this when a user asks about hexagonal / ports-and-adapters design: keeping the domain core framework-free, what a port is, driving vs driven adapters, where to put interfaces, or whether their boundary is actually isolating the core.

You ADVISE only. You read, explain, and assess; you never edit files, commit, run commands, or deploy. Short illustrative interfaces or sketches in your reply are descriptions of a recommendation, not changes applied to anything.

**Ground every assessment in their actual code.** Before judging:
- `search_project` for the domain/core folder, interface definitions, and where infrastructure (DB clients, HTTP, framework attributes) is imported.
- `find_symbol` to see who implements and who consumes a port interface — that reveals which way dependencies actually point.
- `get_repository` for stack and layout.
If the relevant code is not found, say so and answer conditionally.

## Core idea

- The **domain core** holds business logic and entities. It must depend on nothing external — no ORM, no web framework, no SDK, no I/O. If you see `using`/`import` of a DB or HTTP library inside the core, that is the primary smell.
- A **port** is an interface owned by the core that names a capability the core needs or offers. The core talks only to ports.
- An **adapter** implements a port (or calls one) and lives outside the core. Adapters know about frameworks; the core never knows about adapters.
- **Dependency inversion at the boundary:** the core defines the port interface; the adapter depends on the core, never the reverse. Dependencies always point inward toward the domain.

## Driving vs driven

- **Driving / primary (left side):** something that triggers the application — HTTP controller, CLI, message consumer, UI. It calls an *inbound* port (often an application/use-case interface) implemented by the core.
- **Driven / driven secondary (right side):** something the application uses — database repository, queue publisher, email/SMS sender, external HTTP client. The core defines the *outbound* port interface; the adapter implements it.
- Mnemonic: driving adapters *call into* the core; the core *calls out to* driven adapters through ports it owns.

## Why and when

- Use it when business logic is valuable and long-lived, infrastructure is likely to change, or you want to test logic without a DB or network.
- The payoff is testability and swappability: run the core against in-memory fakes; replace Postgres with another store by writing a new adapter, not by touching the core.
- For a small CRUD app with no real domain logic, full hexagonal can be overkill — say so and suggest a lighter layering.

## Testing the seam

- Core tests should construct the core with **fake/in-memory implementations** of its outbound ports — no DB, no HTTP. Fast and deterministic.
- Each real adapter gets its own narrow integration test against the real infra.
- If testing the core requires spinning up a database, the boundary is leaking — point that out.

## What to look for when assessing

- Does the core import any framework or I/O library? It should not.
- Are port interfaces defined in the core (good) or in the infrastructure layer (inverted dependency, bad)?
- Do use cases depend on concrete repositories/clients instead of interfaces? That defeats the pattern.
- Is there a fat "service" that mixes HTTP parsing, business rules, and SQL in one place? No real boundary exists.
- Are DTOs/framework models leaking into the core as domain types? The adapter should map them.

## Common pitfalls to flag

- Anemic core: entities are bare data and all logic sits in adapters/services — nothing to isolate.
- Ports defined where the adapters live, so the core ends up depending outward.
- One giant port interface instead of small, capability-focused ones.
- Mapping skipped: persistence or wire models used directly as domain objects.
- Over-engineering: ports and adapters wrapping trivial code with no domain logic.
