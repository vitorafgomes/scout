---
paths:
  - "ui/packages/main/src/oauth/github-auth.ts"
  - "ui/packages/main/src/oauth/github-bot.ts"
  - "ui/packages/main/src/ipc/github.ts"
  - "ui/packages/main/src/ipc/github-verdict.ts"
  - "ui/packages/main/src/notifications/github-activity.ts"
  - "ui/packages/renderer/src/app/features/github-connect.component.ts"
---

# Desktop GitHub card

Owns the GitHub card on the Features screen, the owner's GitHub session or token, the Scout bot's App key and installation token, and following a verdict the owner gives from a review card. Does not own the worker's GitHub functions ([[pages/services/plugin-github]]), the review card itself ([[pages/services/desktop-quick-ask]]) or the approval queue ([[pages/services/control-plane]]).

## Rules this project must keep

- [[pages/invariants/secrets-never-in-source]]: the session, the pasted token, the bot's private key and its installation token stay in the vault or the 0600 token files; never in a log line or the renderer. The activity poll reads the token per tick and never logs or keeps it; the App's JSON Web Token for `GET /app` is made per read and never logged.
- [[pages/invariants/no-executive-writes]]: the bot's creation and every verdict start from the owner's click in the desktop; the verdict runs as an approved action under ADR 0032. The mention search, the comment reads, `GET /app` and the count of the App's repositories (one new call from main to api.github.com, of the kind the `Metadata` row already declares, made only with a browser session and reaching the renderer only as a number in the status) are reads; Review it only asks the chat, and posting the review stays the approved write it is.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0019-github-app-device-flow|ADR 0019 - GitHub through a read-only GitHub App and the device flow]] - the owner's sign-in and the pasted token
- [[pages/decisions/0044-scout-acts-on-github-as-its-own-app|ADR 0044 - Scout acts on GitHub as its own App, when the owner chooses it]] - the optional Scout bot
- [[pages/decisions/0043-the-verdict-on-a-pull-request|ADR 0043 - The verdict on a pull request, with the project's tests in an isolated container]] - the verdict, v1.1: the card click is the approval
- [[pages/decisions/0045-theme-and-notification-center|ADR 0045 - The desktop's theme switch and notification center]] - v1.1: the bell's GitHub activity through the search API; v1.4: the mention of the Scout bot
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/services/desktop-ui|Desktop UI]] - the app around the card

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/desktop-github/public-contract.md` |
| mermaid; The GitHub card does not use; GitHubAuth (ui/packages/main/src/oauth/github-auth.ts:98) runs the device flow,; No client id; Use a token; Pending; Connected; Failed | `context/pages/services/desktop-github/how-it-works-mermaid.md` |
| Repositories the App is installed on | `context/pages/services/desktop-github/how-it-works-repositories-the-app-is-installed-o.md` |
| Below Connect the card offers Use; Picture; Name and slug follow a rename | `context/pages/services/desktop-github/scout-bot-below-connect-the-card-offers-use.md` |
| Name, handle and the mention; The GitHub card sits in the | `context/pages/services/desktop-github/scout-bot-name-handle-and-the-mention.md` |
| Verdict from the review card | `context/pages/services/desktop-github/verdict-from-the-review-card.md` |
| GitHub activity for the bell | `context/pages/services/desktop-github/github-activity-for-the-bell.md` |
| Mentions of the Scout bot | `context/pages/services/desktop-github/mentions-of-the-scout-bot.md` |
| Hot spots | `context/pages/services/desktop-github/hot-spots.md` |
| Testing footprint | `context/pages/services/desktop-github/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
