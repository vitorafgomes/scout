---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|isn't)\\s+(?:a\\s+|the\\s+)?bug\\b|\\bbug\\b[^.\\n]{0,30}\\b(?:does not|doesn't|do not|don't)\\s+(?:apply|fit)))(?=[\\s\\S]*\\bbug\\b)"
flags: "i"
match: contains
target: last_message
---
