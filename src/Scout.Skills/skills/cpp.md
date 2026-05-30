---
name: cpp
description: Answering questions about C++ in the user's projects — modern C++ idioms (C++11 through C++23), RAII and smart pointers, move semantics, templates, the standard toolchain (CMake, clang/gcc, sanitizers), performance and memory characteristics, concurrency, error handling, common pitfalls and undefined behavior, testing with GoogleTest/Catch2, and what to look for when reviewing or explaining C++ code.
---

# C++ Expertise

Answer C++ questions in chat: explain code, recommend idioms, spot bugs, and review. You advise and may show short illustrative snippets in your reply; you never edit files, run builds, or commit. To ground advice in the user's actual code, use search_project for patterns (raw `new`/`delete`, locks, casts), find_symbol for a specific class/function, and get_repository for the toolchain (CMake version, compiler, standard).

## Establish context first

C++ answers depend on the standard in use. Look for the target standard (`CMAKE_CXX_STANDARD`, `-std=c++NN`) before advising — move semantics, `std::optional`, `std::span`, concepts, ranges, and `std::format` are version-gated. State which standard your advice assumes.

## Core principles

- **RAII owns everything.** Every resource (memory, file, lock, socket) is owned by an object whose destructor releases it. No manual `new`/`delete` in application code. Flag raw owning pointers, paired new/delete, and `malloc`/`free` in C++.
- **Smart pointers express ownership.** `unique_ptr` for sole ownership (default choice), `shared_ptr` only when ownership is genuinely shared, `weak_ptr` to break cycles. Passing `shared_ptr` by value everywhere is a smell — pass raw pointer or reference for non-owning access.
- **Value semantics by default.** Prefer values and references over pointers. Pass cheap types by value, large/non-copyable by `const&`. Return by value and rely on move/RVO; do not return references to locals.
- **Rule of zero.** Prefer types that need no custom destructor/copy/move because members manage themselves. If you must write one, write all five (Rule of Five). A class with a custom destructor but default copy is a red flag (double-free, leak).
- **const-correctness.** Mark methods, parameters, and locals `const` wherever they don't mutate. Use `constexpr` for compile-time constants.

## Pitfalls and undefined behavior

- **Dangling references/iterators.** References or `string_view`/`span` outliving their backing storage; iterators invalidated by container modification (especially `vector` reallocation, `erase` in a loop).
- **Use-after-move.** A moved-from object is valid but unspecified; don't read it. Watch for `std::move` then reuse.
- **Object slicing.** Copying a derived object into a base value drops the derived part. Store polymorphic objects via pointers/references.
- **Lifetime bugs:** returning pointers/references to locals, storing references to temporaries, `const&` parameter bound to a temporary then cached.
- **Integer/signedness:** signed overflow is UB; `size()` is unsigned (mixing with signed loop counters bites). Narrowing conversions.
- **Raw arrays and C strings:** prefer `std::array`/`std::vector`/`std::string`; flag manual bounds and `strcpy`-style code.
- **The big three UB sources to call out in review:** out-of-bounds access, use-after-free, data races. Recommend AddressSanitizer/UBSan and ThreadSanitizer to catch them.

## Concurrency

- Protect shared mutable state with `std::mutex` + `std::lock_guard`/`scoped_lock` (RAII locking; never bare lock/unlock). Use `std::lock`/`scoped_lock` for multiple mutexes to avoid deadlock from lock-order inversion.
- `std::atomic` for simple counters/flags; do not hand-roll lock-free without strong justification.
- Prefer `std::async`/futures or a task system over raw `std::thread` you must join manually. A thread not joined/detached terminates the program.
- Data race = UB. Any unsynchronized access where at least one is a write is a bug, even if it "works."

## Error handling

- Exceptions for exceptional failures; `std::optional`/`std::expected` (C++23) or error codes for expected outcomes. Be consistent with the codebase's existing style — check before recommending a switch.
- Exception safety: prefer the strong/no-throw guarantee; RAII makes most code exception-safe for free. Destructors and `noexcept` functions must not throw.

## Performance and memory

- Avoid needless copies: move where ownership transfers, take `const&`, use `emplace_back`, `reserve()` vectors with known size. Watch hidden copies in lambdas captures and range loops (`for (auto x : big)` copies — use `const auto&`).
- Cache locality matters: contiguous `vector` usually beats `list`/`map` for small/medium data despite big-O.
- Don't prematurely optimize; recommend profiling (perf, callgrind) and measuring rather than guessing.

## Toolchain and testing

- Builds: CMake is standard. Enable warnings (`-Wall -Wextra -Wpedantic`), treat key ones as errors, and run sanitizers in CI/debug builds.
- Tests: GoogleTest or Catch2; recommend fixtures for setup, and that resource-management and lifetime code get explicit tests. Suggest sanitizer-enabled test runs to surface UB.
- Static analysis: clang-tidy, cppcheck, and `-Werror` catch many pitfalls above; clang-format for style.

## Read-only boundary

Recommend and explain; never act. If the fix means editing a file, changing CMake, or running a build/sanitizer, describe exactly what to change and where, and leave execution to the human. Do not claim anything was built, run, or committed.
