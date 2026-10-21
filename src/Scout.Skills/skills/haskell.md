---
name: haskell
description: Answering questions about the Haskell programming language — idiomatic pure-functional style, laziness and strictness (thunks, space leaks, BangPatterns, seq, foldl'), the Cabal/Stack/GHC toolchain and Hackage/Stackage ecosystem, type classes and higher-kinded types, Functor/Applicative/Monad and do-notation, IO and purity, the Maybe/Either/exception error model, STM/MVar/async concurrency, performance and memory profiling, testing with HUnit/Hspec/QuickCheck, and reviewing or explaining Haskell (.hs) code in the user's projects.
---

# Haskell Expertise

Use this when a user asks how Haskell code works, why a type does not check, why a program leaks memory or is slow, how to make code idiomatic, or what to look for when reviewing Haskell. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run cabal/stack/ghc, or commit. To ground advice in the user's real code, use search_project for patterns (e.g. `foldl`, `unsafePerformIO`, `do`), find_symbol for a type/class/function, and get_repository for package layout and dependencies.

## Core model
- **Purity.** A function's result depends only on its arguments; no hidden mutation. Side effects are values of type `IO a`, threaded explicitly. The type signature is the contract — read it first.
- **Laziness is the default.** Expressions are thunks evaluated only when forced. This enables infinite structures and clean composition, but unforced thunks pile up into space leaks. Strictness must be added deliberately.
- **Types are total guidance.** `Maybe`/`Either` make absence and failure explicit; partial functions (`head`, `fromJust`, incomplete `case`) are the main source of runtime crashes. Prefer total functions and exhaustive pattern matches.

## Idiomatic style
- Prefer small composable pure functions; push `IO` to the edges and keep the core pure. Flag business logic buried inside large `IO` blocks.
- Use the type system to model the domain: `newtype` over raw `String`/`Int`, sum types (ADTs) over boolean flags or stringly-typed states, `Maybe`/`Either` over sentinels or null-like patterns.
- Pattern matching and guards over nested `if`; `where`/`let` for naming sub-expressions. Use combinators (`map`, `filter`, `foldr`/`foldl'`, `traverse`) over explicit recursion when they read clearly.
- `do`-notation is sugar for `>>=`/`>>`; it is not imperative code. `<-` binds a result, plain lines sequence effects. Equivalent point-free style (`.`, `<$>`, `<*>`) is fine but flag it when it hurts readability.

## Type classes, Functor/Monad
- Type classes are constraints, not OO interfaces: `(Eq a, Show a) => ...` means "any `a` with these instances." Derive `Eq`/`Show`/`Ord`/`Generic` rather than hand-writing.
- `Functor` (`fmap`/`<$>`) maps over a context; `Applicative` (`<*>`, `pure`) combines independent effects; `Monad` (`>>=`) sequences dependent ones. Pick the weakest that works — `traverse`/`<$>` over a full `do` block when there is no dependency.
- Common monads: `Maybe`/`Either` (short-circuit), `[]` (nondeterminism), `IO` (effects), `State`/`Reader`/`Writer`, and `ExceptT`/monad transformers (or `mtl`) for stacking. Effect systems exist but most code uses `IO` + transformers.

## Laziness, strictness, performance
- The classic space leak: `foldl (+) 0` builds a giant thunk chain. Recommend `foldl'` (strict, from `Data.List`) or `foldr` for lazy/short-circuiting consumers. This is the single most common Haskell performance bug — look for it in review.
- Force values with `seq`, `$!`, `BangPatterns` (`!x`), or strict fields (`data T = T !Int`). Lazy accumulators in long-running loops/folds are a red flag.
- Strings: `String = [Char]` is a linked list and slow for real text — recommend `Text` (`Data.Text`) for text and `ByteString` for bytes. Flag heavy `++`/`String` use in hot paths; use `Builder` for incremental construction.
- Profile before optimizing: GHC's `-prof` with cost centres, heap profiling (`-hT`/`-hc`), and `+RTS -s` for allocation/GC stats reveal the real leak instead of guessing.

## Concurrency & error handling
- Lightweight green threads via `forkIO`; the `async` library (`async`, `concurrently`, `mapConcurrently`) is the idiomatic high-level API. `MVar` for simple shared state, `STM` (`TVar`, `atomically`) for composable lock-free transactions — prefer STM over manual locking to avoid deadlock.
- Exceptions: pure code uses `Maybe`/`Either`; `IO` can throw. Use `bracket`/`finally`/`try`/`catch` (from `Control.Exception`) and prefer `bracket` for resource cleanup so it survives async exceptions. Flag bare `error`/`undefined`/partial functions on untrusted input.
- Never use `unsafePerformIO` to fake purity — flag it on sight unless there is a stated, justified invariant.

## Pitfalls to flag when reviewing
- Partial functions: `head`, `tail`, `last`, `fromJust`, `read`, non-exhaustive `case`/lambda. Suggest total alternatives (`listToMaybe`, `readMaybe`, pattern matches covering all constructors). Enable `-Wall`/`-Wincomplete-patterns`.
- `foldl` (lazy) where `foldl'` is meant; lazy accumulators; thunk buildup in `State`/loops.
- `String` instead of `Text`; `++` or `nub` (O(n^2)) in hot loops.
- Overuse of `unsafePerformIO`, orphan instances, deeply nested monad transformer stacks where a simpler design fits.
- Ignoring warnings; missing top-level type signatures on exported functions.

## Toolchain & testing
- GHC is the compiler. Build with Cabal (`.cabal`, `cabal.project`) or Stack (`stack.yaml`, Stackage snapshots for pinned deps); packages come from Hackage. `ghci` is the REPL for exploring types (`:t`, `:i`).
- Recommend `-Wall` and tools `hlint` (lint/idiom hints) and `ormolu`/`fourmolu` (formatting). Read the `.cabal` for dependencies and language extensions (`default-extensions`).
- Tests: `hspec`/`tasty`/`HUnit` for examples, and `QuickCheck` for property-based tests (idiomatic in Haskell — test invariants over generated input, not just cases). Suggest properties for pure functions and failure paths.
- When explaining a type error, read the signatures, name the constraint or kind mismatch, and show the minimal fix; use find_symbol to inspect the actual type/class before advising.
