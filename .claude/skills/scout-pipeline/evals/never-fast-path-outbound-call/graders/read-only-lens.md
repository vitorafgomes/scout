---
type: regex
pattern: "^(?![\\s\\S]*(?:\\bno\\s+[`*]*read-only[`*]*\\s+lens\\b|\\bread-only[`*]*\\s+lens\\b[^.\\n]{0,40}\\b(?:not needed|not required|need not|needn't|does not run|doesn't run|is skipped|unnecessary|overkill|can be skipped|not applicable)\\b|\\b(?:can|may|could|should)\\s+skip\\s+(?:the\\s+)?[`*]*read-only\\b))(?=[\\s\\S]*(?:`read-only`|\\bread-only[`*]*\\s+lens\\b|\\blens(?:es)?\\b[^.]{0,120}?\\bread-only\\b|\\b(?:invariants|boundaries)\\b[^.\\n]{0,60}?\\bread-only\\b|\\bread-only\\b[^.\\n]{0,60}?\\b(?:invariants|boundaries)\\b))"
flags: "i"
match: contains
target: last_message
---
