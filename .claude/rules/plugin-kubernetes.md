---
paths:
  - "src/Scout.Plugin.Kubernetes/**"
  - "tests/Scout.Plugin.Kubernetes.Tests/**"
---

# Plugin.Kubernetes

Owns the Kubernetes clients, the kernel functions and the mapping of failures to short messages. Does not own finding the kubeconfig or the owner's consent, which the desktop main process does ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: no write, log, secret, exec, attach, port-forward, proxy or watch call exists, and model input cannot redirect a GET to another API path. `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:250` invokes every function through a kernel against an in-process API server and fails on any request that is not a GET; `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:565` invokes every function on a pod whose environment, annotations and termination messages carry marker values, with the log and secrets paths served, and fails if any reply carries a marker or any request reaches a log or secrets path (2026-11-12); `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:239` fails if a seventh function appears; `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:320` and `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:337` send traversal, proxy, encoded-slash and field-selector inputs and fail on any recorded request.
- [[pages/invariants/secrets-never-in-source]]: the kubeconfig path and context names arrive through configuration only; no message returns a token, a response body or an exec env value (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:93`, `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:123`).
- [[pages/invariants/pluggable-boundaries]]: `KubernetesClient` types stay inside this project; the host sees a `KernelPlugin`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/decisions/0009-descriptive-vs-executive-writes|Descriptive vs executive writes - refining the read-only invariant]] (Kubernetes is strict read-only; any write needs its own ADR)
- [[pages/decisions/0020-kubernetes-from-local-kubeconfig|ADR 0020 - Kubernetes from the kubeconfig on this computer, with consent per context]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/plugin-kubernetes/public-contract.md` |
| How it works | `context/pages/services/plugin-kubernetes/how-it-works.md` |
| Hot spots | `context/pages/services/plugin-kubernetes/hot-spots.md` |
| Testing footprint | `context/pages/services/plugin-kubernetes/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
