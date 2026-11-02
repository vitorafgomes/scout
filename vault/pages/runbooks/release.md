---
title: "Release - cut a Scout Desktop release"
version: "1.0"
date: 2026-11-02
changes: "Initial runbook"
page_type: runbook
status: active
description: "Tag main with the new version, let the Release workflow publish the desktop packages, and check them on the release page and the project site."
source:
  - chat
  - .github/workflows/release.yml
  - docs/site.js
reliability: high
---

# Release - cut a Scout Desktop release

> Run this when `main` has changes that should reach people who download Scout Desktop from the project site.

## When to run this

- The owner asks for a release, or a merged change should reach the packages on https://vitorafgomes.github.io/scout/.

## Preconditions

- [ ] `main` is green in `CI` and `UI CI`: `gh run list --branch main --limit 5`
- [ ] Pick the next version from the latest release (the tag is the version; `ui/package.json` keeps a placeholder and is never bumped): `gh release list --limit 1`
- [ ] No release exists yet for the new version: `gh release list`

## Required access

- Push access to `vitorafgomes/scout` (to push a tag).
- The workflow publishes with its own `GITHUB_TOKEN`; no personal token is involved.

## Steps

1. **Tag `main` and push the tag.** The tag is `v<major>.<minor>.<patch>`, optionally with a suffix such as `-beta.1`; the workflow writes it, without the `v`, into the app's version on the runner.
   ```bash
   git switch main && git pull --ff-only
   git tag v0.0.2
   git push origin v0.0.2
   ```
   Expected: a `Release` run starts for `v0.0.2`.

2. **Watch the Release workflow.**
   ```bash
   gh run watch "$(gh run list --workflow release.yml --limit 1 --json databaseId -q '.[0].databaseId')"
   ```
   Expected: `check`, the three `build` legs and `publish` succeed. A leg takes up to 30 minutes.

3. **Check the release page.**
   ```bash
   gh release view v0.0.2
   ```
   Expected: not a draft, not a prerelease, marked Latest, with four packages and three checksum files:
   `Scout-Desktop-0.0.2-linux-x86_64.AppImage`, `Scout-Desktop-0.0.2-linux-amd64.deb`, `Scout-Desktop-0.0.2-mac-arm64.dmg`, `Scout-Desktop-0.0.2-win-x64.exe`, `SHA256SUMS-linux.txt`, `SHA256SUMS-mac.txt`, `SHA256SUMS-windows.txt`. No `.blockmap` or `.yml` file.

4. **Check the site.** Open https://vitorafgomes.github.io/scout/.
   Expected: the big button reads "Download for <your system>" and downloads your package; the line under it shows `Version 0.0.2`, the file name and its size; each platform card lists its packages with sizes.

## Verification

- `sha256sum -c SHA256SUMS-linux.txt` next to a downloaded Linux package prints `OK`.
- The site's version line matches the tag.

## When something fails

- **`check` fails** with "Tag vX is not a version": the tag is not shaped `v<major>.<minor>.<patch>[-suffix]`. Delete it (`git push origin :refs/tags/vX` and `git tag -d vX`) and push a well-formed tag.
- **Started by hand** (`workflow_dispatch`): choose the tag, not a branch, under "Use workflow from"; from a branch `check` fails with "A release runs from a version tag".
- **One `build` leg fails:** the other legs still finish (`fail-fast: false`), but `publish` waits for all of them and does not run. Fix the cause, then rerun the failed jobs from the run page; `publish` runs once every leg is green.
- **Rerun after a release exists:** `publish` sees the existing release and uploads with `gh release upload --clobber`, replacing assets with the same name and keeping the notes.
- **The site shows no version:** the GitHub API failed or the visitor passed 60 unauthenticated requests in an hour; the buttons then go to the releases page, which is the intended fallback.

## Rollback

1. Delete the release and its tag: `gh release delete vX --cleanup-tag --yes`.
2. The site's buttons move to the previous release, which is again the latest.

## Related

- ADRs: [[pages/decisions/0029-project-site-and-releases]]
- Services: [[pages/services/desktop-ui]]
- Similar runbooks: [[pages/runbooks/local-models]]
