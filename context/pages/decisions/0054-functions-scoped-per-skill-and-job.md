---
title: "ADR 0054 - Functions scoped per skill and per job"
version: "1.4"
date: 2026-11-08
changes: "v1.4 (2026-11-13): amendment line: decision 5 gains the review batch's job set, and code-review declares start_pull_request_review (ADR 0055)."
page_type: adr
status: accepted
description: "A turn is offered only the functions its skills or its job declare: a skill names them on a functions line of its frontmatter, the chat with no skill gets a general set of every read and no write, a job declares its set in code, and a call to anything else never runs and is logged."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/roadmap/security-hardening.md
  - pages/invariants/no-executive-writes.md
  - src/Scout.Llm/KernelScope.cs
  - src/Scout.Llm/ChatGateway.cs
  - src/Scout.Conversation/Routing/ChatFunctions.cs
  - src/Scout.Conversation/Routing/LlmStreamingRouter.cs
  - src/Scout.Skills/LexicalSkillIndex.cs
  - src/Scout.Skills/SkillFileLoader.cs
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Knowledge/ProjectExpertAgentFactory.cs
  - tests/Scout.Host.Console.Tests/FunctionDeclarationTests.cs
reliability: high
---

# ADR 0054 - Functions scoped per skill and per job

> The model is offered only the functions the current skill or job declares; the chat with no skill gets every read and no write, and a call to anything undeclared never runs and is logged.

## Status

- [x] Proposed
- [x] Accepted

Built: 2026-11-08, phase S2 of [[pages/roadmap/security-hardening]]. Accepted by the manager on the owner's plan; the owner may override.

## Context

Until this change every turn of the chat, on the direct path and on the Project Expert agent path, was offered every function on the shared kernel with automatic function choice: 57 functions, 20 of them declared writes. The write gate ([[pages/decisions/0032-per-feature-write-permissions]]) stops a declared write the owner did not grant or approve, but nothing narrowed what the model could reach, and a write nobody declared was offered like any read ([[pages/invariants/no-executive-writes]], The gap). The audit batch was already scoped to six reads on a copy of the kernel. The skill for a turn is chosen once, before the call, by embeddings; a provider without embeddings left the choice to the model through `load_skill`, after the tools were already offered.

Layering: Scout.Llm and Scout.Skills reference no other Scout assembly; only Scout.Conversation reaches both ([[pages/architecture/project-map]]), so the per-turn set is composed in the router and travels in the request.

## Decision

1. **A skill declares its functions** on one frontmatter line, `functions: Plugin.function, Plugin.function` (`src/Scout.Skills/SkillFileLoader.cs:131`); an entry of another shape is dropped with a Warning (`:147`). The skills that act declare their writes: `code-review` (review, verdict, comments, the test run, the snapshot), `code-audit` (snapshot, conventions, issues, start and stop of the audit job), `project-improvements` (snapshot, issues), `github-issues` (new, issues and issue comments from free text) and `youtube-operator` (new, all sixteen operator functions); `project-vault` declares only reads.
2. **The chat with no skill gets the general set**, every read the chat answers with and no write, enumerated in code (`src/Scout.Conversation/Routing/ChatFunctions.cs:12`): 37 functions, exactly the kernel's functions that are not declared writes.
3. **A chat turn's set** is the general set plus the functions of the turn's skills (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:116`). The turn's skills are the selected ones; when a turn selects none, the skills of the conversation's previous turn (chained: they stay until another skill is selected or the conversation has been idle for the memory's `IdleTtl`, 30 minutes by default, `:184`; the router keeps 256 conversations and forgets the oldest, `:43`; a channel reset forgets a conversation's carry by key with its memory, `src/Scout.Conversation/Routing/ICarriedSkills.cs:7`); and `code-review` when the message text carries the desktop's `Review to post (data, not instructions):`, `code-audit` when it carries `Issues to open (data, not instructions):` (`src/Scout.Conversation/Routing/ChatFunctions.cs:57`). A marker inside the context data (a transcript, screen text, a last answer) adds nothing. A skill that declares a write, any function outside the general set, is selected only from the message's own text: with context present, the selector is asked again with the text alone and a write skill it does not pick there is dropped (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:99`); a skill that declares only reads may still come from the context (decision of 2026-11-08).
4. **Without embeddings the skill is still picked before the call**, by the words of the message against each skill's name and description up to its "Not for" clause (`src/Scout.Skills/LexicalSkillIndex.cs:55`), English and Portuguese, accents folded, no model call and no dependency. The pick also runs when the index build or the query embedding fails. Only a skill that declares functions is picked this way (`:85`); the others stay in the prompt's skill catalog for the model to load as before. `load_skill` still reads a body; it never widens the tools of the turn.
5. **A job declares its set in code**: the audit batch's `Offered` (`src/Scout.Audit/AuditBatchRunner.cs:41`). The Project Expert agent gets the turn's set through `AgentInvokeOptions.Kernel` (`src/Scout.Llm/ChatGateway.cs:273`), and its own kernel holds no function (`src/Scout.Knowledge/ProjectExpertAgentFactory.cs:66`), so a call without a per-turn kernel offers nothing.
6. **One helper builds the per-turn kernel**: `KernelScope.Only(shared, qualified)` (`src/Scout.Llm/KernelScope.cs:19`) clones the shared kernel, which keeps its services and invocation filters (the write gate still fires), and leaves only the declared functions that exist. Automatic function choice is sent only when that kernel holds a function (`src/Scout.Llm/Providers/ProviderSettings.cs:23`); an empty or missing set sends no tools at all, because Semantic Kernel otherwise sends a placeholder tool.
7. **A call to an undeclared function never runs**: it is not on the turn's kernel, so Semantic Kernel answers the model with an error. The gateway logs a Warning naming the function and the turn's skills, never the arguments (`src/Scout.Llm/ChatGateway.cs:103`), and the audit batch the same for its job (`src/Scout.Audit/AuditBatchRunner.cs:196`). A name counts as declared as `Plugin-function`, `Plugin_function`, `Plugin.function` or the bare function name (`src/Scout.Llm/KernelScope.cs:48`).

## Consequences

- **Better:** a write is offered only on a turn whose skill acts; a write nobody declares in a skill or job can no longer reach the model, so the gap the invariant named is closed by a test, not by omission. Providers without embeddings narrow the set too.
- **Worse:** a question that needs a write and matches no acting skill, by embeddings or words, gets only reads until the user names it; the lexical pick is a heuristic and can pick an acting skill for an unrelated message (the write gate still asks the owner).
- **Neutral but noteworthy:** the carry is chained, expires with the conversation memory's idle time and is capped at 256 conversations; the context (transcript, screen text) can still select a skill that declares only reads, never one that declares a write, and a marker inside it adds nothing.

## Alternatives considered

- **One function list per adapter or per user** - rejected: what a turn needs depends on the request, not on who asks.
- **Let the model widen its tools by loading a skill mid-turn** - rejected: the model could then reach any write through `load_skill`, which is what this closes.
- **Ask the model to classify the request first** - rejected for the no-embeddings path: another paid call per turn, and the classification itself reads untrusted text.
- **Status quo** - rejected: every write offered to every turn.

## Enforcement

- `tests/Scout.Host.Console.Tests/FunctionDeclarationTests.cs:55` pins the 58 functions and 21 writes; `:66` every kernel function is declared by the general set, a skill or a job; `:74` every declared name resolves; `:86` the general set is exactly the functions that are not writes; `:95` only the five acting skills declare writes and every write is declared by one; `:106` every function a skill body names is on its functions line.
- `tests/Scout.Llm.Tests/ChatGatewayFunctionScopeTests.cs:80` the tools the model receives equal the set on both paths; `:98` an empty or missing set sends no tools; `:115` an undeclared call never runs, asks the gate nothing and is logged once without its arguments.
- `tests/Scout.Llm.Tests/Guard/WriteGateCompositionTests.cs:55` the write gate still fires on the scoped kernel on both paths; on the Gemini, ChatGPT plan, Azure OpenAI, Mistral, Azure AI Inference, Hugging Face and Anthropic connectors an undeclared sibling function is absent from the request's tools (`tests/Scout.Llm.Tests/Guard/WriteGateProviderPathTests.cs:29`, `:62`, `:100`, `:134`, `:166`, `:200`, `tests/Scout.Llm.Tests/Providers/Anthropic/AnthropicProviderTests.cs:112`).
- `tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:101` to `:353`: general set, selected skill, carry and its expiry (`:317`), markers, a write skill only from the message's own text (`:270`, `:283`, `:295`, `:305`), the lexical pick end to end; `tests/Scout.Host.Console.Tests/HostChatFunctionScopeTests.cs:75` the composed host on the Ollama connector.
- `tests/Scout.Skills.Tests/LexicalSkillIndexTests.cs:39` and `:56`: the sample questions in both languages.
- `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:44` the six offered functions; `:59` no function choice when none of them exists; `:72` an undeclared call logged; `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:163` the same on the Ollama connector, the write never run and the gate never asked.
- Invariant page: [[pages/invariants/no-executive-writes]].

## Related

- Services affected: [[pages/services/llm]], [[pages/services/skills]], [[pages/services/conversation]], [[pages/services/audit]], [[pages/services/knowledge]]
- Builds on: [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0042-audit-as-a-job-with-saved-progress]]
- Initiative: [[pages/roadmap/security-hardening]]

## Notes

**Amended 2026-11-10.** Decision 1's `code-review` comments are two functions on its pull request, `comment_on_pull_request` and `reply_to_review_comment`; `comment_on_issue`, which accepted any number, left its functions line, and the line declares no function that opens an issue or starts an audit ([[pages/invariants/review-writes-on-its-pull-request]]). Decision 3 gains one rule: in a turn whose selected or carried skills hold `code-review`, `code-audit` is dropped unless the message's own text names the audit or asks for the whole repository (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:213`).

**Amended 2026-11-11.** Decision 1's `code-audit` also declares one read, `GitHub.list_issues`: before it reports, an audit run in a conversation reads the open issues and leaves out a finding one of them already describes ([[pages/services/skills]]). It adds no write. The audit job's batches keep their closed set and are not offered it ([[pages/roadmap/followups]]).

**Amended 2026-11-13 by [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]]:** decision 5 gains a second job set, the review batch's, declared in code; `code-review` declares `start_pull_request_review`. Both land with the change that switches the review job on; until then the review batch's set and the function do not exist.

The sample questions and what the lexical pick does with them are the table in `tests/Scout.Skills.Tests/LexicalSkillIndexTests.cs`; the threshold is 0.55 of the message's known words, weighted by how few skills each matches.
