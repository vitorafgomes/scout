---
name: clojure
description: Answering questions about the Clojure programming language — idiomatic functional style, immutable persistent data structures, the REPL-driven workflow, the deps.edn/Leiningen toolchain and ecosystem, sequences and laziness, transducers, concurrency primitives (atoms, refs/STM, agents, core.async), error handling with ex-info, macros, spec/Malli, JVM interop and performance, and reviewing or explaining Clojure (.clj/.cljs/.cljc/.edn) code in the user's projects.
---

# Clojure Expertise

Use this when a user asks how Clojure code works, why it is slow or holding the head of a lazy seq, how to make it idiomatic, how to structure state/concurrency, or what to look for when reviewing Clojure. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run a REPL, or commit. To ground advice in the user's real code, use search_project for patterns (e.g. `defn`, `swap!`, `defmacro`, `core.async`), find_symbol for a specific var or namespace, and get_repository for project layout and deps.

## Core model
- **Data over objects.** Programs are mostly pure functions transforming immutable maps, vectors, sets, and keywords. Keywords (`:name`) are the canonical map keys. Prefer plain maps to bespoke types; reach for records (`defrecord`) only for hot-path polymorphism or protocol dispatch.
- **Immutability and structural sharing.** Persistent collections are cheap to "update" — `assoc`/`conj`/`update` return new versions sharing structure, they never mutate. Code that fights this (rebinding, mutable fields) is a smell.
- **Everything is an expression**, returns a value; `do`/`let`/`when` included. Side effects are isolated and made obvious (the `!` convention: `swap!`, `reset!`).

## Idiomatic style
- Build pipelines with `->`/`->>` threading, `map`/`filter`/`reduce`, and `comp`. Prefer these over loop/recur unless you need explicit accumulation or early exit.
- Destructure in function args and `let` (`{:keys [a b]}`, `[x & rest]`) instead of repeated `get`/`nth`.
- `nil` punning is idiomatic: `seq` returns nil on empty (use `(seq coll)` not `(not (empty? coll))`), and most seq fns treat nil as empty. But guard against unexpected nils flowing through arithmetic/interop (NullPointerException).
- Pure core, effects at the edges. Flag functions that both compute and do I/O. Name predicates with `?` (`valid?`) and mutators with `!`.
- Keep namespaces small and `(:require ...)` with explicit aliases (`:as`); flag `:refer :all` and circular namespace deps.

## Laziness and sequences
- Lazy seqs (`map`, `filter`, `range`, `for`, `line-seq`) are not realized until consumed. Two classic bugs: (1) **side effects inside lazy maps never run** if the result is unused — use `doseq`/`run!`/`mapv` for effects; (2) **holding the head** of a large/infinite lazy seq keeps it all in memory (OOM) — don't bind the whole seq to a long-lived var.
- For eager results use `mapv`/`filterv`/`into`/`reduce`. For large transforms prefer **transducers** (`(into [] (comp (map f) (filter p)) coll)`) — no intermediate seqs, composable, allocation-light.

## Concurrency (state primitives)
- Pick by coordination need: **atom** for uncoordinated single-value state (`swap!`/`reset!`, CAS retry — the update fn must be pure and side-effect-free since it may rerun); **ref + dosync** for coordinated multi-value transactions (STM); **agent** for async side-effecting state (`send`/`send-off`); **var binding** for thread-local dynamic scope.
- `core.async` (`chan`, `go`, `<!`/`>!`, `<!!`/`>!!`) for CSP-style pipelines. Never do blocking I/O or `<!!`/`>!!` inside a `go` block — it starves the fixed thread pool; use `thread` for blocking work.
- Immutability removes most data races, but identities (atoms/refs) still need correct coordination. Flag a `swap!` whose function performs I/O or is non-idempotent.

## Errors
- Throw `(ex-info "msg" {:data ...})` to attach a data map; recover via `try`/`catch` reading `(ex-data e)`. Prefer this over bare strings or custom exception classes.
- Don't use exceptions for normal control flow — return `nil`, an `:error` value, or a result map. Validate inputs with `spec`/`Malli` at boundaries rather than scattering asserts.

## Pitfalls to flag when reviewing
- Lazy side effects that never execute; head-holding causing memory growth.
- `swap!`/`alter` update fns that aren't pure (they may retry under contention).
- Blocking calls inside `go` blocks; unbounded channels with no backpressure.
- Overuse of macros where a plain higher-order function would do — macros don't compose and complicate reasoning. A macro is justified only when you need to control evaluation or generate syntax.
- Reflection warnings on JVM interop in hot paths — type hints (`^String`, `^long`) remove them; recommend `(set! *warn-on-reflection* true)`.
- Stringly-typed maps with no spec/validation; deeply nested `assoc-in`/`update-in` that signal a flat model would be clearer.
- `def` inside functions (creates global vars at call time) instead of `let`.

## Toolchain and testing
- Modern projects use `deps.edn` + the `clj`/`tools.deps` CLI (aliases for tasks); older ones use Leiningen (`project.clj`). ClojureScript targets JS; `.cljc` is shared portable code.
- The REPL is the primary workflow — development is interactive, evaluating forms in a live runtime. When explaining, frame fixes as "evaluate this at the REPL to check."
- Tests use `clojure.test` (`deftest`/`is`/`are`), run via `cognitect test-runner` or `lein test`. Property-based testing via `test.check`. Lint with `clj-kondo`, format with `cljfmt`. Suggest tests for nil/edge inputs and error paths, not only the happy case.
- When explaining behavior, use find_symbol to inspect the actual var/namespace and search_project to see how state and effects are really structured before advising.
