---
name: python
description: Answering questions about Python code — idiomatic style and PEP 8, the toolchain (pip/venv/poetry/uv, ruff, mypy, pytest), data model and typing, performance and memory (CPython, GIL, generators), concurrency (asyncio, threads vs processes), exception handling, packaging, and common Python pitfalls when reviewing or explaining .py code.
---

# Python Expertise

Use this when a user asks how to write, review, explain, or fix Python (`.py`) code: idioms, style, typing, the standard toolchain, performance, concurrency, or why some Python behaves unexpectedly. Advise and explain only; you may show short illustrative snippets, but never edit files, run commands, install packages, or commit. Ground advice in the user's real code: `search_project` to find how a pattern is used, `find_symbol` to locate a function/class/module, `get_repository` for stack and dependency context.

## Idiomatic style

- Follow PEP 8: `snake_case` for functions/variables, `CapWords` for classes, `UPPER_CASE` for constants, 4-space indent. Recommend `ruff`/`black` for formatting and `ruff`/`flake8` for linting rather than hand-fixing style.
- Prefer comprehensions and generator expressions over manual loops that build lists; prefer `enumerate`, `zip`, and unpacking over index arithmetic.
- Use context managers (`with`) for files, locks, sockets, DB sessions — guarantees cleanup. Flag any open file or acquired resource that is not in a `with`.
- Use f-strings for formatting; avoid `%` and `str.format` in new code. Avoid string concatenation in loops.
- "Easier to ask forgiveness than permission": `try/except` around the operation rather than pre-checking (`if os.path.exists` then open — a race). But never bare `except:` — catch specific exceptions.

## Typing and the data model

- Encourage type hints (`def f(x: int) -> str:`) and checking with `mypy` or `pyright`. Use `list[int]`, `dict[str, int]`, `X | None` on modern Python; `Optional`/`List` from `typing` only for older targets.
- Use `@dataclass` (or `pydantic` for validation/serialization) instead of dicts-as-objects or hand-written `__init__`/`__eq__`.
- Know the data model: `__init__`, `__repr__`, `__eq__`, `__hash__`, `__enter__/__exit__`, `__iter__`. Equality and hashing must stay consistent — a mutable object used as a dict key is a bug.

## Performance and memory

- CPython is bytecode-interpreted; hot numeric loops are slow. For heavy compute, recommend vectorizing with NumPy/pandas or pushing work into a C-backed library — not micro-optimizing pure-Python loops.
- The GIL means threads do NOT give CPU-bound parallelism. Use `multiprocessing` or a process pool for CPU work; threads/`asyncio` are for I/O-bound work.
- Generators and iterators stream data lazily — use them for large datasets instead of materializing whole lists. Watch for accidental full reads (`list(...)`, reading an entire file).
- Memory: default mutable arguments (`def f(x=[])`) are shared across calls — a classic bug; use `None` and create inside. Large long-lived caches and reference cycles holding `__del__` objects leak; `functools.lru_cache` on an unbounded key space grows forever.

## Concurrency and errors

- `asyncio`: `async def` / `await`; never call blocking I/O inside a coroutine (it stalls the event loop) — use async libraries or run blocking work in an executor. Every awaitable should be awaited; un-awaited coroutines are silent no-ops.
- Catch narrow exceptions, add context, and re-raise (`raise ... from e`) rather than swallowing. Don't use exceptions for normal control flow on hot paths.
- Prefer `pathlib.Path` over `os.path` string juggling; use `logging` over `print` for diagnostics.

## Testing

- `pytest` is the standard: plain `assert`, fixtures for setup, `parametrize` for cases, `monkeypatch`/`unittest.mock` for isolation. Tests live in `tests/` or `test_*.py`. Recommend covering edge cases and error paths, not just the happy path.

## What to look for when reviewing

- Mutable default arguments; bare/overbroad `except`; resources opened without `with`.
- Threads used for CPU-bound work (GIL); blocking calls inside `async` code.
- Missing type hints on public APIs; dicts used where a dataclass belongs.
- `==` vs `is` confusion (`is` only for `None`/singletons); modifying a list while iterating it.
- Unpinned or missing dependencies — check `requirements.txt`/`pyproject.toml` via `get_repository`.

When unsure how the project actually does something, search the user's code before generalizing.
