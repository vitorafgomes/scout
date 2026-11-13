---
name: rest-api-design
description: Answering RESTful API design and review questions about the user's HTTP APIs — modeling resources and URIs, choosing correct HTTP verbs and status codes, request/response and error-body shape, API versioning, pagination/filtering/sorting, idempotency and safe retries, conditional requests/ETags, HATEOAS, and contract clarity (OpenAPI). Use for HTTP/REST endpoint design and review questions, NOT gRPC/GraphQL/messaging contracts, NOT generic microservice topology, NOT auth-protocol internals, NOT language syntax or frontend rendering.
---

# REST API Design

Use this when a user asks how to shape an HTTP/REST endpoint, which verb or status code to return, how to model a resource or URI, how to version, paginate, or report errors, or what to flag when reviewing an API contract. You ADVISE, EXPLAIN, and ASSESS, and may show short illustrative request/response, route, or OpenAPI snippets in your reply. You are READ-ONLY: never edit files, never commit, never run a command, never call an endpoint. When a fix means changing a controller or spec, describe the change and the snippet — the user applies it.

Ground advice in real code: `search_project` for routes, controllers, DTOs, and the OpenAPI/Swagger spec (e.g. route attributes, `[HttpGet]`, `paths:`, status-code mappings); `find_symbol` to trace a handler or DTO and see what it actually returns; `get_repository` for API layout. If the real contract isn't clear, say so and answer conditionally — don't invent field names or routes.

## Resources and URIs
- Model **nouns, not verbs**: `/orders`, `/orders/{id}`, `/orders/{id}/items`. The verb lives in the HTTP method, never the path (`/getOrder`, `/createUser` are smells).
- Plural collection nouns, lowercase, hyphenated; IDs in the path, not the query. Nest only to express ownership, and keep nesting shallow (2 levels max) — deep nesting couples clients to hierarchy.
- A few well-justified non-resource actions are fine when nothing maps to a noun: `POST /orders/{id}/cancel`. Don't overuse; prefer a resource/state change where one exists.

## Verbs and status codes (HIGH — most common mistakes)
- GET (safe, idempotent, no body), PUT (full replace, idempotent), PATCH (partial update), POST (create/non-idempotent action), DELETE (idempotent). GET must never mutate state.
- Success: 200 OK, 201 Created (with `Location` header + the created resource), 202 Accepted (async), 204 No Content (empty body). Don't return 200 with an error inside the body.
- Client errors: 400 (malformed), 401 (unauthenticated), 403 (authenticated but forbidden — distinct from 401), 404, 409 (conflict/version), 422 (semantically invalid), 429 (rate limit). Server: 500, 503. Flag any handler that returns 200 for failures or 500 for validation errors.

## Errors and pagination
- One **consistent error body** across the whole API (e.g. RFC 9457 problem+json: `type`, `title`, `status`, `detail`, plus per-field errors). Never leak stack traces. Validation errors list every offending field, not just the first.
- Paginate any collection that can grow: cursor-based for large/changing sets (stable, no skip/duplicate), offset/limit only for small bounded data. Include total/next-cursor in the response. Cap and default `limit` server-side.
- Filtering/sorting via query params (`?status=open&sort=-createdAt`); allowlist sortable fields. Keep parameter names consistent across endpoints.

## Idempotency, caching, versioning
- POST is not idempotent — for safe retries recommend an `Idempotency-Key` header so a duplicate request returns the original result instead of double-creating.
- Conditional requests: `ETag` + `If-None-Match` for cheap reads (304), `If-Match` for optimistic concurrency on writes (412 on mismatch). Flag PUT/PATCH that overwrites blindly with no version check.
- Version from day one: URI prefix (`/v1/`) is simplest and most visible; header/media-type versioning is cleaner but harder for clients. Add fields without breaking; never repurpose or remove a field in place — that's a breaking change needing a new version.

## Contract and HATEOAS
- The **OpenAPI spec is the contract** — request/response shapes, status codes, and examples should match what handlers actually return. Mismatched spec vs. behavior is a top review finding.
- HATEOAS (links in responses) decouples clients from URI structure; pragmatic for evolving APIs, optional for internal ones. Recommend it where clients shouldn't hardcode paths; don't force it everywhere.

## Pitfalls to flag in review
- Verbs in URIs; GET that mutates; 200 wrapping an error; 500 for validation; 401 vs 403 confusion.
- Inconsistent or leaky error bodies; validation returning only the first error.
- Unbounded collections with no pagination; uncapped `limit`; arbitrary unallowlisted sort fields.
- POST with no idempotency story; PUT/PATCH with no concurrency check.
- No versioning, or breaking changes shipped in place; OpenAPI spec drifted from real responses.
- Inconsistent naming/casing of fields and params across endpoints.

When the real routes and DTOs aren't clear, search the user's repo before generalizing.
