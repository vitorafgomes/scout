---
name: redis-development
description: Answering questions about Redis design and performance — choosing data structures, key naming, memory/TTL/eviction, pipelining and connection pooling, JSON vs hash, Redis Query Engine (FT.CREATE/FT.SEARCH), vector search with RedisVL, RAG, semantic caching, streams vs pub/sub, clustering, security, and observability.
---

# Redis Best Practices

Use this knowledge when a user asks how to model data in Redis, why their Redis is slow or using too much memory, how to query/index, how to build vector search or RAG, or how to secure and monitor Redis. Recommend and explain; do not execute anything. To ground answers in the user's own code, use search_project for usage patterns, find_symbol for cache/client wrappers, and get_repository for stack context.

Rules below are grouped by category and priority. Cite the rule when explaining a recommendation.

## 1. Data Structures & Keys (HIGH)

- **Choose the right data structure.** Strings for blobs/counters, hashes for objects with fields, sets for unique membership, sorted sets for ranking/leaderboards/time ranges, lists for queues, streams for event logs. Avoid serializing a whole object into one string when you only read a few fields — use a hash.
- **Consistent key naming.** Use a stable scheme like `app:entity:id` (e.g. `user:1000:session`). Predictable namespaces make keys discoverable, prevent collisions, and let you reason about eviction and TTL by prefix.

## 2. Memory & Expiration (HIGH)

- **Memory limits and eviction.** Set `maxmemory` and pick an eviction policy intentionally: `allkeys-lru`/`allkeys-lfu` for pure caches, `volatile-*` when only keys with a TTL may be evicted, `noeviction` when data loss is unacceptable (writes then fail instead). Wrong policy silently evicts data you needed.
- **TTL on cache keys.** Every cache entry should have a TTL so stale data expires and memory is reclaimed. Set TTL at write time (`SET key val EX seconds`). Keys without TTL accumulate forever.

## 3. Connection & Performance (HIGH)

- **Avoid slow commands in production.** `KEYS *`, large `SMEMBERS`/`HGETALL`/`LRANGE 0 -1`, and unbounded `SORT` block the single-threaded server. Use `SCAN`/`HSCAN`/`SSCAN` for iteration; keep per-command work bounded.
- **Pipelining for bulk operations.** Batch many commands into one round trip with pipelining to cut network latency dramatically. Use it for bulk loads/reads; group logically related ops.
- **Connection pooling / multiplexing.** Reuse connections via a pool (or a multiplexed client like StackExchange.Redis). Opening a connection per request exhausts sockets and adds handshake latency.
- **Connection timeouts.** Configure connect and command timeouts so a stalled Redis fails fast instead of hanging the app. Pair with retry/backoff at the client.

## 4. JSON Documents (MEDIUM)

- **JSON paths for partial updates.** With RedisJSON, update a single field via `JSON.SET key $.field value` instead of rewriting the whole document — less bandwidth, less contention.
- **JSON vs hash.** Use hashes for flat key/value objects (cheaper, simpler). Use JSON for nested/structured documents, arrays, or when you need path-based queries and indexing via RQE.

## 5. Redis Query Engine (HIGH)

- **Use DIALECT 2.** Specify `DIALECT 2` for current query syntax and features; older default dialect lacks capabilities and behaves inconsistently.
- **Correct field types.** Match index field type to use: `TEXT` for full-text, `TAG` for exact-match/enum/IDs, `NUMERIC` for ranges/sorting, `GEO` for coordinates, `VECTOR` for embeddings. Wrong type breaks queries or kills performance.
- **Index only fields you query.** Each indexed field costs memory and write overhead. Index what you filter/sort/search on; leave the rest unindexed.
- **Manage indexes for zero-downtime updates.** To change a schema, create a new index alongside the old one, let it backfill, then switch reads and drop the old index. Dropping first causes an availability gap.
- **Write efficient queries.** Filter on indexed fields, prefer `TAG` exact matches over broad `TEXT`, use `LIMIT`/paging, and project only needed fields with `RETURN`. Avoid scanning the whole index.

## 6. Vector Search & RedisVL (HIGH)

- **HNSW vs FLAT.** FLAT = exact, brute-force, best for small datasets or when perfect recall is required. HNSW = approximate, fast and scalable for large datasets; tune `M` and `EF_CONSTRUCTION`/`EF_RUNTIME` to trade recall vs latency.
- **Hybrid search.** Combine vector similarity with metadata filters (TAG/NUMERIC) in one query to narrow candidates and improve relevance — more accurate than pure vector search.
- **Configure vector indexes properly.** Set the distance metric (`COSINE`, `L2`, `IP`) to match how embeddings were trained, and the correct `DIM`. Mismatched metric or dimension yields meaningless results.
- **RAG pattern.** Embed query, retrieve top-K similar vectors with metadata, then pass retrieved context to the LLM. Store source text/metadata alongside vectors so retrieval returns usable context, not just IDs.

## 7. Semantic Caching (MEDIUM)

- **Configure semantic cache properly.** Set a similarity threshold that balances hit rate against false matches; too loose returns wrong cached answers. Give cached entries a TTL and scope the cache per use case.
- **LangCache for LLM responses.** Cache LLM responses keyed by query embedding so semantically equivalent prompts reuse a stored answer, cutting cost and latency. Validate threshold on real traffic before trusting it.

## 8. Streams & Pub/Sub (MEDIUM)

- **Streams vs Pub/Sub.** Use Streams when you need persistence, consumer groups, replay, or at-least-once delivery (event logs, task queues). Use Pub/Sub for fire-and-forget fan-out where missed messages are acceptable (live notifications). Pub/Sub does not store messages.

## 9. Clustering & Replication (MEDIUM)

- **Hash tags for multi-key operations.** In a cluster, multi-key commands and transactions require all keys on the same slot. Force colocation with a hash tag: `{user:1000}:session`, `{user:1000}:cart` hash on `user:1000`.
- **Read replicas for read-heavy workloads.** Route reads to replicas to scale read throughput; accept eventual consistency on replica reads. Keep writes on the primary.

## 10. Security (HIGH)

- **Always use authentication in production.** Require a password (`requirepass`) or, better, per-user ACL credentials. Never run an internet-reachable Redis without auth.
- **ACLs for fine-grained access.** Define users with least-privilege command and key-pattern permissions instead of one shared superuser, so an app can only touch its own keys.
- **Secure network access.** Bind to private interfaces, use TLS in transit, restrict with firewall/security groups, and disable or rename dangerous commands (`FLUSHALL`, `CONFIG`, `KEYS`) where possible.

## 11. Observability (MEDIUM)

- **Monitor key metrics.** Track hit rate (`keyspace_hits`/`keyspace_misses`), `used_memory` vs `maxmemory`, `evicted_keys`, `connected_clients`, ops/sec, and replication lag. Rising evictions or falling hit rate signal cache pressure.
- **Observability commands for debugging.** `INFO` for server stats, `SLOWLOG GET` for slow commands, `MEMORY USAGE key` and `MEMORY DOCTOR` for memory analysis, `LATENCY DOCTOR` for spikes, `CLIENT LIST` for connection issues.
