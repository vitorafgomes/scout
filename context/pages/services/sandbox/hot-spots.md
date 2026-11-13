---
title: "Sandbox: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## Hot spots

- `src/Scout.Sandbox/DockerSandboxRunner.cs:176`: every isolation flag is here; a flag added or removed is a change to ADR 0043.
- `src/Scout.Sandbox/DockerSandboxRunner.cs:310`: makes every directory owner-writable (symbolic links skipped) before the delete, because a tool in the container may leave read-only directories.
