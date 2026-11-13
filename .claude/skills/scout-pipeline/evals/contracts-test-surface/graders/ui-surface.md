---
type: regex
pattern: "^(?![\\s\\S]*(?:\\b(?:not|never|don't|no need to)\\s+(?:to\\s+)?run\\s+(?:the\\s+|any\\s+)?(?:[`*]*ui[`*/]*\\s+checks?\\b|[`*]*bun run (?:typecheck|lint|format|test)(?![:\\w-]))|\\bno\\s+[`*]*ui[`*/]*\\s+checks?\\b|\\bbun run (?:typecheck|lint|format|test)(?![:\\w-])[`*]*\\s+(?:(?:is|are)\\s+(?:not needed|not required|unnecessary)|need not|needn't)))(?=[\\s\\S]*bun run typecheck)(?=[\\s\\S]*bun run lint)(?=[\\s\\S]*bun run format)(?=[\\s\\S]*bun run test)"
flags: "i"
match: contains
target: last_message
---
