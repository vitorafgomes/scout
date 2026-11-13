---
title: "Index: invariants"
version: "1.0"
date: 2026-11-13
changes: "Generated from the page headers."
page_type: index
source: ["context/pages/invariants/"]
---

# Invariants

- [[pages/invariants/a-guard-must-be-able-to-fire|A guard must be able to fire]] - Every check that rejects an input has an input that reaches it and a test that supplies one; a check the code before it makes unreachable is a defect, and a failed operation never answers as if it worked.
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]] - With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. A blank or absent provider is locked to Ollama by a test; None is only ever an explicit choice.
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]] - Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. Since ADR 0032 a feature may declare writes: the youtube feature is the first, and its writes on YouTube run only as actions the owner approved in the desktop and the control plane hands out under the live grant. Each turn is offered only the functions its skill or job declares (ADR 0054), so a write reaches the model only on a turn whose skill acts; everything else is upheld by omission, an optional model-judged pre-filter on requests, and, for a function declared as a write, the control plane's gate at the call.
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]] - Chat adapters, LLM providers and plugins sit behind abstractions and are selected or gated by Scout: configuration; the core names no vendor. The set of options is closed at compile time.
- [[pages/invariants/review-writes-on-its-pull-request|A review writes on its pull request, an audit opens issues]] - A turn that runs the code-review skill is offered writes on its pull request only and never a function that opens an issue or starts an audit; the code-audit skill may open issues and is never offered a write on a pull request; only code-audit, project-improvements and github-issues declare create_issue.
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]] - Tokens and API keys come from user-secrets in development and environment variables in production; committed appsettings ship empty values. For appsettings only .gitignore and a convention hold this; the desktop GitHub token and kubeconfig custody is test-covered.
