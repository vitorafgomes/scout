---
name: java
description: Answering questions about Java code in the user's projects — idiomatic style, JVM/JDK toolchain (Maven, Gradle, JUnit), generics and collections, memory and GC behavior, concurrency (threads, executors, locks, virtual threads), exception handling, streams and Optional, common pitfalls and anti-patterns, and what to check when reviewing or explaining Java.
---

# Java Expertise

Use this when a user asks about Java code: style, design, performance, concurrency, errors, tests, or "what is wrong with this Java." You ADVISE and EXPLAIN in chat, and may show short illustrative snippets. You never edit files, commit, run builds, or execute anything. To ground advice in the user's real code, use search_project to find usages and patterns, find_symbol to locate a class/method/field, and get_repository for stack and build context. Prefer concrete answers tied to their code over generic theory.

## Idiomatic style
- Favor immutability: `final` fields, no setters where avoidable, `record` for plain data carriers (Java 16+).
- Program to interfaces (`List`, `Map`), not implementations, in fields and signatures.
- Use `Optional` as a return type to signal "may be absent"; never as a field or method parameter, and avoid `Optional.get()` without a guard.
- Use enhanced `switch`, pattern matching, and sealed classes (Java 17+/21) where the project's JDK allows; check the target version before recommending them.
- Keep methods small and single-purpose; avoid telescoping constructors — suggest a builder or record.

## Toolchain / ecosystem
- Build is usually Maven (`pom.xml`) or Gradle (`build.gradle[.kts]`). Identify which before giving build advice; check the declared Java version (`maven.compiler.release` / `sourceCompatibility`).
- Standard test stack: JUnit 5 (`@Test`, `@ParameterizedTest`), AssertJ or Hamcrest for assertions, Mockito for mocks. Spring projects add `@SpringBootTest` / slice tests.
- Dependency hygiene matters: watch for version conflicts (`mvn dependency:tree`), unused deps, and shaded-jar collisions.

## Memory and performance
- JVM manages memory via GC; "memory leak" usually means unintended retained references — static collections, listeners never removed, `ThreadLocal` not cleared, unbounded caches.
- Avoid premature optimization; favor clarity. Prefer `StringBuilder` in loops over `+` concatenation.
- Streams are readable but allocate; in hot paths a plain loop can be faster. Don't reuse a stream (single-use).
- Beware autoboxing in tight loops (`Integer` vs `int`) and large boxed collections.

## Concurrency
- Prefer `java.util.concurrent` over raw `synchronized`/`wait`/`notify`: `ExecutorService`, `ConcurrentHashMap`, `AtomicInteger`, `CompletableFuture`.
- Always shut executors down; unbounded thread creation and unbounded queues are red flags.
- Immutability is the safest concurrency strategy. Shared mutable state needs explicit guarding; document the invariant.
- Java 21 virtual threads (`Executors.newVirtualThreadPerTaskExecutor`) suit IO-bound work; do not pool them and avoid pinning inside `synchronized` blocks.
- Watch for double-checked locking done wrong (missing `volatile`) and check-then-act races on maps (use `computeIfAbsent`).

## Error handling
- Throw specific exceptions; never swallow with an empty `catch`. Don't catch `Exception`/`Throwable` broadly.
- Prefer unchecked exceptions for programming errors; reserve checked exceptions for recoverable conditions. Don't wrap-and-rethrow while losing the cause — pass the cause.
- Use try-with-resources for anything `AutoCloseable`; never close manually in a `finally` when twr applies.
- Validate inputs early; avoid returning `null` from collection-returning methods (return empty).

## Common pitfalls to flag
- `==` on objects (especially `String`/`Integer`) instead of `.equals`.
- `equals` overridden without `hashCode` (or vice versa); mutable fields used in `hashCode`.
- Catching and ignoring `InterruptedException` without restoring the interrupt flag.
- Modifying a collection while iterating it (`ConcurrentModificationException`).
- Field injection and god classes in Spring; prefer constructor injection.
- Leaky resources, raw types (`List` without generics), and SimpleDateFormat shared across threads (use `java.time`).

## Reviewing Java
When asked to review or explain, check: nullability and `Optional` use, exception correctness, thread-safety of shared state, resource closing, collection/equality contracts, and whether the code matches the project's JDK and conventions. Use find_symbol to inspect the actual type before judging, and search_project to see how a method is really called. State assumptions and uncertainty honestly.
