---
title: "Security hardening: Scout's own agent safety and a sharper security dimension"
version: "1.0"
date: 2026-11-08
changes: "Initial initiative page, from the owner's request to complement Scout with ideas from public agent-security skills and checklists, recorded by mechanism"
page_type: roadmap
status: active
description: "Measured prompt-injection resistance, functions scoped per skill and job, Electron fuses and a sender check on grant IPC, an evidence chain on security findings, and Scout's own threat model."
source:
  - chat
reliability: medium
---

# Security hardening: Scout's own agent safety and a sharper security dimension

> When this is done, Scout's rule that repository, pull request, issue and meeting content is data and never an instruction is a measured property with its own tests; the model is offered only the functions the current skill or job needs; the packaged desktop cannot be turned into a Node runtime and only the main window can grant writes; a security finding names the path from the attacker's input to the sink before it becomes an issue; and the context folder holds Scout's threat model.

On 2026-11-08 the owner asked to study six public agent-security skills, checklists and top-ten lists and to complement what Scout has. Every idea below is recorded by mechanism; nothing is copied from those projects, whatever their licence, and the mechanisms they offer that conflict with Scout's purpose are listed under Out of scope. The study notes stay in the owner's session, not here.

## Why now

Scout reads untrusted text all day (repositories, pull requests, issues, meeting audio, YouTube comments) and lets a model decide which function to call. The write contract ([[pages/decisions/0032-per-feature-write-permissions]], [[pages/invariants/no-executive-writes]]) already stops a write the owner did not grant and approve, so the worst case today is a wrong or silenced report, not an action. These items close the gaps that remain before more features read more untrusted text.

## What Scout already has

- **The write contract.** Every call leaving the process is declared read, write or irreversible; writes run only under the owner's grant; irreversible actions need the owner's approval in the desktop; every write is logged; the model and the chat can only ask. The filter is `src/Scout.Llm/Guard/WriteGateFilter.cs:19`.
- **Content is data.** The code-review and code-audit skills, and the live translator, the comment desk and the sandbox run, tell the model that the content they read is data, never instructions ([[pages/services/skills]]).
- **The sandbox.** A project's tests run only in a disposable container with no network, a read-only filesystem, an image pinned by digest and an unprivileged user ([[pages/services/sandbox]]).
- **The desktop.** Context isolation and the renderer sandbox (`ui/packages/main/src/browser-window-factory.ts:28`), a content security policy (`ui/packages/main/src/main.ts:1069`), an `openExternal` host allowlist over https (`ui/packages/main/src/ipc/app.ts:6`), credentials in the OS keyring ([[pages/invariants/secrets-never-in-source]]).

## Scope

- **In scope:** the phases below, each one version with its tests; S1 and S5 also get an ADR.
- **Out of scope:** anything that writes or edits code (remediation patches, generated fixes), skills that update themselves or follow instructions from a URL, scanners or detectors run on the host (anything that runs belongs in the sandbox behind the owner's switch), and copying any text, table or rule from the studied projects; category ids from public taxonomies are cited by id only.

## Target outcome

By 2027-01-31: an injection eval suite runs in CI and fails when a planted instruction silences a finding or reaches a write; no LLM turn is offered a function its skill or job did not declare; the packaged app ships with the Node-runtime fuses off; the grant and approve channels refuse a call from any window but the main one; code-audit and code-review security findings carry their evidence chain; the threat model page exists and the review gate has an agent-safety lens.

## Status

- **As of 2026-11-08:** S1 done ([[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]); S2 done ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]). Placed after the persistence foundation (store interfaces, the architecture test, the EF Core spike) and before the new voice and meeting features.
- **Next milestone:** S3, measured injection resistance.
- **Blocker:** none.

## Workstreams / phases

### S1 - Electron fuses and the grant sender check
- **Owner:** Scout team
- **Status:** done 2026-11-08, [[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]. The packaged app ships with run-as-Node, `NODE_OPTIONS` and the inspector flags off, and cookie encryption, asar integrity and asar-only loading on (`ui/build/electron-builder.yml:40`); the file protocol privileges stay on, because the renderer loads from `file://` and stays blank with them off; the smoke test reads them from the shipped binary (`ui/e2e/smoke/packaged-app.spec.ts:193`). Grant, revoke, enable, disable and credential delete answer only the main window's own page; approve and reject answer the main, quick-ask and operator windows, the three that show approval cards; credential entry and setup, and every other channel that stores or deletes a credential or grants or revokes access (the vault, GitHub, YouTube, Discord, Slack, usage keys, the ChatGPT plan, Kubernetes, allowed users), answer only the main window; the verdict on a pull request answers the main and quick-ask windows; the operator's deciding channels answer only the operator and main windows; a sub-frame or another page is refused; each refusal logs the channel and the sender kind (`ui/packages/main/src/ipc/sender-check.ts:41`, tests `ui/packages/main/test/ipc-features.test.ts:854`, `ui/packages/main/test/ipc-operator.test.ts:1728`, `ui/packages/main/test/ipc-github-verdict.test.ts:364`, `ui/packages/main/test/sender-check.test.ts:64`).
- **Scope:** turn off the fuses that let the packaged binary run as Node or take `NODE_OPTIONS` and inspector flags, turn on cookie encryption, asar integrity and loading only from the asar; no fuse is configured today. The grant, revoke, approve and reject channels check that the call came from the main window's frame, as [[pages/decisions/0032-per-feature-write-permissions]] says only the main window grants.
- **Success criteria:** a test reads the fuses of the built package; a call to each grant channel from any window but the main one is refused, a call to each approve or verdict channel from any window that shows no approval cards or verdict buttons is refused (the operator window shows approval cards and decides), a call to each credential or access channel from any window but the main one is refused, with a test per channel.

### S2 - Functions scoped per skill and per job
- **Owner:** Scout team
- **Status:** done 2026-11-08, [[pages/decisions/0054-functions-scoped-per-skill-and-job]]. A skill names its functions on a `functions` frontmatter line (`src/Scout.Skills/SkillFileLoader.cs:131`); the chat with no skill gets the general set, every read and no write (`src/Scout.Conversation/Routing/ChatFunctions.cs:12`); a turn's set adds its skills' functions, the previous turn's skills when it selects none, and `code-review` or `code-audit` when the desktop's review or issues marker is in the text (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:145`); without embeddings, or when they fail, an acting skill is picked by the words of the message (`src/Scout.Skills/LexicalSkillIndex.cs:55`); the gateway builds a per-turn kernel holding only that set on both paths (`src/Scout.Llm/KernelScope.cs:19`, `src/Scout.Llm/ChatGateway.cs:197`, `:264`), the agent's own kernel holds none (`src/Scout.Knowledge/ProjectExpertAgentFactory.cs:66`), the audit batch builds its kernel the same way (`src/Scout.Audit/AuditBatchRunner.cs:177`), and a call to an undeclared function never runs and is logged by name. Two skills were added: `github-issues` and `youtube-operator`.
- **Scope:** a skill, and each job (audit batch, project expert), declares the functions it may call; the kernel offers only those for that turn.
- **Success criteria:** met: the tools in the request the model receives equal the declared set on both gateway paths (`tests/Scout.Llm.Tests/ChatGatewayFunctionScopeTests.cs:80`), the audit batch is offered its six (`tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:44`), and a function no skill, job or general set declares fails `tests/Scout.Host.Console.Tests/FunctionDeclarationTests.cs:66`.

### S3 - Measured injection resistance
- **Owner:** Scout team
- **Status:** not started
- **Scope:** fixture repositories, pull requests and issue texts that carry reviewer-targeted instructions (claims that the audit passed, requests to report nothing, requests to call a write), run through code-review and code-audit against a scripted model and, on demand, a real one; the assertions are that the planted defect is still reported and that no write function is requested.
- **Success criteria:** the scripted suite runs in CI; the live suite has a runbook and a recorded score per provider.

### S4 - Evidence chain on security findings
- **Owner:** Scout team
- **Status:** not started
- **Scope:** a security finding in code-audit and code-review names the attacker-controlled source, the path to the sink, whether the sink is reachable and the blast radius, with a confidence; a finding without a chain drops in severity or is left out. Each finding carries the public category id it maps to (web, and the LLM and agentic lists when the audited repository is itself an LLM application), verified against the current edition before it is written. The security dimension also checks container files, CI workflows (unpinned actions, privileged triggers), lockfiles and install scripts, and flags invisible Unicode (tag characters, bidirectional controls, zero-width characters) in a diff.
- **Success criteria:** the skill tests assert the chain fields and the category id on a seeded finding; a seeded finding with no reachable sink is not raised as high.

### S5 - Scout's threat model and an agent-safety review lens
- **Owner:** Scout team
- **Status:** not started
- **Scope:** a page under `pages/architecture/` listing every trust boundary (chat messages, repository, pull request and issue text, meeting audio, YouTube comments, model output, sandbox output, IPC between the desktop windows and main, the gRPC socket) and the control that covers each, linked to its invariant or ADR; the review gate gains an `agent-safety` lens for diffs that touch prompts, skills, LLM-invocable functions or IPC ([[pages/conventions/tooling]]).
- **Success criteria:** every boundary on the page links a control and a test; the lens is in the pipeline table and its eval.

### Smaller items
- **Serve the renderer from a custom protocol** instead of `file://`, then turn the `grantFileProtocolExtraPrivileges` fuse off; with the fuse off today the packaged windows stay blank ([[pages/decisions/0053-electron-fuses-and-ipc-sender-check]], 2026-11-08).
- **The approval card shows the exact payload** the executor will run, not the summary the model wrote; the payload is already fixed when the action is queued.
- **Saved project conventions are tagged as repository-derived data** and can never override a skill's rules.
- **The bundled skills carry a hash recorded at build time**; a changed or added file under the skills directory is refused or flagged, since the loader reads the whole directory tree (`src/Scout.Skills/SkillFileLoader.cs:40`).

## Decisions pending

- None open: S1 and S2 have their ADRs; S5 gets one when it starts.

## Services affected

- [[pages/services/desktop-ui]], [[pages/services/llm]], [[pages/services/skills]], [[pages/services/conversation]], [[pages/services/audit]], [[pages/services/knowledge]], [[pages/services/sandbox]]

## Risks

- **Risk:** scoping functions per skill hides a function a question legitimately needs - **mitigation:** the declared list is tested per path and the chat's own skill keeps the read functions it uses today.
- **Risk:** a fuse breaks a packaged feature that relies on Node in the main process - **mitigation:** the AppImage smoke test runs the packaged app after the change.
- **Risk:** live injection scores vary by model - **mitigation:** CI asserts only the scripted suite; live scores are recorded, not gated.

## Related

- Sister pages: [[pages/roadmap/future-improvements]], [[pages/roadmap/gap-plan]]
- The audit with validated findings, which S4 sharpens: F6 on [[pages/roadmap/future-improvements]]
