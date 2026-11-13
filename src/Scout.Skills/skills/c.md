---
name: c
description: Answering questions about the C programming language — idiomatic C99/C11 style, manual memory management (malloc/free, ownership, lifetimes), pointers and arrays, undefined behavior, the build toolchain (gcc/clang, make/CMake, headers and linking), structs and unions, the standard library, error handling via return codes/errno, threads and atomics, performance and the preprocessor, plus reviewing or explaining C (.c/.h) code in the user's projects.
---

# C Expertise

Use this when a user asks how to write, structure, debug, or review C code, why a C program crashes or leaks, how memory/pointers/UB work, or how to build and link C. You ADVISE and EXPLAIN only — show short illustrative snippets in your reply, but never edit files, run compilers, or commit. To ground advice in the user's real code, use search_project for patterns (allocation sites, error checks), find_symbol for a function/struct/macro, and get_repository for build setup and C standard.

## Core principles

- **Every malloc has exactly one owner and one free.** Be explicit about who allocates and who frees. Document ownership in comments and function names (e.g. `*_create`/`*_destroy`, `*_dup` returns owned memory). Leaks and double-frees come from fuzzy ownership.
- **Check every return value.** `malloc`, `realloc`, `fopen`, `read`/`write`, library calls — all can fail. Unchecked NULL or short reads are the most common real bugs.
- **Undefined behavior is not "works on my machine".** Out-of-bounds access, use-after-free, signed overflow, reading uninitialized memory, and aliasing violations may appear to work, then break under optimization or on other targets. Treat UB as a definite bug.
- **Prefer C99/C11.** Declare variables near first use, use `<stdint.h>` fixed-width types (`uint32_t`), `bool` from `<stdbool.h>`, designated initializers, and `size_t` for sizes/indices.

## Memory and pointers

- `malloc` does not zero memory; use `calloc` for zeroed buffers. Always `sizeof(*ptr)` not `sizeof(Type)` to stay correct under type changes.
- `realloc` can move and can fail — never write `p = realloc(p, n)`; failure leaks the old `p`. Use a temp pointer.
- Set freed pointers to NULL when the pointer outlives the free, to avoid use-after-free and double-free.
- Arrays decay to pointers; `sizeof` on a parameter gives pointer size, not array size. Pass length explicitly.
- Strings are NUL-terminated; off-by-one on the terminator is a classic overflow. Prefer `snprintf` over `sprintf`, and bound copies (`strncpy` does not always NUL-terminate — know that).

## Error handling

- C has no exceptions. Convention: return an int/enum status, or return NULL/-1 and set `errno`. Pick one convention per module and stay consistent.
- Use a single cleanup path (`goto cleanup;`) to free resources on error rather than freeing in every branch — this is idiomatic, not a smell.
- `errno` is only meaningful after a call that documents setting it; check the return first, then read `errno`.

## Toolchain and build

- Compile with warnings on: `-Wall -Wextra`; treat them as bugs. Use `-fsanitize=address,undefined` (ASan/UBSan) and Valgrind to catch leaks and UB at runtime.
- Headers declare; `.c` files define. Guard headers (`#ifndef`/`#define` or `#pragma once`). Keep `static` on internal-linkage functions/globals to avoid symbol clashes.
- Linking errors ("undefined reference") mean a definition or library is missing at link time, distinct from compile errors. Check `get_repository` for the build system (Make/CMake) and standard flags.

## Concurrency

- Shared mutable state needs synchronization: `<threads.h>` or pthreads with mutexes, or `<stdatomic.h>` for lock-free counters/flags. `volatile` is NOT a threading primitive — it does not provide atomicity or ordering.
- Data races are UB. Look for globals/struct fields written by one thread and read by another without a lock or atomic.

## Pitfalls to flag in review

- Unchecked allocations and I/O return values.
- Buffer sizing with the wrong `sizeof`, missing length passed alongside a pointer, off-by-one on bounds or NUL terminator.
- `realloc` self-assignment, leaks on error paths, mismatched alloc/free (e.g. `free` on a `new`/`mmap` region).
- Integer issues: signed overflow (UB), implicit narrowing, mixing signed/unsigned in comparisons, using `int` where `size_t` belongs.
- Macros with unparenthesized args or arguments evaluated twice; prefer `static inline` functions or `enum` constants over `#define` for typed values.
- Returning pointers to local (stack) variables; format-string mismatches in `printf` family.

## Testing

- Common frameworks: Unity, Check, CMocka, or assert-based harnesses. Suggest tests that exercise error paths and boundary sizes, not just the happy path.
- Recommend running the suite under ASan/UBSan/Valgrind so memory bugs surface in CI, not production.
