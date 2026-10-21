export const meta = {
  name: 'scout-review',
  description: 'Pipeline gate 6: review the working-tree diff with scout-reviewer, one lens per agent, then try to refute every finding before it is reported',
  whenToUse: 'After gates 4-5 are green. args: { module: "Scout.Llm", lenses: ["invariants","read-only"], dispatch: "<reviewer dispatch template, filled>", range?: "HEAD" }',
  phases: [
    { title: 'Review', detail: 'one scout-reviewer per lens, opus' },
    { title: 'Refute', detail: 'an independent reviewer tries to refute each finding' },
  ],
}

const module = (args && args.module) || '<unnamed module>'
const dispatch = (args && args.dispatch) || ''
const range = (args && args.range) || 'HEAD'

// `invariants` always runs; the gate 6 table adds the others. `performance` is scout-optimizer's,
// never a lens here.
const requested = ((args && args.lenses) || []).filter(l => l !== 'performance' && l !== 'invariants')
const LENSES = ['invariants', ...new Set(requested)]

const FINDINGS = {
  type: 'object',
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          file: { type: 'string' },
          line: { type: 'integer' },
          severity: { type: 'string', enum: ['P1', 'P2'] },
          cites: { type: 'string', description: 'the vault page, ADR or root Rule letter the finding violates' },
          why: { type: 'string' },
          fix: { type: 'string' },
        },
        required: ['file', 'line', 'severity', 'cites', 'why', 'fix'],
      },
    },
    checked: { type: 'array', items: { type: 'string' }, description: 'pages, ADRs and Rules checked and clean' },
    uncited: { type: 'array', items: { type: 'string' }, description: 'non-blocking observations, max 3' },
  },
  required: ['findings', 'checked', 'uncited'],
}

const VERDICT = {
  type: 'object',
  properties: {
    refuted: { type: 'boolean' },
    reason: { type: 'string' },
  },
  required: ['refuted', 'reason'],
}

function reviewPrompt(lens) {
  const scope = lens === 'invariants'
    ? 'Lens: invariants. This lens also owns regression coverage (the map and the gate 5 evidence), Rule H and J, and the generic correctness lens.'
    : `Lens: ${lens}. Check only this lens; another agent covers invariants and regression coverage.`
  return `Review the working-tree diff (\`git diff ${range}\` + \`git status --porcelain\`, read untracked files) for module: ${module}.
${scope}

Dispatch from the manager (map, gate 5 evidence, Jev probabilities, skill results):
${dispatch}

Follow your procedure. Return findings ONLY where you verified the cited line in the actual code and a vault page, ADR or root Rule backs it. At most five findings, most severe first. An empty findings list on a sound diff is correct.`
}

function refutePrompt(f, lens) {
  return `An independent review (lens: ${lens}) of the working-tree diff for module ${module} reported this finding:

  ${f.file}:${f.line} | ${f.severity} | violates ${f.cites}
  why: ${f.why}
  fix: ${f.fix}

Try to REFUTE it. Open the file at the cited line, read the vault page, ADR or Rule it cites, and decide whether the code as written actually violates it in a way that changes correctness, turns a descriptive write into an executive one, leaks a credential, lets a vendor type into the core, or leaves a requirement unproven. Default to refuted=true if the violation is stylistic, speculative, already handled elsewhere in the diff, or not backed by the cited page. Read-only.`
}

const results = await pipeline(
  LENSES,
  lens => agent(reviewPrompt(lens), {
    label: `review:${lens}`,
    phase: 'Review',
    agentType: 'scout-reviewer',
    schema: FINDINGS,
    model: 'opus',
    effort: 'high',
  }),
  (review, lens) => {
    if (!review) return null
    return parallel(review.findings.map(f => () =>
      agent(refutePrompt(f, lens), {
        label: `refute:${f.file.split(/[\\/]/).pop()}:${f.line}`,
        phase: 'Refute',
        agentType: 'scout-reviewer',
        schema: VERDICT,
        model: 'sonnet',
        effort: 'high',
      }).then(v => ({ ...f, lens, verdict: v }))
    )).then(verified => ({ lens, checked: review.checked, uncited: review.uncited, verified: verified.filter(Boolean) }))
  },
)

const lensResults = results.filter(Boolean)
const all = lensResults.flatMap(r => r.verified)
const confirmed = all.filter(f => f.verdict && f.verdict.refuted === false)
const refuted = all.filter(f => !f.verdict || f.verdict.refuted !== false)

log(`${confirmed.length} confirmed, ${refuted.length} refuted, ${LENSES.length - lensResults.length} lens(es) failed to return`)

return {
  module,
  lensesRun: lensResults.map(r => r.lens),
  confirmed: confirmed.map(({ verdict, ...f }) => f),
  refuted: refuted.map(f => ({ file: f.file, line: f.line, cites: f.cites, reason: f.verdict ? f.verdict.reason : 'refuter did not return' })),
  checked: [...new Set(lensResults.flatMap(r => r.checked))],
  uncited: lensResults.flatMap(r => r.uncited).slice(0, 3),
}
