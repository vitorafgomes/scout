---
title: "ADR 0051 - Desktop end-to-end tests"
version: "1.4"
date: 2026-11-08
changes: "v1.4 (2026-11-11): the fake worker answers a delete request, for the Meetings notices flow."
page_type: adr
status: accepted
description: "Playwright drives the built desktop app through Electron, on an isolated profile per test, with a fake worker that speaks the real line protocol and no request leaving the machine; Linux CI runs it under xvfb in a job of its own."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/conventions/testing.md
  - pages/services/adapter-local.md
  - pages/services/desktop-quick-ask.md
  - ui/e2e/
  - ui/e2e/smoke/
  - pages/roadmap/prototype-migration.md
  - src/Scout.ControlPlane/
reliability: high
---

# ADR 0051 - Desktop end-to-end tests

> The desktop has tests that open the real built app, click through it as the owner does, and check what the owner sees.

## Status

- [x] Proposed
- [x] Accepted

Built: 2026-11-08, the Chat flows. The owner, translated: "Write the ADR and start with the Chat."

## Context

Until now the desktop had no end-to-end test: `ui/e2e` was a stub whose scripts printed a no-op message. The component specs (vitest and jsdom in the renderer, `bun test` in main) render one component or call one handler, and two kinds of bug went past them:

- a template error in production code that only `ng build` caught;
- real window behaviour: focus, the frameless drag region, several windows at once.

Both need the app as it ships: the built renderer in a real Chromium, the preload bridge, main and a worker process.

## Decision

- **Playwright's Electron support drives the built app.** `_electron.launch` starts the Electron binary on `packages/main/dist/main.js`, which loads the built renderer and the built preload, as `bun run build` leaves them (`ui/e2e/fixtures/app.ts:211`). Never the AppImage: packaging adds minutes and tests nothing the Chat flows need. `@playwright/test` is pinned to the exact version `1.64.1` (published 2026-10-14, more than two weeks before this ADR) in `ui/e2e/package.json`. Electron mode downloads no browser, so there is no `playwright install`; the Electron binary itself comes from `node packages/main/node_modules/electron/install.js`, because Bun does not run Electron's install script.
- **An isolated profile per test.** Each test gets its own temporary folder: `--user-data-dir` points the profile there, `HOME` and the `XDG_*` folders point inside it, so nothing touches the owner's profile, autostart entry or kubeconfig, and the fixture refuses to go on when `app.getPath('userData')` is not inside that folder (`ui/e2e/fixtures/app.ts:261`). `WAYLAND_DISPLAY` is dropped and `--ozone-platform=x11` passed, so the X11 relaunch never fires.
- **The vault on a private keyring.** The vault refuses to work without an OS secret store, and Chromium's `basic` store counts as none, so `ui/e2e/run.sh` starts a private D-Bus session from `ui/e2e/session-bus.conf` (no service activation, so no desktop portal starts on it) with its own `gnome-keyring-daemon` in a temporary folder, its runtime folder (`XDG_RUNTIME_DIR`) included, unlocked with a fixed password, and the app is launched with `--password-store=gnome-libsecret` (`ui/e2e/fixtures/app.ts:218`). The owner's keyring is never touched: the fixture refuses to launch the app unless `run.sh` started it (`ui/e2e/fixtures/app.ts:162`), so a bare `playwright test` cannot reach the session keyring. Until 2026-11-08 the private daemon inherited the owner's runtime folder and created, then removed on exit, the control socket of the owner's own keyring daemon there; the runner now gives it a runtime folder inside its temporary folder (`ui/e2e/run.sh:11`).
- **A fake worker speaking the real line protocol.** `ui/e2e/fixtures/fake-worker.mjs` prints `SCOUT_READY`, reads one JSON request per stdin line and answers with `SCOUT_LOCAL` `begin`, cumulative `update` lines that end in the stream cursor, `complete` with the final text, and `fail` with `Cancelled.` on a `cancel`, the shapes `LocalResponseSink` writes ([[pages/services/adapter-local]]). Each test scripts its answers through a `script.json` beside the fake; a reply can wait for a file the test writes, or for the cancel, so no flow depends on timing. The fake records every line it reads, so a test can show that a refused question never reached it. Production code never reads the script.
- **The worker seam.** `resolveWorkerBinaryWithOverride` (`ui/packages/main/src/worker/resolve-binary.ts:186`), which main's supervisor calls (`ui/packages/main/src/main.ts:815`), returns the path in `SCOUT_WORKER_BIN` only when `app.isPackaged` is `false`; when the app is packaged, or the caller does not say, it returns the worker `resolveWorkerBinary` always returned. The supervisor logs the path of every worker it spawns (`ui/packages/main/src/worker/supervisor.ts:318`), so a fake worker is visible in the log. It is safe because the variable only matters to an unpackaged app, which runs from a source checkout whose owner can already change any code it runs; a packaged AppImage never runs a binary the environment names. `ui/packages/main/test/worker-resolve-binary.test.ts:111` proves the packaged app ignores it. The control plane is pointed at a missing file through the existing `SCOUT_CONTROLPLANE_BIN`, so it is unavailable in every run and the job needs no .NET; the worker then starts with no write grants, as it does whenever the control plane does not answer.
- **No network.** Three guards, each failing the test at teardown with what it caught (`ui/e2e/fixtures/app.ts:309`): a Playwright route on the app's context aborts and records any `http`, `https`, `ws` or `wss` request a window makes (`ui/e2e/fixtures/app.ts:234`); a console listener records every Content Security Policy refusal that names a remote address (`:227`), because the renderer's policy (`connect-src 'self'`) stops a remote fetch before it reaches the network, where the route would never see it; and `ui/e2e/fixtures/main-fetch-guard.cjs`, loaded into main with `-r` before `main.js` (`ui/e2e/fixtures/app.ts:215`), replaces main's `globalThis.fetch` before main captures it, rejects every call and records its address, and every address that is not loopback fails the test. A probe fetch from a window and one from main each failed the fixture with the address quoted. The guard covers `fetch`, the only HTTP client main uses in these flows; the six flows run with all three guards on and none fires.
- **Linux CI under xvfb.** The `e2e` job of `.github/workflows/ui-ci.yml` (`.github/workflows/ui-ci.yml:156`) runs on `ubuntu-latest` apart from the three-OS unit job: install, the Electron binary, `xvfb`, `gnome-keyring` and `dbus`, the user-namespace switch Electron's sandbox needs on Ubuntu 24.04 (`:192`), `bun run build`, then `bun run test:e2e`, and the Playwright report and traces uploaded only on failure (`:213`).

## Consequences

- **Better:** a flow that breaks in the built app fails a test: each Chat flow failed when the fix it protects was taken out of production code ([[pages/conventions/testing]]).
- **Worse:** CI time, one more job of a few minutes (install, Electron download, build; the six flows themselves take about 12 seconds); one more dev dependency to keep current; the suite runs on Linux only, because it needs `xvfb-run`, `dbus-run-session` and `gnome-keyring-daemon`.
- **Flake handling:** no fixed sleep anywhere; every check is a web-first assertion that waits (`toHaveText`, `toBeInViewport`, `expect.poll`); one test at a time (`workers: 1`); one retry on CI and none locally (`ui/e2e/playwright.config.ts:8`), and a test that passes only on retry shows as flaky in the report and is a bug to fix, not to retry away; a failed test keeps its trace and main's log.
- **Neutral but noteworthy:** the fake worker answers only what the Chat flows need; a later flow (quick ask, approvals, AI Sources, Settings) extends it.

## Alternatives considered

- **WebdriverIO with its Electron service** - works, but a second test stack beside Playwright, with its own runner, assertions and report.
- **Spectron** - abandoned in 2022, unsupported on the Electron versions Scout ships.
- **Component tests only** - what we had; they missed the production template error and every window behaviour above.
- **Driving the AppImage** - closer to what ships, but needs packaging on every run, and a packaged app must ignore the worker seam by design.

## Enforcement

- **Code rule / test** - `ui/packages/main/test/worker-resolve-binary.test.ts:111` (the packaged app ignores `SCOUT_WORKER_BIN`); the fixture's three network guards fail any test that reaches for the network.
- **CI** - the `e2e` job of `.github/workflows/ui-ci.yml`.
- **Convention page** - [[pages/conventions/testing]] (the UI tests section), [[pages/conventions/tooling]].

## Related

- Services affected: [[pages/services/desktop-ui]], [[pages/services/desktop-quick-ask]], [[pages/services/adapter-local]]
- Related conventions: [[pages/conventions/testing]], [[pages/conventions/tooling]]
- Invariants: [[pages/invariants/no-executive-writes]] (the seam adds no write and no outbound call)

## Notes

`SCOUT_CONTROLPLANE_BIN`, which predates this ADR, is honoured even in a packaged app (`resolveControlPlaneBinary` in `ui/packages/main/src/worker/resolve-binary.ts:51`); the safety claim above is about the worker seam only.

## Amendment: the packaged smoke test (2026-11-08)

The owner, translated: "guarantee that what is generated has the expected quality." The Chat flows above still never drive the AppImage; a separate smoke test does, after packaging, and checks only that the package starts clean. "Never the AppImage" in the Decision and "Driving the AppImage" under Alternatives stay true of the flows.

- **What it opens.** `bun run test:smoke` in `ui/` runs `ui/e2e/smoke/run.sh`, which takes the one AppImage in `ui/build/dist` and fails when there is none or more than one (`ui/e2e/smoke/run.sh:14`), extracts it with `--appimage-extract` into a temporary folder (`ui/e2e/smoke/run.sh:19`), and hands the extracted folder to Playwright, which launches its `scout-desktop`. That is the packaged app: `app.isPackaged` is true, the worker seam is ignored, and main runs the worker and the control plane shipped under `resources/worker/linux-x64`. `SCOUT_SMOKE_APP_DIR` names an already extracted folder instead of the AppImage; the mutation runs below used it. The smoke specs live under `ui/e2e/smoke/`, outside `testDir` of the flows, so `bun run test:e2e` never runs them.
- **Isolation.** The private D-Bus session, keyring and display are the flows' own: `run.sh` is reused with `-c smoke/playwright.config.ts`. On top of them:
  - the whole run sits in user, network, PID and mount namespaces with only loopback up (`ui/e2e/smoke/run.sh:25`), so main, the worker and the control plane cannot reach the network, nor anything listening on the owner's loopback such as Ollama; the spec refuses to run unless `lo` is the only interface (`ui/e2e/smoke/packaged-app.spec.ts:98`). Main's `fetch` is contained by the namespace, not by the fetch guard, because a packaged app does not take `-r`;
  - `XDG_RUNTIME_DIR` points into the run's temporary folder (`ui/e2e/smoke/run.sh:24`), so the control-plane socket and the private keyring's control socket never land in the owner's runtime folder;
  - `HOME`, the `XDG_*` folders and `TMPDIR` are per test (`ui/e2e/smoke/packaged-app.spec.ts:133`), which also keeps the worker's single-file extraction under the test's home, and the profile check is the flows' one;
  - each test closes the app, waits at most 10 s, then kills its process group (`ui/e2e/smoke/packaged-app.spec.ts:150`), and the PID namespace takes any process left when the run ends, so a main process blocked by an error dialog fails the test instead of hanging the run.
- **Main's log.** Main logs to stderr only, and Playwright reads stderr from the first byte, so the test launches the app through a two-line wrapper that `exec`s `scout-desktop` with stderr copied to a file by `tee` (`ui/e2e/smoke/packaged-app.spec.ts:127`); the test reads every line, and a failed test attaches the file.
- **The four tests** (`ui/e2e/smoke/packaged-app.spec.ts`):
  - the window opens (`:130`): a fresh profile shows the welcome screen, its heading and Use on this computer;
  - the packaged worker starts (`:141`): after Use on this computer, Settings shows the worker `running`, which main reports only after the worker printed `SCOUT_READY`; main's `supervisor: worker spawned` line names the worker inside the extracted AppImage, which exists; `control-plane: host spawned` names the shipped control plane and no `control-plane: unavailable` line appears. A fresh profile defaults to Ollama with nothing listening; the worker starts anyway, needing no chat token for local use;
  - no startup error (`:160`): until the worker runs, main writes no JSON line at level `error` and no `Uncaught`, `Unhandled` or `Error occurred in handler` line, and no window logs a console error or an uncaught exception, except one allowlisted by its exact text (`:25`): every window logs the Content Security Policy refusal of `<base href="./">` (`ui/packages/renderer/src/index.html:6`) under `base-uri 'none'` (`ui/packages/main/src/main.ts:229`). It is harmless, since the base is the folder of `index.html`, and it is an open defect: the fix removes the mismatch, then the allowlist entry;
  - a missing worker shows an error, not a hang (`:180`), the failing-call test for the worker binary: a hardlinked copy of the extracted app without `Scout.Host.Console` shows the worker `crashed` in Settings, and Start shows "Worker binary not found:" with the path main resolved.
- **Mutation proof.** Each test failed against a hardlinked copy of the extracted app with one change; a change inside the asar was made on its extracted contents and repacked with `@electron/asar`, which electron-builder already installs; a repack whose only change was a space after `<sc-app></sc-app>` passed all four:
  - `<sc-app></sc-app>` removed from the renderer's `index.html`: the window test found no welcome heading;
  - the worker replaced by a script that exits 1: the worker test read `crashed` where it expected `running`;
  - `resources/protos` removed: the worker test found main's `control-plane: unavailable` warning (the host had spawned, then the client could not load its protos), and the startup test caught main's `Error occurred in handler for 'scout:actions.list'` (main itself logs the failure only as warnings);
  - a `logger.error` after "main window created", an unhandled rejection at the same point, and a `console.error` at the top of the renderer bundle: the startup test failed on each, quoting the line;
  - a `throw` from a timer in main: Electron's error dialog blocked main, and the startup test failed waiting for the main window, after 41 s, and the cleanup above ended the run;
  - the missing-binary throw in main's bundle replaced by a promise that never settles: the missing-worker test read `starting` where it expected `crashed`.
  - the same throw replaced by a plain `Error('worker could not start')`: the missing-worker test read "Scout did not start: ... worker could not start" where it expected "Worker binary not found:" and the path.
- **Runtime.** On the owner's machine the four tests take about 9 s and the extraction about 4 s, about 15 s in all; five runs in a row at a load average of 45 took 17 to 25 s each, and one loaded run 30 s. Packaging (`bun run dist:linux`) is not part of it.
- **CI.** Not wired; the release workflow change is a separate decision. The step it would take: in the Linux leg of `.github/workflows/release.yml`, after Package and before the upload, install `xvfb`, `gnome-keyring` and `dbus`, lift the AppArmor limit on unprivileged user namespaces as the `e2e` job does (`.github/workflows/ui-ci.yml:192`), which `unshare` needs as much as Electron, run `bun run test:smoke`, and upload `ui/e2e/smoke/test-results` on failure.
- **Enforcement.** `ui/e2e/smoke/packaged-app.spec.ts`, run by `bun run test:smoke` before an AppImage is staged ([[pages/runbooks/release]]).
- **Since ADR 0053 (2026-11-08).** The shipped executable has the inspector fuse off, so the tests that launch the app drive a hard-linked copy of the extracted app whose executable has only that fuse turned back on, and a fifth test reads the fuses of the untouched shipped executable ([[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]). The four tests above stay as written.

## Amendment: the real control plane host and the loaded states (2026-11-09)

[[pages/roadmap/prototype-migration]] could not capture Features, Approvals, Meetings and the operator pages in their loaded states, because every flow ran with the control plane pointed at a missing file. A test can now ask for the real host on data of its own. "The control plane is pointed at a missing file ... so it is unavailable in every run and the job needs no .NET" in the Decision stays true only of a test that does not ask.

- **The real host, not a fake of it.** Two ways were weighed: a gRPC server written for the harness, or `Scout.Host.ControlPlane` itself on a private data folder. The real host won, because it needs nothing outside the test's folder and a fake would be a second implementation of the contract to keep in step:
  - its data folder is .NET's local application data, which on Linux is `XDG_DATA_HOME`, already inside the test's folder (`src/Scout.ControlPlane/Features/FeatureStateStoreOptions.cs:23`);
  - its credential store is the in-memory one and its activator the null one (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69`, `:70`), so it opens no keyring;
  - its only outbound call is to the Ollama daemon, made on the provider and model rpcs alone, which none of these screens call. A system-call trace of the host over the Features, Approvals and Production specs showed no `connect` at all, and two `bind` calls: its socket in the test's runtime folder and the .NET diagnostics socket in the temporary folder.
- **How a test asks.** `test.use({ scoutData: SCOUT_DATA })` (`ui/e2e/fixtures/app.ts:157`). The fixture copies that folder to `Scout` under the test's `XDG_DATA_HOME` before the app starts and points `SCOUT_CONTROLPLANE_BIN` at the host (`ui/e2e/fixtures/app.ts:201`); main launches it and dials its socket as it does for the owner. Without `scoutData` the control plane is the missing file it was.
- **Which build of the host.** The one written last: of the Release and Debug folders under `src/Scout.Host.ControlPlane/bin` that hold the host, the fixture takes the one whose newest file is newer (`newestControlPlaneHost`, `ui/e2e/fixtures/app.ts:127`). Main's own rule for an unpackaged app is Release when there is one, else Debug (`resolveControlPlaneBinary`, `ui/packages/main/src/worker/resolve-binary.ts:51`), under which a Release build left from an earlier day would run in place of the Debug build just made; the fixture does not leave the choice to it. Seen on the owner's machine: Debug with only Debug built, Release after the CI job's build command, Debug again once the Debug build was newer. CI builds only Release, so it runs that. A failed test attaches the path as `control-plane-host` (`ui/e2e/fixtures/app.ts:296`). When neither build exists the test fails at once with "The control plane host is not built. Run 'dotnet build src/Scout.Host.ControlPlane' first." (`ui/e2e/fixtures/app.ts:133`), before it creates any folder.
- **The data.** `ui/e2e/fixtures/scout-data/` holds the files the host reads, in the shapes it writes: `features.json` (YouTube with writes granted on 2026-11-01), `actions.json` (two YouTube actions waiting, an upload and a comment reply), `writes.log` (three entries: a save, an approved thumbnail, a rejected update), `mandate.json` and `projects.json` (two projects, one with its script ready, one queued). `ui/e2e/fixtures/loaded.ts` names that folder as `SCOUT_DATA`. Every name, id and sentence in them is invented.
- **Meetings are the worker's, not the control plane's.** Main asks the worker for them over the line protocol, so the fake worker answers a `meetings` request with the `meetings` of the test's script, an empty list when the script has none (`ui/e2e/fixtures/fake-worker.mjs:57`); `KEPT_MEETINGS` in `loaded.ts` is three of them. Since 2026-11-11 it also answers a `delete` request, for one meeting or for all, with `deleted` and the count, and drops those meetings from its list, so the Meetings notices have an end-to-end test (`ui/e2e/tests/meetings-notices.spec.ts:6`).
- **Isolation added for every flow** (`ui/e2e/fixtures/app.ts:192` to `:195`, `:93`, `:102`, `:103`):
  - `XDG_RUNTIME_DIR` is a folder of the test's own. Until now main inherited the owner's, so every flow made sure `scout` existed in the owner's runtime folder before it failed to start the missing host, and a real host would have put its socket beside the socket of the owner's running Scout;
  - `TMPDIR` is a folder of the test's own, so the host's diagnostics socket, which a killed host leaves behind, goes with the test;
  - `DOCKER_HOST` names a socket that does not exist in the test's folder, because the loaded Features screen asks the `docker` program for its state and would reach the owner's daemon; the Docker card's state therefore depends only on whether `docker` is installed, and no spec asserts it;
  - `KUBECONFIG` and `DOCKER_CONTEXT` are dropped from the environment, so the Kubernetes panel reads only the test's `HOME`;
  - `TZ` is `UTC`, so a date or time the renderer formats from a fixture reads the same on every machine;
  - no variable of the invoking shell whose name starts with `scout_`, in any casing, reaches the app. The host binds its configuration from the environment, so an exported `Scout__Features__StateStore__FilePath` would have pointed the host under test, with its startup cleanup and its queue writes, at the owner's file. The fixture then adds its own three: `SCOUT_WORKER_BIN`, `SCOUT_CONTROLPLANE_BIN` and `SCOUT_E2E_FETCH_LOG`.
- **The isolation is checked on every launch, inside main** (`ui/e2e/fixtures/app.ts:239`, the refusal at `:261`). Beside the profile, the fixture reads from main its runtime folder, `os.tmpdir()`, `DOCKER_HOST`, its time zone and every environment name that starts with `scout_` or is `KUBECONFIG` or `DOCKER_CONTEXT`, and refuses to go on with "The app is not confined to this test's folder" and what it found unless the first two are the test's own folders, the third is the test's socket that does not exist, the zone is UTC and the names are exactly the fixture's three. With `KUBECONFIG`, `DOCKER_CONTEXT` and `Scout__Features__StateStore__FilePath` exported to the run, the suite passed, and each of the seven lines taken out failed the launch naming what leaked: the owner's runtime folder, `/tmp`, an empty Docker host, the machine's zone, `KUBECONFIG`, `DOCKER_CONTEXT`, and the exported `Scout__` variable with `SCOUT_E2E_PRIVATE_SESSION`.
- **A long temporary folder, and a setup that fails.** The host's socket lies under the test's folder, which lies under the invoking shell's `TMPDIR`, and main refuses a socket path of 100 characters or more. The fixture works the path out first and fails with "The temporary folder ... (TMPDIR) is too long", the path and the limit, before it creates anything (`ui/e2e/fixtures/app.ts:175`). Everything after the test's folder is created runs under a `finally` that closes the apps and removes the folder (`ui/e2e/fixtures/app.ts:311`), so a copy that fails leaves nothing; before, it left the folder.
- **The Playwright version in use** is `@playwright/test` 1.65.0 (`ui/e2e/package.json:13`); the Decision names the 1.64.1 it was built on.
- **The five specs** (`ui/e2e/tests/`), and what each fails without:
  - `features-loaded.spec.ts`: the five cards in the catalog's order, Not connected on GitHub, Kubernetes and YouTube, On on Desktop, and in the panels "Writes allowed since 2026-11-01" for YouTube and "Writes not allowed" for GitHub. With `features.json` emptied to `{}` YouTube read "Writes not allowed";
  - `approvals-loaded.spec.ts`: the count 2 on Waiting for you, the two cards with their feature, action, summary, Approve and Reject, and the three writes in History, newest first. With `actions.json` and `writes.log` emptied the count was not found;
  - `meetings-loaded.spec.ts`: the three meetings, newest first, each with its length, lines and questions, and Delete all. With `KEPT_MEETINGS` emptied no row was found;
  - `production-loaded.spec.ts`: "Mandate saved 2026-11-02" and the default format on the Channel row, the two projects newest first with their status, no error, and the objective and audience in the mandate form. Without `mandate.json` the row read "No mandate yet", and with `projects.json` emptied no project was found;
  - `control-plane-dies.spec.ts`, the failing-call test: with Approvals open on the two actions, `desktop.killControlPlane()` kills the host, found as the child of this test's Electron process by name (`ui/e2e/fixtures/app.ts:278`), and Refresh shows "Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected." When the failed load no longer set the error in `ui/packages/renderer/src/app/review/review.component.ts`, the sentence was not found. The assertion waits 30 s, because a call that starts before main sees the host's exit waits out the client's 10 s deadline.
- **Times in the fixtures.** The app runs at `TZ=UTC`, so the dates the specs read, "Writes allowed since 2026-11-01" and "Mandate saved 2026-11-02", are the fixture's own on every machine. Before that was set they followed the machine's zone: with the invoking shell at `Pacific/Kiritimati` (UTC+14) the two specs read 2026-11-02 and 2026-11-03 and failed, where at `Pacific/Honolulu` they passed; with it, both pass in both zones.
- **What this cannot show.** A feature's Connected state lives in main, not in the control plane: GitHub and YouTube need a sign-in over the network, and Kubernetes a context chosen in the panel from a kubeconfig in the test's `HOME`. No spec does either, so the loaded Features screen shows GitHub, Kubernetes and YouTube as Not connected.
- **Capturing a loaded screen.** A spec that uses the fixture calls `page.screenshot({ path })` where it needs the picture, and `bun run test:e2e tests/<spec>` in `ui/` runs that one spec.
- **Runtime.** On the owner's machine, shared with other builds at a load average of 40 to 95, the twelve tests took 25 to 58 s per run over twelve runs, and 66 s in one run that overlapped a build of the host; all thirteen passed. The job itself gains the SDK setup and a build of three projects. The seven tests before took 27 s at a load of 44.
- **CI.** The `e2e` job sets up .NET and builds the host before `bun run test:e2e` (`.github/workflows/ui-ci.yml:200`, `.github/workflows/ui-ci.yml:205`), with the two steps of the unit job copied as they are (`.github/workflows/ui-ci.yml:71`, `.github/workflows/ui-ci.yml:110`): the same action, the SDK `global.json` pins and the Release configuration, so main resolves the same build in both jobs. The twelve tests passed against that Release build on the owner's machine. `tools/release/release-workflow.test.sh` reads the workflow and fails when the `e2e` job has no host build before its tests, no .NET setup as the unit job's before the build, or a build command that differs from the unit job's; each case failed with its step removed, moved after the tests, or changed to Debug. The workflow's path filter already took the three control-plane projects, and its concurrency group is unchanged.
- **Enforcement.** The five specs and the launch check, run by `bun run test:e2e`; `tools/release/release-workflow.test.sh` for the job; [[pages/conventions/testing]].
