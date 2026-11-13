# What makes a change sensitive

A change is **Sensitive** when it matches any row. Read the pages of that row before the plan, and
give the review the lenses it names.

| The change | Read first | Lenses | The trap |
|---|---|---|---|
| Adds or changes a call that leaves the process: an SDK call, an HTTP or gRPC call, a filesystem write | `context/pages/invariants/no-executive-writes.md`, ADR 0032, ADR 0009 | `read-only` | A write nobody declared, or one outside Scout's own folders. An unclear case is forbidden |
| Adds a function or tool the model can invoke, a plugin operation or a control-plane command | ADR 0032, ADR 0012, ADR 0054 | `read-only` | The model decides when to call it: judge what it can reach |
| Touches credentials, tokens, auth headers, configuration binding, `appsettings*.json` | `context/pages/invariants/secrets-never-in-source.md` | `secrets` | A value in a settings file, a token in a log line or an error message |
| Adds or changes an adapter, an LLM provider, a plugin registration, `Scout.ControlPlane.Contracts`, or a type that crosses one of those abstractions | `context/pages/invariants/pluggable-boundaries.md`, ADR 0012 | `boundaries` | A vendor SDK type in the core or in another adapter; a second path to the model |
| Adds or changes a call across an external boundary | `context/pages/conventions/testing.md` | `boundaries` | The happy path is tested and the timeout, 5xx or reset is not |
| Changes the default provider or model | `context/pages/invariants/local-first-default.md`, ADR 0008 | `invariants` | A contributor without paid credentials can no longer run Scout |

`invariants` always runs. Add `scout-optimizer` when the diff touches streaming, knowledge
indexing or retrieval, a hosted service, the gRPC control plane or an LLM provider call. Add
`/code-review` at `medium`, without `--fix`, when it touches `Scout.ControlPlane.Contracts` or a
host. You may add a lens; you may not drop one a row names.

## Rows that are not sensitive but still need a page read

| The change | Read first | The trap |
|---|---|---|
| Touches streaming | ADR 0013 | A stream collected before the first chunk reaches the channel; platform edit rate limits |
| Touches `ui/**` | ADR 0004, ADR 0011 | The renderer reaches the worker only through the control plane |
| Adds a guard, a validation or an error path | `context/pages/invariants/a-guard-must-be-able-to-fire.md` | A check the lines before it make unreachable; write the test whose input makes it fire |
| Answers a finding as intended behaviour | `context/pages/conventions/decisions-that-look-like-defects.md` | The next review reports it again; add the entry in the same change |

## Never fast path, whatever the extension

`.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `Scout.slnx`,
`.github/workflows/**`, `appsettings*.json`, `.claude/**`, `tools/**`, `ui/package.json`,
`ui/bun.lock`, `ui/build/**`.
