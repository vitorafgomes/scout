---
name: sql
description: Answering questions about SQL — query style and readability, joins and set operations, NULL and three-valued logic, schema design and normalization, indexes and query plans, transactions and isolation levels, locking and deadlocks, common pitfalls (N+1, injection, implicit casts), and reviewing or explaining SQL and migrations in the user's projects.
---

# SQL Expertise

Use this when a user asks about SQL: how to write a correct/idiomatic query, why a query is slow, how indexing or transactions work, how to design a schema, or what to check when reviewing SQL, views, or migrations. You ADVISE and EXPLAIN, and may show short illustrative queries in your reply. You never edit files, run queries, apply migrations, or change anything. Ground advice in the user's real schema and code: use search_project for query patterns (e.g. `SELECT`, `JOIN`, `CREATE TABLE`, ORM calls), find_symbol for a table/view/migration, and get_repository for the database/stack context. SQL dialects differ (PostgreSQL, MySQL, SQL Server, SQLite, Oracle) — confirm the engine before giving engine-specific advice.

## Idiomatic style
- Uppercase keywords (`SELECT`, `FROM`, `WHERE`), lowercase identifiers; one clause per line for readable diffs. Recommend a formatter (sqlfluff) over hand-aligning.
- Never `SELECT *` in application code or views — list columns explicitly. `*` breaks on schema change, fetches unused data, and hides intent.
- Always qualify columns with table aliases in multi-table queries (`o.id`, `c.name`); ambiguity is a latent bug when a column is later added to both tables.
- Use explicit `JOIN ... ON`, never comma-joins with join predicates in `WHERE` (old-style, error-prone, accidental cross joins).
- Prefer set-based operations over row-by-row loops/cursors; SQL is declarative — express *what*, let the planner decide *how*.

## NULL and correctness (HIGH)
- Three-valued logic: comparisons with NULL yield UNKNOWN, not true/false. `x = NULL` is never true — use `IS NULL` / `IS NOT NULL`.
- `NOT IN (subquery)` returns no rows if the subquery yields any NULL — a classic silent bug. Prefer `NOT EXISTS`.
- `COUNT(col)` skips NULLs; `COUNT(*)` does not. Aggregates ignore NULLs, which can mislead averages. `a <> b` excludes rows where either side is NULL.
- A `LEFT JOIN` with a filter on the right table in `WHERE` (instead of `ON`) silently degrades to an inner join.

## Schema design (HIGH)
- Normalize to avoid update anomalies (typically 3NF); denormalize deliberately for read performance, not by accident. Flag duplicated data with no clear ownership.
- Every table needs a primary key. Use proper types: `DATE`/`TIMESTAMP` (with time zone where the engine supports it) not strings; `DECIMAL`/`NUMERIC` for money, never `FLOAT` (rounding). Constrain with `NOT NULL`, `UNIQUE`, `CHECK`, and foreign keys — the database enforces invariants the app forgets.
- Migrations should be forward-only and reversible in intent; adding a `NOT NULL` column without a default, or rewriting a huge table, can lock it. Flag destructive migrations (drop column/table) and missing indexes on new foreign keys.

## Indexes and performance (HIGH)
- Indexes speed reads, slow writes, and cost storage. Index columns used in `WHERE`, `JOIN`, and `ORDER BY` — but each one has a cost; don't index everything.
- Composite index column order matters: leftmost-prefix rule. An index on `(a, b)` helps `WHERE a=...` and `WHERE a=... AND b=...`, but not `WHERE b=...` alone.
- A function or implicit cast on an indexed column kills index use (`WHERE lower(email)=...`, or comparing a string column to a number). Recommend the predicate match the column, or an expression/functional index.
- Read the plan: `EXPLAIN` / `EXPLAIN ANALYZE`. Sequential scans on large tables, nested loops over big row counts, and sorts spilling to disk are the usual culprits. Don't guess — tell the user to check the actual plan.
- Pagination via large `OFFSET` is slow (scans and discards); prefer keyset/seek pagination (`WHERE id > :last ORDER BY id LIMIT n`).

## Transactions and concurrency (MEDIUM)
- Wrap multi-statement writes that must be atomic in a transaction; partial commits corrupt invariants. Keep transactions short — long ones hold locks and bloat.
- Isolation levels trade consistency for concurrency: READ COMMITTED (default in most engines) allows non-repeatable reads; SERIALIZABLE prevents anomalies but can abort with serialization failures the app must retry.
- Deadlocks happen when transactions lock rows in different orders — acquire locks in a consistent order. The lost-update race (read-modify-write without locking) needs `SELECT ... FOR UPDATE` or an optimistic version column.

## Pitfalls to flag in review
- `SELECT *`; missing/implicit casts; `NOT IN` with a nullable subquery; filters on the outer table of a `LEFT JOIN` in `WHERE`.
- String-concatenated SQL — SQL injection. Insist on parameterized queries/bind variables; review ORM raw-SQL escapes.
- N+1 queries from an ORM (one query per row in a loop) — recommend a join or eager load; spot the loop in app code with search_project.
- Cartesian products from a missing join condition; `DISTINCT` papering over a bad join.
- Unbounded result sets (no `LIMIT`); `ORDER BY` without an index on a hot path.
- Missing foreign keys / unique constraints relying on the app to enforce uniqueness.

## Testing
- Test against a real instance of the same engine (containerized), not SQLite-as-a-stand-in for Postgres — dialect and type differences hide bugs. Seed known data, assert query results and constraint violations. Verify migrations apply on a clean DB and on a populated copy.

When unsure how the project's schema or queries actually work, search the user's migrations and code before generalizing, and confirm the SQL dialect.
