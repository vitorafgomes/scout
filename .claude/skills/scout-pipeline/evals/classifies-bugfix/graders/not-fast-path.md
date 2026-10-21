---
type: regex
pattern: "full pipeline|not (a |the )?fast path|no fast path|never (a |the )?fast path|fast path[^.]{0,40}\\b(no|not|does not|doesn't)\\b"
flags: "i"
match: contains
target: last_message
---
