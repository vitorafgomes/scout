---
paths:
  - "ui/packages/main/src/**"
  - "ui/packages/preload/src/**"
  - "ui/packages/renderer/src/**"
  - "ui/packages/shared/src/**"
  - "ui/build/**"
---

# Desktop operator window

## Rules this project must keep

- **ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel.** Here: `scout:operator.window` opens or focuses Scout's own operator window and changes nothing else; the other thirty-six `scout:operator.*` channels read or change only the control plane's own operator state (the mandate with its default format, a queued project with its format and direction, a claim decision.
- **Scout performs descriptive writes only.** Here: the media tools are fetched at build time by the packaging script only, from pinned URLs whose SHA-256, and size where the table pins one, is checked before anything is kept; nothing at run time downloads or replaces them, and neither the worker nor chat can reach the script.
- **Secrets never live in source.** Here: the fetch needs no credential and the worker environment gains nine paths, none secret.

The full statement of each, with the code and tests that hold it: `context/pages/services/desktop-operator/how-the-rules-are-kept.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]] - the operator's state lives in the control plane; these pages read and edit it there, hold no pipeline logic, and live in their own desktop window, not a local web site.
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]] - the owner's YouTube write grant gates a start, a production, an assembly, a publish, an experiment, a comment sync request, a draft request, a reply approval, a hold and using an idea; the approval of a package, its publish settings, a proposal decision, an adoption, each reply and each hold are the owner's, in the desktop. The grant itself needs the upload scopes on the Google sign-in ([[pages/services/desktop-ui|Desktop UI]], YouTube connection).
- [[pages/services/operator|Operator]] - the worker side: the stages, and the `ffmpeg` and font paths it binds under `Scout:Operator`.
- [[pages/services/control-plane|ControlPlane]] - the rules behind every refusal these pages show.
- [[pages/roadmap/youtube-channel-operator|YouTube channel operator]] - phases 2 to 6.

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Operator window | `context/pages/services/desktop-operator/operator-window.md` |
| The Production page (route operator/production in; Channel; Mandate; What Scout can make; Make a video; Projects | `context/pages/services/desktop-operator/production-the-production-page-route-operator-pr.md` |
| Project detail; Claims; Delegate; Delete | `context/pages/services/desktop-operator/production-project-detail.md` |
| Presenter | `context/pages/services/desktop-operator/production-presenter.md` |
| Scenes | `context/pages/services/desktop-operator/production-scenes.md` |
| Background; Recording; Help marks; The screen reads the list on | `context/pages/services/desktop-operator/production-background.md` |
| In main, registerOperatorHandlers (ui/packages/main/src/ipc/operator.ts:321, wired at | `context/pages/services/desktop-operator/production-in-main-registeroperatorhandlers-ui-p.md` |
| Owner files; Previews | `context/pages/services/desktop-operator/production-owner-files.md` |
| Assembly | `context/pages/services/desktop-operator/assembly.md` |
| Reports | `context/pages/services/desktop-operator/reports.md` |
| Experiments | `context/pages/services/desktop-operator/experiments.md` |
| Comments | `context/pages/services/desktop-operator/comments.md` |
| Scout Desktop ships the ffmpeg the; Pinned table; Fetch; Piper; Rhubarb; Mouths; Voices; Face models; Presenters; Packaged; Worker environment; macOS; CI | `context/pages/services/desktop-operator/media-tools-scout-desktop-ships-the-ffmpeg-the.md` |
| Licences | `context/pages/services/desktop-operator/media-tools-licences.md` |
| the evidence for a rule of this project: the code and the tests that hold it | `context/pages/services/desktop-operator/how-the-rules-are-kept.md` |
| The operator window is covered by | `context/pages/services/desktop-operator/tests-the-operator-window-is-covered-by.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
