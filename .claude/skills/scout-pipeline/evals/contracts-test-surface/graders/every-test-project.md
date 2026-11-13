---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|don't|needn't|no need to|no reason to)\\s+(?:have\\s+to\\s+|to\\s+)?run\\s+[*_]*(?:every|each|all)\\b|\\brun(?:ning)?\\s+[*_]*(?:every|each|all)\\b[^.\\n,;]{0,30}?\\b(?:is|are)\\s+(?:unnecessary|not required|not needed|not necessary)\\b|(?:tests/\\*|test projects?)[`*]*\\s+(?:need not|needn't|do not need to|does not need to|don't need to|doesn't need to)\\s+(?:be\\s+)?run\\b|(?:tests/\\*|test projects?)[`*]*\\s+(?:are|is)\\s+not\\s+needed\\b))(?=[\\s\\S]*(?:\\b(?:every|each|all)[*_]*( \\d+| ten| eleven)?(?: \\.NET)? (test projects?|projects? under tests|\\.Tests projects?|test surfaces?)|tests/\\*|Scout\\.ControlPlane\\.Tests[\\s\\S]*Scout\\.Host\\.Console\\.Tests))"
flags: "i"
match: contains
target: last_message
---
