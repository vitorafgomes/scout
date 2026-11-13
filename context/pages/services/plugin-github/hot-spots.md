---
title: "Plugin.GitHub: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Plugin.GitHub/GitHubClient.cs:183`: `ToApiExceptionAsync`, the one mapping of a failed response for every read and for the write client, maps 401, 403, 404, 429 and 5xx to user-facing errors. A 429, or a 403 that GitHub marks as a rate limit (`X-RateLimit-Remaining: 0`, a `Retry-After`, or a body naming a rate limit, as the secondary limit's "You have exceeded a secondary rate limit" does; `:215`), is "GitHub's rate limit was reached; try again in N seconds.", N in the order GitHub's REST rate-limit guide gives (docs.github.com, "Rate limits for the REST API", read 2026-11-07): the `Retry-After` seconds, else, when `X-RateLimit-Remaining` is `0`, the time left to `X-RateLimit-Reset`, else one minute (`:233`); only a 403 with none of the three is "GitHub denied the request (insufficient token permissions).". The body is read only for a 403, and a body that fails to read counts as no rate limit, so the caller still gets a `GitHubApiException`.
