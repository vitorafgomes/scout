---
title: "ADR 0029 - Project site on GitHub Pages and desktop packages as GitHub Releases"
version: "1.5"
date: 2026-11-09
updated: 2026-11-14
changes: "Noted 2026-11-14: the desktop Beta channel offers prereleases. Amended 2026-11-09: the site knows an arm64 visitor, so the big button never offers Linux on arm64 the x86_64 package, and two packages of one kind are labelled by architecture.."
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
  - tools/release/next-version.sh
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

Updated: 2026-11-08. Amended 2026-11-02 by [[pages/decisions/0031-desktop-self-update]]: the build no longer deletes the updater metadata. Amended 2026-11-08 (below): the version is computed from the commits and the owner releases by running the workflow from `main` with a kind.

## Context

The owner asked for a project site "with a description of what the project is and the versions that can be downloaded", and for the download packages of every operating system to be published so visitors can get them. The repository page had no site. The desktop packages existed only as artifacts of the `UI CI` workflow, kept for 14 days and visible only to someone signed in to GitHub who found the right run. Electron-builder named them after the product name, `Scout Desktop-<version>-...`, with a space that GitHub rewrites in release asset names, so a download URL could not be predicted. The approved design is a dark one-page site: what Scout is, a download button for the visitor's system, one card per platform, how it works and how to start.

## Decision

- **Site.** Static HTML, one stylesheet and one ES module under `docs/`, in English, with no framework, no build step and no external resource (no web font, no CDN): `docs/index.html`, `docs/styles.css`, `docs/site.js`, the app icon `docs/icon.png` and an empty `docs/.nojekyll` so Pages serves the folder as is. GitHub Pages serves it from the `main` branch, folder `docs/`.
- **Download buttons.** The page carries no version number; the version line and "Latest release" stay hidden until the API returns a release. `docs/site.js` keeps the logic in four exported pure functions and a DOM part that runs only in a browser (`docs/site.js:152`):
  - `detectPlatform` (`docs/site.js:27`) maps the user agent and `navigator.platform` to `linux`, `mac` or `windows`; Android, iOS and anything unknown give `null`. Amended 2026-11-08: an iPad presenting the desktop user agent (platform `MacIntel` with more than one touch point, `navigator.maxTouchPoints`) gives `null` too, so it is never offered a dmg.
  - Amended 2026-11-09: a fifth function, `detectArch` (`docs/site.js:40`), gives `arm64` for a visitor known to run an arm64 machine, from `aarch64` or `arm64` in the user agent or `navigator.platform`, or from the browser's architecture hint (`navigator.userAgentData.getHighEntropyValues`, value `arm`), and `null` otherwise. A browser that reports none of the three, such as one that freezes its Linux user agent at `x86_64` and offers no hint, counts as not arm64.
  - `pickDownloads` (`docs/site.js:57`) sorts a release's assets by suffix: `.AppImage` and `.deb` for Linux, `.dmg` for macOS (labelled Apple silicon or Intel from the name), `.exe` for Windows. Blockmaps, `latest*.yml`, the checksum files and anything else are ignored. Amended 2026-11-09: when a platform has two packages of one kind, each row's label carries the architecture read from the file name, `AppImage (arm64)` beside `AppImage (x86_64)`.
  - `formatSize` (`docs/site.js:47`) prints `231 MB`, `4.3 MB` or `512 KB`.
  - `buildModel` (`docs/site.js:85`) builds what the page shows: the version without the leading `v`, the detected platform's card marked, and the big button pointing at that platform's first package (Linux when nothing is detected). With no release, or for a platform the release lacks, every link goes to `https://github.com/vitorafgomes/scout/releases/latest` with no size. Amended 2026-11-09: on Linux the big button is the first package whose name does not carry `arm64`, the AppImage before the deb, for a visitor not known to be arm64, and the first one whose name carries `arm64` for an arm64 visitor, who gets the releases page while a release has no such package. Before, it was the first AppImage by name, so Linux on arm64 was offered the `x86_64` AppImage, and an `arm64` package, which sorts first, would have gone to every x64 visitor. macOS and Windows keep their first package.
  - In a browser the module calls `GET https://api.github.com/repos/vitorafgomes/scout/releases/latest` once (`docs/site.js:108`); any error or a status other than 200 renders the no-release model. The static HTML already carries those links, so the page works with JavaScript off, and a `<noscript>` line says so.
- **The tag is the version.** The owner asked that the version number live in the GitHub Release ("the version number should be in the GitHub Releases"). The tag, without its leading `v`, is the only source of the version: the `build` job writes it into `ui/package.json` on the runner with `npm pkg set version` before packaging (`.github/workflows/release.yml:168`), so electron-builder names the packages after it and the app reports it. The `version` in the repository's `ui/package.json` stays a placeholder (`0.0.1`) and is never bumped for a release.
- **Release workflow.** `.github/workflows/release.yml` runs on a pushed `v*` tag, or by hand from a tag:
  - `check` (`.github/workflows/release.yml:27`), from a tag, fails unless the run's ref is a tag shaped `v<major>.<minor>.<patch>`, optionally followed by `-` or `.` and a suffix (`v1.2.3`, `v1.2.3-beta.1`). Started by hand from a branch other than `main`, `check` fails before any runner builds; from `main`, see the amendment of 2026-11-08. `check` hands the version, the tag, and on the `main` path the notes to the other jobs as outputs, so `build` sets the app version from them (`.github/workflows/release.yml:168`) on both paths.
  - `build` (`.github/workflows/release.yml:110`) uses the `UI CI` matrix (Ubuntu `linux-x64`, macOS `osx-arm64`, Windows `win-x64`) and its setup steps without lint, typecheck, format or tests. Amended 2026-11-08 on a review of the release workflow: `build` waits for `verify` and `verify-ui` (`.github/workflows/release.yml:102`), which call `ci.yml` and `ui-ci.yml` as reusable workflows, so the .NET build, tests and diagnostics and the UI lint, typecheck, format, tests and end-to-end tests pass on the commit before any package is built. It publishes the worker, packages with `--publish never` so electron-builder never tries to publish on a tag by itself (`.github/workflows/release.yml:198`), keeps `latest-linux.yml`, `latest.yml` and the Windows `.blockmap`, which electron-updater reads, and deletes every other YAML file, the macOS metadata and blockmaps and the Intel dmg (`.github/workflows/release.yml:210`; until ADR 0031 it deleted every blockmap and YAML file), writes `SHA256SUMS-<platform>.txt` over the packages only with `sha256sum` where it exists and `shasum -a 256` otherwise, in bash on every runner, and uploads `release-<platform>` with the kept `latest*.yml` (`.github/workflows/release.yml:223`).
  - `publish` (`.github/workflows/release.yml:227`) downloads every `release-*` artifact and runs `gh release create <tag> --verify-tag --title "Scout Desktop <tag>" --generate-notes`; when the release already exists, which happens on a rerun, it runs `gh release upload --clobber` instead (`.github/workflows/release.yml:252`). The token is the workflow's own `GITHUB_TOKEN`; the workflow runs with `contents: read` (`.github/workflows/release.yml:20`) and only the `publish` job is granted `contents: write` (`.github/workflows/release.yml:231`), so the build legs, which run third-party setup actions and the package install, never hold a write token.
  - The release is published, not a draft and not a prerelease, because the site reads `releases/latest`, which skips both.
- **Predictable names.** `artifactName` is `Scout-Desktop-${version}-${os}-${arch}.${ext}` (`ui/build/electron-builder.yml:7`). For tag `v0.0.1` the release holds `Scout-Desktop-0.0.1-linux-x86_64.AppImage`, `Scout-Desktop-0.0.1-linux-amd64.deb`, `Scout-Desktop-0.0.1-mac-arm64.dmg`, `Scout-Desktop-0.0.1-win-x64.exe`, `SHA256SUMS-linux.txt`, `SHA256SUMS-mac.txt` and `SHA256SUMS-windows.txt`.
- **Intel Macs are not released.** The macOS leg publishes only the `osx-arm64` worker, and the packaged app looks for `osx-x64` on an Intel Mac (`ui/packages/main/src/worker/resolve-binary.ts:17`), so the x64 dmg electron-builder also produces could not start its worker. The release drops it and the site says "Apple silicon".
- **Tests.** `docs/site.test.js` covers the four pure functions and the one HTTP call (`fetchLatestRelease`, `docs/site.js:108`, with an injected `fetch`: a 403 rate limit, a 404 before the first release and a network failure all yield `null`, the page's fallback) and runs as the `Test (site)` step of `UI CI` (`.github/workflows/ui-ci.yml:67`); `docs/**` is in that workflow's path filters. Amended 2026-11-09: it covers `detectArch` and the Linux pick too, for an arm64 visitor with and without an arm64 package in the release.

## Amendment 2026-11-08: computed versions, released by the owner from `main`

The owner asked (2026-11-08) whether the version changes by itself with every build, and that the owner decide what is released to users. Until then `ui/package.json` stayed at `0.0.1`, a release was a tag the owner chose and pushed, and local test builds were versioned by hand (`0.2.1-gapN`) so the updater would not replace them.

- **The next version comes from the commits.** `tools/release/next-version.sh` reads the latest `vX.Y.Z` tag reachable from `HEAD` and the conventional-commit subjects since it: a breaking change bumps the major (the minor while the major is 0), a `feat` the minor, anything else the patch. It prints `X.Y.Z`, `X.Y.Z-beta.N` or a test version `X.Y.Z-test.<UTC minute>.g<sha>`, and with `--notes` the subjects grouped under Features, Fixes and Other ([[pages/conventions/tooling]]). The tag is still the only source of the version: the script computes the tag, it does not write a version anywhere in the repository.
- **The owner releases by running the workflow from `main`.** `workflow_dispatch` takes `kind` (`release` or `prerelease`) and an optional `version` that replaces the computed one (`.github/workflows/release.yml:6`). From `main`, `check` checks out the full history, computes the version with `next-version.sh` (`release`, or `prerelease beta`), refuses a `version` that does not fit the kind and a tag that already exists, and writes the version and the notes to its job summary (`.github/workflows/release.yml:41`). `publish` then runs `gh release create <tag> --target <the run's commit> --notes-file <the notes>`, with `--prerelease` for a prerelease (`.github/workflows/release.yml:257`); `gh` creates the tag with the release. A tag pushed by hand, or a run started from a tag, behaves exactly as before: `--verify-tag --generate-notes`, never a prerelease (`.github/workflows/release.yml:259`).
- **The tag and the release appear only after every build leg passes.** Creating them before the build would leave the site and the updater pointing at a release with no packages for the half hour of the build, and a failed leg would leave an orphan tag. Cancelling the run before `publish` is how the owner stops a version they did not want. A tag created with the workflow's `GITHUB_TOKEN` starts no other workflow, which is why the whole release happens in this one run.
- **Prereleases reach nobody by default.** The site reads `releases/latest`, which skips them, and the desktop update check skips them three times: it reads `releases/latest` (`ui/packages/main/src/update/update-notice.ts:21`), drops a release marked prerelease (`ui/packages/main/src/update/update-notice.ts:286`, on the Stable channel), and sets electron-updater's `allowPrerelease` to false on that channel (`ui/packages/main/src/update/update-notice.ts:379`). The alternative rejected below (prereleases until 1.0) stays rejected; a prerelease is now an opt-in step before a release, not the default. Offering betas to people who opt in needs a desktop setting (off by default), a check against the release list (`/releases`) instead of `releases/latest`, and electron-updater's `allowPrerelease` set from that setting. The release side is ready: electron-builder writes `latest-linux.yml` for a `-beta.N` version as for a release (checked on 2026-11-08), and the build keeps it. None of the desktop side exists yet. *Update 2026-11-14:* the desktop side is built: the Beta channel in Settings offers prereleases to an owner who chooses it ([[pages/decisions/0031-desktop-self-update]], amendment of 2026-11-14).
- **Local test builds version themselves.** `bun run dist:linux:test` builds with `next-version.sh test` and restores `ui/package.json` byte for byte afterwards (`tools/release/with-test-version.sh`), so a test AppImage sorts above the last release and below the next one.

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
- The `check` job refuses a run whose ref is neither a version tag nor `main`, a `version` that does not fit the kind, and a tag that already exists.
- `tools/release/next-version.test.sh` and `tools/release/with-test-version.test.sh` run in CI's first step.
- [[pages/runbooks/release]] is the procedure to cut a release.

## Related

- Services affected: [[pages/services/desktop-ui]]
- Related conventions: [[pages/conventions/testing]]
- Earlier packaging decision: [[pages/decisions/0005-scout-desktop-m1-scope]]
