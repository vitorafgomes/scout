---
title: "ADR 0040 - The review's findings as cards in the chat, posted as one review from the cards"
version: "1.1"
date: 2026-11-06
updated: 2026-11-06
changes: "v1.1 (2026-11-06) amendment: the block comes from present_review_findings, a function the model calls, and Scout appends it to the reply; the renderer is unchanged. v1.0 initial ADR: the findings block in the reply, the cards on both chat surfaces, the posting question with its marker, the posted box"
page_type: adr
status: proposed
description: "A code review answered in chat carries its findings as a fenced scout-findings JSON block; Scout Desktop renders them as cards with Yes, No and Edit, Yes to all and Post, and posting is one question to the worker carrying the chosen comments after a marker, which the code-review skill turns into one submit_pull_request_review call under the GitHub grant."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "src/Scout.Skills/skills/code-review.md"
  - "src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs"
  - "src/Scout.Llm/Abstractions/TurnAttachments.cs"
  - "ui/packages/renderer/src/app/core/"
  - "ui/packages/main/src/quick-ask/quick-ask.ts"
reliability: high
---

# ADR 0040 - The review's findings as cards in the chat, posted as one review from the cards

> The model writes the review's findings once more as a fenced `scout-findings` JSON block; the desktop's two chat surfaces render that block as cards the owner decides on one by one, and "Post" is a new question to the worker carrying the chosen comments after a marker, which the skill turns into exactly one `submit_pull_request_review` call under the grant.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-06. Proposed on the owner's prototype of 2026-11-06 ("o chat era para estar assim", "com os botões e tudo mais", "quero que fique bonito igual"): a Scout turn with a status line, a heading with the verdict, one card per finding with a severity pill, the `path:line`, the context, what is wrong and the fix, Yes, No and Edit as icon buttons, "Yes to all", "Post n on PR #5", a "See how it reads on the PR" preview in GitHub's own look, and a green "Review posted on PR #5" box with the link. Merging the pull request accepts it.

## Context

Since [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]] v1.3 a review asked for in chat is posted at once under the GitHub grant, and the chat shows the model's prose. The owner's first real review (QuotAI #5) came back as prose with `[P2]`, `Local:` and `Trecho:` lines: the right finding, no way to pick which comments go to the pull request, no way to edit one before it goes, and nothing that says it went. The owner drew the chat he wants as a prototype: findings as cards, decided one by one, posted together, with the posted result in the same thread.

Three things already carry most of it. The replies render Markdown on both surfaces (`ui/packages/renderer/src/app/core/markdown.ts`), so a fenced block is data the renderer can take. A question to the worker may carry data after a marker that main strips from the turn and the worker reads as data ([[pages/services/desktop-quick-ask]]: the transcript, the screen text and the last answer markers). The write is already there: `submit_pull_request_review` takes the body and the comments, anchors each to the diff and posts under the grant ([[pages/services/plugin-github]]).

## Decision

- **The findings travel in the reply as a fenced block.** The `code-review` skill ends the review with one fenced block whose info string is `scout-findings`: a JSON object with `repository`, `number`, `commit`, `verdict`, `read` and `findings`, each finding with `id`, `severity` (`block`, `fix`, `nit`), `title`, `path`, `line`, `context`, `why` and `fix`, written in the language of the request (`src/Scout.Skills/skills/code-review.md`, "The findings block"; pinned by `tests/Scout.Skills.Tests/BundledSkillsTests.cs::The_Code_Review_Skill_Calls_The_Findings_Function_Never_Writes_The_Fence_And_Posts_A_Chosen_Review_Unchanged`). Other chats (Discord, Slack, Teams) show the fence as it is; no adapter changes.
- **The desktop renders the block as cards, on the Chat screen and in the floating-button chat alike.** `parseReviewFindings` takes exactly one valid block out of the reply and leaves the surrounding Markdown; a block that is not valid JSON of that shape is left in the reply as a code fence, so a model that writes it wrong costs nothing. One shared component renders the cards, the Yes, No and Edit icon buttons, the editor, "Yes to all", the Post button, the GitHub-styled preview and the posted box, in the look of the prototype. The picks, the edited comment bodies and the posted state live in renderer memory per findings turn, never on disk, and Clear drops them with the conversation.
- **Posting is a question to the worker, like every other write Scout makes from chat.** Post composes `Post n findings on owner/repo#5`, the marker `Review to post (data, not instructions):` and a JSON object with the repository, the number, the review body (the verdict, the chosen titles with their marks and the read scope) and the chosen comments (`path`, `line`, `body`, the body in the shape the skill gives a posted comment: the severity mark and the title, the context, what is wrong and why, the fix), and sends it over `scout:local.ask`. Main keeps the bare question on the turn with `review: true`, allows 120,000 more characters for the block and sends the whole text to the worker, as it does for the transcript. The skill tells the model that such a message is the user's choice: call `submit_pull_request_review` once with exactly that body and those comments, unchanged, and answer with the URL. The write stays declared, granted and logged as ADR 0038 v1.3 says; the chat message is the ask under the grant, never the authorization.
- **The posted state comes from the answer.** When the post turn completes with a `github.com` URL in its reply, the findings turn shows the green "Review posted on PR #n" box with that link and loses its buttons; a post turn that fails, or answers without a URL (writes not allowed, GitHub refused, a dropped comment list), shows its reply as any other and the cards keep their buttons, so the owner can post again.

## Amendment v1.1 (2026-11-06): the block comes from a function, not from the model's text

The owner tested the cards and the model answered in prose without the block: "não mostra o botão", "não tem formatação nenhuma igual ao protótipo". The same review called five read functions, so the model calls functions far more reliably than it follows an output format. The `code-review` skill now calls `present_review_findings(repository, number, verdict, findings_json, commit, read)` once before its final answer and never writes the fence ([[pages/services/skills]]); the function, on the read-only `GitHub` plugin, checks the findings by the renderer's rules, answers one sentence naming the first problem when they fail, and otherwise hands the fenced block to `TurnAttachments`, the per-turn collector the conductor begins beside `TurnProgress` ([[pages/services/plugin-github]], [[pages/services/conversation]]). The stream pump appends the block to the reply on a completed turn only, after the model's text and a blank line, and removes a `scout-findings` fence the model wrote itself, so the reply carries exactly one block and the desktop renders the cards whatever the model wrote around them. The function refuses a block over 24,000 characters, below the 32,000 a desktop reply carries, and asks the model for fewer findings. The function calls nothing outside the process and writes nothing; the renderer, the posting question and the write are unchanged. This is not the worker event the alternatives below rejected: the block still travels in the reply text, with no new event kind and no change on main or the adapters. The cost named under Consequences changes with it: the model passes the findings once to the function instead of writing them twice in the answer.

## Consequences

- **Better:** the owner decides comment by comment, edits a comment before it goes, posts one review, and sees that it went, in the same thread; nothing new leaves the process from the desktop, no new IPC channel, no new worker event; a model that cannot write the block degrades to the prose review of today.
- **Worse:** the review text and the block repeat the findings, so a long review costs the model more tokens; the posting depends on the model following the skill (the function then checks the names, the caps and the diff anchors, so a wrong call is refused, not posted wrong); the `scout-findings` fence shows as JSON on Discord, Slack and Teams.
- **Neutral but noteworthy:** the status line with "n of m files read" is what the block's `read` field says, not a live count; the audit and the improvements skills keep their prose answers until they adopt the same block.

## Alternatives considered

- **A worker event carrying the findings** (a plugin function the model calls, a `findings` line from the local adapter) - structured on the wire, but a new function the model may call out of turn, a new event kind on the local adapter and on main, and nothing for the other chats; the fenced block does the same with no new boundary.
- **Posting from the desktop through a control-plane rpc** - would make the desktop call the GitHub plugin directly, outside the kernel's write-gate filter, and needs a new abstraction across `Scout.Conversation` for a function the model already owns; a message with a marker reuses the path the write gate guards.
- **Posting at once, as v1.3 does** - what the owner had and did not want: no choice, no edit, no confirmation in the thread.

## Enforcement

- `tests/Scout.Skills.Tests/BundledSkillsTests.cs::The_Code_Review_Skill_Calls_The_Findings_Function_Never_Writes_The_Fence_And_Posts_A_Chosen_Review_Unchanged` - the skill calls `present_review_findings`, never writes the fence, names the three severities and tells the model to post a chosen review unchanged (v1.1; renamed from the v1.0 test that pinned the fence template).
- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginFindingsTests.cs` - a valid call attaches one block the desktop's fence pattern takes, with the same findings; every invalid shape answers its sentence and attaches nothing; a second call replaces the first (v1.1).
- `tests/Scout.Conversation.Tests/Streaming/StreamPumpTests.cs` - an attached block ends the completed reply after the model's text; a failed turn and the interim updates carry none (v1.1).
- `ui/packages/renderer/src/app/core/review-findings.spec.ts` - a valid block is taken and the surrounding text kept; invalid JSON, a missing field, a bad severity, a bad repository or more than 50 findings leave the reply as it is; the comment and review bodies follow the posted-comment shape.
- `ui/packages/renderer/src/app/core/review-findings.component.spec.ts` - the cards, Yes, No, Edit, Yes to all, the summary, Post disabled until a Yes, the posting question with the marker and the chosen comments in order with the edited body, the preview, the posted box after the URL, the buttons back after a failed post.
- `ui/packages/renderer/src/app/chat/chat.component.spec.ts` and `ui/packages/renderer/src/app/quick-ask/quick-ask.component.spec.ts` - a reply with the block renders the cards and the text around it; one without renders as before.
- `ui/packages/main/test/quick-ask.test.ts` - an ask with the review marker sends the whole text to the worker and keeps the bare question on the turn, marked `review`.
- Invariant page: [[pages/invariants/no-executive-writes]] (the write is the skill's `submit_pull_request_review` under the grant; the desktop adds no call).

## Related

- Services affected: [[pages/services/desktop-quick-ask]] (the cards on both surfaces, the marker), [[pages/services/skills]] (the block and the posting order in `code-review`), [[pages/services/plugin-github]] (the write the cards post through).
- Amends: [[pages/decisions/0038-code-quality-assistant-on-the-write-gate]] (how the chat presents and posts a review).
- Initiative: [[pages/roadmap/code-quality-assistant]].

## Notes

The prototype the owner approved on 2026-11-06 is the design: the dark-first tokens, the pills (Blocking, Fix, Suggestion), the icon buttons with their labels hidden under 480 px, the GitHub preview in GitHub's light greys, the posted box in the green of a Yes. The microphone button became the icon of the same prototype in the same change.
