---
type: regex
pattern: "(every|each|all) (test project|project under tests)|tests/\\*|Scout\\.ControlPlane\\.Tests[\\s\\S]*Scout\\.Host\\.Console\\.Tests"
flags: "i"
match: contains
target: last_message
---
