---
title: "Release - cut a Scout Desktop release"
version: "1.5"
date: 2026-11-08
updated: 2026-11-11
changes: "The smoke test checks the launcher entry against the running app, and the verification checks the dock shows Scout's icon for the running window. Earlier: the Release workflow runs the CI and UI CI workflows before it packages.."
page_type: runbook
status: active
description: "Preview the computed version, run the Release workflow from main as a release or a prerelease, and check the packages on the release page and the project site; a version tag pushed by hand still works."
source:
  - chat
  - .github/workflows/release.yml
  - tools/release/next-version.sh
  - tools/release/with-test-version.sh
  - ui/build/electron-builder.yml
  - ui/build/scripts/write-google-client.mjs
  - docs/site.js
  - ui/e2e/smoke/
reliability: high
---

# Release - cut a Scout Desktop release

> Run this when `main` has changes that should reach people who download Scout Desktop from the project site.

## When to run this

- The owner asks for a release, or a merged change should reach the packages on https://vitorafgomes.github.io/scout/.

## Preconditions

- [ ] `main` is green in `CI` and `UI CI`: `gh run list --branch main --limit 5`
- [ ] Preview what the run will release, from an up-to-date `main` (the tag is the version; `ui/package.json` keeps a placeholder and is never bumped):
  ```bash
  git switch main && git pull --ff-only
  bash tools/release/next-version.sh release            # or: prerelease beta
  bash tools/release/next-version.sh --notes release
  ```
  Expected: the version (for example `0.3.0`) and the commit subjects since the last release under Features, Fixes and Other. A `feat` since the last release bumps the minor, only fixes and other changes bump the patch, a breaking change (`feat!:` or a `BREAKING CHANGE:` footer) bumps the major, or the minor while the major is 0. "Nothing has been committed since vX.Y.Z" means there is nothing to release.

## Required access

- Permission to run workflows on `vitorafgomes/scout` (or push access, to push a tag by hand).
- The workflow publishes with its own `GITHUB_TOKEN`; no personal token is involved.
- The repository secret `SCOUT_GOOGLE_CLIENT_SECRET`, created once by the owner in GitHub (Settings, Secrets and variables, Actions, New repository secret) with the secret of Scout's Google OAuth Desktop client. Only the `Package` step of `.github/workflows/release.yml` receives it; `ui-ci.yml` never does, so pull request builds, forks included, never see it. Without it the release still builds: the `Package` log says `google client: not bundled` and Sign in with Google needs the user's own client file ([[pages/services/desktop-ui]], YouTube connection).

## Google client in a local build

`bun run package:all` (and every `dist` script) starts with `node build/scripts/write-google-client.mjs`. With `SCOUT_GOOGLE_CLIENT_SECRET` in the environment it writes `ui/packages/main/resources/google-client.json` (`client_id` from `SCOUT_GOOGLE_CLIENT_ID`, else `DEFAULT_GOOGLE_CLIENT_ID` in `ui/packages/main/src/oauth/google-auth.ts`, and the secret) and prints `google client: bundled`; without it, it deletes a stale copy and prints `google client: not bundled`. The file is gitignored (`ui/.gitignore`). For a local dist with the bundled client, export the secret from the owner's own client file for that one command, never into a file in the repository:

```bash
SCOUT_GOOGLE_CLIENT_SECRET="$(jq -r '.installed.client_secret // .client_secret' ~/.config/@scout/desktop/google-oauth-client.json)" \
  bun run dist:linux
```

## Smoke-test a local AppImage before staging it

A local `bun run dist:linux` leaves the AppImage in `ui/build/dist`; before it is staged for anyone to try, run the packaged smoke test on it ([[pages/decisions/0051-desktop-end-to-end-tests]], amendment of 2026-11-08):

```bash
cd ui
bun run dist:linux
bun run test:smoke
```

Expected: `6 passed` in under a minute, among them the launcher entry: the AppImage's `scout-desktop.desktop` says `StartupWMClass=scout-desktop` and `Icon=scout-desktop`, and the running app's desktop name is `scout-desktop.desktop`, the class its window reports. It needs `xvfb-run`, `dbus-run-session`, `gnome-keyring-daemon` and `unshare` with unprivileged user namespaces. It refuses to run when `ui/build/dist` holds no AppImage or more than one; delete the stale one. A failure attaches main's log under `ui/e2e/smoke/test-results`; do not stage that AppImage. The Release workflow does not run the smoke test yet.

## Test builds on this machine

`bun run dist:linux:test` in `ui/` builds the AppImage with its own version, `next-version.sh test`, for example `Scout-Desktop-0.3.0-test.202611081659.g1e084105-linux-x86_64.AppImage`: the next version, the UTC minute and the commit. It sorts above the last release, so the updater never offers to replace it with that release, and below the next one, which the updater will offer. `ui/package.json` is put back byte for byte however the build ends ([[pages/conventions/tooling]]). A test build is never published.

## Steps

1. **Run the Release workflow from `main`, choosing the kind.** The owner decides what reaches users: `release` publishes `vX.Y.Z`, marked Latest, which the site and the updater offer; `prerelease` publishes `vX.Y.Z-beta.N`, marked Pre-release, which neither the site nor the updater offers. `version` replaces the computed version when set (`1.0.0` for a release, `1.0.0-beta.1` for a prerelease).
   ```bash
   gh workflow run release.yml --ref main -f kind=release
   # a beta:            gh workflow run release.yml --ref main -f kind=prerelease
   # a chosen version:  gh workflow run release.yml --ref main -f kind=release -f version=1.0.0
   ```
   Or on GitHub: Actions, Release, Run workflow, branch `main`, then pick the kind.
   Expected: a `Release` run on `main`. Its `check` job summary reads "Releasing v0.3.0 (release)" with the notes, the same as the preview. A wrong version is stopped by cancelling the run before `publish`: the tag and the release are created only by `publish`, on the commit the run started from, once every build leg is green, so a cancelled or failed run leaves no tag and no release behind.

   A tag pushed by hand still releases as before, with generated notes and never marked Pre-release: `git tag v0.3.0 && git push origin v0.3.0`.

2. **Watch the Release workflow.**
   ```bash
   gh run watch "$(gh run list --workflow release.yml --limit 1 --json databaseId -q '.[0].databaseId')"
   ```
   Expected: `check`, then `verify` (the CI workflow) and `verify-ui` (the UI CI workflow), then the three `build` legs and `publish` succeed; nothing is packaged until both verify jobs pass. A leg takes up to 30 minutes.

3. **Check the release page.**
   ```bash
   gh release view v0.3.0
   ```
   Expected for a release: not a draft, not a prerelease, marked Latest, with the notes from the preview (a pushed tag gets GitHub's generated notes), four packages, three checksum files and the updater metadata:
   `Scout-Desktop-0.3.0-linux-x86_64.AppImage`, `Scout-Desktop-0.3.0-linux-amd64.deb`, `Scout-Desktop-0.3.0-mac-arm64.dmg`, `Scout-Desktop-0.3.0-win-x64.exe`, `SHA256SUMS-linux.txt`, `SHA256SUMS-mac.txt`, `SHA256SUMS-windows.txt`, `latest-linux.yml`, `latest.yml` and `Scout-Desktop-0.3.0-win-x64.exe.blockmap`. No `latest-mac.yml`, no mac blockmap, no Intel dmg.
   A prerelease is marked Pre-release and not Latest, with the same files, `latest-linux.yml` and `latest.yml` included (electron-builder writes `latest-linux.yml` for a `-beta.N` version too, checked on 2026-11-08). The site and the updater skip it.
   The two `latest*.yml` files and the blockmap are what Update and restart reads ([[pages/decisions/0031-desktop-self-update]]); never delete them from a release. A release without them leaves AppImage and Windows users with a failed install ("This release has no update for this package") until they use the Release notes link. The checksum files cover the packages only, not the metadata.

4. **Check the site.** Open https://vitorafgomes.github.io/scout/.
   Expected: the big button reads "Download for <your system>" and downloads your package; the line under it shows `Version 0.3.0`, the file name and its size; each platform card lists its packages with sizes.

## Verification

- `sha256sum -c SHA256SUMS-linux.txt` next to a downloaded Linux package prints `OK`.
- The site's version line matches the tag.
- On a Linux desktop, the downloaded AppImage, integrated or run, shows Scout's round icon in the dock and the task switcher, not a generic one: the launcher entry matches the running window. `xprop WM_CLASS` on the window prints `"scout-desktop", "scout-desktop"`.
- An installed older AppImage or Windows build shows "Scout 0.3.0 is available" within a day (or at once with Settings, Check now) and offers Update and restart, which downloads, restarts and reports the new version. A macOS or `.deb` install offers Download instead. A build released before Update and restart existed (0.0.1, for example) never offers it, so its users download the new version once by hand; from then on they get Update and restart.

## When something fails

- **`check` fails** with "Tag vX is not a version": the tag is not shaped `v<major>.<minor>.<patch>[-suffix]`. Delete it (`git push origin :refs/tags/vX` and `git tag -d vX`) and push a well-formed tag.
- **Started by hand from another branch:** `check` fails with "A release runs from a version tag, or by hand from main"; choose `main` (a new release) or the tag (a rebuild of that release) under "Use workflow from".
- **"The tag vX already exists":** the computed or given version was released already. Rebuild it by running the workflow from that tag, or pass another `version`.
- **"Nothing has been committed since vX":** `main` holds nothing new since that release.
- **"Version X does not fit a release":** a `version` with a suffix was given with `kind=release`, or one without a suffix with `kind=prerelease`.
- **One `build` leg fails:** the other legs still finish (`fail-fast: false`), but `publish` waits for all of them and does not run, so a run from `main` has created no tag yet. Fix the cause, then rerun the failed jobs from the run page; `publish` runs once every leg is green and creates the tag and the release then.
- **Rerun after a release exists:** `publish` sees the existing release and uploads with `gh release upload --clobber`, replacing assets with the same name and keeping the notes.
- **The site shows no version:** the GitHub API failed or the visitor passed 60 unauthenticated requests in an hour; the buttons then go to the releases page, which is the intended fallback.

## Rollback

1. Delete the release and its tag: `gh release delete vX --cleanup-tag --yes`.
2. The site's buttons move to the previous release, which is again the latest.

## Related

- ADRs: [[pages/decisions/0029-project-site-and-releases]]
- Services: [[pages/services/desktop-ui]]
- Similar runbooks: [[pages/runbooks/local-models]]
