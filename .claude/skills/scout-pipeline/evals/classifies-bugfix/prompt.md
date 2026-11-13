---
name: Classifies a provider bug as the bug-fix lane, never fast path
runs: 2
max_turns: 6
timeout_seconds: 240
tags: ["classification"]
plugins: ["../.."]
---
Do not edit any file. Classify this task the way this repo's pipeline requires and reply with the classification, the shape (fast path or full pipeline, with the reason), the lane file you would read, and who implements it. Then stop.

Task: "When OpenRouter answers HTTP 429 mid-stream, Scout.Llm drops the partial answer instead of finalizing the stream; the Discord message stays half-written. Fix it."
