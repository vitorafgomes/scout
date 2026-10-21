---
type: regex
pattern: "(every|each|all)( \\d+| ten| eleven)? (test projects?|projects? under tests|\\.Tests projects?|test surfaces?)|tests/\\*|Scout\\.ControlPlane\\.Tests[\\s\\S]*Scout\\.Host\\.Console\\.Tests"
flags: "i"
match: contains
target: last_message
---
