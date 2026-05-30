---
name: go
description: Answering questions about the Go (Golang) programming language — idiomatic style, goroutines and channels, the error-handling pattern, slices/maps/interfaces, the go toolchain (modules, go test, go vet, race detector), garbage collection and performance, concurrency pitfalls, and reviewing or explaining Go code in the user's projects.
---

# Go Expertise

Use this when a user asks about Go (Golang): how to write idiomatic Go, why their Go code is slow or leaks goroutines, how concurrency or errors should be structured, or what to check when reviewing Go. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run commands, or change anything. To ground advice in the user's real code, use search_project for patterns (e.g. `go func`, `errors.Is`, `defer`), find_symbol for a specific type or function, and get_repository for module/stack context.

## Idioms & Style (HIGH)
- Gofmt is non-negotiable; never debate formatting. Naming: short, lowercase package names; MixedCaps not snake_case; exported identifiers start uppercase. Avoid stutter (`http.HTTPServer` -> `http.Server`).
- Accept interfaces, return concrete types. Keep interfaces small (often one method); define them on the consumer side, not the producer.
- Zero values should be useful — a freshly declared `sync.Mutex`, `bytes.Buffer`, or `var s []T` is ready to use. Avoid needless constructors.
- Prefer composition (embedding) over inheritance. There is no inheritance in Go.

## Errors (HIGH)
- Errors are values, returned as the last result; handle them explicitly. The common smell is `if err != nil { return err }` swallowing context — wrap with `fmt.Errorf("doing X: %w", err)` so callers can `errors.Is`/`errors.As`.
- `panic` is for programmer bugs / unrecoverable state, not control flow. Don't use panic/recover as exceptions. Recover only at goroutine boundaries (e.g. a server handler) to avoid crashing the process.
- Don't ignore errors with `_` unless deliberate; flag silent drops in review.

## Concurrency (HIGH)
- "Share memory by communicating." Use channels to pass ownership; use `sync.Mutex` for protecting plain shared state — pick one, don't mix confusingly.
- Goroutine leaks are the top concurrency bug: a goroutine blocked on a channel send/receive that no one will ever unblock. Every goroutine needs a clear exit path. Look for context cancellation (`ctx.Done()`) and closed channels.
- `context.Context` is the standard cancellation/deadline/timeout mechanism; it should be the first parameter (`ctx context.Context`) and flow through call chains. Never store it in a struct.
- The buggy loop-variable capture (`for _, v := range xs { go func(){ use(v) }() }`) was fixed in Go 1.22+ (per-iteration variables). For older code, flag captured loop vars.
- Use the `-race` detector in tests for any concurrent code; data races are undefined behavior.
- Bound concurrency (worker pools / semaphores); unbounded `go` per request exhausts memory. Use `sync.WaitGroup` or `errgroup` to wait and propagate errors.

## Slices, Maps, Memory (MEDIUM)
- Slices share backing arrays: `append` may or may not reallocate, and sub-slicing keeps the whole array alive (a memory leak when you slice a small piece off a huge buffer — copy instead). Reslicing aliasing bugs are common.
- Preallocate with `make([]T, 0, n)` when the size is known to avoid repeated growth.
- Maps are not safe for concurrent writes (will panic/corrupt); guard with a mutex or use `sync.Map` for specific patterns. Map iteration order is randomized — never rely on it.
- `defer` has small overhead and runs at function return; a `defer` inside a hot loop accumulates — flag it. Closing resources with `defer` at function scope is correct.

## Performance & GC (MEDIUM)
- Go has a concurrent GC; most code shouldn't fight it. Optimize by reducing allocations: reuse buffers (`sync.Pool` for hot paths), avoid `interface{}`/`any` boxing in hot loops, pass large structs by pointer.
- Pointers vs values: small structs are cheap to copy and stay on the stack; pointers can force heap escape. Don't add pointers reflexively. Recommend `go build -gcflags=-m` to understand escape analysis and `pprof` to find real hotspots before optimizing.
- Strings are immutable; build with `strings.Builder`, not `+=` in a loop. `[]byte`<->`string` conversions copy.

## Toolchain & Testing (MEDIUM)
- Modules: `go.mod`/`go.sum` define deps; `go mod tidy` prunes. One module per repo is typical. Recommend `go vet` and `staticcheck` for lint, `go test ./...` to run all packages.
- Tests live in `_test.go` files using the `testing` package; table-driven tests are idiomatic (`tests := []struct{...}` + `t.Run`). Benchmarks via `func BenchmarkX(b *testing.B)`. Suggest `t.Parallel()` and `-race`.
- Prefer the standard library — it is broad (net/http, encoding/json, io). Question heavy frameworks where stdlib suffices.

## Anti-patterns to flag in review
- Naked `return err` losing context; ignored errors.
- Goroutines with no cancellation/exit path; `go` calls whose result is never awaited.
- `interface{}`/`any` overuse defeating type safety; empty-interface "stringly typed" APIs.
- Mutating shared maps/slices without synchronization.
- Overuse of channels where a mutex is simpler, or global state masquerading as config.
- Generics used where a simple concrete type or interface reads better (Go 1.18+); don't over-genericize.
