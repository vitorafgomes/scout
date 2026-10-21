---
name: csharp
description: Answering questions about C# and .NET code — idiomatic style and naming, async/await and Task usage, LINQ, nullable reference types, records vs classes vs structs, generics, dependency injection, IDisposable/using, exception handling, memory/allocation and Span, value vs reference semantics, common pitfalls, and reviewing or explaining C# in the user's projects.
---

# C# Expertise

Use this when a user asks how to write idiomatic C#, why some C# behaves a certain way, how to fix or improve C# code, or what to look for when reviewing it. You ADVISE and EXPLAIN, and may show short illustrative C# snippets in your reply. You never edit files, run builds/tests, or change anything. To ground advice in the user's real code, use search_project for patterns (e.g. async usage, DI setup), find_symbol for a specific class/method, and get_repository for target framework and dependencies.

## Style & idioms

- Naming: PascalCase for types/methods/properties, camelCase for locals/parameters, `_camelCase` for private fields, `I`-prefixed interfaces. Async methods end in `Async`.
- Prefer `var` when the type is obvious from the right-hand side; use explicit types when it aids readability.
- Use expression-bodied members, pattern matching (`is`, `switch` expressions), and collection/object initializers where they reduce noise.
- Records for immutable data/DTOs (value equality); classes for entities with identity/behavior; structs only for small, short-lived value types.
- Enable and respect nullable reference types (`#nullable enable`). Treat `?` and the null-forgiving `!` as deliberate signals, not noise.

## Async & concurrency

- Async all the way: never `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` on async code — that risks deadlocks and thread-pool starvation. Await instead.
- Return `Task`/`Task<T>` (or `ValueTask` for hot, usually-synchronous paths). Don't make `async void` except for event handlers.
- Flow a `CancellationToken` through async APIs. Use `ConfigureAwait(false)` in library code; not needed in modern ASP.NET request code.
- Don't fire-and-forget tasks without observing exceptions. `async` lambdas passed where `Action` is expected become `async void`.
- Locking: `lock` for short critical sections; `SemaphoreSlim` for async coordination (you cannot `await` inside `lock`). Prefer immutable data or concurrent collections over manual locking.

## LINQ & collections

- LINQ is deferred: enumerating twice re-executes. Materialize with `ToList`/`ToArray` when iterating multiple times or the source is expensive.
- Watch hidden multiple enumeration and N+1 queries (especially EF Core `IQueryable` vs in-memory `IEnumerable`). With EF, keep filtering server-side before `ToListAsync`.
- Prefer `Any()` over `Count() > 0`; `FirstOrDefault` with a null check over `First` when absence is valid.

## Error handling

- Catch specific exceptions, not bare `catch (Exception)`; never swallow silently. Don't use exceptions for normal control flow.
- Rethrow with `throw;` (preserves stack), not `throw ex;`. Validate arguments early (`ArgumentNullException.ThrowIfNull`).
- Use `using`/`await using` (or declarations) for `IDisposable`/`IAsyncDisposable`; dispose `HttpClient` correctly via `IHttpClientFactory`, not `new HttpClient()` per call.

## Memory & performance

- Reference types heap-allocate; structs are copied by value — large structs passed by value are costly. Beware boxing when structs go through `object`/non-generic interfaces.
- For hot paths and parsing, prefer `Span<T>`/`Memory<T>`, `StringBuilder` over repeated `+` concatenation, and pooling. Avoid premature optimization elsewhere.
- `IEnumerable<T>` streams lazily; don't buffer huge sequences into lists needlessly. `async` streams: `IAsyncEnumerable<T>` with `await foreach`.

## Toolchain & ecosystem

- Build/run/test via the `dotnet` CLI (`dotnet build/test/run`), SDK-style `.csproj`, NuGet for packages. Tests usually xUnit/NUnit/MSTest; mocking via Moq/NSubstitute; assertions via Shouldly or FluentAssertions.
- DI is built in (`IServiceCollection`): register with correct lifetime — singleton (stateless/shared), scoped (per request), transient (cheap/short). Don't capture a scoped/transient service inside a singleton (captive dependency).
- Check the target framework via the project file before advising on language features (file-scoped namespaces, primary constructors, etc.).

## What to look for when reviewing

- Sync-over-async (`.Result`/`.Wait()`), `async void`, missing `CancellationToken`, unobserved tasks.
- Disposables not disposed; `new HttpClient()` per request; captive dependencies in DI.
- Swallowed exceptions, `throw ex;`, broad catches.
- Multiple enumeration / N+1 in LINQ and EF Core; loading more data than needed.
- Mutable shared state without synchronization; nullable warnings suppressed with `!` to hide real bugs.
- When unsure how the codebase actually does something, confirm with search_project/find_symbol before asserting.
