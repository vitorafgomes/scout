---
type: regex
pattern: "^(?![\\s\\S]*((not|never) (a |the )?fast[- ]path|no fast[- ]path|fast[- ]path[^.\\n]{0,40}(does not|doesn't|cannot|can't|not) (apply|hold)))[\\s\\S]*fast[- ]path"
flags: "i"
match: contains
target: last_message
---
