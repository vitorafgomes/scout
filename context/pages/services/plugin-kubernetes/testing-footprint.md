---
title: "Plugin.Kubernetes: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.Kubernetes service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Plugin.Kubernetes/", "tests/Scout.Plugin.Kubernetes.Tests/"]
reliability: high
---

## Testing footprint

`tests/Scout.Plugin.Kubernetes.Tests` (53 tests on 2026-11-12, 3 files). `Fakes/FakeKubernetesApi.cs` is an `HttpListener` on 127.0.0.1 with a temp kubeconfig whose contexts point at it with bearer tokens, at a closed port, at an exec user whose command is `false` and at one whose command sleeps 40 s; it records every request before routing by path, serves list routes that honour `limit` with `remainingItemCount`, and can delay its answers. The real client runs against it: pods with `CrashLoopBackOff` (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:50`), the first allowed context by default (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:62`), a refused context with no request (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:82`), unreachable (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:115`), exec failure with no request (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:123`), missing kubeconfig (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:135`), `limit=100` with the remaining count (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:270`), a request timeout (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:401`), a hung credential command failing in 15 s, the one test that waits that long (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:420`), one client build under concurrent first calls through an injected builder (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:433`), and the GET-only, function-set and name checks above. Labels: `list_contexts` returns labels (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:505`), `PROD` selects the prod context (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:513`), a raw context name or unknown label is refused with no request (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:526`), and no reply from fixtures named `AKS???01-admin` on `*.example.invalid` clusters contains `example.invalid` or `-admin` (`tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:538`). DI gating, including binding `Contexts:0..n:Name` and `:Label`, is in `tests/Scout.Plugin.Kubernetes.Tests/KubernetesServiceCollectionExtensionsTests.cs:16`.
