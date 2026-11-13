---
title: "Sandbox: Cache footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: Cache footprint."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## Cache footprint

None. Each run's caches live in its work folder and go with it.
