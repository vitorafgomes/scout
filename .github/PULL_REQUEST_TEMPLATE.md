## :clipboard: Checklist

<!-- Tick what is true. A box left empty is explained under "Not done, and why". -->
- [ ] My commits follow the project's guidelines
- [ ] Tests for the change were added
- [ ] Documentation was updated, or none is needed
- [ ] This description names every request
- [ ] Code and comments are in English
- [ ] No secrets or credentials committed
- [ ] If this touches the control-plane contracts (`Scout.ControlPlane.Contracts`), the feature
      catalog, the LLM provider abstraction, or the chat adapter abstraction, an ADR has been drafted
- [ ] `dotnet format` clean and tests green locally

## :gear: What kind of change is this?

<!-- Tick the main kind. -->
- [ ] Bugfix
- [ ] Feature
- [ ] Code style
- [ ] Refactoring, no functional change
- [ ] Build
- [ ] Documentation
- [ ] Tests
- [ ] Other (describe)

## :dart: What was asked

<!-- One line per request: its number, what was asked, the issue it comes from. -->
- **R1**

## :hammer_and_wrench: What was done

<!-- One line per request, then its main files. -->
- **R1**
  - Main files:

## :construction: Not done, and why

<!-- A request left out and the reason, or "Nothing". -->
-

## :heavy_plus_sign: Done beyond what was asked, and why

<!-- Anything in the diff that no request asked for. -->
-

## :test_tube: Tests implemented

<!-- One row per test. Say whether it was seen failing before the change. -->
| Request | Test | File | What it proves | Seen failing before the change |
|---|---|---|---|---|
| | | | | |

## :raised_hand: Tests to run by hand

<!-- What the tests do not cover, as steps a person can follow. The reviewer ticks them. -->
<!-- Each case: its number, a title, and the request it proves. -->
- [ ] **1.**
  - Set up:
  - Steps:
  - Expected:

## :books: Documentation

<!-- "None" is an answer. -->
- Updated:
- Decision record followed:
- Deliberately not documented:

## :rocket: How to run it

<!-- The commands, one per line. -->
```text

```

## :warning: Risk and impact

<!-- "Not touched" and "None" are answers. -->
| Item | Answer |
|---|---|
| Public contract | |
| Data | |
| Settings | |
| Dependencies | |
| Security | |
| How to roll back | |

### Write contract

<!-- Every write Scout makes is declared by its feature, granted by the owner
     in the desktop, approved per action when irreversible, and logged
     (context/pages/decisions/0032-per-feature-write-permissions.md). -->

- [ ] Every call this PR adds or changes that leaves the process is declared by its feature (read, write or irreversible write); a write runs only under the owner's grant, an irreversible one only after the owner's per-action approval, and every write is logged.
- [ ] Nothing in this PR writes or edits code, or writes on the host outside Scout's own folders.

## :eyes: For the reviewer

<!-- Where to start and where to look hardest. Fill N and M from the review of this change. -->
- Start reading at:
- Look hardest at:
- Out of scope:
- Known weak points:
- Review: applied N, rejected M

## :thumbsup: Reviewers

<!-- A mention of the review bot on the last line asks for its review. -->
