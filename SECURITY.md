# Security Policy

## Supported versions

Scout is pre-alpha. Security fixes are applied to `main`. There are no
released versions yet.

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

Scout's security model is built around **read-only** integrations. The
following are in scope for security reports:

- Any code path that performs a write operation against an external system
  (this should be structurally impossible; a PoC is high-value)
- Credential leakage — in logs, telemetry, LLM prompts, or error messages
- Injection via chat input reaching external APIs
- Privilege escalation via plugin loading or configuration
- Supply-chain issues in dependencies

Out of scope:

- Denial of service against the bot itself via excessive chat traffic
  (deploy with rate limiting)
- Issues requiring physical access to the host
