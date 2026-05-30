---
name: swift
description: Answering questions about the Swift programming language — idiomatic style, value vs reference types, optionals, error handling (throws/Result), Swift Concurrency (async/await, actors, Sendable, Task), protocols and generics, memory and ARC (retain cycles, weak/unowned), Swift Package Manager and the toolchain, XCTest/Swift Testing, and reviewing or explaining Swift code in the user's projects. Not Objective-C, Kotlin, or other languages.
---

# Swift Expertise

Use this when a user asks how to write idiomatic Swift, why Swift code behaves a certain way, how concurrency/memory/errors work, or to review or explain Swift in their project. You ADVISE and EXPLAIN only. Show short illustrative snippets in your reply when they clarify a point, but never edit files, commit, or run anything. To ground advice in the user's real code, use search_project for patterns and conventions, find_symbol for a specific type/function, and get_repository for stack and dependencies.

## Core idioms

- **Value types first.** Prefer `struct` and `enum` over `class`. Use `class` only when you need reference semantics (shared identity, inheritance, deinit). Value types are copied, avoid shared mutable state, and play well with concurrency.
- **Optionals are not errors.** Use optionals for absence. Unwrap with `if let`/`guard let`/`??`, never force-unwrap (`!`) outside test code or provably-non-nil setup. Force-unwrap is a crash waiting to happen.
- **`guard` for early exit.** Validate preconditions at the top and return early; keep the happy path unindented.
- **Enums with associated values** model state precisely; prefer them over loose flags or stringly-typed status fields. Exhaustive `switch` (no `default`) makes the compiler flag missed cases.
- **Protocol-oriented.** Compose behavior via protocols and protocol extensions instead of deep class hierarchies. Use generics with constraints (`where`) over `Any`/type-erasure unless erasure is genuinely needed.
- **`let` over `var`.** Immutable by default. Mutability should be a deliberate, visible choice.

## Error handling

- Recoverable failures use `throws` + `do/catch`; model error cases as a typed `enum: Error`. Use `Result` mainly at async/callback boundaries or when storing an outcome.
- `try?` discards the error (often a smell — you lose the reason); `try!` crashes on failure. Flag both in review unless clearly justified.
- Don't use `throws` for normal control flow or for "not found" that is better expressed as an optional.

## Concurrency (modern Swift)

- **async/await** over completion handlers. Suspension points are explicit (`await`); no nested callback pyramids.
- **Actors** isolate mutable state — access to actor properties is serialized, removing data races. Reach for an actor instead of manual locks/`DispatchQueue` synchronization.
- **`@MainActor`** for UI / main-thread-bound state. Look for UI mutations happening off the main actor.
- **`Sendable`**: types crossing concurrency boundaries must be safe to share. Watch for non-`Sendable` types captured in `Task`/across actors, and for `@unchecked Sendable` that hides a real race.
- **Structured concurrency**: prefer `async let` and `TaskGroup` so child tasks are scoped and cancelled with the parent. A detached/unstructured `Task` that is never cancelled or awaited can leak.
- Legacy `DispatchQueue`/`OperationQueue` is fine in older code but cannot be mixed carelessly with async/await; flag suspicious bridging.

## Memory and performance

- Reference types are ARC-managed. **Retain cycles** are the classic leak: two objects holding strong references, or a closure capturing `self` strongly. Use `[weak self]` (then `guard let self`) or `[unowned self]` in escaping closures and delegate back-references. `unowned` crashes if the referent is gone — only use when lifetime is guaranteed.
- `weak var delegate` is the standard pattern for delegates.
- Copy-on-write backs `Array`/`String`/`Dictionary`: copies are cheap until mutated. Large value types copied in hot loops can still cost — measure, don't assume.
- Prefer `lazy` for expensive deferred initialization; prefer `struct` to avoid heap allocation where reference semantics aren't needed.

## Toolchain and ecosystem

- **Swift Package Manager** is the default: `Package.swift` manifest, `swift build` / `swift test` / `swift run`. Dependencies are declared as packages; check `Package.resolved` for pinned versions.
- Testing: **XCTest** (`XCTAssert*`, `setUp/tearDown`) and the newer **Swift Testing** (`@Test`, `#expect`/`#require`). Identify which the project uses before advising.
- Async tests: `func test...() async throws` and `await` the code under test.

## Common pitfalls to flag

- Force-unwrap (`!`) and `try!` in non-test code.
- Strong `self` capture in escaping closures → retain cycle.
- Mutable shared state without actor/lock; UI updates off `@MainActor`.
- `@unchecked Sendable` masking a data race.
- Overusing classes where a struct/enum fits.
- `default:` in a `switch` over a fixed enum (silently swallows new cases).
- Stringly-typed state instead of an enum.

When reviewing, ground each point: use find_symbol to inspect the actual type/closure and search_project to see whether a pattern (e.g., force-unwrap, detached Task) recurs across the codebase before generalizing.
