---
title: "Desktop UI: Layout"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Layout."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Layout

Four Bun packages under `ui/packages/`:

| Package | Role | Built with |
|---|---|---|
| `shared` | IPC channel names, request and response types, `VaultKey` union, the version compare and download pick | type-only |
| `main` | Electron main process: IPC handlers, vault, supervisor, Discord REST calls, OAuth loopback (ChatGPT sign-in), tray | `bun build` to ESM |
| `preload` | Context bridge that exposes the typed channels to the renderer | `bun build` to CJS |
| `renderer` | Angular 22 app: wizard, chat, sources, features, review, meetings, llm, logs and settings screens, and the operator window's production, assembly and approvals pages | `ng build` |

Packaging: `ui/build/scripts/publish-worker.sh:14` lists the two hosts, `Scout.Host.Console` and `Scout.Host.ControlPlane`, and publishes each self-contained single-file per RID into the same `ui/build/worker/<rid>/` folder (`publish-worker.ps1` does the same). `ui/build/electron-builder.yml:55` copies `src/Scout.ControlPlane.Contracts/protos/*.proto` into `resources/protos`, because the client loads the protos at runtime. `package:all` also fetches the pinned `ffmpeg` and fonts into the resources the package ships, checking each SHA-256 ([[pages/services/desktop-operator]]). The electron-builder config targets AppImage and deb on Linux, dmg on macOS, nsis on Windows, and names every package `Scout-Desktop-<version>-<os>-<arch>.<ext>` without a space (`ui/build/electron-builder.yml:7`), because GitHub rewrites spaces in release asset names. A version tag such as `v1.2.3` runs `.github/workflows/release.yml`, which writes the tag's version into `ui/package.json` on the runner (the repository keeps a placeholder `0.0.1`), builds the AppImage, the deb, the Apple silicon dmg and the Windows installer and publishes them as a GitHub Release with one `SHA256SUMS-<os>.txt` per platform; the project site links to them ([[pages/decisions/0029-project-site-and-releases]], [[pages/runbooks/release]]). The control-plane host runs with the worker folder as its working directory, so it also reads the console host's `appsettings.json`; no section overlaps today (the console reads `Scout:Llm`, the control plane `Scout:Providers`, `Scout:Features` and `Scout:LocalRuntime`).
