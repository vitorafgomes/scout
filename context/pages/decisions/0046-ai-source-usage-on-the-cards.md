---
title: "ADR 0046 - What each AI Source card says about usage"
version: "1.1"
date: 2026-11-07
updated: 2026-11-07
changes: "Amended 2026-11-07: OpenRouter is connectable on its card (key checked with GET /api/v1/key, models from GET /api/v1/models), so its balance shows; an optional Admin key on the OpenAI API and Claude API cards shows the organization's spend this month from the providers' cost reports.."
page_type: adr
status: accepted
description: "Every AI Source card shows the tokens Scout itself used today and this month, counted in the gateway from each answer's usage; OpenRouter also shows the money left and its reset from its key endpoint and the real cost per answer; a ChatGPT plan shows when its usage limit was reached; every other figure the provider does not report reads 'Not reported by the provider' with a link to its own usage page."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Llm/ChatGateway.cs
  - ui/packages/main/src/ipc/usage.ts
  - https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs
  - https://platform.claude.com/docs/en/api/beta/organization/cost_report/retrieve
  - https://platform.claude.com/docs/en/manage-claude/admin-api
  - https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties
  - https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key
reliability: high
---

# ADR 0046 - What each AI Source card says about usage

> The AI Sources cards show what is true: Scout's own token count per source, OpenRouter's balance where its key endpoint gives it, the moment a plan's usage limit was hit; never a number the provider does not report.

## Status

- [x] Proposed
- [x] Accepted

Updated: 2026-11-07. The owner approved the AI Sources prototype earlier (https://claude.ai/artifact/22uDcWkkzFjf6T6T1LpWft) and asked again (translated): "we had already said that AI Sources must show the consumption on the cards". The research of the same day (documentation only, no provider called) found what each provider exposes; the prototype's "rate window" percentage for OpenAI is a per-minute throttle and its ChatGPT plan percentage and reset have no official source, so both read "Not reported by the provider".

## Decision

- **Scout's own count, every source.** The gateway reads the usage each answer carries (prompt, completion and total tokens; and OpenRouter's `usage.cost` when present) at the one place every stream update passes (`StreamTrace.Observe`, `src/Scout.Llm/ChatGateway.cs`), per source and model, and the worker reports it to main on its existing prefixed stdout channel; main keeps per source a day and a month total in a small file under Scout's own folder, resetting by local date. An answer without usage counts nothing and the card says "Usage not reported for this model".
- **OpenRouter balance.** While OpenRouter is connected, main reads `GET https://openrouter.ai/api/v1/key` with the connected key when the AI Sources screen opens and at most once every ten minutes: `limit`, `limit_remaining`, `limit_reset`, and the daily and monthly usage it reports. A declared read on the OpenRouter source; Rule I for timeout, 401 and 5xx (the card keeps the last value with its time).
- **ChatGPT plan.** The card shows "Usage limit reached at HH:MM" from the last `subscription_sharing_usage_limit_exceeded` the gateway saw, cleared by the next successful answer; no percentage and no reset time.
- **Everything else** a provider does not report reads "Not reported by the provider", with a link to that provider's own usage page.
- **No money estimate** from a price table: money appears only where the provider reports it (OpenRouter).

## Amended 2026-11-07: OpenRouter connectable, and an optional Admin key

The owner asked to see, on each AI Source card, the credit left when connected by API, and chose (translated) "do 1 and 2" of two options: (1) connect OpenRouter in the desktop, so its card shows the real balance, because OpenRouter is the only provider whose normal key reports credit left; (2) an optional Admin key for OpenAI and for Claude, in a separate field on the card, showing the organization's spend this month, never credit left. The documentation below was read on 2026-11-07; no provider was called.

- **OpenRouter is a keyed provider.** `OpenRouter` joins `CloudLlmProvider` and the card works like the OpenAI API card: Connect, Change key, Disconnect after "Disconnect OpenRouter?", In use, the Models link ([[pages/decisions/0017-provider-connect-disconnect]], Amended 2026-11-07 (OpenRouter)). Its key lives under `llm.openrouter.api-key`, which moved from the main-only keys to the renderer-writable `VaultKey` list (store, check, delete; never read back) so the same connect and disconnect path serves it. The balance read of the Decision above now runs, because a key can be stored; it re-reads at once when the stored key changes, so Change key never shows the old key's balance. The worker gets `SCOUT__LLM__OPENROUTER__APIKEY` and `SCOUT__LLM__OPENROUTER__CHAT__MODEL` (default `openai/gpt-4o-mini`) and no embeddings host: an OpenRouter provider whose embeddings block is empty has no embeddings, like the Claude API ([[pages/services/llm]]).
- **An optional Admin key on the OpenAI API and Claude API cards.** A collapsed "Organization spend" section on each connected card, and on a disconnected one while its Admin key is still stored so the key can always be removed, says "Optional. An Admin key can read your organization's billing; Scout uses it only to show this month's spend and keeps it on this computer.", and offers Add Admin key, or Remove after "Remove the Admin key?". It shows "US$ 12.40 spent this month (organization)", in the currency the provider returns (`usd` and `USD` read as US$, any other code as the code), or "The Admin key was rejected" after a 401 or 403. It never shows credit left: neither provider reports it.
- **Risk and scope of the Admin key.** An OpenAI Admin key (Organization Owner) or an Anthropic Admin key (`sk-ant-admin...`) reads and can manage the whole organization, far beyond what Scout needs; so it is optional, lives in main only under `llm.openai.admin-key` and `llm.anthropic.admin-key` (main-only vault keys, never in `PUBLIC_VAULT_KEYS`, refused by every renderer vault channel), is never sent to the worker and never logged, and Scout makes one call with it: the cost read below. The renderer sends it once, to `scout:usage.adminKeySave`, which checks it with that read and stores it only when the provider accepts it ([[pages/invariants/secrets-never-in-source]]).
- **The new outbound reads**, all declared reads made by main, none reachable by the model:
  - OpenRouter key check: `GET https://openrouter.ai/api/v1/key` with `Authorization: Bearer`; 200 valid, 401 or 403 rejected, 5 s timeout.
  - OpenRouter model list: `GET https://openrouter.ai/api/v1/models` with the stored key as a bearer token, the models whose `supported_parameters` include `tools`, because Scout answers through function calls, with their `id` and `name` in the API's order. The endpoint is public and its filter defaults to text output.
  - OpenAI organization costs: `GET https://api.openai.com/v1/organization/costs?start_time=<first of the month, 00:00 UTC, Unix seconds>&bucket_width=1d&limit=31` with `Authorization: Bearer <Admin key>`; the spend is the sum of `data[].results[].amount.value`, in `amount.currency`.
  - Anthropic cost report: `GET https://api.anthropic.com/v1/organizations/cost_report?starting_at=<first of the month, 00:00 UTC>&bucket_width=1d&limit=31` with `x-api-key: <Admin key>` and `anthropic-version: 2026-10-21`; `results[].amount` is a decimal string in the lowest currency unit (cents), so the spend is their sum divided by 100. The documentation labels this endpoint legacy and still supported.
  - Both cost reads run when AI Sources opens, at most once every ten minutes, with a 5 s timeout; a failure keeps the last value with its time ("Spend as of HH:MM; the latest read failed."). The month is the UTC calendar month, because both reports bucket by UTC day; Scout's own count keeps the local month.

## Consequences

- **Better:** the card never shows a number that only looks exact; the owner sees what Scout itself spent on every source.
- **Worse:** most balances stay unknown; one new read for OpenRouter.

## Enforcement

- `tests/Scout.Llm.Tests`: the gateway counts usage from each connector's metadata shape (OpenAI-shaped, Responses, Gemini, Mistral, Hugging Face, Ollama where present) and counts nothing when absent; the plan's usage-limit time is kept and cleared.
- `ui/packages/main/test`: the totals file (day and month rollover by local date, corrupt file starts empty), the OpenRouter read (interval, Rule I failures, the key never logged).
- `ui/packages/renderer`: the cards per provider in a matrix spec (connected or not x reported or not x limit reached).
- Amendment: `ui/packages/main/test/ipc-usage.test.ts` (the cost reads per provider, their URL and header, the 401, 403, 5xx and timeout failures keeping the last value, the key never logged or returned, the save that stores only an accepted key, the OpenRouter re-read after a key change), `ui/packages/main/test/worker-build-env.test.ts` (OpenRouter rows; the Admin keys never asked for or sent), `ui/packages/main/test/ipc-vault.test.ts` (no renderer vault channel serves an Admin key), `ui/packages/main/test/ipc-provider-keys.test.ts` (the OpenRouter key check and model list), `ui/packages/renderer/src/app/sources/sources.component.spec.ts` (the OpenRouter card states with its credit, the Organization spend matrix, add, reject and remove).

## Related

- [[pages/services/llm]], [[pages/services/desktop-ui]], [[pages/roadmap/future-improvements]] (F3), [[pages/decisions/0032-per-feature-write-permissions]]
