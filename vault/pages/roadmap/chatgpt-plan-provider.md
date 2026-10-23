---
title: "ChatGPT plan provider"
version: "1.1"
date: 2026-10-22
updated: 2026-10-23
changes: "Phases 1 and 2 built; token delivery decided"
page_type: roadmap
status: active
description: "Let the desktop owner sign in with a ChatGPT Plus or Pro plan and have Scout answer through it, via OpenAI's official Sign in with ChatGPT plan usage, entirely behind Semantic Kernel."
source:
  - chat
  - https://developers.openai.com/siwc/token-sharing-open-source
  - https://developers.openai.com/siwc/token-sharing-open-source/sign-in
  - https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
  - https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
  - https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery
reliability: medium
---

# ChatGPT plan provider

> When this is done, the owner clicks "Continue with ChatGPT" on the AI Sources screen, signs in once, and Scout answers them through their ChatGPT plan with no API key, while the LLM still goes through Semantic Kernel only.

## Why now

The owner asked for it twice on 2026-10-22. [[pages/decisions/0015-cloud-llm-providers-by-api-key]] marked the card unsupported because no sanctioned third-party flow was known. OpenAI opened one at DevDay in September 2026: open-source, locally hosted apps may use a user's ChatGPT plan through OAuth and the public Responses API. Scout qualifies (MIT, runs on the owner's machine).

## Scope

- **In scope:** OAuth sign-in from the desktop main process; token storage, refresh and sign-out; a new `Scout.Llm` provider that reaches `POST https://api.openai.com/v1/responses` through `Microsoft.Extensions.AI` and is handed to Semantic Kernel as an `IChatCompletionService`; plan error codes mapped to user messages; the ChatGPT Plus card enabled; owner-only answering while this provider is active.
- **Out of scope:** remote or paid hosting of Scout (OpenAI requires an interest form for those); hosted tools (web search, file search, code interpreter); audio input.

## Decisions (owner, 2026-10-22)

1. **Only the owner is answered** while the ChatGPT provider is active; other users are told to use an API-key provider. OpenAI's pages do not say whether one plan may serve several people; this keeps Scout inside the personal-use model they describe. Implemented on 2026-10-23 as the allowed-users list of [[pages/decisions/0021-allowed-users]], which answers the listed ids rather than only the owner and does not point refused users to an API-key provider.
2. ~~**Embeddings come from local Ollama.**~~ Superseded on 2026-10-23 by the owner's principle "I choose what to connect and Scout stays with only that provider": with the ChatGPT plan there are no embeddings at all, skill auto-selection and project search are off quietly, and skills stay reachable through `list_skills` and `load_skill`.
3. **Spike before building** (done, below).

## Spike results (2026-10-22)

A throwaway console app, outside the repository, with `Microsoft.SemanticKernel` 1.78.0 and `Microsoft.Extensions.AI.OpenAI` 10.11.0 (OpenAI SDK 2.14.0), signed in with the owner's Plus account.

| Check | Result |
|---|---|
| OAuth with `client_id=dynamic_agent_client`, PKCE S256, loopback `http://127.0.0.1:1455/auth/callback` | issued client id returned; scopes `chatgpt.tokens.use.direct email offline_access openid profile resource.invoke`; access token 3600 s plus refresh token |
| `GET /v1/models` with the token | 200; body is `{"models":[...]}` keyed by `slug` (not the API's `data`/`id`); seven models listed, e.g. `gpt-6.1-sol`, `gpt-5.6-luna` |
| `ResponsesClient(...).AsIChatClient(model)` streaming | works |
| A system-role message | 400 `System messages are not allowed`; fixed by a delegating `IChatClient` that moves system text into `ChatOptions.Instructions` |
| Semantic Kernel over `AsChatCompletionService()` with a system prompt | works with that middleware |
| Kernel function, `FunctionChoiceBehavior.Auto()`, adapter alone | the model returns a function call; nothing invokes it; empty answer |
| Same, with `UseFunctionInvocation()` on the `IChatClient` before `AsChatCompletionService()` | the kernel function runs and the answer uses it |
| A non-streaming call | 400 `Stream must be set to true`; the middleware must serve non-streaming requests by aggregating a stream |

The request shape that works: `store: false` (`CreateResponseOptions.StoredOutputEnabled = false` through `ChatOptions.RawRepresentationFactory`), no `temperature`, no `max_output_tokens`, instructions instead of system messages, streaming always.

## Workstreams

### Phase 1 - Provider in Scout.Llm
- A `ChatGptPlan` provider factory: `ResponsesClient` with the access token, wrapped by a delegating client (system to instructions, strip unsupported options, `store: false`, stream-only), then `UseFunctionInvocation()`, then `AsChatCompletionService()`. Embeddings from the Ollama factory.
- Plan error codes (`subscription_sharing_usage_limit_exceeded`, `subscription_sharing_usage_unavailable`, `subscription_sharing_invalid_user`, `subscription_sharing_user_not_eligible`, `subscription_sharing_unsupported_capability`) mapped to domain exceptions next to the quota mapping of the provider-errors fix.
- Failing-call tests through the real connector with a fake `HttpMessageHandler`, per [[pages/conventions/testing]].

### Phase 2 - Sign-in and token lifecycle in the desktop
- "Continue with ChatGPT" on the card; OAuth in the main process with a loopback listener on `127.0.0.1`, state, nonce, PKCE, a persisted `ext_agent_host_id` (`urn:uuid:`), `agent_name_hint=Scout`, ID-token validation against OpenAI's JWKS.
- Tokens in the OS keychain vault; one refresher (the main process) because refresh tokens rotate; sign-out with revocation.
- The worker gets a fresh access token without restarting every hour through a 0600 token file it re-reads per request (decided 2026-10-23, [[pages/decisions/0018-chatgpt-plan-sign-in]]).

### Phase 3 - Owner-only answering and the card
- ~~Adapters pass the author's platform id; with this provider active, a message from anyone but the configured owner gets a fixed reply naming the API-key alternative.~~ Covered on 2026-10-23 by the allowed-users list ([[pages/decisions/0021-allowed-users]]): with `ChatGptPlan` in use the worker answers only the ids the owner lists under Settings, and nobody while the list is empty. The refusal does not name the API-key alternative.
- ~~Model picker from `/v1/models`.~~ Done on 2026-10-23, and moved at the owner's request from the ChatGPT Plus card to Models, Analysis, where each connected provider lists its models ([[pages/services/desktop-ui]], [[pages/decisions/0017-provider-connect-disconnect]]).
- ~~A superseding ADR for the ChatGPT Plus part of ADR 0015~~ done: [[pages/decisions/0018-chatgpt-plan-sign-in]].

## Risks

- **Preview program.** OpenAI calls this a preview; endpoints and scopes may change. Mitigation: all of it lives behind the provider and the sign-in module.
- **Plus usage window.** Usage counts against the plan's five-hour limit shared with ChatGPT itself; `subscription_sharing_usage_limit_exceeded` must reach the user as a clear message.
- **Tool format.** The preview page says function tools must be namespaced or passed as `additional_tools`; the spike's plain kernel function worked, but plugins with many functions are untested.

## Services affected

[[pages/services/llm]], [[pages/services/desktop-ui]], [[pages/services/adapter-discord]], [[pages/services/conversation]].
