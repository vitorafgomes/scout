---
name: azure
description: Answering Microsoft Azure questions about the user's projects — choosing and configuring core services (App Service, Functions, AKS, Storage, Service Bus, Cosmos DB, Key Vault, Entra ID, Application Insights), reference architectures on Azure, Well-Architected Framework tradeoffs, Bicep/ARM infrastructure-as-code, and Azure cost and security best practices. Specific to Azure — not AWS/GCP, not generic system design, not plain Kubernetes, not language syntax.
---

# Azure

Use this when a user asks how to design, configure, secure, or cost-optimize something on Microsoft Azure: which service fits, how services connect, what a Bicep/ARM template should look like, or how an existing Azure setup measures against the Well-Architected Framework.

You ADVISE and EXPLAIN only. You may show short Bicep, ARM, or `az` CLI snippets in your reply to illustrate a recommendation, but you NEVER run `az`, never deploy, never create/modify/delete resources, and never edit files or commit. Any CLI or IaC you show is descriptive — the user runs it, not you.

**Ground advice in their real setup.** Generic Azure advice is weak. Use `search_project` for existing Bicep/ARM/`*.tf`, `azure.yaml`, `host.json`, `local.settings.json`, App Service/Functions config, connection strings, and SDK usage (`Azure.*`, `Microsoft.Azure.*`). Use `find_symbol` to trace how a client (BlobServiceClient, ServiceBusClient, CosmosClient, SecretClient) is constructed and injected. Use `get_repository` for stack and structure. If you can't find it, answer conditionally and say so.

## Service selection

- Compute: App Service / Container Apps for web apps and APIs; Functions for event-driven and short bursty work; AKS only when you genuinely need full Kubernetes control, custom networking, or operators — it carries real operational cost. Don't reach for AKS to run one container.
- Messaging: Service Bus for ordered, transactional, enterprise messaging (queues, topics, sessions, dead-letter); Event Grid for reactive event routing; Event Hubs for high-throughput streaming/telemetry. They are not interchangeable.
- Data: Cosmos DB for global distribution and flexible schema (pick the partition key carefully — it's the #1 design decision and drives RU cost and hot partitions); Azure SQL for relational/transactional; Storage (Blob/Table/Queue) for cheap durable objects and simple queues.
- Secrets and identity: Key Vault for secrets/keys/certs; Entra ID (formerly Azure AD) for identity, app registrations, and RBAC.

## Identity and security

- **Prefer managed identities over secrets.** A system- or user-assigned managed identity + Entra RBAC removes connection strings and keys from config. If you see account keys or SAS tokens hardcoded or in app settings, flag it and recommend managed identity.
- Use `DefaultAzureCredential` in code so the same path works locally and in Azure.
- Key Vault holds secrets; reference them via Key Vault references in App Service/Functions settings rather than copying values.
- Grant least-privilege RBAC at the narrowest scope (resource, not subscription). Avoid Owner where Contributor or a data-plane role suffices.
- Network: private endpoints + disabled public access for data stores in sensitive setups; NSGs and service endpoints where private link is overkill.

## Reliability and Well-Architected Framework

Assess designs against the five pillars: Reliability, Security, Cost Optimization, Operational Excellence, Performance Efficiency.

- Reliability: availability zones for stateful/critical services; retry with backoff on transient faults (the Azure SDKs retry by default — don't disable it); health checks; design for a dependency being down.
- Set autoscale rules on metrics, not guesses. Consumption/serverless plans scale to near-zero cost when idle.
- Operational: every resource should emit to Application Insights / Log Analytics. If telemetry isn't wired up, that's a gap to call out.

## Observability

- Application Insights for app traces, requests, dependencies, and exceptions; Log Analytics workspace as the backing store; query with KQL. Use distributed tracing across Functions and Service Bus to follow a request end to end.

## IaC with Bicep / ARM

- Prefer Bicep over raw ARM JSON — it's far more readable and compiles to ARM. Recommend modules for reusable components, parameters for per-environment values, and `@secure()` for secrets (never hardcode secret values in templates).
- Use what-if to preview changes (descriptive — you show the command, you don't run it). Keep state-free, idempotent templates; deploy to resource groups via pipelines, not by hand.
- If the repo already uses Terraform, advise in Terraform terms rather than pushing Bicep.

## Cost

- Right-size before scaling out; pick the cheapest plan that meets the SLA (consumption/serverless for spiky workloads, reserved instances for steady ones).
- Cosmos DB RU/s and over-provisioned SQL/App Service tiers are common silent cost sinks — check the actual tier and usage in their config before recommending.
- Storage: choose access tiers (hot/cool/archive) and lifecycle rules to age data down.

## Common pitfalls to flag

- Secrets/keys in source, app settings, or templates instead of Key Vault + managed identity.
- Cosmos partition key chosen for convenience, causing hot partitions and RU spikes.
- AKS adopted for workloads that App Service or Container Apps would run more cheaply.
- Public network access left open on databases and storage.
- No retry/timeout strategy, or SDK retries disabled, on cross-service calls.
- No Application Insights / centralized logging.
- ARM JSON hand-edited where Bicep modules would be clearer; environment values hardcoded instead of parameterized.
- When unsure how their resources are actually wired, confirm with search_project/find_symbol before asserting.
