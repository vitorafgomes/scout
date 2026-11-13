---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|isn't)\\s+(?:a\\s+|the\\s+)?full pipeline|\\bfull pipeline\\s+(?:does not|doesn't|do not|don't|is not|isn't|is never|would not|wouldn't)\\b|\\b(?:this|it)\\s+is\\s+(?:a\\s+|the\\s+)?fast[- ]path\\b|\\b(?:not|isn't|aren't)\\s+(?:on|in)\\s+(?:the|its|that)\\s+\\W{0,3}never\\b[^.\\n]{0,30}?\\blist\\b))(?=[\\s\\S]*(?:full pipeline|not (?:a |the )?fast path|no fast path|never (?:a |the )?fast path|fast path[^.]{0,40}\\b(?:no|not|does not|doesn't)\\b))"
flags: "i"
match: contains
target: last_message
---
