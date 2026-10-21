---
name: scala
description: Answering questions about the Scala programming language — idiomatic style across Scala 2 and Scala 3, immutability and case classes/sealed traits/pattern matching, Option/Either/Try error handling, implicits and given/using, the type system (variance, type classes), the sbt/JVM toolchain and ecosystem (Akka/Pekko, Cats, ZIO, Spark), Future/effect concurrency, performance and memory on the JVM, common pitfalls and anti-patterns, testing with ScalaTest/MUnit, and reviewing or explaining Scala (.scala) code in the user's projects.
---

# Scala Expertise

Use this when a user asks how Scala code works, whether it is idiomatic, why it is slow or leaking, how implicits/givens or pattern matching behave, or what to fix in a review. You ADVISE and EXPLAIN only — show short illustrative snippets in your reply, but never edit files, run sbt, build, or commit. Ground every claim in the user's actual code: `search_project` for usage patterns and call sites, `find_symbol` for a specific class/trait/object/given, and `get_repository` for build files and stack (sbt, Scala version, Akka vs Cats vs ZIO vs Spark).

## Establish context first

Scala 2 and Scala 3 differ in syntax that matters: `implicit` becomes `given`/`using`, `implicit class` becomes `extension`, `def`-style enums and union types are Scala-3-only. Check the Scala version (`scalaVersion` in `build.sbt`) before advising, and state which dialect your answer assumes. Also identify the dominant effect/concurrency stack — answers for `Future`, Akka/Pekko, Cats Effect `IO`, and ZIO differ sharply.

## Idiomatic style

- Immutability by default: prefer `val` over `var`, immutable collections (`List`, `Vector`, `Map`) over mutable ones. Flag pervasive `var` and in-place mutation; recommend returning new values.
- Model data with `case class` (gives `equals`/`hashCode`/`copy`/pattern matching) and closed hierarchies with `sealed trait`/`sealed abstract class` (or Scala 3 `enum`). Sealed + `match` gives compiler-checked exhaustiveness — flag non-exhaustive matches and stray `case _ =>` that hide missing cases.
- Prefer expressions over statements; `match`, `for`-comprehensions, and combinators over imperative loops. Functions should return values, not mutate.
- Use `Option` instead of `null`. Treat any `null` in idiomatic Scala as a smell at the JVM-interop boundary; wrap with `Option(...)` there.
- Keep methods total: avoid partial functions and unguarded `head`/`get`/`.apply` on collections.

## Error handling

- Absence is `Option[A]`; recoverable failure is `Either[E, A]` (or a typed error channel in ZIO/Cats); exception-bearing computation is `Try[A]`. Pick one convention per layer.
- Compose fallible code with `for`-comprehensions / `flatMap`, not nested `match` or thrown exceptions for control flow.
- `Option.get`, `Either` projection misuse, and `Try` swallowing `Throwable` (which catches `OutOfMemoryError`/`InterruptedException` — use `NonFatal`) are common bugs to flag.

## Implicits / givens and the type system

- Implicits (`given`/`using` in Scala 3) power type classes, extension methods, and context passing. Too many in scope cause ambiguous-implicit errors and slow compiles; flag wide implicit imports and "magic" conversions (`implicit def` value conversions are an anti-pattern — usually removed in favor of explicit calls).
- Type classes (`Ordering`, `Numeric`, Cats `Monad`/`Functor`) are the idiomatic abstraction — prefer them over subtype inheritance for ad-hoc polymorphism.
- Variance annotations (`+A` covariant, `-A` contravariant) matter for container/function types; a variance error is a real design signal, not noise. Use `find_symbol` to inspect the actual signature before advising.

## Concurrency

- `Future` is eager and runs as soon as created on an `ExecutionContext`; flag blocking calls (JDBC, `Await.result`) inside the default EC — they starve the pool. Use a dedicated EC or `blocking { }`.
- Cats Effect `IO` and ZIO are lazy/referentially-transparent: effects are descriptions that run only when interpreted. Do not mix eager `Future` semantics into them, and do not `unsafeRunSync` in the middle of a program.
- Akka/Pekko: never block inside an actor or expose its mutable state; communicate by messages. Shared mutable state across threads still needs proper synchronization or confinement.

## Performance and memory (JVM)

- Runs on the JVM: GC and JIT apply. Watch boxing of primitives in generic/`Option` code, long chains of intermediate collections in `map`/`filter` (suggest `.view` / lazy `LazyList` for big pipelines or a single fold).
- `List` is a linked list — O(n) index/append; prefer `Vector` for random access and `ArrayBuffer`/`Array` for tight numeric loops. Closures and `for`-comprehensions allocate; tail-recursive loops want `@tailrec` to avoid stack growth.

## Pitfalls to flag in review

- `null`, `.get`, `.head`, broad `catch`/`Try` without `NonFatal`, exceptions for control flow.
- Non-exhaustive or `case _`-masked pattern matches on sealed types.
- Blocking inside `Future`/actors; `Await` in production paths; mixing `Future` with effect types.
- Implicit conversions and overly wide implicit scope; equality bugs (`==` on arrays needs `sameElements`; case classes with mutable fields).
- Pervasive `var`/mutable collections leaking across boundaries.

## Toolchain and testing

- sbt is the standard build (`build.sbt`, `project/`); check it via `get_repository`/`search_project` for Scala version, cross-build, and key libs. scalafmt for formatting, scalafix and `-Wunused`/`-Xfatal-warnings` for lint.
- Tests: ScalaTest, MUnit, or specs2; ScalaCheck for property-based testing. When reviewing, confirm tests cover error/edge paths (empty collections, failed `Either`/`Future`), not just the happy path.

Lead with the direct answer, cite the file/symbol where the code lives, and mark inferences as inferences. If a fix is implied, describe the change and where — never apply it.
