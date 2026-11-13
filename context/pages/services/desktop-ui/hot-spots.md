---
title: "Desktop UI: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Hot spots

- `ui/packages/main/src/worker/supervisor.ts:218` - restart policy; the watchdog test race fixed in PR 55 lives in its spec.
- `ui/packages/main/src/vault/safe-storage-vault.ts:164` - synchronous `safeStorage` API, deprecated in Electron 45, removed in 46 (tracked in [[pages/roadmap/followups]]).
- `ui/packages/main/src/control-plane/launcher.ts:231` - the host is started once per app run with no restart policy.
- `ui/build/electron-builder.yml` - `executableName` must stay filesystem-safe for AppImage; `ui/packages/main/test/packaging-config.test.ts` guards it.
- `ui/build/electron-builder.yml` - `extraMetadata.desktopName` (`scout-desktop.desktop`) is the name the running app reports to the desktop: Electron takes the X11 window class (and, by the builder's account, the Wayland app_id) from it, and without it from a slug of `productName`, so renaming the product would silently change the class. The Linux desktop entry's `StartupWMClass`, its file name (`syncDesktopName`) and its `Icon` (`executableName`) must all equal that basename, or the dock shows a generic icon for the running window; `ui/packages/main/test/packaging-config.test.ts` guards the four together and `ui/e2e/smoke/packaged-app.spec.ts` checks the entry inside the built AppImage against the running app.
- `ui/build/icons/icon.svg` - the source of the app icon, round since 2026-11-11 (a `#1a1a2e` circle on a transparent square with the white letter; the drawing is still a placeholder). `icon.png` (app, Linux hicolor set and tray), `icon.ico`, `icon.icns` and the project site's `docs/icon.png` are generated from it, in `ui/build/icons`: `rsvg-convert -w 512 -h 512 icon.svg -o icon.png && cp icon.png ../../../docs/icon.png`, then `rsvg-convert -w 1024 -h 1024 icon.svg | uv run --with pillow python -c "import io,sys; from PIL import Image; im=Image.open(io.BytesIO(sys.stdin.buffer.read())); im.save('icon.ico', sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]); im.save('icon.icns')"`. The round-icon block of `ui/packages/main/test/packaging-config.test.ts` checks the PNGs' transparent corners, navy circle and white letter, the ICO's sizes and the ICNS's 1024 px entry.
- `ui/build/electron-builder.yml:40` - `electronFuses` turns off run-as-Node, `NODE_OPTIONS` and the inspector flags, keeps the file protocol privileges the `file://` renderer needs, and turns on cookie encryption (one-way: turning it off corrupts the cookie store), asar integrity and asar-only loading; a packaged build cannot be debugged with `--inspect` ([[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]).
