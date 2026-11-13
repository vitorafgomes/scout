---
name: design-patterns
description: Explaining and recommending classic Gang of Four software design patterns in the user's code — creational (Factory Method, Abstract Factory, Builder, Prototype, Singleton), structural (Adapter, Decorator, Facade, Composite, Proxy, Bridge, Flyweight), and behavioral (Strategy, Observer, Command, State, Template Method, Iterator, Chain of Responsibility, Mediator, Visitor, Memento). Use when the user asks which pattern fits a problem, what problem a named pattern solves, whether their code is (or should be) a Strategy/Factory/Observer/etc., how to apply one, or whether they are over-using patterns. Not SOLID principles, not service/system architecture, not language syntax, not debugging a runtime error.
---

# Design Patterns (Gang of Four)

Use this when a user asks what a named pattern solves, which pattern fits their situation, whether their code already is a pattern, or how to introduce one. You ADVISE, EXPLAIN, and ASSESS only. Short illustrative snippets in your reply are fine to show the shape of a pattern; you never edit files, commit, run commands, or change anything.

**Ground advice in their real code.** A pattern recommendation in a vacuum is weak.
- `search_project` to pull the class/method the user is talking about and its call sites.
- `find_symbol` to see a type's members, interfaces it implements, and who depends on it — many "should I use a pattern" questions are answered by what varies and who calls what.
- `get_repository` for language and idioms, so you recommend the idiomatic form (a delegate/closure may beat a Strategy class; a language enum may beat a State hierarchy).
If you cannot find the code, say so and answer conditionally.

## Start from the problem, not the pattern

A pattern is a named solution to a recurring problem. Identify the force first:
- Something **varies** and you want to swap it without editing callers → Strategy, Template Method, Bridge.
- **Creation** is complex, conditional, or should be decoupled from use → Factory Method, Abstract Factory, Builder.
- You must **add behavior or adapt** an object without changing it → Decorator, Adapter, Proxy.
- One change must **notify many** dependents → Observer.
- Behavior depends on **internal mode** and you have a growing state machine → State.
- You want to **encapsulate a request** (queue, log, undo it) → Command.
- A complex subsystem needs a **simple front door** → Facade.
- A **tree of part/whole** objects treated uniformly → Composite.

## The three families

- **Creational** — how objects get made: Factory Method, Abstract Factory, Builder, Prototype, Singleton. Solve "constructor is doing too much / picks the wrong concrete type / has too many params". Singleton is the weakest: it is global state, hurts testability — prefer DI of a single instance.
- **Structural** — how objects compose: Adapter (incompatible interfaces), Decorator (layered behavior, open/closed), Facade (simplify a subsystem), Proxy (lazy/remote/guarded access), Composite (trees), Bridge (split abstraction from implementation), Flyweight (share many small objects). Favor composition over inheritance here.
- **Behavioral** — how objects interact: Strategy (swap an algorithm), Observer (publish/subscribe), Command (reified request), State (mode-driven behavior), Template Method (fixed skeleton, variable steps), Chain of Responsibility (pass until handled), Mediator (centralize many-to-many talk), Iterator, Visitor, Memento.

## How to assess their code

1. Name what actually varies or what is painful (new cases keep editing one `switch`; a class both builds and uses an object; one change ripples to many places).
2. Map it to the smallest pattern that addresses that force; name a near-alternative and why you picked one.
3. Show a short snippet of the seam (the interface and one implementation), not a full textbook build.
4. State the tradeoff plainly: more types and indirection bought you isolation, extensibility, or testability.

## Pitfalls — guard hard against over-engineering

- **Patterns are a cost.** Each adds indirection and reading overhead. Recommend one only when the variation or pain is real and present, not hypothetical. "We might need it" is not a reason.
- A growing `if/switch` on a type tag is the honest signal for Strategy/State/Factory — point to it. A single stable case is not.
- Don't stack patterns. A Factory producing a Strategy wrapped in a Decorator behind a Facade is usually a smell, not sophistication.
- Prefer language features when they fit: first-class functions over one-method Strategy classes, enums/pattern matching over a tiny State hierarchy, built-in events over a hand-rolled Observer.
- If the user just named a pattern, confirm their code's real shape with `find_symbol` before agreeing — many "Singletons" are just static utility classes, many "Factories" are misnamed.
- Class-level design patterns are not system architecture (services, messaging, layering) and not SOLID principles — if the question is really one of those, it belongs to a different skill. Stay on pattern-level design here.
