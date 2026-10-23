---
title: "ADR 0018 - ChatGPT plan through Sign in with ChatGPT"
version: "1.0"
date: 2026-10-23
changes: "Initial ADR"
page_type: adr
status: accepted
description: "The ChatGPT Plus card signs in through OpenAI's official browser flow for open-source local apps; the desktop owns the tokens and the worker answers through the Responses API inside Semantic Kernel."
supersedes: ""
superseded_by: ""
source:
  - chat
  - vault/pages/roadmap/chatgpt-plan-provider.md
  - https://developers.openai.com/siwc/token-sharing-open-source
reliability: high
---

# ADR 0018 - ChatGPT plan through Sign in with ChatGPT

> The owner signs in with a ChatGPT Plus or Pro plan in the system browser; the desktop keeps and refreshes the tokens, hands the worker the current access token through a 0600 file, and the worker answers through the Responses API as a Semantic Kernel chat service.

## Status

Accepted on 2026-10-23 at the owner's request. Supersedes the "ChatGPT Plus is not supported" bullet of [[pages/decisions/0015-cloud-llm-providers-by-api-key]], which said the card would be reopened only if OpenAI published a sanctioned third-party flow. OpenAI did so at DevDay in September 2026; [[pages/roadmap/chatgpt-plan-provider]] records the research and the spike.

## Context

The owner asked twice for the ChatGPT Plus card to work and then asked whether Scout could connect "by getting the credentials through the browser". OpenAI's Sign in with ChatGPT plan usage lets open-source, locally hosted apps call `POST https://api.openai.com/v1/responses` with an OAuth access token on the user's plan. A spike on 2026-10-22 with the owner's account proved sign-in, model listing, streaming, instructions and kernel function calls, and found that the endpoint rejects system-role messages, non-streaming calls, `temperature` and `max_output_tokens`.

## Decision

- **Sign-in in the desktop main process.** `ui/packages/main/src/oauth/chatgpt-auth.ts:77`: system browser, loopback `http://127.0.0.1:1455/auth/callback`, dynamic registration with `client_id=dynamic_agent_client` then the issued client id, PKCE S256, state, nonce, a persisted per-install `ext_agent_host_id`, ID-token validation against OpenAI's JWKS with Node's own crypto, and a check that `chatgpt.tokens.use.direct` was granted.
- **The desktop owns the tokens.** Session, issued client id and host id live in the OS keychain under main-only vault keys (`ui/packages/main/src/vault/safe-storage-vault.ts:6`) that the renderer's vault channels reject. One refresher, the main process, because refresh tokens rotate. Sign-out revokes the refresh token and clears the session.
- **The worker reads the access token from a file.** The main process writes `{access_token, expires_at}` atomically with mode 0600 to `<userData>/chatgpt-token.json` on sign-in and every refresh; the worker gets the path as `Scout:Llm:ChatGptPlan:TokenFile` and re-reads it per request when it changed or nears expiry. This avoids restarting the worker every hour, and keeps one refresher.
- **Inside Semantic Kernel.** `src/Scout.Llm/Providers/ChatGptPlan/ChatGptPlanLlmProviderFactory.cs:21` builds a Responses `IChatClient` wrapped by `ChatGptPlanChatClient` (system text to instructions, unsupported options removed, `store: false`, streaming only), then function invocation, then `AsChatCompletionService()`. There are no embeddings: the plan token reaches only the Responses API, and the owner chose that Scout uses only the provider they connected, so skill auto-selection and project search are off with this provider (skills stay available through `list_skills` and `load_skill`).
- **The renderer sees status only.** Channels `scout:chatgpt.signIn`, `scout:chatgpt.signOut`, `scout:chatgpt.status` (`ui/packages/shared/src/ipc-contract.ts:10`) return the signed-in flag, the account email and whether plan usage was granted; never a token.

## Consequences

- **Plan limits apply.** Usage counts against the plan's five-hour window, shared with ChatGPT itself; the usage-limit error reaches the user as the quota message.
- **Owner-only answering is not in this change.** Until it lands, anyone who can talk to the bot spends the owner's plan; the owner decided it should answer only them (roadmap phase 3).
- **A token file exists on disk** while signed in, readable only by the user; it is deleted on sign-out.
- **Preview program.** OpenAI calls this a preview; endpoints and scopes may change, and every dependency on them is in the provider and the sign-in module.

## Related

[[pages/invariants/secrets-never-in-source]], [[pages/invariants/pluggable-boundaries]], [[pages/decisions/0012-llm-communication-through-semantic-kernel]], [[pages/services/llm]], [[pages/services/desktop-ui]].
