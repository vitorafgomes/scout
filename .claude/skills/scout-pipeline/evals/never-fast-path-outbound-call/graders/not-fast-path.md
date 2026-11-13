---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|isn't)\\s+(?:a\\s+|the\\s+)?full pipeline|\\bfull pipeline\\b[^.\\n,;]{0,20}?\\b(?:not needed|not required|not necessary|unneeded|unnecessary|disproportionate|overkill|does not apply|doesn't apply)|\\bshape\\b[*:\\s]*(?:the\\s+|a\\s+)?fast[- ]path|\\b(?:this|it)\\s+is\\s+(?:a\\s+|the\\s+)?fast[- ]path\\b|\\b(?:this|it|which)\\s+(?:can|may|could|should|will)?\\s*takes?\\s+(?:a\\s+|the\\s+)?fast[- ]path\\b|\\b(?:not|isn't|aren't)\\s+(?:on|in)\\s+(?:the|its|that)\\s+\\W{0,3}never\\b[^.\\n]{0,30}?\\blist\\b))(?=[\\s\\S]*(?:full pipeline|never (?:a |the )?fast[- ]path|not (?:a |the )?fast[- ]path|no fast[- ]path|never-list|never list|never trivial))"
flags: "i"
match: contains
target: last_message
---
