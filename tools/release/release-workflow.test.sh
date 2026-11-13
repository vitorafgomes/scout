#!/usr/bin/env bash
# Case matrix for .github/workflows/release.yml: extracts the bodies of the check job's version step and the publish
# step, runs them in throwaway repositories with the GITHUB_* environment of each trigger, and a fake gh that logs its
# arguments, then checks the computed version and the gh call that would publish.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
command -v python3 >/dev/null || { echo "FAIL python3 is needed to read release.yml"; exit 1; }
python3 - "$ROOT/.github/workflows/release.yml" "$work" <<'PY' || { echo "FAIL release.yml has no check step 'The version' or publish step 'Publish the GitHub Release'"; exit 1; }
import sys, yaml
jobs = yaml.safe_load(open(sys.argv[1]))['jobs']
step = lambda job, name: next(s['run'] for s in jobs[job]['steps'] if s.get('name') == name)
open(sys.argv[2] + '/check.sh', 'w').write(step('check', 'The version'))
open(sys.argv[2] + '/publish.sh', 'w').write(step('publish', 'Publish the GitHub Release'))
PY
gates=$(python3 - "$ROOT/.github/workflows" <<'PY'
import sys, yaml
d = sys.argv[1]
load = lambda name: yaml.safe_load(open(f'{d}/{name}'))
triggers = lambda wf: wf.get('on', wf.get(True)) or {}
release = load('release.yml')['jobs']
needs = release['build'].get('needs', [])
needs = [needs] if isinstance(needs, str) else needs
for gate in ('ci.yml', 'ui-ci.yml'):
    callers = [job for job, body in release.items() if body.get('uses') == f'./.github/workflows/{gate}']
    if not callers:
        print(f'FAIL no release job calls {gate} :: the release runs the verification gates')
    for job in callers:
        if job not in needs:
            print(f'FAIL build does not need {job}, which runs {gate} :: packaging waits for the verification gates')
    on = triggers(load(gate))
    if 'workflow_call' not in on:
        print(f'FAIL {gate} cannot be called: no workflow_call trigger :: the release reuses the gates')
    for kept in ('push', 'pull_request'):
        if kept not in on:
            print(f'FAIL {gate} lost its {kept} trigger :: the gates still run on their own triggers')
ui = load('ui-ci.yml')
if 'github.workflow' not in str((ui.get('concurrency') or {}).get('group', '')):
    print('FAIL the ui-ci.yml concurrency group lacks github.workflow :: a release run and a push to main never cancel each other')
for event in ('push', 'pull_request'):
    if 'doc/**' not in ((triggers(ui).get(event) or {}).get('paths') or []):
        print(f'FAIL ui-ci.yml {event} paths lack doc/** :: an edit to the architecture reference runs its navigation test')
steps = lambda job: ui['jobs'][job]['steps']
at = lambda job, found: next((i for i, s in enumerate(steps(job)) if found(s)), None)
host = lambda job: at(job, lambda s: s.get('name') == 'Build control-plane host')
dotnet = lambda job: at(job, lambda s: str(s.get('uses', '')).startswith('actions/setup-dotnet@'))
flows = at('e2e', lambda s: 'test:e2e' in str(s.get('run', '')))
if host('e2e') is None or flows is None or host('e2e') > flows:
    print('FAIL the ui-ci.yml e2e job does not build the control-plane host before its end-to-end tests :: the specs that ask for the real host find it built')
elif dotnet('e2e') is None or dotnet('e2e') > host('e2e') or steps('e2e')[dotnet('e2e')] != steps('ui')[dotnet('ui')]:
    print('FAIL the ui-ci.yml e2e job does not set up .NET as the ui job does before it builds the host :: both jobs build with the SDK global.json pins')
elif steps('e2e')[host('e2e')].get('run') != steps('ui')[host('ui')].get('run'):
    print('FAIL the ui-ci.yml e2e job builds the control-plane host differently from the ui job :: main resolves the same binary in both jobs')
if 'check' not in needs:
    print('FAIL build does not need check :: packaging waits for the version check')
PY
) || { echo "FAIL could not read release.yml, ci.yml and ui-ci.yml for the gate check"; fail=1; }
[ -z "$gates" ] || { echo "$gates"; fail=1; }
pins=$(python3 - "$ROOT/.github/workflows" <<'PY'
import glob, re, sys, yaml
files = sorted(glob.glob(f'{sys.argv[1]}/*.yml') + glob.glob(f'{sys.argv[1]}/*.yaml'))
if not files:
    print(f'FAIL no workflow file under {sys.argv[1]} :: every external action is pinned to a commit')
m = lambda node: {k.value: v for k, v in node.value} if isinstance(node, yaml.MappingNode) else {}
checked = 0
for path in files:
    name = path.rsplit('/', 1)[1]
    lines = open(path).read().split('\n')
    for job, body in m(m(yaml.compose(open(path))).get('jobs')).items():
        body = m(body)
        steps = body.get('steps')
        refs = [body['uses']] if 'uses' in body else []
        refs += [m(s)['uses'] for s in (steps.value if isinstance(steps, yaml.SequenceNode) else []) if 'uses' in m(s)]
        for ref in refs:
            uses = ref.value
            if uses.startswith('./'):
                continue
            checked += 1
            if not re.fullmatch(r'[^@\s]+@[0-9a-f]{40}', uses):
                print(f'FAIL {name} job {job} uses {uses}, not a commit of 40 lowercase hex :: every external action is pinned to a commit')
            elif not re.fullmatch(r'\s+#\s*v\d+(\.\d+)*\S*\s*', lines[ref.end_mark.line][ref.end_mark.column:]):
                print(f'FAIL {name} job {job} uses {uses} without a trailing comment naming the version it was pinned from :: every external action is pinned to a commit')
if files and not checked:
    print('FAIL no external action found in any job of the workflows, so nothing was checked :: every external action is pinned to a commit')
PY
) || { echo "FAIL could not read the workflows under .github/workflows for the pin check"; fail=1; }
[ -z "$pins" ] || { echo "$pins"; fail=1; }
mkdir -p "$work/bin" "$work/release" "$work/runner"; touch "$work/release/Scout-Desktop-1.0.0-linux-x86_64.AppImage"
cat > "$work/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "gh $*" >> "$GH_LOG"
if [ "$1 $2" = "release view" ]; then [ -n "$FAKE_RELEASE_EXISTS" ]; exit; fi
exit 0
FAKE
chmod +x "$work/bin/gh"
g() { git -C "$work/repo" -c user.name=t -c user.email=t@t -c commit.gpgsign=false -c tag.gpgsign=false -c core.hooksPath=/dev/null "$@"; }
repo() { rm -rf "$work/repo"; git init -q -b main "$work/repo"; mkdir -p "$work/repo/tools/release"; cp "$ROOT/tools/release/next-version.sh" "$work/repo/tools/release/"
  g commit -q --allow-empty -m "chore: start"; g tag v1.2.3; g commit -q --allow-empty -m "feat: a read"; g commit -q --allow-empty -m "fix: a crash"; }
# run <ref_type> <ref_name> <event> <kind> <version override> [release exists]
run() { rm -f "$work/out" "$work/summary" "$work/gh.log"; touch "$work/gh.log"
  check=$(cd "$work/repo" && GITHUB_REF_TYPE=$1 GITHUB_REF_NAME=$2 GITHUB_EVENT_NAME=$3 KIND=$4 VERSION_OVERRIDE=$5 \
    GITHUB_SHA=$(g rev-parse HEAD) GITHUB_OUTPUT="$work/out" GITHUB_STEP_SUMMARY="$work/summary" bash -eo pipefail "$work/check.sh" 2>&1); rc=$?
  [ "$rc" = 0 ] || return 0
  eval "$(python3 - "$work/out" <<'PY'
import sys, shlex
lines = open(sys.argv[1]).read().split('\n'); i = 0
while i < len(lines):
    line = lines[i]
    if '<<' in line:
        key, delim = line.split('<<', 1); buf = []; i += 1
        while lines[i] != delim: buf.append(lines[i]); i += 1
        print(f"OUT_{key}={shlex.quote(chr(10).join(buf))}")
    elif '=' in line:
        key, value = line.split('=', 1); print(f"OUT_{key}={shlex.quote(value)}")
    i += 1
PY
)"
  (cd "$work" && PATH="$work/bin:$PATH" GH_LOG="$work/gh.log" FAKE_RELEASE_EXISTS=$6 TAG=$OUT_tag CREATE=$OUT_create \
    PRERELEASE=$OUT_prerelease NOTES=$OUT_notes RUNNER_TEMP="$work/runner" GITHUB_SHA=$(g rev-parse HEAD) \
    bash -eo pipefail "$work/publish.sh" >/dev/null 2>&1) || { echo "FAIL the publish step failed :: $name"; fail=1; }
}
publish_call() { grep -E '^gh release (create|upload)' "$work/gh.log"; }
ok() { [ "$rc" = 0 ] || { echo "FAIL check exited $rc :: $name"; echo "$check" | sed 's/^/    /'; fail=1; }; }
refused() { [ "$rc" != 0 ] || { echo "FAIL check passed, want refused :: $name"; fail=1; }
  printf '%s' "$check" | grep -qF "$1" || { echo "FAIL check output lacks '$1' :: $name"; fail=1; }
  [ ! -s "$work/gh.log" ] || { echo "FAIL gh ran after a refused check :: $name"; fail=1; }; }
version_is() { grep -qx "version=$1" "$work/out" 2>/dev/null || { echo "FAIL want version $1, got '$(grep '^version=' "$work/out" 2>/dev/null)' :: $name"; fail=1; }; }
has() { publish_call | grep -qF -- "$1" || { echo "FAIL the publish call lacks '$1': '$(publish_call)' :: $name"; fail=1; }; }
lacks() { if publish_call | grep -qF -- "$1"; then echo "FAIL the publish call has '$1': '$(publish_call)' :: $name"; fail=1; fi; }

repo
name="kind=release from main computes the version and creates the tag on the run's commit with the notes"
run branch main workflow_dispatch release ""; ok; version_is 1.3.0
has "gh release create v1.3.0 --target $(g rev-parse HEAD)"; has "--notes-file"; lacks "--prerelease"; lacks "--verify-tag"
grep -qF "## Releasing v1.3.0 (release)" "$work/summary" || { echo "FAIL the job summary does not name the version :: $name"; fail=1; }
grep -qxF -- "- feat: a read" "$work/runner/notes.md" || { echo "FAIL the release notes lack the feat :: $name"; fail=1; }
name="kind=prerelease from main is a beta marked prerelease"
run branch main workflow_dispatch prerelease ""; ok; version_is 1.3.0-beta.1; has "gh release create v1.3.0-beta.1 --target"; has "--prerelease"
g tag v1.3.0-beta.1
name="the next prerelease counts the existing beta tags"
run branch main workflow_dispatch prerelease ""; ok; version_is 1.3.0-beta.2
name="an override replaces the computed version"
run branch main workflow_dispatch release 2.0.0; ok; version_is 2.0.0; has "gh release create v2.0.0 --target"
name="an override with a suffix does not fit a release"
run branch main workflow_dispatch release 2.0.0-beta.1; refused "does not fit a release"
name="an override without a suffix does not fit a prerelease"
run branch main workflow_dispatch prerelease 2.0.0; refused "does not fit a prerelease"
name="an override whose tag exists is refused"
run branch main workflow_dispatch release 1.2.3; refused "The tag v1.2.3 already exists."
name="a dispatch from another branch is refused"
run branch feat/x workflow_dispatch release ""; refused "A release runs from a version tag, or by hand from main"
name="a push to main is refused"
run branch main push "" ""; refused "A release runs from a version tag, or by hand from main"
name="a pushed tag publishes with GitHub's notes from that tag, never as a prerelease"
run tag v1.3.0-beta.7 push "" ""; ok; version_is 1.3.0-beta.7
has "gh release create v1.3.0-beta.7 --verify-tag"; has "--generate-notes"; lacks "--prerelease"; lacks "--target"
name="a tag that is not a version is refused"
run tag vnext push "" ""; refused "Tag vnext is not a version."
name="a rerun on an existing release uploads with --clobber"
run tag v1.3.0 push "" "" yes; ok; has "gh release upload v1.3.0 release/Scout-Desktop-1.0.0-linux-x86_64.AppImage --clobber"; lacks "release create"
name="a rerun from main after publish created the release uploads with --clobber"
run branch main workflow_dispatch release 2.0.0 yes; ok; has "gh release upload v2.0.0"; lacks "release create"
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail
