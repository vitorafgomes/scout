---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|isn't)\\s+(?:an?\\s+)?executive\\b|\\bno\\s+executive\\s+(?:concern|issue|risk|impact)\\b|\\b(?:executive|ADR 0009)\\s+(?:writes?\\s+)?(?:do not|does not|don't|doesn't)\\s+apply\\b|\\b(?:write|call|label)\\s+is\\s+(?:a\\s+|purely\\s+|only\\s+)?descriptive\\b(?:\\s+write\\b|(?![^.\\n]{0,40}\\b(?:executive|write)\\b))|\\bnot\\s+an?\\s+write\\b(?![^.\\n]{0,60}\\bis\\s+an?\\s+(?:\\w+\\s+)?write\\b)))(?=[\\s\\S]*(?:\\bexecutive\\b|\\bADR 0009\\b|\\b(?:is|as)\\s+an?\\s+(?:\\w+\\s+)?write\\b|\\bwrites?\\s+to\\s+(?:GitHub|the\\s+issue|the\\s+repo(?:sitory)?)\\b|\\bRule A\\b[^.\\n]{0,60}\\bwrites?\\b))"
flags: "i"
match: contains
target: last_message
---
