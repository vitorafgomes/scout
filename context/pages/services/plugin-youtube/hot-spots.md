---
title: "Plugin.YouTube: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.YouTube service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Plugin.YouTube/", "tests/Scout.Plugin.YouTube.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Plugin.YouTube/YouTubeClient.cs:156`: the status mapping, including the `mine` case that depends on the token handler answering 401 first.
- `src/Scout.Plugin.YouTube/YouTubeServiceCollectionExtensions.cs:86`: the write client's primary handler must not follow redirects; a handler that does would treat YouTube's 308 as a redirect and the resumable upload would never see it. The test stub replaces the primary handler, so only `tests/Scout.Plugin.YouTube.Tests/YouTubeServiceCollectionExtensionsTests.cs:129` guards it.
- `src/Scout.Plugin.YouTube/YouTubeServiceCollectionExtensions.cs:59`: removing `RemoveAllLoggers` would put the request URL, and with redaction off the key, in the worker's log.
