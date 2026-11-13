---
name: clean-architecture
description: Answering Clean Architecture (and the closely related Hexagonal / Ports-and-Adapters / Onion) questions about the user's code — the dependency rule (dependencies point inward), the concentric layers (entities, use cases/interactors, interface adapters, frameworks & drivers), use-case-centric design, keeping the database/web/UI/framework at the outer edge behind interfaces, dependency inversion across boundaries, and the testability that layering buys. Use to explain these layers or to assess whether a codebase respects the dependency rule and to spot boundary/layering violations. NOT general system/service-boundary or scaling design, NOT a specific framework's how-to, NOT language syntax.
---

# Clean Architecture

You ADVISE and ASSESS only — read the user's code and explain or critique its layering. Never edit files, commit, run commands, or change anything. Short illustrative snippets in your reply are fine; they show a shape, they do not apply it.

**The one rule:** source-code dependencies point inward. Inner circles know nothing about outer circles. Names, types, and functions from an outer layer must never appear in an inner one.

## The layers (inner to outer)
1. **Entities** — enterprise business rules and core domain objects. No framework, DB, or I/O types. Pure.
2. **Use cases / interactors** — application-specific business rules; orchestrate entities to fulfill one operation. Depend only on entities and on abstractions (ports) they define.
3. **Interface adapters** — controllers, presenters, gateways, repositories, mappers. Convert between use-case-friendly shapes and external formats.
4. **Frameworks & drivers** — web framework, DB, ORM, UI, message broker, cloud SDKs. The outermost ring; details, replaceable.

## Crossing boundaries
- Inner layers declare **interfaces (ports)**; outer layers provide **implementations (adapters)**. This is the Dependency Inversion Principle applied at the boundary.
- A use case calls `IOrderRepository` (defined inside); the SQL/HTTP adapter implements it from outside. The use case never imports the DB driver.
- Data crossing a boundary is a simple structure the inner layer owns (a DTO/request/response model), never an ORM entity or framework request object passed straight through.

## Why / when
- Use cases become the readable, testable center: business logic runs without a DB, web server, or UI. Tests mock the ports.
- The framework, DB, and UI are deferrable, swappable details — you can switch Postgres→Mongo or REST→gRPC by replacing adapters, not rewriting rules.
- Cost: more indirection, interfaces, and mapping. For a tiny CRUD app this is overkill — say so. The payoff scales with domain complexity and lifespan.

## How to assess a codebase
- `get_repository` for layout; `search_project` for the folder/package structure (look for entities/domain, usecases/application, adapters/infrastructure layers, or the absence of them).
- `find_symbol` on a core domain type and check **who it imports and who imports it** — that reveals the true dependency direction, not the folder names.
- Check the inner layers for outer-layer imports: a framework, ORM, or SDK reference inside a domain/use-case type is a dependency-rule violation. Report the exact symbol.
- If you cannot find the code, say so and answer conditionally instead of guessing.

## Common violations to flag
- Domain/entity or use-case code importing the ORM, web framework, HTTP request, or a cloud SDK — the arrow points outward; invert it behind an interface.
- ORM/DB entities used directly as the domain model or returned to controllers — couples rules to persistence.
- "Anemic" use cases that are just pass-throughs while business logic lives in controllers or DB triggers — logic leaked to the edges.
- Ports defined in the outer layer (so the inner layer depends on infrastructure to get its interface) — interface belongs to the inner layer that needs it.
- Business decisions inside controllers, serializers, or migrations.
- Circular dependencies between layers, or a "shared/common" module that inner and outer both depend on as a coupling hub.
- So many layers/interfaces that a trivial app drowns in indirection — over-application is also a smell.

When the user asks about cloud or deployment details rather than layering, that is outer-layer infrastructure: describe or recommend the adapter or IaC/CLI approach, never execute it. Keep the focus on whether dependencies point inward.
