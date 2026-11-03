---
title: "ADR 0029 - Project site on GitHub Pages and desktop packages as GitHub Releases"
version: "1.1"
date: 2026-11-02
changes: "Amended: the build keeps latest*.yml and the Windows blockmap for the updater (ADR 0031)"
page_type: adr
status: accepted
description: "A static one-page site in docs/ on the main branch describes Scout and links to the latest release; a version tag builds the desktop packages on three runners and publishes them as a GitHub Release with the gh CLI; the tag is the only source of the version."
supersedes: ""
superseded_by: ""
source:
  - chat
  - docs/index.html
  - docs/site.js
  - docs/site.test.js
  - .github/workflows/release.yml
  - .github/workflows/ui-ci.yml
  - ui/build/electron-builder.yml
reliability: high
---

# ADR 0029 - Project site on GitHub Pages and desktop packages as GitHub Releases

> Scout has a one-page site at https://vitorafgomes.github.io/scout/, served by GitHub Pages from `docs/` on `main`, and every version tag publishes the desktop packages for Linux, macOS and Windows as a GitHub Release that the site's buttons point at.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-02. Amended 2026-11-02 by [[pages/decisions/0031-desktop-self-update]]: the build no longer deletes the updater metadata.

## Context

The owner asked for a project site "com uma descrição do que é o projeto e as vezes que pode fazer download", and for the download packages of every operating system to be published so visitors can get them. The repository page had no site. The desktop packages existed only as artifacts of the `UI CI` workflow, kept for 14 days and visible only to someone signed in to GitHub who found the right run. Electron-builder named them after the product name, `Scout Desktop-<version>-...`, with a space that GitHub rewrites in release asset names, so a download URL could not be predicted. The approved design is a dark one-page site: what Scout is, a download button for the visitor's system, one card per platform, how it works and how to start.

## Decision

- **Site.** Static HTML, one stylesheet and one ES module under `docs/`, in English, with no framework, no build step and no external resource (no web font, no CDN): `docs/index.html`, `docs/styles.css`, `docs/site.js`, the app icon `docs/icon.png` and an empty `docs/.nojekyll` so Pages serves the folder as is. GitHub Pages serves it from the `main` branch, folder `docs/`.
- **Download buttons.** The page carries no version number; the version line and "Latest release" stay hidden until the API returns a release. `docs/site.js` keeps the logic in four exported pure functions and a DOM part that runs only in a browser (`docs/site.js:131`):
  - `detectPlatform` (`docs/site.js:26`) maps the user agent and `navigator.platform` to `linux`, `mac` or `windows`; Android, iOS and anything unknown give `null`.
  - `pickDownloads` (`docs/site.js:47`) sorts a release's assets by suffix: `.AppImage` and `.deb` for Linux, `.dmg` for macOS (labelled Apple silicon or Intel from the name), `.exe` for Windows. Blockmaps, `latest*.yml`, the checksum files and anything else are ignored.
  - `formatSize` (`docs/site.js:37`) prints `231 MB`, `4.3 MB` or `512 KB`.
  - `buildModel` (`docs/site.js:68`) builds what the page shows: the version without the leading `v`, the detected platform's card marked, and the big button pointing at that platform's first package (Linux when nothing is detected). With no release, or for a platform the release lacks, every link goes to `https://github.com/vitorafgomes/scout/releases/latest` with no size.
  - In a browser the module calls `GET https://api.github.com/repos/vitorafgomes/scout/releases/latest` once (`docs/site.js:87`); any error or a status other than 200 renders the no-release model. The static HTML already carries those links, so the page works with JavaScript off, and a `<noscript>` line says so.
- **The tag is the version.** The owner asked that the version number live in the GitHub Release ("o numero da versao deveria ser no Releases do github"). The tag, without its leading `v`, is the only source of the version: the `build` job writes it into `ui/package.json` on the runner with `npm pkg set version` before packaging (`.github/workflows/release.yml:90`), so electron-builder names the packages after it and the app reports it. The `version` in the repository's `ui/package.json` stays a placeholder (`0.0.1`) and is never bumped for a release.
- **Release workflow.** `.github/workflows/release.yml` runs on a pushed `v*` tag, or by hand from a tag:
  - `check` (`.github/workflows/release.yml:16`) fails unless the run's ref is a tag shaped `v<major>.<minor>.<patch>`, optionally followed by `-` or `.` and a suffix (`v1.2.3`, `v1.2.3-beta.1`). Started by hand from a branch, the ref is not a tag and `check` fails before any runner builds.
  - `build` (`.github/workflows/release.yml:32`) uses the `UI CI` matrix (Ubuntu `linux-x64`, macOS `osx-arm64`, Windows `win-x64`) and its setup steps without lint, typecheck, format or tests. It publishes the worker, packages with `--publish never` so electron-builder never tries to publish on a tag by itself (`.github/workflows/release.yml:109`), keeps `latest-linux.yml`, `latest.yml` and the Windows `.blockmap`, which electron-updater reads, and deletes every other YAML file, the macOS metadata and blockmaps and the Intel dmg (`.github/workflows/release.yml:119`; until ADR 0031 it deleted every blockmap and YAML file), writes `SHA256SUMS-<platform>.txt` over the packages only with `sha256sum` where it exists and `shasum -a 256` otherwise, in bash on every runner, and uploads `release-<platform>` with the kept `latest*.yml` (`.github/workflows/release.yml:132`).
  - `publish` (`.github/workflows/release.yml:136`) downloads every `release-*` artifact and runs `gh release create <tag> --verify-tag --title "Scout Desktop <tag>" --generate-notes`; when the release already exists, which happens on a rerun, it runs `gh release upload --clobber` instead (`.github/workflows/release.yml:157`). The token is the workflow's own `GITHUB_TOKEN`; the workflow runs with `contents: read` (`.github/workflows/release.yml:9`) and only the `publish` job is granted `contents: write` (`.github/workflows/release.yml:140`), so the build legs, which run third-party setup actions and the package install, never hold a write token.
  - The release is published, not a draft and not a prerelease, because the site reads `releases/latest`, which skips both.
- **Predictable names.** `artifactName` is `Scout-Desktop-${version}-${os}-${arch}.${ext}` (`ui/build/electron-builder.yml:7`). For tag `v0.0.1` the release holds `Scout-Desktop-0.0.1-linux-x86_64.AppImage`, `Scout-Desktop-0.0.1-linux-amd64.deb`, `Scout-Desktop-0.0.1-mac-arm64.dmg`, `Scout-Desktop-0.0.1-win-x64.exe`, `SHA256SUMS-linux.txt`, `SHA256SUMS-mac.txt` and `SHA256SUMS-windows.txt`.
- **Intel Macs are not released.** The macOS leg publishes only the `osx-arm64` worker, and the packaged app looks for `osx-x64` on an Intel Mac (`ui/packages/main/src/worker/resolve-binary.ts:17`), so the x64 dmg electron-builder also produces could not start its worker. The release drops it and the site says "Apple silicon".
- **Tests.** `docs/site.test.js` covers the four pure functions and the one HTTP call (`fetchLatestRelease`, `docs/site.js:87`, with an injected `fetch`: a 403 rate limit, a 404 before the first release and a network failure all yield `null`, the page's fallback) and runs as the `Test (site)` step of `UI CI` (`.github/workflows/ui-ci.yml:64`); `docs/**` is in that workflow's path filters.

## Classification

The site and the release are the repository publishing itself through GitHub Actions, with the workflow's built-in token. Scout the product makes no new call and gains no new function, so [[pages/invariants/no-executive-writes]] and ADR 0009's descriptive and executive split are untouched. No new secret exists ([[pages/invariants/secrets-never-in-source]]). The site's one call is an unauthenticated read of public release metadata from the visitor's browser.

## Consequences

- **Better:** visitors get the right package for their system in one click, from a stable URL; the README links the same packages; every release carries checksums.
- **Worse:** the builds are not code-signed, so macOS Gatekeeper and Windows SmartScreen warn on first start; the site and the README say so, and [[pages/roadmap/followups]] tracks signing.
- **Worse:** the GitHub API allows 60 unauthenticated requests per hour per visitor IP. Over that, or when the API fails, the page falls back to the static links to the releases page, with no version or size.
- **Worse:** Intel Mac users get no package until the macOS leg also publishes the `osx-x64` worker.
- **Neutral but noteworthy:** the page has no screenshot yet; the example Discord conversation stands in ([[pages/roadmap/followups]]).

## Alternatives considered

- **A Pages deploy workflow (`actions/deploy-pages`)** - rejected: it adds a workflow and permissions for a folder that needs no build; serving `docs/` from `main` is the same result.
- **A third-party release action** - rejected: the `gh` CLI is already on every runner, and a third-party action is one more supplier with write access to the repository.
- **Bump `ui/package.json` in a PR and require the tag to match it** - rejected by the owner: the version would live in two places and every release would need a PR first; the tag alone is enough, and the repository's `version` stays a placeholder.
- **Prereleases until 1.0** - rejected: `releases/latest` ignores prereleases, so the site's buttons and the README links would find nothing.
- **Status quo (CI artifacts)** - rejected: they expire after 14 days and need a GitHub sign-in to download.

## Enforcement

- `docs/site.test.js` runs in `UI CI` on every pull request that touches `docs/**`.
- The `check` job refuses a run whose ref is not a version tag.
- [[pages/runbooks/release]] is the procedure to cut a release.

## Related

- Services affected: [[pages/services/desktop-ui]]
- Related conventions: [[pages/conventions/testing]]
- Earlier packaging decision: [[pages/decisions/0005-scout-desktop-m1-scope]]
