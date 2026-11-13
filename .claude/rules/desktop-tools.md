---
paths:
  - "ui/**"
---

# Tools for the desktop app

Angular under `ui/packages/renderer/`, the Electron main process under `ui/packages/main/`, the
preload bridge under `ui/packages/preload/`, shared types under `ui/packages/shared/`. Bun runs
everything. The rules of each part are in the rules file of that part; this file says which tool
to reach for.

## Commands, in `ui/`

| To | Run |
|---|---|
| Type-check, lint, format check | `bun run typecheck`, `bun run lint`, `bun run format` |
| Test every package | `../tools/checks/in-docker.sh bun run test`, with a count per package |
| Test one package | `../tools/checks/in-docker.sh bun run --cwd packages/<package> test` |

## Tools for Angular and Electron work

| For | Use |
|---|---|
| What Angular recommends for this version, its documentation, examples | The Angular CLI's own server, `angular-cli`: `get_best_practices`, `search_documentation`, `find_examples`, `list_projects`. Ask it before writing Angular code from memory: the framework changes between versions |
| Where a symbol is used, a rename, inspections in TypeScript | The WebStorm server, when WebStorm is open on the project |
| Seeing the change in the running app | The `run` skill |
| Building what an artboard draws | `reference/prototype.md` of the pipeline skill, and `context/pages/conventions/desktop-prototype.md`; the artboards are under `context/prototypes/desktop/` |

The renderer reaches the worker only through the preload bridge and the control plane; a new IPC
channel is a change to what the app can reach, and is planned as one.
