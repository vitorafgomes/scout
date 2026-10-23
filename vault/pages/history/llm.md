---
title: "Llm - history"
version: "1.0"
date: 2026-10-22
changes: "Initial history note: provider quota and billing 429s recognised on the streaming path"
page_type: reference
status: artefact
description: "What changed in the LLM gateway and when."
source:
  - chat
  - src/Scout.Llm/ChatGateway.cs
  - src/Scout.Llm/Abstractions/LlmQuotaExceededException.cs
  - src/Scout.Conversation/Streaming/StreamPump.cs
  - tests/Scout.Llm.Tests/ChatGatewayRateLimitTests.cs
reliability: high
---

# Llm - history

> Dated changes to [[pages/services/llm]]; the service page states how it works now.

## 2026-10-22 - A provider without credit was reported as a connection error

- **Symptom:** with an OpenAI key whose account had no credit, a Discord user got an empty or partial reply ending in "(connection error)". The log showed `System.ClientModel.ClientResultException: HTTP 429 (insufficient_quota: credit_balance_exhausted)` raised from the OpenAI SDK, through Semantic Kernel's streaming chat call, into `ChatGateway.StreamChatAsync`, then `StreamPump stream failed mid-flight` and `Discord response failed`.
- **Root cause:** both stream loops in `ChatGateway` caught only Semantic Kernel's `HttpOperationException` with status 429. In Semantic Kernel 1.78 the OpenAI connector wraps errors in `HttpOperationException` on the non-streaming path only; an error raised while the stream is enumerated reaches the caller as the SDK's raw `ClientResultException`. No streaming 429 from OpenAI, or from Ollama and OpenRouter, which use the same connector, was ever recognised, so every one fell through to the pump's generic failure. The rate-limit test was green because it built an `HttpOperationException` by hand, which the real streaming path never throws.
- **What changed:** one helper in `ChatGateway`, used by both stream loops, maps a 429 from either exception type. A body carrying a billing code (`credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`) or the type `insufficient_quota` becomes the new `LlmQuotaExceededException` with the provider code; any other 429, or an unreadable body, becomes `LlmRateLimitException`. `StreamPump` finalises a quota refusal through `CompleteAsync` with `StreamPumpOptions.QuotaExceededMessage`, which tells the user the account has no credit or reached its spending limit and carries no retry advice, and logs one Warning with the provider code and no stack. The generic failure footer in the Discord and Slack sinks now reads "(the AI provider returned an error; see Scout logs)" instead of claiming a connection problem; Teams already said "(response interrupted)" and is unchanged.
- **Tests:** `ChatGatewayRateLimitTests` drives the real OpenAI connector through a stubbed `HttpMessageHandler` for the quota body (chat and agent paths), the throttling body, and the absence of the API key in the exception, plus a hand-made `HttpOperationException` carrying a quota body; `StreamPumpFailureTests.Quota_Exceeded_MidStream_Tells_The_User_The_Account_Has_No_Credit`; the footer assertions in `DiscordResponseSinkTests` and `SlackResponseSinkTests`.
- **Not changed:** a production client is built without a custom `HttpClient`, so the SDK's own retry policy still retries a quota 429 before it surfaces; the user sees the right message, a few seconds late. Retry policy is out of scope here.
- **Invariants:** [[pages/invariants/pluggable-boundaries]] holds for 429s; `ClientResultException` and `HttpOperationException` with that status are translated inside `Scout.Llm`, and the domain exception keeps the SDK one only as an `InnerException` typed `Exception`. Any other status still propagates as the raw SDK exception, as it did before. [[pages/invariants/secrets-never-in-source]] holds; the domain exception carries the provider code and never the key.
