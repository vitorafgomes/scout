#!/bin/sh
# Self-test for docs-only.sh.
SCRIPT="$(dirname "$0")/docs-only.sh"
FAILED=0

# expect <true|false> <description> <the changed paths, as printf would write them>
expect() {
  OUT=$(printf "$3" | sh "$SCRIPT"); CODE=$?
  if [ "$CODE" -ne 0 ] || [ "$OUT" != "$1" ]; then
    echo "FAIL: $2 (exit $CODE, said '$OUT')"; FAILED=1
  fi
}

expect true "pages, the log and artboards under context/ are documents" 'context/log.md\ncontext/index.md\ncontext/pages/decisions/0058-x.md\ncontext/pages/history/audit.md\ncontext/prototypes/desktop/Main.dc.html\n'
expect false "the contract of context/, which a test reads, runs everything" 'context/CLAUDE.md\n'
expect false "a convention page, which tests read, runs everything" 'context/log.md\ncontext/pages/conventions/tooling.md\n'
expect false "the instructions at the root, which tests read, run everything" 'AGENTS.md\n'
expect false "Markdown at the root runs everything" 'README.md\n'
expect false "a bundled skill is the product's code" 'src/Scout.Skills/skills/aws.md\n'
expect false "Markdown under ui/ runs everything" 'ui/README.md\n'
expect false "Markdown under docs/ runs everything" 'docs/guide.md\n'
expect false "one source file among documents runs everything" 'context/log.md\nsrc/Scout.Audit/AuditWorker.cs\n'
expect false "a desktop source file runs everything" 'ui/packages/main/src/main.ts\n'
expect false "Markdown under .claude/ is the harness's code" '.claude/rules/audit.md\n'
expect false "Markdown under tools/ runs everything" 'tools/checks/README.md\n'
expect false "a workflow runs everything" '.github/workflows/ci.yml\n'
expect false "the site's page runs everything" 'docs/index.html\n'
expect false "a build file runs everything" 'Directory.Packages.props\n'
expect false "a folder whose name only starts like context runs everything" 'contextual/notes.md\n'
expect false "a path git quoted for its odd characters runs everything" '"context/a\\nb.md"\n'
expect false "no changed path is not a documents-only change" ''
expect true "blank lines are skipped" '\n\ncontext/log.md\n'
expect true "a last line without a newline is read" 'context/log.md'
expect false "a source file on a last line without a newline is read" 'context/log.md\nsrc/A.cs'

[ "$FAILED" -eq 0 ] && echo "docs-only.sh: all cases pass"
exit "$FAILED"
