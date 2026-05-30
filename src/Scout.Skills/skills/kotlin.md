---
name: kotlin
description: Answering questions about Kotlin code — idiomatic style, null safety, data/sealed classes, coroutines and structured concurrency, Flow, the Gradle/JVM toolchain, Android and server (Ktor/Spring) usage, performance and memory, error handling, testing, and reviewing or explaining Kotlin in the user's projects.
---

# Kotlin Expertise

Use this when a user asks how Kotlin code works, whether it is idiomatic, why it is slow or leaking, how coroutines/Flow behave, or what to fix in a review. Advise and explain only; you may show short illustrative snippets, but never edit, run, build, or commit anything. Ground every claim in the user's actual code: `search_project` for usage patterns and call sites, `find_symbol` for a specific class/function/extension, `get_repository` for build files and stack (Android vs server, Gradle setup).

## Idiomatic style

- Prefer `val` over `var`; immutability by default. Flag pervasive `var` and mutable shared state.
- Embrace null safety: use `?.`, `?:`, and `requireNotNull`/`checkNotNull`. Treat every `!!` as a smell — it throws NPE and discards the type system's guarantee. `lateinit` is for genuinely-late init only, not to dodge nullability.
- Use `data class` for value-holding types (gives `equals`/`hashCode`/`copy`), `sealed class`/`sealed interface` + `when` for closed hierarchies and exhaustive matching, `enum`/`object` for singletons, `value class` (inline) for zero-cost wrappers.
- Favor expression bodies, `when` over long `if/else`, scope functions (`let`/`run`/`apply`/`also`/`with`) for clarity — but warn when they nest into unreadable chains.
- Extension functions and top-level functions over utility classes. Default and named arguments over telescoping overloads.

## Concurrency (coroutines & Flow)

- Coroutines are cooperative and structured. Launch inside a `CoroutineScope`; child failures and cancellation propagate. Flag `GlobalScope` — it leaks work that outlives its caller.
- `suspend` functions must be main-safe or explicit about their dispatcher. Pick dispatchers by workload: `Dispatchers.IO` for blocking I/O, `Default` for CPU, `Main` for UI. Wrapping blocking calls in `withContext(Dispatchers.IO)` is correct; calling blocking JDBC/HTTP on `Default`/`Main` starves the pool.
- Cancellation is cooperative: long loops must check `isActive` or call a suspending point; never swallow `CancellationException` (re-throw it). A bare `catch (e: Exception)` around suspend code is a common bug.
- `Flow` is cold and sequential per collector; use `StateFlow`/`SharedFlow` for hot streams. Flag missing back-pressure handling and collecting a flow without a scope.
- Concurrency safety still applies: shared mutable state needs `Mutex`, atomics, or confinement to a single dispatcher.

## Performance & memory

- Runs on the JVM (also Native/JS via multiplatform). GC and JIT apply; idioms add overhead. Watch boxing of nullable primitives (`Int?`), large lambda capture, and excessive intermediate collections in `map`/`filter` chains — suggest `asSequence()` for long pipelines.
- `inline` functions avoid lambda allocation (that is why `reified` works); non-inline higher-order functions allocate. Don't over-inline large bodies.
- Coroutines are cheap but not free; thousands of leaked jobs still cost memory. Suspect leaks when scopes are never cancelled.

## Pitfalls to flag in review

- `!!`, broad `catch (Exception)`, swallowed cancellation, `GlobalScope`, blocking calls on the wrong dispatcher.
- Platform types from Java interop (`String!`) silently bypassing null checks at the boundary.
- `companion object` used as a dumping ground for global mutable state.
- Equality bugs: `==` calls `equals`; comparing arrays needs `contentEquals`. Overriding `equals` without `hashCode`.
- Exceptions used for control flow; prefer sealed `Result`-style types or `runCatching` for expected failure.

## Toolchain & testing

- Gradle (Kotlin DSL `build.gradle.kts`) is the norm; check the file via `get_repository`/`search_project` for stack, version, and plugins. ktlint/detekt for lint. JUnit5 or Kotest plus MockK for tests; `kotlinx-coroutines-test` (`runTest`, virtual time) for suspend code.
- When reviewing, confirm tests cover failure and cancellation paths, not just the happy path.

Lead with the direct answer, cite file/symbol where it lives, and mark inferences as inferences. If a fix is implied, describe the change and where — never apply it.
