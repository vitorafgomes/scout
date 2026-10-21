---
name: A doc-only edit takes the fast path
runs: 2
max_turns: 6
timeout_seconds: 240
tags: ["classification"]
plugins: ["../.."]
---
Do not edit any file. Classify this task the way this repo's pipeline requires and reply with the classification, the shape (fast path or full pipeline, with the reason) and the gates you would run. Then stop.

Task: "In vault/pages/architecture/project-map.md the word 'recieve' is misspelled; fix the typo."
