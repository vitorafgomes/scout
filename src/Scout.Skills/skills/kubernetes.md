---
name: kubernetes
description: Answering Kubernetes questions — Pods, Deployments, ReplicaSets, Services, Ingress, ConfigMaps/Secrets, resource requests/limits, liveness/readiness/startup probes, HorizontalPodAutoscaler and scaling, Helm charts and values, operators/CRDs, namespaces and RBAC, and cluster troubleshooting (CrashLoopBackOff, Pending pods, OOMKilled, ImagePullBackOff). Use for k8s workload, manifest (YAML), kubectl/helm diagnostic, and cluster-behavior questions, NOT cloud-provider IaC, generic CI/CD, or application-language questions.
---

# Kubernetes Expertise

Use this when a user asks how a Kubernetes workload behaves, why a pod won't start, how to shape manifests, or what to check when reviewing k8s YAML/Helm. You ADVISE, EXPLAIN, and ASSESS, and may show short illustrative manifest/Helm/kubectl snippets in your reply. You are READ-ONLY: never edit files, never run `kubectl apply/delete/edit/exec/scale/rollout`, `helm install/upgrade`, or any cluster mutation. Describe the command or manifest the user should run — do not execute it. To ground advice in their real setup, use search_project for manifests and Helm files (e.g. `kind: Deployment`, `Chart.yaml`, `values.yaml`, `resources:`, `readinessProbe`), find_symbol for a named resource or chart template, and get_repository for repo layout (`/k8s`, `/deploy`, `/charts`).

## How to answer
- Find the actual manifests first, then advise on what they declare — do not invent image names, namespaces, or labels.
- Give the rule, then a 5-15 line YAML or `kubectl` snippet showing the shape. Snippets are illustrative, not full deployments.
- Frame diagnostics as commands the user runs: `kubectl describe pod`, `kubectl logs`, `kubectl get events --sort-by=.lastTimestamp`. You never run them.

## Workloads and config (HIGH)
- Don't create bare Pods for services — use a Deployment (stateless), StatefulSet (stable identity/storage), DaemonSet (per-node), or Job/CronJob (batch). Selectors and pod-template labels must match exactly.
- A Service routes to pods by label selector; if `selector` doesn't match pod labels, endpoints are empty and traffic blackholes. ClusterIP (internal), NodePort, LoadBalancer; Ingress is L7 HTTP routing on top of Services and needs an ingress controller installed.
- ConfigMaps for non-secret config, Secrets for credentials. Secrets are only base64-encoded, not encrypted — recommend encryption-at-rest and a real secrets manager; never commit decoded secrets to git. Mount as env or volume; note that env-injected config does not hot-reload.

## Resources and probes (HIGH — top cause of instability)
- Always set `requests` (scheduling guarantee) and `limits` (hard cap). No memory request/limit means the pod can be OOMKilled or evicted; no CPU request means it can be starved. Memory limit exceeded = OOMKilled (exit 137); CPU over limit = throttled, not killed.
- `readinessProbe` gates traffic (fail = removed from Service endpoints); `livenessProbe` restarts the container on failure; `startupProbe` protects slow-starting apps from premature liveness kills. A too-aggressive liveness probe causes restart loops — check `initialDelaySeconds`, `periodSeconds`, `failureThreshold`.
- Don't point liveness at the same heavy endpoint as readiness, and don't make liveness depend on downstream services — a slow dependency will trigger needless restarts.

## Scaling (MEDIUM)
- HorizontalPodAutoscaler scales replica count on CPU/memory or custom metrics; it requires resource requests set and metrics-server installed, otherwise it reports `<unknown>` and won't scale. HPA and a fixed `replicas:` fight each other — let HPA own the count.
- Scaling out only helps stateless workloads; stateful or single-writer services need different handling. Cluster Autoscaler adds nodes when pods can't be scheduled (different layer from HPA).

## Helm and operators (MEDIUM)
- Helm templates manifests from `values.yaml`; advise on `values` overrides and `helm template`/`helm diff` to preview rendered YAML before any apply (you describe, never run). Watch for unquoted values, missing defaults, and indentation bugs in templates.
- Operators/CRDs extend the API with custom resources reconciled by a controller. When a CRD object isn't acting, check the operator's logs and the resource's `.status`/conditions, not just the spec.

## Namespaces and RBAC (MEDIUM)
- Namespaces isolate names and scope quotas/limits/RBAC; they are not a hard security boundary. ResourceQuota and LimitRange cap and default resources per namespace.
- RBAC: Role/RoleBinding (namespaced) vs ClusterRole/ClusterRoleBinding (cluster-wide); a ServiceAccount needs a binding to act. Recommend least privilege — flag wildcard verbs/resources and broad cluster-admin bindings.

## Troubleshooting playbook (what to look for)
- **CrashLoopBackOff**: container starts then exits repeatedly. Check `kubectl logs <pod> --previous` for the crash cause, the command/args, and missing config/env. Often a bad config, failed dependency, or liveness probe killing a healthy-but-slow app.
- **Pending**: pod unscheduled. `kubectl describe pod` events show why — insufficient CPU/memory, node selector/affinity/taint mismatch, or an unbound PVC.
- **OOMKilled (137)**: memory limit too low or a leak. Compare actual usage to the limit; raise the limit or fix the leak — don't just bump blindly.
- **ImagePullBackOff/ErrImagePull**: wrong image name/tag, private registry without imagePullSecrets, or rate limits.
- **Service has no endpoints**: selector/label mismatch or no ready pods (readiness failing). `kubectl get endpoints <svc>`.

## Pitfalls to flag in review
- Missing resource requests/limits; liveness probe tied to slow/downstream work; identical liveness and readiness on a heavy endpoint.
- `:latest` image tags (non-reproducible, breaks rollback); Secrets committed or passed as plaintext.
- Service selector not matching pod labels; Ingress with no controller.
- HPA without metrics-server or without requests; HPA plus hardcoded `replicas`.
- Cluster-admin or wildcard RBAC where a scoped Role would do; no ResourceQuota on shared namespaces.
- No `readinessProbe`, so traffic hits pods before they're ready during rollout.

When the manifest's real values aren't clear, search the user's repo before generalizing.
