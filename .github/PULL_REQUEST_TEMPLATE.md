## Summary

<!-- What does this PR do? One or two sentences. -->

## Related issue

<!-- Link to the issue this PR addresses, or write "N/A" for trivial fixes. -->

Fixes #

## Changes

<!-- Bullet list of the main changes. -->

-
-

## Write contract

<!-- Every write Scout makes is declared by its feature, granted by the owner
     in the desktop, approved per action when irreversible, and logged
     (context/pages/decisions/0032-per-feature-write-permissions.md). -->

- [ ] Every call this PR adds or changes that leaves the process is declared by its feature (read, write or irreversible write); a write runs only under the owner's grant, an irreversible one only after the owner's per-action approval, and every write is logged.
- [ ] Nothing in this PR writes or edits code, or writes on the host outside Scout's own folders.

## Tests

- [ ] Unit tests added or updated
- [ ] `dotnet format` clean
- [ ] `dotnet test` green locally

## Checklist

- [ ] Commits are atomic and focused
- [ ] Code and comments are in English
- [ ] No secrets or credentials committed
- [ ] If this touches the control-plane contracts (`Scout.ControlPlane.Contracts`), the feature
      catalog, the LLM provider abstraction, or the chat adapter abstraction, an ADR has been drafted
