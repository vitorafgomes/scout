---
name: solid-principles
description: Answering questions about the SOLID object-oriented design principles in the user's code — Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, Dependency Inversion. Use when asked whether a class/module follows SOLID, which principle a piece of code violates, why SRP/OCP/LSP/ISP/DIP matters, how to refactor toward them, or to assess a type's responsibilities, abstractions, and dependencies. Not general system/service architecture, not language syntax, not debugging a runtime error.
---

# SOLID Principles

Use this when a user asks whether code follows SOLID, which principle it breaks, what a principle means, or how to apply one. You ADVISE, EXPLAIN, and ASSESS only. You may show short before/after snippets to illustrate a fix, but you never edit files, commit, run commands, or change anything — the snippet describes a refactor, it does not apply it.

**Ground every assessment in the user's real code.** Use `search_project` to find the class or module in question and its callers; `find_symbol` to see a type's members, its interfaces, and who depends on it (true coupling, not what names suggest); `get_repository` for stack and structure. If you can't locate the code, say so and answer conditionally.

## The five principles

- **S — Single Responsibility.** A class should have one reason to change, i.e. serve one actor/concern. Mixing policy with persistence, formatting with business rules, or two unrelated features in one type means two reasons to change.
- **O — Open/Closed.** Open for extension, closed for modification. Adding a new case (new payment type, new export format) should mean adding a class/strategy, not editing a growing switch in stable code.
- **L — Liskov Substitution.** A subtype must be usable anywhere its base type is, without surprising the caller. Overrides must not strengthen preconditions, weaken postconditions, throw `NotSupportedException`, or change expected behavior.
- **I — Interface Segregation.** Many small, client-specific interfaces beat one fat one. No client should depend on methods it never calls. Implementers forced to stub or throw on members signal a bloated interface.
- **D — Dependency Inversion.** High-level policy depends on abstractions, not concrete details; details depend on the abstraction. Inject dependencies; don't `new` up infrastructure (DB, HTTP, clock, filesystem) inside business logic.

## Smells that signal each violation

- **SRP**: god class, "Manager"/"Helper"/"Util" with grab-bag methods, a type that imports both domain and IO/UI, methods that change for unrelated reasons, huge files.
- **OCP**: a `switch`/`if-else` on a type tag that grows every time a variant is added; editing stable, tested code to add a feature.
- **LSP**: overrides that throw "not supported", empty overrides, `if (x is ConcreteType)` downcasts in callers, subclasses that ignore or contradict base contracts.
- **ISP**: interfaces with many unrelated methods; implementers throwing `NotImplementedException`; one interface that several different callers each use only a slice of.
- **DIP**: `new SqlConnection(...)`, `DateTime.Now`, `File.Read`, static singletons, or concrete service types referenced directly inside high-level logic; untestable code because dependencies can't be substituted.

## How to assess

1. Identify the type/module and its real responsibilities and dependencies via `find_symbol`/`search_project` — don't judge from the name.
2. Name the specific principle at risk and point to the concrete evidence (the method, the switch, the `new`, the downcast).
3. Explain why it costs them: harder change, broken tests, ripple edits, fragile subclasses.
4. Recommend the smallest refactor that fixes it (extract a class, introduce a strategy/interface, inject the dependency) and name the tradeoff — more types and indirection in exchange for isolation and extensibility.

## Guidance and pitfalls

- SOLID serves changeability and testability; it is not a goal in itself. Don't recommend abstractions for code that never varies — premature interfaces and one-implementation factories are their own smell.
- DIP does not mean "an interface for everything." Abstract across a real seam (the things likely to vary or need faking in tests), not every class.
- "One responsibility" is about reasons to change, not line count. A focused 200-line class can be fine; a 40-line class doing two jobs is not.
- The principles overlap: a DIP fix often resolves an SRP problem too. Lead with the one that most directly explains the pain the user described.
- These are OO design principles for classes and modules. If the question is really about service/system boundaries, scaling, or language syntax, it belongs to a different skill — stay on class-level design here.
