---
title: "Desktop operator window: Production: Background"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop operator window service page."
page_type: service
status: active
description: "Read when a change touches: Background; Recording; Help marks; The screen reads the list on."
source: - "context/pages/services/desktop-ui.md" - ui/packages/main/src - ui/packages/renderer/src/app/production - ui/packages/renderer/src/app/assembly - ui/packages/renderer/src/app/reports - ui/packages/renderer/src/app/experiments - ui/packages/renderer/src/app/comments - ui/build
reliability: high
---

## Production

- **Background** (`:1649`), after Scenes - the picture the assembler puts behind every scene without its own. With `backgroundFile` set, its preview through `scout-media` with `?v=<updatedAt>`, alt text "Background of the video" (`:1665`), and "Replace background"; otherwise "No background yet: every scene without its own picture is a dark text card." (`:1670`) and "Attach background". The button (`:1678`) shows only on a `script_ready`, `media_ready`, `assembled`, `approved`, `assembly_queued`, `assembling` or `failed` project (`:37`), the control plane's set, and sends the project id, an empty scene id and the kind `background` to `scout:operator.sceneMedia` (`:3008`); the answer replaces the project and a refusal shows under the block. A Your own video project has no Background block; the Recording block takes its place (`:1611`).

- **Recording** (`:1613`), only in a Your own video project, after Scenes: "No recording yet: attach the video you recorded; Scout transcribes it on this machine." (`recording-empty`, `:1627`) while the project has no `recordingFile`, and "Attach recording", or "Replace recording" once it has one (`recording-attach`, `:1636`), on a `recording_waiting` or `queued` project or a `failed` one with no script (`:55`, `:2857`); it sends the project id, an empty scene id and the kind `recording` to `scout:operator.sceneMedia` (`:3008`), and the answer replaces the project, so the attached recording queues it for transcription on this machine. There is no preview of the recording.

- **Help marks** - a "?" button (`help-<block>`, the first at `:188`) beside the title of Mandate, Topic candidates ("The angles Scout found for this topic before writing. They stay here for reference once the script exists."), Script, Claims, Presenter, Scenes, Background and Recording, labelled "What this block is for", carries the owner's text for that block from `HELP` (`:120`, decision 6 of [[pages/roadmap/production-screen]]) as `data-tip` and `aria-description`; a CSS tooltip shows it on hover and on keyboard focus (`.help:hover::after` and `.help:focus-visible::after`, `:2117`). The button does nothing else; inside a `<summary>` a click on it, being a button, does not fold the block.

The screen reads the list on open and every 10 s while it is open (`ui/packages/renderer/src/app/production/production.component.ts:35`, `ui/packages/renderer/src/app/production/production.component.ts:2707`), and each of those polls reads the mandate and the readiness again until each has been read once (`ui/packages/renderer/src/app/production/production.component.ts:3445`), so after an outage the mandate fills in and the page banner clears when the control plane answers. The list carries whole projects, so each refresh takes the selected project's detail from it and reads that project with `scout:operator.project` only when it is no longer in the list; a click on a project reads it once. A failed list read shows the error above the sections and keeps the last list and project.
