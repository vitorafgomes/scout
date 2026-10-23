---
title: "ADR 0020 - Kubernetes from the kubeconfig on this computer, with consent per context"
version: "1.1"
date: 2026-10-23
updated: 2026-10-23
changes: "Amended 2026-10-23: each allowed context has a label; the model and chat users see labels, never context names or servers"
page_type: adr
status: accepted
description: "Scout reads the owner's own kubeconfig in place, only for the contexts the owner ticks, through a GET-only plugin with no logs, secrets, exec or watch."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.Kubernetes/
  - ui/packages/main/src/kubernetes/kubeconfig.ts
  - ui/packages/main/src/ipc/kubernetes.ts
reliability: high
---

# ADR 0020 - Kubernetes from the kubeconfig on this computer, with consent per context

> When the owner already has a kubeconfig, the Kubernetes card asks whether Scout may use it, lists its contexts, and Scout reads only the ones the owner ticks, with GET requests only.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-23.

## Context

The owner asked that Scout use the Kubernetes configuration already in `~/.kube` when there is one, and that it ask before using credentials that are already on the machine. [[pages/decisions/0009-descriptive-vs-executive-writes]] keeps Kubernetes strict read-only and requires its own ADR for any Kubernetes write. The catalog asked the owner to paste a flattened kubeconfig and promised a keyring that does not exist ([[pages/invariants/secrets-never-in-source]]). A kubeconfig often holds several clusters, and its users may authenticate with a token, a client certificate, or an exec command (`aws eks get-token`, `gke-gcloud-auth-plugin`) that runs on every client build.

## Decision

- **Consent per context.** Main locates the kubeconfig the way kubectl does: the first entry of `KUBECONFIG`, otherwise `~/.kube/config` (`ui/packages/main/src/kubernetes/kubeconfig.ts:19`). The Kubernetes card asks "Use the Kubernetes configuration on this computer?", shows the path and one checkbox per context with its server and how it signs in, and nothing is used until the owner ticks contexts and clicks "Use selected contexts".
- **Store only the path and the names.** The selection `{path, contexts}` is kept under the main-only vault key `kubernetes.selection` (`ui/packages/main/src/vault/safe-storage-vault.ts:6`). Main validates every name against the file it located itself; the renderer never sends a path (`ui/packages/main/src/ipc/kubernetes.ts:76`). The kubeconfig is read in place by the worker and never copied. The worker receives `SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH` and a name and label pair per context (`ui/packages/main/src/worker/build-env.ts:47`), and a running worker restarts on use and on disconnect.
- **Exec plugins are the owner's own commands, disclosed first.** A context whose user has an `exec` section shows "Runs <command and arguments> to get a token" before consent. Its `env` values are never shown, logged or returned, and the value of an argument whose flag looks secret (`--client-secret`, `--token=`) is shown as `***`. The plugin runs that command through the KubernetesClient library exactly as kubectl would; a failure becomes "The command that gets the token for context X failed." (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:334`).
- **Labels, not context names** (amended 2026-10-23, owner request). Real context names such as `AKSPRD01-admin` on `*.example.invalid` say which subscription and role a cluster runs under. Each ticked context gets a label, prefilled with its name: trimmed, 1 to 32 letters, digits, spaces or hyphens, unique ignoring case (`ui/packages/shared/src/kubernetes-labels.ts:9`), checked on the card and again in main. The selection is stored as `{path, contexts: [{name, label}]}`; a selection stored before labels reads with each name as its label (`ui/packages/main/src/ipc/kubernetes.ts:43`). The worker gets `SCOUT__PLUGINS__KUBERNETES__CONTEXTS__<n>__NAME` and `__LABEL`. The plugin's `list_contexts` returns labels only, every function's `context` parameter takes a label (trimmed, any case) and maps it to the name, and every reply and error names the label; a value that is not an approved label, including a raw context name, is refused before a client is built without echoing it. The model and chat users therefore see labels, never context names or servers; the card shows labels once connected.
- **Six read functions, GET only.** `list_contexts`, `list_namespaces`, `list_pods`, `describe_pod` (container statuses and the last 20 events of that pod), `list_deployments`, `list_events` (warnings first, last 10 minutes by default) (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:68` to `:176`). A namespace or pod name that is not a DNS-1123 Kubernetes name is refused before any request (`:223`), because the library concatenates both into the request path and the pod name into a field selector; without that check, model input such as `../secrets/x` or `x/services/y:80/proxy/z` would reach the secrets or proxy endpoints. A context outside the selection is refused before a client is built (`:238`); an omitted context means the first allowed one. One client per context, built once and cached (`:289`), with a 15 s request timeout and the same 15 s for an exec credential command (`:38`). Lists ask the server for at most 100 items and say how many more there are.
- **No pod logs and no secrets in v1.** Chat output is posted to a shared channel, and logs and secret or config map data routinely carry credentials and personal data. No log, secret, config map data, exec, attach, port-forward, proxy or watch function exists. Pod logs are an owner decision tracked in [[pages/roadmap/followups]].
- **No SelfSubjectAccessReview.** Checking what the credentials may do is a POST to the cluster; boundary cases default to forbidden ([[pages/decisions/0009-descriptive-vs-executive-writes]]), so Scout does not send it. Instead the card always says "Scout only reads from your clusters, but these credentials may allow changes. A read-only user is safer:" with copyable `kubectl` commands that create a `scout-reader` service account bound to the built-in `view` cluster role and a token for it.
- **Failures are short and never quote the cluster or the file.** 401 and 403 become "The credentials for context X cannot read Y."; an unreachable cluster "Could not reach the cluster for context X."; a missing or unreadable kubeconfig "The Kubernetes configuration for context X could not be read." The cluster's response body and the library's exception text are never returned, because they can carry exec env values or cluster detail.

## Consequences

- **Better:** no pasted kubeconfig, no copy of credentials, and the owner sees exactly which clusters and which commands Scout will use before anything runs. Read-only holds in code by the GET-only plugin, whatever the credentials allow.
- **Worse:** the owner's own credentials usually allow writes; read-only rests on the plugin alone unless the owner creates the recommended read-only user. Consent covers context names only: the worker re-reads the live file each time it builds a client, so a context whose user later gains or changes an exec command runs the new command without a new disclosure. Only the first `KUBECONFIG` entry is read, so a merged multi-file setup shows only the first file's contexts.
- **Neutral but noteworthy:** the control plane's Kubernetes catalog entry asks for no credentials (`tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:75`): the desktop card replaces the pasted-kubeconfig fields, as the GitHub card replaces its token dialog ([[pages/decisions/0019-github-app-device-flow]]). A host without the desktop sets `Scout:Plugins:Kubernetes` in its configuration instead.

## Alternatives considered

- **Paste a flattened kubeconfig** (the old catalog text) - copies every credential into Scout, cannot carry exec authentication, and asks the owner to handle secrets by hand.
- **Use every context automatically** - violates the owner's request to ask first, and would reach production clusters the owner never meant to share.
- **SelfSubjectAccessReview to warn about write-capable credentials** - a POST; rejected by the boundary rule above.
- **In-cluster service account** - only applies when Scout runs inside a cluster; tracked in [[pages/roadmap/followups]].

## Enforcement

- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:250` - every function, driven through the real KubernetesClient against an in-process API server, sends only GET requests.
- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:239` - the plugin exposes exactly the six read functions.
- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:320` and `:337` - traversal, proxy, encoded-slash and field-selector inputs are refused and no request is sent.
- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:82` - a context the owner did not approve is refused and no request is sent.
- `ui/packages/main/test/ipc-kubernetes.test.ts:130` - the worker gets no Kubernetes variables until the owner uses selected contexts.
- `ui/packages/main/test/ipc-kubernetes.test.ts:222` - no token, key, certificate, password or exec env value reaches the renderer or a log line.
- `ui/packages/renderer/src/app/features/kubernetes-connect.component.spec.ts:124` - nothing is used until the owner ticks contexts and clicks "Use selected contexts".
- Invariant pages: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/plugin-kubernetes]], [[pages/services/desktop-ui]], [[pages/services/host-console]], [[pages/services/control-plane]]
- Applies [[pages/decisions/0009-descriptive-vs-executive-writes]]: Kubernetes stays strict read-only.
- Related: [[pages/decisions/0019-github-app-device-flow]] (the same main-owned card pattern), [[pages/decisions/0011-backend-frontend-grpc-uds]] (the renderer never sees a credential).

## Notes

The exec failure is recognised by the KubernetesClient 19.0.2 message prefix "external exec failed", which the library raises whether the command cannot start, times out, exits without output or prints something that is not an ExecCredential.
