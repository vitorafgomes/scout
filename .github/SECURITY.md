# Security Policy

## Supported versions

Scout is pre-alpha. Security fixes are applied to `main` and reach the desktop
app in the next release built from it. The Release workflow
(`.github/workflows/release.yml`) builds the packages and publishes them on the
[releases page][releases]; the release marked Latest there is the one the
project site and the desktop app's update notice offer, and a prerelease
(`-beta.N`) is offered to nobody by default. No support policy for older
releases is written yet: to get a fix, update to the Latest release, from the
desktop app's update notice or by downloading it.

[releases]: https://github.com/vitorafgomes/scout/releases

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report them privately via GitHub's ["Report a vulnerability"][ghsa] feature
on this repository, which opens a private security advisory.

[ghsa]: https://github.com/vitorafgomes/scout/security/advisories/new

Please include:

- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested fix, if you have one

We aim to acknowledge reports within 72 hours and issue a fix or mitigation
plan within 14 days for valid reports.

## Scope

Scout's security model is the write contract: every write is declared by its
feature, runs only after the owner grants that feature in the desktop app, and,
when it cannot be undone, only after the owner approves that one action there;
every write is logged, and nothing writes code or touches the host outside
Scout's own folders. The following are in scope for security reports:

- Any code path that performs a write its feature does not declare, a write
  without the owner's grant, an irreversible action without the owner's
  approval, or a write that is not logged (a PoC is high-value)
- A chat message, prompt or model output that grants a permission or approves
  an action
- Any write to code, or to the host outside Scout's own folders, and any test
  run outside the isolated container the owner enabled
- Credential leakage — in logs, telemetry, LLM prompts, or error messages
- Injection via chat input reaching external APIs
- Privilege escalation via plugin loading or configuration
- Supply-chain issues in dependencies

Out of scope:

- Denial of service against the bot itself via excessive chat traffic
  (deploy with rate limiting)
- Issues requiring physical access to the host
