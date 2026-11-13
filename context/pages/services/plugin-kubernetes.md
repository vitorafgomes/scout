---
title: "Plugin.Kubernetes"
version: "1.2"
date: 2026-10-23
updated: 2026-11-12
changes: "The four read rows the Kubernetes feature declares, and the test that no function reads or returns logs or secrets (2026-11-12). Earlier: KubernetesClient 21.0.8 (2026-11-08); the exec failure prefix is unchanged"
page_type: service
status: active
description: "Read-only Kubernetes plugin: six Semantic Kernel functions over the KubernetesClient library, reading the owner's kubeconfig in place for the contexts the owner allowed; self-gates off without a path and a context."
source: ["src/Scout.Plugin.Kubernetes/", "tests/Scout.Plugin.Kubernetes.Tests/"]
reliability: high
---

# Plugin.Kubernetes

> Lets the model list namespaces, pods, deployments and events and describe a pod, in the clusters the owner allowed from the kubeconfig on this computer ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]).

## Responsibility

Owns the Kubernetes clients, the kernel functions and the mapping of failures to short messages. Does not own finding the kubeconfig or the owner's consent, which the desktop main process does ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/plugin-kubernetes.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/plugin-kubernetes/public-contract\|Public contract]] | Public contract | 3501 |
| [[pages/services/plugin-kubernetes/how-it-works\|How it works]] | How it works | 4400 |
| [[pages/services/plugin-kubernetes/hot-spots\|Hot spots]] | Hot spots | 759 |
| [[pages/services/plugin-kubernetes/testing-footprint\|Testing footprint]] | Testing footprint | 2652 |

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
