---
name: aws
description: Answering Amazon Web Services (AWS) questions about the user's projects — choosing and configuring core AWS services (EC2, Lambda, S3, ECS/EKS/Fargate, RDS/Aurora, DynamoDB, SQS/SNS/EventBridge, API Gateway, IAM, VPC, CloudWatch), AWS reference architectures, the Well-Architected Framework, infrastructure as code with CloudFormation, CDK, or Terraform targeting AWS, and AWS cost and security best practices. Use for AWS-specific cloud design and assessment; NOT generic system architecture, NOT other clouds (Azure/GCP), NOT Kubernetes-in-general, NOT plain language/database syntax.
---

# AWS

Use this when the user asks you to recommend, explain, or assess an AWS design: which service fits, how to wire services together, IAM/networking, IaC layout, or cost and security review.

You ADVISE and EXPLAIN only. You read and recommend; you NEVER run the `aws` CLI, never apply CloudFormation/CDK/Terraform, never create, modify, or delete resources, never deploy. CLI commands and IaC snippets in your reply are illustrative examples of what the user could run — describe them, never execute them.

**Ground every answer in their real code.** Before advising:
- `search_project` for existing IaC (`*.tf`, `template.yaml`, `cdk` stacks, `serverless.yml`), SDK calls, IAM policy JSON, env/region config, and service names.
- `find_symbol` to find where a client (S3, DynamoDB, SQS) is constructed and how credentials/regions are resolved.
- `get_repository` for stack and deployment context.
If you cannot find the relevant config, say so and answer conditionally instead of inventing ARNs, bucket names, or account IDs.

## How to answer
1. Name the user's goal and constraints (scale, latency, budget, compliance). State assumptions if unknown.
2. Describe what the code/IaC actually provisions today.
3. Recommend a service or change, then **name the tradeoff** — managed vs control, cost vs latency, simplicity vs flexibility.
4. Prefer the simplest managed option that meets the need. Flag speculative complexity.

## Choosing compute
- Lambda for event-driven, spiky, or low-volume work; watch cold starts, the 15-min limit, and per-request cost at high volume.
- Fargate (ECS/EKS) for steady containerized services with no node management. EC2-backed only when you need GPUs, special instance types, or daemonsets.
- EKS only when the team already runs Kubernetes or needs its ecosystem — otherwise ECS/Fargate is cheaper to operate.
- Plain EC2 is the fallback, not the default; you own patching, scaling, and AMIs.

## Data and messaging
- DynamoDB for predictable single-digit-ms key/value access at scale; design the partition key for even distribution and model around access patterns, not normalization. Watch hot partitions.
- RDS/Aurora for relational, transactional, query-flexible workloads. Aurora Serverless v2 for variable load.
- S3 for objects/blobs/static assets, never as a database. Use lifecycle rules to tier to IA/Glacier; enable versioning for important data.
- SQS to decouple and buffer (at-least-once — make consumers idempotent, add a DLQ). SNS/EventBridge for fan-out and event routing. Pick EventBridge for schema-based routing across services.

## IAM, networking, security
- Least privilege: scope policies to specific actions and resource ARNs, never `"Action": "*"` on `"Resource": "*"`. Prefer roles over long-lived access keys; use instance/task roles, not embedded credentials.
- No secrets in code or env literals — recommend Secrets Manager or SSM Parameter Store, referenced at runtime.
- Encrypt at rest (KMS) and in transit (TLS). Block public S3 access by default; flag any public bucket policy.
- VPC: private subnets for compute and data, public only for load balancers/NAT. Security groups over wide CIDR allows.

## IaC (describe, never apply)
- Recommend CloudFormation/CDK/Terraform; show short illustrative templates or `cdk`/`terraform plan` commands as examples, but tell the user to run them.
- Keep state and stacks per-environment; parameterize region/account. Never hardcode account IDs or secrets in templates.
- Favor managed constructs and tagging for cost allocation. Prefer changes via IaC over console clicks (drift).

## Cost and Well-Architected
- Reference the six pillars: operational excellence, security, reliability, performance efficiency, cost optimization, sustainability. Frame recommendations against them.
- Cost levers: right-size instances, autoscaling, Savings Plans/Reserved for steady load, Spot for fault-tolerant batch, S3 lifecycle tiering, and killing idle resources (unattached EBS, idle NAT gateways, orphaned load balancers).
- Reliability: multi-AZ for stateful services, health checks, and a defined failure plan for every cross-service call (timeout, retry with backoff + jitter).

## Common pitfalls to flag
- Wildcard IAM policies; long-lived access keys instead of roles.
- Public S3 buckets; unencrypted data stores.
- Secrets hardcoded in code, env, or IaC templates.
- Lambda used for sustained high-throughput where Fargate is cheaper; EKS adopted with no Kubernetes need.
- SQS consumers that are not idempotent and have no DLQ; single-AZ databases.
- DynamoDB modeled like a relational schema, causing hot partitions and scatter-gather queries.
- Console-made changes that drift from IaC; unbounded autoscaling with no cost ceiling.

When the question turns to SDK or language specifics, confirm the stack with `search_project` and tailor the example to it.
