---
title: "Sandbox: Known violations / P1 debt"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: Known violations / P1 debt."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## Known violations / P1 debt

- Stack detection reads the root only; a repository whose solution sits in a subfolder needs a `Command` from the caller.
- .NET: the target frameworks are read from the projects the root's solution lists and their `Directory.Build.props` files, never from a file either imports (`Directory.Build.targets`, `<Import>`, a props file outside the snapshot), so a framework set only there reads as declaring none and runs on the .NET 10 SDK, said in the note; the lower frameworks of a multi-target solution run on the highest one's runtime (`DOTNET_ROLL_FORWARD=Major`), since each SDK image carries one runtime.
- Node, Python and Go: one image per line, so a pin to an exact patch (`.nvmrc` 20.11.0) runs that line's pinned patch; the note names both.
- On Linux the container runs as the owner's uid; when Scout itself runs as root it falls back to 1000:1000, which cannot write a root-owned work folder.
- The logs under `WorkDirectory` are kept with no retention.
- Java: the declared version is read from the root's `pom.xml` and build files only: a parent pom, a convention plugin under `buildSrc`, a version catalog or a toolchain set in a subproject is not seen, and the run says "No Java version was declared".
- Java: the GitHub plugin's `run_pull_request_tests` passes the project's own test command when its conventions profile or CI workflow names one, and that command replaces the stack's; a `./gradlew` in it runs as the image's `gradle --no-daemon --offline` ([[pages/services/plugin-github]]), but `mvn test` runs without `-o` (the local repository under `/work` still applies, through `MAVEN_OPTS`).
- The default Docker Desktop file sharing covers the home folder; a `WorkDirectory` outside it answers with the file-sharing sentence above until the owner shares it.
