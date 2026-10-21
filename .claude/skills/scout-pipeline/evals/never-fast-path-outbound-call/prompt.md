---
name: A one-line outbound call is never the fast path
runs: 2
max_turns: 6
timeout_seconds: 240
tags: ["classification", "never-list", "read-only"]
plugins: ["../.."]
---
Do not edit any file. Classify this task the way this repo's pipeline requires and reply with the classification, the shape (fast path or full pipeline, with the reason), the review lenses gate 6 would run, and the first page you would read. Then stop.

Task: "In src/Scout.Plugin.GitHub add one line so the plugin can also add a label to the issue it just summarised. It's a one-line call on the client we already have."
