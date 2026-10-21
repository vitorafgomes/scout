---
name: elixir
description: Answering questions about the Elixir programming language — idiomatic functional style, pattern matching and the pipe operator, mix/hex toolchain, OTP (GenServer, Supervisor, Task, Agent), processes and the actor model, "let it crash" error handling, immutability and BEAM performance/memory, Ecto and protocols, ExUnit testing, common pitfalls, and reviewing or explaining Elixir (.ex/.exs) code in the user's projects. Not Erlang-only, not Phoenix-framework specifics, not other languages.
---

# Elixir Expertise

Use this when a user asks how Elixir code works, how to make it idiomatic, how OTP/processes behave, why something crashes, or what to look for when reviewing Elixir. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run mix, install hex deps, or commit. To ground advice in the user's real code, use search_project for patterns, find_symbol for a module/function/struct, and get_repository for project layout and deps.

## Anchor to the project first
- Confirm Elixir/OTP version (`.tool-versions`, `mix.exs` `elixir:` requirement) and deps (`mix.exs` `deps/0`, `mix.lock`). Stdlib and syntax shift across versions.
- Check whether it is an OTP application (`application/0` with a supervision tree) or a plain library — concurrency advice depends on this.

## Core model
- **Immutable data, no shared mutable state.** "Rebinding" a variable does not mutate; functions transform and return new values. Flag any mental model that assumes in-place mutation.
- **Pattern matching is everything.** `=` is a match, not assignment. Match in function heads, `case`, `with`, and destructuring. Prefer multiple function clauses with guards over nested `if`/`cond`.
- **Pipe `|>`** threads a value as the first argument of each call. Idiomatic pipelines read top-to-bottom; flag pipes that start from a bare function call or pipe into `if`/`case` awkwardly.
- **Tagged tuples** `{:ok, value}` / `{:error, reason}` are the convention for fallible results; handle both, do not match only `{:ok, _}`.

## Idiomatic style
- Prefer `Enum` for eager transforms, `Stream` for large/lazy/infinite data and composed pipelines (avoids intermediate lists). Reach for recursion only when `Enum`/`Stream`/`Map`/`Comprehension` cannot express it.
- Use `with` for happy-path chaining of multiple `{:ok, _}` steps; it short-circuits on the first non-match. Avoid deeply nested `case`.
- Naming: `snake_case` functions/vars, `CamelCase` modules, trailing `?` for booleans, trailing `!` for raise-on-failure variants. Keep a `do`/`do!` pair consistent (`fetch`/`fetch!`).
- Structs (`defstruct`) over bare maps for domain types; `@enforce_keys` for required fields. Use `@type`/`@spec` and run Dialyzer for contracts.
- Comprehensions (`for`) with filters/`into:` are clearer than manual `Enum.reduce` for simple cases.

## OTP, processes, concurrency
- The unit of concurrency is the lightweight BEAM process (not OS thread); they share nothing and communicate by message passing. Millions can run; spawning is cheap.
- `GenServer` for stateful servers; `Task` for one-off async/`async_stream`; `Agent` for simple shared state; `Supervisor` for restart strategies. Recommend per workload, and put long-lived processes under a supervision tree.
- Do not hide CPU-bound or business logic inside a single GenServer — it serializes all callers and becomes a bottleneck. Keep `handle_call` fast; offload heavy work to a `Task` or pool.
- `GenServer.call` is synchronous with a timeout (default 5s) — flag long-running work behind `call` that risks `:timeout` exits. `cast` is fire-and-forget (no backpressure).

## "Let it crash" and errors
- Prefer matching expected failures as `{:error, reason}` and let unexpected bugs crash the process; supervisors restart to a known-good state. Do not wrap everything in defensive `try/rescue`.
- Reserve exceptions (`raise`, `try/rescue`) for truly exceptional/programmer errors, not normal control flow. `!`-functions raise; non-`!` return tagged tuples.
- Errors are values: thread them through `with`. Flag swallowed errors (matching `_` on `{:error, _}` and ignoring it).

## Performance and memory
- Each process has its own heap and is GC'd independently — no global stop-the-world. Large messages are copied between processes; avoid passing huge terms repeatedly.
- Lists are linked: prepend (`[x | list]`) is O(1), append/length are O(n). Build reversed then `Enum.reverse`, or use the right structure. Use `Stream` to avoid materializing big intermediate lists.
- Binaries/strings are UTF-8 binaries; prefer binary pattern matching and iolists (`IO.iodata`) over repeated `<>` concatenation in hot paths. ETS for shared read-heavy in-memory tables.

## Pitfalls to flag when reviewing
- Atoms are not garbage-collected — never `String.to_atom/1` on untrusted input (use `to_existing_atom`); unbounded atom creation can crash the VM.
- A GenServer as an accidental global lock; blocking the caller with a synchronous `call` to a slow process.
- Using `try/rescue` for flow control; rescuing too broadly and masking real crashes.
- Treating processes like objects (one per entity by reflex) when a plain data structure suffices.
- `Enum` over a stream/large collection forcing full materialization; N+1 queries in Ecto contexts.

## Toolchain and testing
- `mix` is the entry point: `mix deps.get`, `mix compile`, `mix test`, `mix format` (enforced style), `mix dialyzer`, `mix credo` (lints). Deps live in `mix.exs`, pinned in `mix.lock`.
- ExUnit (`test/`, `*_test.exs`) with `describe`/`test`, `assert`/`refute`, `setup`. Use `async: true` only when tests share no global state (DB, ETS, named processes). Suggest tests for error paths and crash/restart behavior, not just the happy path.
- When explaining a failure, name the mechanism (no matching clause, `:timeout`, supervisor restart, `FunctionClauseError`) and show the minimal idiomatic fix; use find_symbol to inspect the actual module/function before advising. When unsure of project specifics, say so and point to the exact file/symbol to check.
