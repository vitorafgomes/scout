#!/usr/bin/env bash
# Case matrix for pre-git-guard.sh: ASK = must be blocked for approval, PASS = must go through.
HOOK="${1:-$(dirname "$0")/pre-git-guard.sh}"
fail=0
check() {
  want=$1; cmd=$2
  got=$(printf '{"tool_input":{"command":%s}}' "$(jq -Rn --arg c "$cmd" '$c')" | bash "$HOOK" | grep -c permissionDecision)
  [ "$got" = "1" ] && actual=ASK || actual=PASS
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $cmd"; fail=1; fi
}
# must ASK
for c in "git commit -m x" "git push origin main" "git merge feat/x" "git rebase main" \
         "git revert HEAD" "git cherry-pick abc1234" "git pull" "git reset --hard" \
         "git reset --soft HEAD~1" "git reset HEAD~2" "git tag v1.0" "git tag -d v1.0" \
         "git branch -D old" "git branch -m a b" "git config alias.ci commit" \
         "gh pr create --fill" "gh pr merge 12" "git am patch.diff" "git update-ref HEAD abc" \
         "npm test && git push"; do check ASK "$c"; done
# must PASS
for c in "git status" "git diff HEAD" "git log --grep=commit" "git show HEAD" "git blame f.ts" \
         "git tag -l" "git tag --list" "git merge --abort" "git branch" "git branch -a" \
         "gh pr view 12" "gh pr list" "ng test --no-watch" "git log --oneline -5" \
         "git config user.name" "git stash list"; do check PASS "$c"; done
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail
