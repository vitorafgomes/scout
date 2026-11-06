---
name: code-review
description: Reviewing a pull request or a diff in any programming language - "review PR 42", "code review", "look at this pull request and tell me what is wrong", "comment on the PR", "revisa esse PR", "faz o code review". Reads the pull request from GitHub, judges correctness, safety, tests, error handling and clarity against the languages the diff touches, and answers with findings anchored to file and line. Not for asking what a repository is about, not for listing pull requests, not for planning improvements to a whole project.
---

# Code review

Use this when a user asks for a review of a pull request, a branch or a diff, in any language. The review is grounded in the diff GitHub returns, never in what the title promises.

## Read first, in this order

1. `get_pull_request(owner, repo, number)`: title, description, branches, the extensions of the files it touches, the file list and the diffs that fit the budget. If the user gave a URL, the owner, repo and number are in it. If no repository is named and none is remembered in this conversation, ask which one; do not guess.
2. `list_pull_request_comments(owner, repo, number)`: what reviewers already said. A finding that repeats an existing comment is not reported again; say it is already raised.
3. `get_pull_request_file(owner, repo, number, path)` for every file whose diff was left out, and for any file where the hunks alone are not enough to judge.
4. `get_file_contents(owner, repo, path, head branch)` when a judgement needs the code around a hunk: the callers of a changed function, the interface a class implements, the test that should cover the change.
5. For each extension in the pull request, load the language skill with `load_skill` when one exists: `.cs` csharp, `.ts`/`.tsx` typescript, `.js`/`.jsx` javascript, `.py` python, `.go` go, `.java` java, `.kt` kotlin, `.rs` rust, `.rb` ruby, `.php` php, `.swift` swift, `.scala` scala, `.dart` dart, `.c`/`.h` c, `.cpp`/`.hpp` cpp, `.sql` sql, `.sh` bash, `.ps1` powershell, `.ex`/`.exs` elixir, `.clj` clojure, `.hs` haskell, `.lua` lua, `.pl` perl, `.r` r. Apply that skill's rules to those files. A language without a skill is reviewed with the general rubric below.

Review every file the pull request changes, including tests, configuration and build files. A pull request too large to read in full is reviewed file by file, and the answer says which files were read.

## What to look for, in any language

Report a finding only when the diff shows it. Lead with what would break production or a user.

- **Correctness.** A branch that cannot be reached, an off-by-one, a null or undefined dereference, a wrong comparison, a changed return value a caller still expects, a concurrency hazard, a resource that is opened and not closed.
- **Error handling.** A swallowed exception, a catch that is too broad, an error turned into a silent default, a failure path that loses the cause, a retry without a limit.
- **Security and data.** Input that reaches a query, a shell, a path or a URL without validation; a secret or token in code, in a log line or in a test fixture; data written where the user did not expect; a permission check removed or weakened.
- **Tests.** Behaviour changed with no test that fails without the change; a test that asserts only that a method was called; a test weakened or deleted to make the build pass; a skipped test.
- **Boundaries and contracts.** A public interface, API or schema changed without the callers; a migration that is not safe to run unattended; a dependency added for something the standard library does.
- **Clarity.** A name that lies, a function doing two things, duplicated logic that will drift, a comment that restates the code or has gone stale, dead code left behind.

Not findings: formatting a formatter fixes, personal style, speculation about code the diff does not touch, praise.

## How to answer

Every finding is anchored to a `path:line` that exists in the diff, states what is wrong in one sentence, why it matters in one sentence, and the smallest fix. Order: blocking, then should fix, then nit. At most ten findings; if there are more, the ten that matter most and a count of the rest. Quote the line, never paraphrase it. End with a one-line verdict: ready to merge, ready after the blocking findings, or needs rework.

```
Review of owner/repo#42 (files read: 6 of 6)

Blocking
- src/orders/Checkout.cs:88 - `total` is summed before the discount loop runs, so a discounted order is charged full price. Apply the discounts before the sum, or sum inside the loop.

Should fix
- src/orders/Checkout.cs:120 - the catch swallows `HttpRequestException` and returns an empty list, so a payment gateway outage reads as "no orders". Let it fail, or return a result that says the call failed.

Nit
- tests/Orders/CheckoutTests.cs:41 - the test asserts `Received()` on the gateway and nothing about the order; assert the total.

Verdict: ready after the blocking finding.
```

## Posting the review

The review above is given in chat. When the user asks for the comments to be posted on the pull request, call `submit_pull_request_review(owner, repo, number, body, comments)` once, with the verdict and the summary as `body` and the findings as `comments`: a JSON array of `{"path", "line", "body"}`, one per finding, `line` being the line in the file after the change as the diff shows it, `body` the finding's sentence and fix. Scout posts a commenting review, never an approval or a request for changes. The function queues the review for Scout's owner to approve on the Review screen of Scout Desktop and answers which comments it dropped for not sitting on a line of the diff; tell the user the review is waiting for the owner, name the dropped comments, and never say it was posted: the chat hears from Scout when it is. If the function is not available, or answers that writes are not allowed, say so and leave the review in chat.

You never edit files, commit, merge or approve anything yourself.
