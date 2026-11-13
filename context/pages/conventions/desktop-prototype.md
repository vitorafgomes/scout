---
title: "Desktop prototype"
version: "1.15"
date: 2026-11-07
updated: 2026-11-12
changes: "v1.15 (2026-11-12): the Main artboard draws a review that found nothing."
page_type: convention
status: active
description: "The clickable prototype of Scout Desktop is kept in context/prototypes/desktop, one artboard per screen; every change to a screen, and every new screen, updates or adds its artboard in the same branch, before the code."
source: ["chat", "context/prototypes/desktop/canvas.json"]
reliability: high
---

# Desktop prototype

> The prototype is part of the documentation: what a screen should look like lives beside what it does, and both change together.

The owner, 2026-11-07, translated: "is there a way to save the prototypes in [the context folder], and when we have to change or create something new we adjust it there too, to keep everything documented?"

## The rule

- **One artboard per screen**, in `context/prototypes/desktop/`, listed in `context/prototypes/desktop/canvas.json`. Each artboard shows the screen in both themes and carries a "Preview state" control for its states (empty, loading, error, connected, a dialog open).
- **A change to a screen updates its artboard in the same branch**, before the code: the artboard is what the owner approves, the code follows it, and the screen's review checks the two match. A new screen adds an artboard and a row below.
- **Real content only**: the labels and fields of the component under `ui/packages/renderer/src/app/`, never placeholder text, never a third-party name. A state the code does not have yet is marked as a design in the page that plans it.
- **Viewing it**: the artboards are `.dc.html` files for the Design artifact type, which supplies their runtime (`./support.js`); that runtime is not Scout's code and is not committed. The owner's published copy is republished from this folder after each change.
- **Format and design rules** for an artboard: [[pages/architecture/desktop-ui-design-system]] for the tokens and components, [[pages/decisions/0045-theme-and-notification-center]] for the shell.

## Artboards

| File | Screen |
|---|---|
| `context/prototypes/desktop/Main.dc.html` | Chat, top bar, theme and notifications |
| `context/prototypes/desktop/Settings.dc.html` | Settings |
| `context/prototypes/desktop/Sources.dc.html` | AI Sources |
| `context/prototypes/desktop/Models.dc.html` | Models |
| `context/prototypes/desktop/Features.dc.html` | Features |
| `context/prototypes/desktop/Review.dc.html` | Approvals |
| `context/prototypes/desktop/Meetings.dc.html` | Meetings |
| `context/prototypes/desktop/Logs.dc.html` | Logs |
| `context/prototypes/desktop/QuickAsk.dc.html` | Quick ask window |
| `context/prototypes/desktop/FloatingButton.dc.html` | Floating button |
| `context/prototypes/desktop/Wizard.dc.html` | First-run wizard |
| `context/prototypes/desktop/Components.dc.html` | Components |
| `context/prototypes/desktop/Production.dc.html` | Operator: Production |
| `context/prototypes/desktop/Assembly.dc.html` | Operator: Assembly |
| `context/prototypes/desktop/Comments.dc.html` | Operator: Comments |
| `context/prototypes/desktop/Reports.dc.html` | Operator: Reports |
| `context/prototypes/desktop/Experiments.dc.html` | Operator: Experiments |

2026-11-08: the Models, Features, Main, Quick ask, Approvals, Logs and Settings artboards were brought in line with the code of the phase-2 branch: the Speech tab states, the GitHub panel's bot name, handle and review request, the bell's mention entry, the conversation partial and errors, the Rejected-before-the-grant card, the Approvals badge, History's approved actions and Signed as column, the partial copy count and Discord's Needs a server card.

2026-11-08: the built app was compared with every artboard; the differences per screen and the migration order, one version per screen, are in [[pages/roadmap/prototype-migration]].

2026-11-10: two proposals drawn for the owner's approval before any code: the Settings artboard's floating button has one Display row ("Follow the mouse cursor", then each monitor by name with the primary one marked), not drawn with one monitor, in place of the Display and Monitor rows; the Production artboard's "More options: format and direction" draws the formats as radio cards with an icon, a blurb and, when off, the reason, Podcast included, with the states More options closed, More options open, A format that is off and Podcast chosen. The code does not follow either yet; [[pages/roadmap/prototype-migration]] has the notes per screen.

2026-11-10: a third proposal drawn for the owner's approval: in the Main artboard's notification bell, under In progress, a running audit has two round 28 px icon buttons at the right of its title, Pause (two bars) and Stop (a square); a running test run in a container has Stop only, because a run can be cancelled but not paused; a paused audit has Continue (a triangle) and Stop, and the text action "Continue the audit" is gone. A paused entry says why: the usage limit sentence as before, or "Paused by you". Stop on a running audit shows "Stopping after this area" in place of the buttons and the time, the progress bar kept, because a stop takes effect after the batch in progress ([[pages/services/audit]]); the entry then moves to Done as "Audit of acme/shop stopped" with the areas covered and the findings kept. Stop on a paused audit or on a test run moves the entry to Done at once. The buttons work in the artboard and mark the entry read. Pausing an audit from the desktop is a new capability: the product pauses a job today only when the AI Source refuses a batch (usage limit, billing, rate limit, sign-in or key).

2026-11-12: the owner approved Main's job controls drawing, the third proposal above, with five answers: the time beside a running job is the time since its last activity, the artboard's "now" being sample data; a job entry is unread again only when its state changes (paused, stopped, finished, failed) or a note comes, never at the end of each area, and the badge follows; an audit that was running when Scout closed shows in progress when it reopens, because the worker resumes it, and only a test run, which cannot resume, shows as failed; every artboard draws the same bell panel as Main, so the seven main-window artboards that still drew the paused audit with the text action "Continue the audit" (Review, Features, Sources, Models, Logs, Settings, Meetings) and the five operator artboards that drew a 380 px panel with no tabs (Assembly, Production, Reports, Comments, Experiments) now carry Main's panel markup, data and script, their other content untouched; and Main's drawing of the job controls is the spec. Main's Bell preview group gained the states only the app has, drawn as the app shows them and not approved yet: the asking step, Pausing after this area, Continuing, Stop on a paused audit ("Stopping"), Stopping the tests, five note lines, the five other pause reasons, the area named in the detail, the finished audit, the tests finished and not finished, and the two worker-health entries ([[pages/roadmap/prototype-migration]]).

2026-11-11: the Meetings artboard's notice after an export and after a delete is built, so its "Design, not built" mark is gone; the owner chose the notice and the row line both for an export. The artboard's unused "Saved <title> as Markdown" text, bound to nothing, was removed; "Saved" is the text ([[pages/roadmap/prototype-migration]]).

2026-11-11: the Models artboard leads the accessibility markup of its rows: the Analysis rows and the two Translation modes keep one row in the tab order, the checked one or else the first, and the arrow keys move the focus to the next or previous row, wrapping, without choosing; a click, Space or Enter chooses. Its Speech rows keep choosing with the arrows, because a Speech choice waits for Save, where an Analysis or Translation choice saves and restarts Scout ([[pages/roadmap/prototype-migration]]).

2026-11-11: the AI Sources artboard has no Local Models card: the owner declined the card that waited for approval, because the Ollama card already offers Manage local models; the section On this computer draws Ollama and LM Studio only, and the app follows ([[pages/roadmap/prototype-migration]]).
2026-11-11: the Sources artboard's "Local models in Ollama" dialog draws the skill-matching model before the code: a "Skill matching" section between the server panel and "Installed" with one `nomic-embed-text` row (Installed, Download, the progress bar with Cancel, or its failure), the line under "Pull a model from the Ollama library" that the first download also gets it, a model row's Download starting the skill row's too, and the Preview states "Skill model missing" and "Skill model download failed" ([[pages/roadmap/prototype-migration]]).
2026-11-12: the Sources artboard draws, before the code, what the Ollama card says after the provider in use moved away from Ollama: "Ollama freed its models from memory. It keeps running for other programs." under the Preview state "Ollama freed its memory", and "Scout could not ask Ollama to free its memory. Ollama frees it by itself after a few minutes without use." under "Ollama not asked to free memory"; its Disconnect Ollama confirmation gains "Ollama frees its memory and keeps running.", and the four server controls stay drawn disabled under "Not supported yet" ([[pages/roadmap/prototype-migration]]).
2026-11-12: the Sources artboard's "Local models in Ollama" dialog draws, before the code, every installed model: the heading "Recommended" over the catalog models not installed, the In use row's Remove disabled with "In use. Choose another model before removing it.", the Recommended row keyed `qwen2.5:3b`, and a new Preview state "Installed outside Scout" with a model outside the catalog (its tag and "llama · 3.2B · Q4_K_M · 2.0 GB on disk") and an embeddings-only model ("Embeddings only", Remove, no Use this); the Models artboard's `llama3.2:3b` row reads its tag and that line. The four server controls stay drawn disabled under "Not supported yet" ([[pages/roadmap/prototype-migration]]).
2026-11-12: the Sources artboard's "Local models in Ollama" dialog draws, before the code, "Pull by name" between Installed and Recommended (a labelled field, Pull, and the link "Browse the Ollama library", which opens `https://ollama.com/library` in the browser) with the Preview states Pull by name empty, Pull by name refused, Pulling by name, Pull by name not in the library, Pull by name failed and Pulled by name; everything else in the dialog stays as drawn ([[pages/roadmap/prototype-migration]]).

2026-11-12: the Sources and Models artboards draw, before the code, a model that cannot call tools: in the Ollama dialog its installed row carries a "No tools" chip on the warn colours and, under it, "This model cannot call tools, so Scout cannot read your repositories or cluster with it. Choose a model that supports tools.", which describes its Use this, still enabled, and the LLM Server panel says the sentence while that model is in use; in Models its Local LLM row carries the chip and the In use strip the sentence. Both add the Preview states "No tools" and "In use without tools" with the invented tag `example-chat:1b`; everything else stays as drawn ([[pages/roadmap/prototype-migration]]).

2026-11-12: the Features artboard draws, for the owner's approval, the YouTube panel states the app has and it did not, as Preview states: Google access, connected with an API key, waiting for the browser (not connected and while connected), OAuth client missing, sign-in failed and Disconnect failed; and the Connect YouTube dialog's four states (open, Save with the key empty, saving, Google refused the key), which the shared credential dialog already drew but no Preview state reached ([[pages/roadmap/prototype-migration]], Features).

2026-11-12: the Features artboard draws, for the owner's approval, the Docker panel states the app has and it did not, as Preview states: checking (the chip "Checking..." and "Scout is checking Docker on this computer."), Docker not read (the chip "Unknown", "Scout could not ask Docker for its state. Try Refresh." and Refresh on the card), a stack downloading and Download images downloading (the "Downloading 1 of 1: <image>" line over its bar), the switch not saved and saved with Scout not restarted (main's sentence under the switch), the restart waiting for the answer (the note), Clear refused, no run yet, the last run failed ("Failed (exit 1)" and the reason under the tiles), and the repositories while Docker is not running (each row's chip "Docker is not running" with Download drawn disabled and described by it, and no image groups); and, for the owner to choose between the two, a repository's detail as the app writes it ("Node · Runs on Node 20.20.2. .nvmrc asks for Node 20.") beside the artboard's "Node 20 (.nvmrc)" ([[pages/roadmap/prototype-migration]], Features).

2026-11-12: the Features artboard draws, for the owner's approval, the GitHub panel states the app has and it did not, as Preview states: a token that can write (the warning under the connected row), App not configured (the line, Connect with GitHub off), sign-in failed, bot creation failed, bot not read (the heading and the reason only), Disconnect failed (main's sentence under the panel) and bot name not read yet; and, for the owner to choose between them and the artboard's Remove at once, the Remove the bot confirmation as the app's shared one draws it and the line "Scout no longer uses the bot. The App stays on GitHub until you delete it there." with "Open the App's settings on GitHub" after a removal, with the notice ([[pages/roadmap/prototype-migration]], Features).

2026-11-12: the Features artboard's panel rows take the catalog's one sentence per row where the drawn sentence left out something the row does or said something untrue, and the GitHub panel draws its fifth read, Notifications, so the card reads Read 5 (the owner's resolution: the app shows the artboard's short sentences, the artboard gains the row Scout really reads); each rewritten sentence is listed on [[pages/roadmap/prototype-migration]] (Features, K2). The Kubernetes path pill draws `k8sDetectedPath`, sample data for the path the app detects, where it wrote `~/.kube/config` (the owner's decision). The artboard's Allow writes card draws each write's lines as a list, one line per action, and a Preview state, "Allow writes confirmation (GitHub), every action of a write on its own line, as the app lists them", draws GitHub's full actions as the app shows them; its default still draws the short sentence per write, a difference that waits for the owner.

2026-11-12: the Main artboard draws, before the code, a review that found nothing as the Findings card state "Review: no findings": the tests chips, the target line, the line "No findings." and, under it, "What Scout says it read: ...", labelled as the model's report, no finding card, no Yes to all or Post row, and the Verdict group with Approve alone, Request changes not shown. It draws no count of files read, which Scout does not measure. Request changes hidden, the read sentence on that line only and Approve under today's rules are defaults waiting for the owner's approval of the state ([[pages/roadmap/followups]]).

Related: [[pages/services/desktop-ui]], [[pages/decisions/0047-the-docker-card]], [[pages/decisions/0046-ai-source-usage-on-the-cards]].
