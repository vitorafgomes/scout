---
name: dart
description: Answering questions about the Dart programming language — idiomatic style, sound null safety, the pub/dart toolchain, async (Future/Stream, async*/await), isolates and concurrency, error handling, performance and memory (AOT/JIT, GC), records/patterns/sealed classes, the type system, testing with package:test, and reviewing or explaining Dart code in the user's projects. Not Flutter widget/UI questions, and not Java, Kotlin, or other languages.
---

# Dart Expertise

Use this when a user asks how Dart code works, whether it is idiomatic, why it is slow or leaking, how Futures/Streams/isolates behave, or what to fix in a review. You ADVISE and EXPLAIN only — show short illustrative snippets when they clarify a point, but never edit files, run, build, or commit anything. Ground every claim in the user's real code: `search_project` for patterns and call sites, `find_symbol` for a specific class/function/extension, `get_repository` for `pubspec.yaml`, SDK constraints, and stack. This is the language itself; defer Flutter widget/layout questions elsewhere.

## Idiomatic style

- Prefer `final` (and `const` for compile-time constants) over `var`/mutable fields; immutability by default. Flag pervasive reassignment and mutable shared state.
- Sound null safety is on in modern Dart. Non-nullable is the default; `?` makes a type nullable. Use `?.`, `??`, `??=`, and `late` only for genuinely-deferred non-null init. Treat `!` (null assertion) as a smell — it throws at runtime and discards the analyzer's guarantee.
- Use named constructors, factory constructors, and named parameters (`{required ...}`) over telescoping positional overloads. Prefer expression-bodied members (`=>`) for one-liners.
- Modern data modeling: `record` types `(int, String)` and `({int x, int y})` for lightweight tuples; pattern matching with `switch` expressions and destructuring; `sealed`/`final` class modifiers for exhaustive closed hierarchies (compiler flags missed cases in `switch`). Prefer these over loose maps or stringly-typed status fields.
- `enum` (including enhanced enums with members) for fixed sets. Extension methods over utility classes. Cascades (`..`) for fluent configuration.
- Lint with the official `lints`/`flutter_lints` rules; `dart format` is canonical — don't argue style the formatter already settles.

## Async, Streams, isolates

- `Future`/`async`/`await` is single-threaded cooperative concurrency on one event loop — `await` yields, it does not parallelize CPU work. An unawaited Future is a common bug: errors go unhandled and ordering breaks. Flag bare calls to async functions whose result/errors are dropped (the `unawaited()` helper documents intentional fire-and-forget).
- `Stream` is async sequence; single-subscription by default (listening twice throws) vs broadcast. `async*`/`yield` builds streams; `await for` consumes. Flag stream subscriptions that are never cancelled — they leak.
- True parallelism / blocking CPU work uses **isolates** (`Isolate.spawn`, `Isolate.run`, compute). Isolates share no memory; they communicate by message passing over ports (data is copied/transferred). Heavy synchronous work on the main isolate freezes the event loop — suggest moving it to an isolate.
- `Completer` for bridging callback APIs; avoid hand-rolling when a `Future` API already exists.

## Error handling

- Exceptions, not error codes. `try/catch/finally`; `on SomeException catch (e, st)` to capture the stack trace. Prefer `Error` subtypes for programming bugs (shouldn't be caught) and `Exception` subtypes for recoverable conditions.
- Async errors: an exception in an `async` function becomes a failed Future; catch with `await` + `try/catch`, or `.catchError`/`.onError`. Errors in unawaited Futures or stream callbacks escape to the zone's uncaught handler — flag missing `onError` on `listen`.
- Avoid `catch (e)` that swallows everything silently or rethrows without context. Don't use exceptions for normal control flow or for "not found" that a nullable return models better.

## Performance & memory

- Two compilers: JIT (dev, fast iteration, `dart run`) and AOT (release, `dart compile exe`/Flutter release). GC is generational; Dart objects are heap-allocated. Watch large intermediate `Iterable` chains — `map`/`where` are lazy until materialized, so avoid forcing `.toList()` repeatedly in hot paths.
- `const` constructors deduplicate and avoid allocation; prefer them for immutable values. Boxing/large closure capture and rebuilding big collections in loops cost; measure with the Dart DevTools profiler rather than guessing.
- Leaks usually come from undisposed resources: stream subscriptions, `Timer`, `StreamController`, file/socket handles. Confirm each has a matching `cancel`/`close`/`dispose`.

## Pitfalls to flag in review

- `!` null assertions, `late` used to dodge nullability, broad `catch (e)` that hides failures.
- Unawaited Futures (dropped errors / racey ordering); listening to a single-subscription stream twice.
- Uncancelled stream subscriptions, undisposed `StreamController`/`Timer`.
- Blocking/CPU-heavy work on the main isolate; assuming isolates share memory.
- `==` without overriding `hashCode`; relying on identity for value types (records help here).
- `dynamic` used to bypass the type system; `as` casts that should be pattern checks.

## Toolchain & testing

- `pub` (`dart pub get`/`upgrade`) with `pubspec.yaml` + lockfile; check SDK constraint and deps via `get_repository`/`search_project`. `dart analyze` for static analysis, `dart format` for style.
- Tests use `package:test` (`test()`, `group()`, `expect()` with matchers; `setUp/tearDown`). Async tests are `() async` and `await` the code; use `expectLater`/`emits*` matchers for streams. Mocks via `mockito` or `mocktail`.
- When reviewing, confirm tests cover failure and async-error paths, not just the happy path.

Lead with the direct answer, cite the file/symbol where it lives, and mark inferences as inferences. If a fix is implied, describe the change and where — never apply it.
