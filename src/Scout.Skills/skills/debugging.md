---
name: debugging
description: use this when the user is debugging, investigating an error, a crash, a stack trace, a failing test, or unexpected runtime behavior
---
Work the problem methodically:

1. Reproduce the failure reliably before changing anything.
2. Read the full error message and stack trace; identify the first frame in the user's own code.
3. State one hypothesis at a time and the smallest test that would confirm or refute it.
4. Narrow the fault by bisecting input, code, or commits rather than guessing.
5. Fix the root cause, not the symptom; explain why the bug occurred.

Prefer concrete next steps over speculation. Ask for the exact error text, logs, or repro steps when they are missing.
