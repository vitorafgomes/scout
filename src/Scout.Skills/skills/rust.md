---
name: rust
description: Answering questions about the Rust programming language — ownership, borrowing and lifetimes, idiomatic style, the cargo/rustc toolchain and crates ecosystem, Result/Option and error handling (? operator, thiserror/anyhow), traits and generics, async/await (tokio), threads and Send/Sync concurrency, unsafe and FFI, performance and zero-cost abstractions, testing with cargo test, and reviewing or explaining Rust (.rs) code in the user's projects.
---

# Rust Expertise

Use this when a user asks how Rust code works, why it does not compile (borrow checker, lifetimes, trait bounds), how to make code idiomatic or faster, or what to look for when reviewing Rust. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run cargo, or commit. To ground advice in the user's real code, use search_project for patterns, find_symbol for a type/fn/trait, and get_repository for crate layout and dependencies.

## Core model
- **Ownership and borrowing.** Each value has one owner; borrows are either many shared `&T` or exactly one mutable `&mut T`, never both at once. Most "cannot borrow as mutable" / "does not live long enough" errors are this rule, not a bug to silence.
- **Lifetimes describe how long a borrow is valid**, not how long data lives. Prefer letting elision infer them. Reach for explicit `'a` only when the compiler asks. Distrust answers that sprinkle lifetimes to "fix" an error without explaining the borrow.
- **Move vs clone.** Moves are the default and free. Suggest `.clone()` only when a borrow genuinely cannot express the need; flag clone-in-a-hot-loop as a smell. `Copy` types (integers, `bool`, small `#[derive(Copy)]`) copy implicitly.

## Idiomatic style
- Errors are values: return `Result<T, E>`, absence is `Option<T>`. Reserve `panic!`/`unwrap`/`expect` for unrecoverable bugs and tests, not normal control flow.
- Propagate with `?`. For libraries define typed errors (commonly `thiserror`); for applications `anyhow`/`eyre` is fine. Flag pervasive `.unwrap()` in non-test code.
- Prefer iterators and combinators (`map`, `filter`, `collect`, `?`) over index loops; they are zero-cost and clearer. Use `match`/`if let`/`let else` for exhaustive handling.
- Model with the type system: newtypes over raw primitives, enums over boolean flags, `Option`/`Result` over sentinel values like `-1` or null.
- Accept borrowed args (`&str`, `&[T]`, `impl AsRef<…>`) and own only when storing. Returning `&str` is cheaper than `String` when the caller just reads.

## Traits, generics, async
- Generics with trait bounds are monomorphized (zero runtime cost, larger binary); `dyn Trait` is dynamic dispatch (one impl, small vtable cost). Pick per call-site needs.
- Common derives: `Debug`, `Clone`, `PartialEq`, `Default`, plus `Serialize`/`Deserialize` (serde). Suggest deriving rather than hand-writing where possible.
- `async fn` returns a future that does nothing until awaited/driven by a runtime (usually `tokio`). Do not block a thread inside async (no `std::thread::sleep`, no sync I/O) — it stalls the executor. `.await` only at suspension points.

## Concurrency
- Shared state across threads: `Arc<Mutex<T>>` (or `RwLock`); single-thread sharing: `Rc<RefCell<T>>`. `Send`/`Sync` are auto-traits the compiler checks — a "not Send" error means a non-thread-safe value crossed a thread/await boundary.
- Prefer message passing (channels) or scoped threads over wide shared mutable state. Hold locks for the shortest span; flag awaiting while holding a `std::sync::Mutex` (deadlock/contention risk — use an async-aware lock).

## Pitfalls to flag when reviewing
- `unwrap`/`expect`/array indexing on untrusted input; integer overflow assumptions (debug panics, release wraps — use `checked_`/`saturating_`/`wrapping_`).
- Unnecessary `clone`, `to_string`, or `collect` to dodge the borrow checker instead of restructuring.
- `unsafe` blocks without a comment stating the invariant being upheld; raw pointers or FFI without a safety argument.
- Blocking calls inside async; `.await` inside a loop that could be concurrent (`join_all`/`futures`).
- Swallowed errors (`let _ = result;`), `Result` ignored without `#[must_use]` awareness.

## Toolchain and testing
- Cargo is the entry point: `cargo build/run/test`, `Cargo.toml` (deps, features), `Cargo.lock` (pinned). Workspaces share one lock across crates.
- Recommend `cargo clippy` for lints and `cargo fmt` (rustfmt) for style; `cargo check` for fast type-checking without codegen.
- Tests: `#[test]` fns and `#[cfg(test)] mod tests`, run by `cargo test`; integration tests live in `tests/`, doc examples are tested too. Suggest tests for error paths and edge cases, not only the happy path.
- When explaining a compiler error, name the rule (borrow, lifetime, trait bound, `Sized`) and show the minimal idiomatic fix; use find_symbol to inspect the actual type/trait in question before advising.
