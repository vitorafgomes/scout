---
title: "Plugin.Kubernetes: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.Kubernetes service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Plugin.Kubernetes/", "tests/Scout.Plugin.Kubernetes.Tests/"]
reliability: high
---

## Public contract

- **Entry point:** `AddKubernetesPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Kubernetes/KubernetesServiceCollectionExtensions.cs:21`; returns early when the kubeconfig path is blank or no context is allowed (`src/Scout.Plugin.Kubernetes/KubernetesServiceCollectionExtensions.cs:33`). Registers the `KubernetesPlugin` singleton, which DI disposes, and a `KernelPlugin` named `Kubernetes`.
- **Model tools:** `list_contexts` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:68`), `list_namespaces` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:77`), `list_pods` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:95`), `describe_pod` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:119`), `list_deployments` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:149`), `list_events` (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:176`). `list_contexts` returns the labels only, one per line, the first being the default ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). Each other function takes an optional `context` label; `list_pods`, `list_deployments` and `list_events` take an optional namespace, all namespaces when omitted.
- **Declared reads** (2026-11-12): the Kubernetes feature in the control plane's catalog lists four read rows, under `WriteClass.None`, which the desktop shows as Reads 4 in the panel and Read 4 on the card ([[pages/services/control-plane]]). Each names what the functions send: `Namespaces`, "List the namespaces of each context you chose" (`list_namespaces`); `Pods`, "Read pod status and restarts; never logs or secrets" (`list_pods`, and the pod read of `describe_pod`); `Deployments`, "Read replicas and rollout status" (`list_deployments`, which prints ready of desired replicas); `Events`, "Read recent events" (`list_events`, and the pod's events in `describe_pod`). `list_contexts` sends nothing: it returns the labels from configuration, so it has no row. The kubeconfig read and an `exec` user's credential command, under External boundaries below, have no row of their own.
- **Configuration keys:** `Scout:Plugins:Kubernetes` with `KubeconfigPath` (`src/Scout.Plugin.Kubernetes/KubernetesOptions.cs:11`) and `Contexts` (`src/Scout.Plugin.Kubernetes/KubernetesOptions.cs:17`), a list of `{Name, Label}` (`src/Scout.Plugin.Kubernetes/KubernetesOptions.cs:21`); a blank label means the name. The desktop sets `SCOUT__PLUGINS__KUBERNETES__KUBECONFIGPATH` and `SCOUT__PLUGINS__KUBERNETES__CONTEXTS__<n>__NAME` and `__LABEL` after the owner picks and labels contexts.
- **External boundaries:** HTTPS GETs to each allowed cluster's API server through `KubernetesClient` 21.0.8 (since 2026-11-08; the "external exec failed" prefix the plugin matches is unchanged in it) with a 15 s request timeout (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:309`); the kubeconfig file, read in place; and, for a user with an `exec` section, the owner's own credential command, which the library runs when it builds the client, now bounded by the same 15 s (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:38` sets the library's process-wide `ExecTimeout`, 2 minutes by default).
