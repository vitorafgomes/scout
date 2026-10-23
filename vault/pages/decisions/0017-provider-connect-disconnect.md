---
title: "ADR 0017 - Connect and disconnect cloud LLM providers, one key per provider"
version: "1.1"
date: 2026-10-23
changes: "Amended 2026-10-23: connect auto-uses, disconnect never falls back to Ollama, provider None; model choice moved to Models, Analysis"
page_type: adr
status: accepted
description: "Each cloud provider keeps its own API key, checked against the provider's model list before it is stored; a stored key can be removed, and removing the one in use switches Scout to another connected cloud provider or to None, never to Ollama."
supersedes: "0015-cloud-llm-providers-by-api-key"
superseded_by: ""
source:
  - chat
  - ui/packages/main/src/ipc/provider-keys.ts
  - ui/packages/main/src/worker/build-env.ts
  - ui/packages/renderer/src/app/core/llm-config.store.ts
  - ui/packages/renderer/src/app/sources/sources.component.ts
reliability: high
---

# ADR 0017 - Connect and disconnect cloud LLM providers, one key per provider

> A cloud card connects by storing a key the provider accepted, is used by a separate choice, and can be disconnected; disconnecting the provider in use switches Scout to another connected cloud provider or to None, never to Ollama (amended 2026-10-23).

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-23. Accepted at the owner's request on 2026-10-23 ("if putting in a key is the way to connect to a provider, and it shows connected, then there has to be a way to disconnect"). Supersedes the "One key slot" bullet of [[pages/decisions/0015-cloud-llm-providers-by-api-key]]; the rest of ADR 0015 stands. Amended the same day (see Amended 2026-10-23): the fallback to Ollama in the Decision below no longer applies.

## Context

[[pages/decisions/0015-cloud-llm-providers-by-api-key]] stored one shared key, `llm.api-key`, for whichever cloud provider was saved last, never checked it, and offered no way to remove it. A card showed Connected only while its provider was the active one, so a key for the other provider was silently overwritten and a stored key could never be taken back. The owner decided: one stored key per provider; check the key with a read call to the provider before saving it; disconnecting the provider in use falls back to Ollama and restarts the worker.

## Decision

- **One key per provider.** Vault keys `llm.openai.api-key` and `llm.gemini.api-key` (`CLOUD_API_KEY_VAULT_KEYS` in `ui/packages/shared/src/client-kinds.ts:47`). At worker start `build-env` reads the active provider's own key and falls back to the legacy `llm.api-key` (`ui/packages/main/src/worker/build-env.ts:100`); with neither it raises the existing `VaultUnavailableError` naming the provider.
- **Migration at app start.** `migrateLegacyApiKey` (`ui/packages/main/src/worker/build-env.ts:121`), awaited in `ui/packages/main/src/main.ts:159` before the window opens, copies `llm.api-key` into the active cloud provider's slot when that slot is empty and deletes the legacy entry; it does nothing when the provider is Ollama or unset, and a second run writes nothing. App start rather than worker start, because the renderer decides Connected from `vault.has` on the per-provider keys and cannot read a value to migrate it; migrating at worker start would show an existing user's provider as Not Connected until the first start.
- **The key is checked before it is stored.** IPC channel `scout:providers.validateKey` (`ui/packages/main/src/ipc/provider-keys.ts:44`) sends one `GET` to the provider's model list: OpenAI `https://api.openai.com/v1/models` with `Authorization: Bearer`, Gemini `https://generativelanguage.googleapis.com/v1beta/models` with `x-goog-api-key`, 5 s timeout. 2xx is `ok`; 401 and 403 are `invalid-key`, and so is a Gemini 400 whose JSON body carries `error.details[].reason` `API_KEY_INVALID` (what Gemini returns for a bad key; a body that is not JSON stays `unexpected`); a network error or timeout is `unreachable`; anything else is `unexpected` with the status. The URLs are fixed per provider; the renderer cannot supply one. The key goes only into that request's header; it is never logged, returned or put in an error.
- **Connect, Use and Disconnect are separate.** `LlmConfigStore.connect` validates and stores only the provider's key and leaves `llm.provider` alone; `use` writes `llm.cloud-model` and `llm.provider`; `disconnect` deletes the provider's key and, when that provider was active, first writes `llm.provider = Ollama` (leaving `llm.model` and `llm.endpoint` as they are) and deletes the legacy key. `setCloudProvider` is removed; nothing else used it.
- **The renderer reads back two non-secret settings and nothing else.** Channel `scout:vault.getPublic` (`ui/packages/main/src/ipc/vault.ts:89`) returns the value of a key on the allow-list `PUBLIC_VAULT_KEYS` (`ui/packages/shared/src/client-kinds.ts:39`): `llm.provider` and `llm.cloud-model`. Any other key, every API key and every `discord.*` entry included, is refused with `vault/invalid-key` before the vault is read. The allow-list is a separate constant from `VAULT_KEYS`, so adding a key to the vault never makes it readable. Provider and model are not secrets, and `LlmConfigStore.loadFromVault` needs them to treat the vault as the truth: the localStorage mirror only paints first and is overwritten when the read returns, so a lost or stale mirror can no longer hide which provider is in use. Keys stay write-only from the renderer.
- **Card states.** OpenAI API and Google Gemini show Not Connected (Connect opens a key form), Connected (Use, Disconnect, Change key) or In use (the model, Change model, Disconnect). Use and Change model restart a running worker. Disconnecting the provider in use restarts a running worker and says "Disconnected. Scout is back on Ollama."; disconnecting another provider needs no restart. If the key cannot be deleted after `llm.provider` was already set to Ollama, the worker still restarts on Ollama, the card shows the error and stays Connected because the key is still stored.

## Amended 2026-10-23

The owner changed decision 3 on 2026-10-23: "it has to answer with the provider that is connected" and "I don't want to use Ollama". The Decision above is kept as written; these rules replace its Ollama fallback and its "connect leaves `llm.provider` alone" rule. The TypeSafe (Jev) guard is not a chat provider and does not count here.

- **Connect uses the provider when nothing else is in use.** If `llm.provider` is not a connected cloud provider (Ollama, `None` or unset), a successful connect also writes `llm.cloud-model` (the provider's default model) and `llm.provider`, and the card restarts a running worker (`ui/packages/renderer/src/app/core/llm-config.store.ts:251`). If another cloud provider is in use, connect only stores the key; Use switches.
- **Disconnecting the provider in use never falls back to Ollama.** If the other cloud provider is connected, Scout switches to it with its default model and restarts ("Disconnected. Scout now answers with <other>."); otherwise it writes `llm.provider = None` and restarts ("Disconnected. No AI provider is connected; Scout will say so until you connect one."). `ui/packages/renderer/src/app/core/llm-config.store.ts:281`, card at `ui/packages/renderer/src/app/sources/sources.component.ts:1270`.
- **Ollama is an explicit choice.** The Ollama card offers Use while its server answers a scan; Use writes `llm.provider = Ollama` and restarts a running worker, and the card then shows In use (`ui/packages/renderer/src/app/sources/sources.component.ts:1313`, store at `ui/packages/renderer/src/app/core/llm-config.store.ts:330`). Ollama and LM Studio have no key and no Disconnect.
- **Provider `None` in the worker.** `build-env` passes `SCOUT__LLM__PROVIDER=None` with no provider variables (`ui/packages/main/src/worker/build-env.ts:75`); `Scout.Llm` selects `NoneLlmProviderFactory` (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:100`), which answers every chat request with "No AI provider is connected. Connect one on the AI Sources screen in Scout." without calling any model, and whose embeddings throw `InvalidOperationException("No AI provider is connected.")` ([[pages/services/llm]]). A blank or absent provider still means Ollama ([[pages/invariants/local-first-default]]).
- **Model choice moved to Models, Analysis (2026-10-23).** The owner asked that the model be chosen under Models, Analysis rather than on the AI Sources cards ("that should be in Model, inside Analysis"; "when connected to a provider, if it has models, they must show there"; "the person can select which one to use"). The cards keep Connect, Change key and Disconnect; Use and Change model are replaced by the link "Choose a model in Models → Analysis". Choosing a model there writes `llm.cloud-model` and `llm.provider` through `LlmConfigStore.use` and restarts a running worker (`ui/packages/renderer/src/app/models/models.component.ts:740`). The connect, auto-use and disconnect rules above are unchanged. The model lists come from `scout:llm.listModels`, a `GET` of the same model-list URLs with the stored key (`ui/packages/main/src/ipc/provider-keys.ts:161`).
- **Why:** with the fallback, disconnecting a cloud provider silently moved prompts to a local model the owner does not want, and connecting a key did nothing until a second click. Answering with the connected provider, or saying plainly that none is connected, matches what the card shows.

## Consequences

- **Better:** each provider keeps its key; a user can take a key back; a mistyped key is caught before it reaches the worker; switching between two connected providers needs no re-entry.
- **Worse:** connecting needs the network; an offline user cannot store a key until the provider answers. The check proves the key is accepted for listing models, not that the account has credit: OpenAI lists models for an account with no balance. The card copy says Connected, never "verified".
- **Neutral but noteworthy:** the renderer gains a read path into the vault, limited to two non-secret keys by an allow-list the main process enforces. Under Ollama a legacy `llm.api-key` left by ADR 0015 is not migrated and no card shows it; it stays unused until a later change removes it. After disconnecting the provider in use the renderer clears its mirror, so the ready screen asks for a local model even when the vault still holds `llm.model`.

## Alternatives considered

- **Keep one slot and add only a Remove button** - rejected by the owner; switching providers would keep losing the other key.
- **Migrate in the renderer** - impossible; the renderer has no vault read.
- **Migrate at worker start inside `buildEnv`** - smaller in lines but leaves the card wrong until the first start, and puts a write inside a function that only assembles an environment.
- **Validate with a chat completion** - costs tokens and needs a model name; a model list is a free read.

## Enforcement

- `ui/packages/main/test/ipc-provider-keys.test.ts` - status mapping per provider, header and URL per provider, the failing call (fetch rejects), the timeout, and that no log line, response or error carries the key.
- `ui/packages/main/test/worker-build-env.test.ts` - per-provider key, legacy fallback, the unchanged Ollama environment, and the migration.
- `ui/packages/renderer/src/app/core/llm-config.store.spec.ts` and `ui/packages/renderer/src/app/sources/sources.component.spec.ts` - connect, use, disconnect, the three card states and the restarts.
- `ui/packages/renderer/src/app/models/models.component.spec.ts` - Models, Analysis: one block per connected provider, choosing a model writes the provider and model and restarts a running worker, one Selected marker; `ui/packages/main/test/ipc-provider-keys.test.ts` also covers the model list filtering, header auth and its failing calls.
- Invariant pages: [[pages/invariants/secrets-never-in-source]], [[pages/invariants/local-first-default]], [[pages/invariants/no-executive-writes]] (the check is a read; disconnect deletes only Scout's own vault entry).

## Related

- Services affected: [[pages/services/desktop-ui]]
- Supersedes: the one-key-slot bullet of [[pages/decisions/0015-cloud-llm-providers-by-api-key]]
- Follow-ups: [[pages/roadmap/followups]]

## Notes

On 2026-10-23 an invalid key sent to the Gemini model list returned HTTP 400 with `"status": "INVALID_ARGUMENT"` and `"reason": "API_KEY_INVALID"`, not 401 or 403; OpenAI returned 401. The Gemini 400 branch exists for that answer.
