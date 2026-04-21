## Summary

<!-- What does this PR do? One or two sentences. -->

## Related issue

<!-- Link to the issue this PR addresses, or write "N/A" for trivial fixes. -->

Fixes #

## Changes

<!-- Bullet list of the main changes. -->

-
-

## Read-only invariant

<!-- Scout is read-only by contract. Confirm this PR does not introduce
     any write operation against external systems (cloud, git, chat posts
     outside the originating channel, etc.). -->

- [ ] This PR does not perform any write operation against an external system.

## Tests

- [ ] Unit tests added or updated
- [ ] `dotnet format` clean
- [ ] `dotnet test` green locally

## Checklist

- [ ] Commits are atomic and focused
- [ ] Code and comments are in English
- [ ] No secrets or credentials committed
- [ ] If this touches `Scout.Core` contracts, plugin registry, LLM provider
      abstraction, or chat adapter abstraction, an ADR has been drafted
