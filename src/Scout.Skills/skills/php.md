---
name: php
description: Answering questions about PHP code — idiomatic modern PHP (7.4–8.x), Composer/PSR autoloading and the ecosystem (Laravel, Symfony, PHPUnit), type declarations and strict_types, request lifecycle and memory model, error/exception handling, security (SQL injection, XSS, mass assignment), performance (N+1, OPcache, autoloading), and what to look for when reviewing PHP.
---

# PHP Expertise

Use this when a user asks about PHP code in their projects: explaining how something works, spotting bugs, reviewing quality, or recommending idioms. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. Never edit files, commit, or run anything. Ground claims in the user's actual code: use search_project for patterns, find_symbol for a class/function/trait, get_repository for the stack and framework.

## Identify the context first
- Check PHP version and dependencies (`composer.json`), framework (Laravel, Symfony, plain), and whether `declare(strict_types=1);` is used. Advice differs sharply across these.
- Prefer modern idioms (PHP 8.x) unless the project pins an older version.

## Idiomatic modern PHP
- Add `declare(strict_types=1);` at the top of every file; it makes type juggling fail loudly instead of silently coercing.
- Type everything: parameter types, return types, typed properties, union/nullable types (`?T`, `A|B`), enums for fixed sets. Use `readonly` properties for immutables, constructor promotion to cut boilerplate.
- Use `===`/`!==`, not `==`. Loose comparison has surprising rules (e.g. `0 == "abc"` was true before PHP 8). Recommend `match` over `switch` (strict comparison, no fallthrough, returns a value).
- Favor composition and small classes; use interfaces for boundaries. Traits are for shared behavior, not a substitute for design.

## Toolchain & ecosystem
- Composer manages dependencies and PSR-4 autoloading; expect `vendor/` and `composer.lock` committed. Code style is PSR-12.
- Common tooling to recommend (as advice, never run): PHPStan or Psalm (static analysis, set a level), PHP-CS-Fixer/PHP_CodeSniffer (style), PHPUnit or Pest (tests). Frameworks: Laravel (Eloquent ORM, Artisan), Symfony (Doctrine, components).

## Request lifecycle & memory
- Classic PHP is share-nothing: each request starts fresh, so globals/statics reset per request — process state does not persist between requests unless externalized (DB, cache, session). Long-running runtimes (Swoole, RoadRunner, Octane) break that assumption: static state leaks across requests and must be reset.
- Enable OPcache in production (compiled bytecode cache); without it every request reparses files. Watch memory_limit and large array/result-set loads.

## Error & exception handling
- Throw exceptions for failures; catch the narrowest type, never empty `catch (\Throwable $e) {}`. In PHP 8 many former warnings are now `Error`/`TypeError`.
- Use `finally` for cleanup. Avoid the `@` error-suppression operator. Set a global handler that logs; never display stack traces to users in production.

## Security — high signal when reviewing
- SQL injection: require parameterized queries / prepared statements (PDO bound params, Eloquent/Doctrine bindings). Flag any string-concatenated SQL.
- XSS: output must be escaped (`htmlspecialchars`, Blade `{{ }}`, Twig autoescape). Flag raw echo of user input or `{!! !!}`.
- Mass assignment: guard `fill()`/`create()` with `$fillable`/`$guarded`. Validate and sanitize all input.
- Never trust `$_GET`/`$_POST`/`$_REQUEST` directly; no secrets in code or VCS; `password_hash`/`password_verify`, not raw md5/sha1.

## Common pitfalls
- N+1 queries with ORMs (Eloquent/Doctrine) — look for queries inside loops; recommend eager loading (`with()`).
- Array vs object confusion; `array_*` callbacks misused; reference (`&`) bugs in `foreach`.
- Truthiness traps: empty string, `0`, `"0"`, empty array are all falsy. `isset` vs `empty` vs `array_key_exists` differ on null.
- Mutable static/global state, especially under long-running servers.

## Testing & review
- Expect PHPUnit/Pest; tests under `tests/`. Look for assertions on behavior, mocked boundaries (DB, HTTP), and a failure-path test per external call.
- When reviewing: confirm strict types and full type coverage, parameterized queries, escaped output, narrow exception handling, no N+1, and that static analysis (PHPStan/Psalm) would pass. Cite the specific concern and show the user where it occurs.
