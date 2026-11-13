---
name: perl
description: Answering questions about Perl code — idiomatic Perl 5, use strict/warnings, sigils and context (scalar/list), references and data structures, regex and text processing, CPAN/cpanm and the toolchain, OO with Moose/Moo/plain bless, error handling (die/eval/Try::Tiny), performance and memory, Test::More/prove testing, common pitfalls, and reviewing or explaining Perl scripts and modules in the user's projects. Not Raku/Perl 6, not Python/Ruby/PHP/other languages.
---

# Perl Expertise

Use this when a user asks about Perl code in their projects: explaining how a script or module works, spotting bugs, reviewing quality, or recommending idioms. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. Never edit files, commit, or run anything. Ground claims in the user's actual code: use search_project for patterns, find_symbol for a package/sub, get_repository for the stack and Perl version.

## Identify the context first
- Assume Perl 5 unless told otherwise; Raku (formerly Perl 6) is a different language. Check `cpanfile`/`dist.ini`/`Makefile.PL`/`META.json` for deps and the `use v5.XX` line for the minimum version (signatures, `say`, postfix deref vary by version).
- Distinguish a one-off script from a distributed module. Modules live in `lib/`, use packages and `__PACKAGE__`, and ship tests under `t/`.

## Idiomatic Perl 5
- `use strict; use warnings;` at the top of every file is non-negotiable. Flag any file missing them — it hides typos, leaked globals, and undef bugs. `use v5.36;` (or later) enables strict, warnings, and signatures together.
- Context matters: a sub or array behaves differently in scalar vs list context (`my $n = @array` is count; `my ($x) = @array` is first element). Most bugs trace to context confusion or to flattening — passing `@a, @b` to a sub merges them; pass references instead.
- Sigils mark usage not type: `$x` scalar, `@x` list, `%x` hash, but element access is `$x[0]`, `$x{k}`. Nested data is always references: `\@`, `\%`, `\&`, deref with `@{ }`, `%{ }`, `->[ ]`, `->{ }`, or postfix `->@*`/`->%*`.
- Prefer `my` lexicals; avoid package globals and `our` unless intentional. Use `//` (defined-or) for defaults, not `||` (which trips on `0`/`""`).

## Toolchain & ecosystem
- CPAN is the package archive; install with `cpanm` (App::cpanminus). Pin deps in a `cpanfile`; isolate with `local::lib` or `Carton`/`carton.lock`. Build/dist via Dist::Zilla, Module::Build, or ExtUtils::MakeMaker.
- Recommend (as advice, never run): `perlcritic` (policy linting), `perltidy` (formatting), `prove` (test runner). Common modules: Moose/Moo (OO), DBI (databases), Try::Tiny (exceptions), Mojolicious/Dancer2/Catalyst (web), JSON::XS, Path::Tiny.

## OO and references
- Three styles you will see: plain `bless` + `@ISA`/`use parent`, Moo (lightweight), Moose (full meta, heavier startup). Explain which one a class uses before advising. `has` declares attributes; `BUILD`/`BUILDARGS` customize construction.
- Roles (Moose::Role/Moo::Role) compose behavior — prefer them over deep inheritance. Watch for circular references: Perl uses refcount GC, so a parent<->child cycle leaks memory until `Scalar::Util::weaken` breaks it.

## Regex & text processing
- Perl's strength. Note `/x` for readable patterns, `/m` and `/s` semantics, named captures `(?<name>...)`, and non-greedy `*?`. Flag catastrophic backtracking on untrusted input.
- Watch encoding: `use utf8;` affects source literals only; decode bytes on input and encode on output (Encode). Mixing bytes and characters silently corrupts data — a very common bug to flag.

## Error handling
- `die` throws, `eval { ... }` catches (check `$@` immediately, it can be clobbered). Prefer Try::Tiny or `use feature 'try'` for clean try/catch and to avoid `$@` pitfalls. Throwing objects (exception classes) beats throwing strings.
- Use `Carp` (`croak`/`carp`) in modules so errors report the caller's line, not the module's. Open/system calls must check return values: `open(my $fh, '<', $f) or die "..."`.

## Performance & memory
- Avoid slurping huge files; iterate line by line (`while (my $line = <$fh>)`). Building giant strings/arrays in loops drives memory; prefer streaming.
- Regex compilation in hot loops: use `qr//` to precompile. `map`/`grep` are idiomatic but allocate; nested `map` can be costly. Cycles leak (see weaken above).

## Common pitfalls to flag in review
- Missing `use strict`/`warnings`; barewords; two-arg `open`; unchecked `open`/`system`/`close`.
- List flattening when passing arrays/hashes to subs (pass refs); context bugs around `wantarray`.
- `$@` overwritten before checked; using `||` where `//` is needed.
- Interpolation traps (`@` in double-quoted strings); `local` vs `my`; modifying `$_` aliased in `for`/`map`.
- Shelling out with interpolated user input (injection) — recommend list-form `system`/`open` or quoting.

## Testing & review
- Expect Test::More/Test2 under `t/`, run with `prove`. Look for `done_testing`, behavior-level assertions, and a failure-path test per external boundary (DB, file, HTTP).
- When reviewing: confirm strict+warnings, references over flattening, immediate `$@`/return-value checks, `croak` in modules, precompiled regex in loops, weakened cycles, and that `perlcritic` would pass. Cite the specific concern and show the user the exact file/sub.
