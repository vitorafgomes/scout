---
title: "Plugin.Kubernetes: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.Kubernetes service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Plugin.Kubernetes/", "tests/Scout.Plugin.Kubernetes.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:334` - the exec failure is recognised by the library's "external exec failed" message prefix; a KubernetesClient upgrade can change it.
- `src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:38` - `ExecTimeout` is a static on the library, so this sets it for the whole worker process.
- The KubernetesClient adds `?watch=false` to list calls; it is a GET and not a watch.
