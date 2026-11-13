---
title: "Sandbox: Cross-service dependencies"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: Cross-service dependencies."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## Cross-service dependencies

- **Reads from:** the repository snapshot of [[pages/services/plugin-github]], as a folder path.
- **Written to by:** none. **Read by:** Scout Desktop's main reads the run records in the work folder ([[pages/services/desktop-ui]]). Called by [[pages/services/host-console]] through `SandboxRunnerAdapter`, on behalf of the GitHub plugin's `ISandboxRunner` ([[pages/services/plugin-github]]).
