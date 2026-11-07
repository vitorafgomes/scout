import {
  ISSUES_MARKER,
  REVIEW_MARKER,
  VERDICT_MARKER,
  type LocalTurn,
  type ReviewCheck,
  type ReviewFinding,
  type ReviewFindings,
  type ReviewTests,
} from '@scout/shared';

const BLOCK = /```scout-findings[ \t]*\r?\n([\s\S]*?)\r?\n```/g;
const REPOSITORY = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;
const FINDING_ID = /^[A-Za-z0-9-]{1,40}$/;
const SEVERITIES: ReadonlySet<unknown> = new Set(['block', 'fix', 'nit']);
const MAX_FINDINGS = 50;
const MAX_TITLE_CHARS = 200;
const MAX_KNOWN = 20;
const MAX_NOTE_CHARS = 300;
const MAX_CHECKS = 100;
const MAX_OUTPUT_CHARS = 4000;
const HTTPS_URL = /^https:\/\/[^\s]+$/;
const VERDICT_QUESTION = /^(Approve|Request changes on) ([A-Za-z0-9._-]+\/[A-Za-z0-9._-]+#\d+)$/;
const GITHUB_URL = /https:\/\/github\.com\/[^\s)\]>"'<]+/;

const MARKS: Readonly<Record<ReviewFinding['severity'], string>> = {
  block: '\u{1F534}',
  fix: '\u{1F7E1}',
  nit: '\u{1F7E2}',
};

/** A finding the owner chose to post, with the comment or issue body it goes with. */
export interface SelectedFinding {
  readonly finding: ReviewFinding;
  readonly body: string;
}

/**
 * The review or audit in a reply's single `scout-findings` block, validated, and the reply without the
 * block; null when there is no such block, more than one, or the block is not a valid review or audit.
 */
export function parseReviewFindings(
  reply: string,
): { review: ReviewFindings; markdown: string } | null {
  const blocks = [...reply.matchAll(BLOCK)];
  if (blocks.length !== 1) return null;
  const block = blocks[0]!;
  let data: unknown;
  try {
    data = JSON.parse(block[1]!);
  } catch {
    return null;
  }
  const review = toReview(data);
  if (review === null) return null;
  const before = reply.slice(0, block.index).trimEnd();
  const after = reply.slice(block.index + block[0].length).trimStart();
  return { review, markdown: `${before}\n\n${after}`.trim() };
}

/** The comment a finding becomes on the pull request: the mark and title, the context, why, and the fix. */
export function commentBody(finding: ReviewFinding): string {
  const context = finding.context ? `${finding.context}\n\n` : '';
  return `${MARKS[finding.severity]} ${finding.title}\n\n${context}${finding.why}\n\n**Fix:** ${finding.fix}`;
}

/** The review's own body: the verdict, one line per selected finding, then what was read. */
export function reviewBody(review: ReviewFindings, selected: readonly ReviewFinding[]): string {
  const lines = selected.map((f) => `- ${MARKS[f.severity]} ${f.title} · \`${f.path}:${f.line}\``);
  const read = review.read ? `\n\n${review.read}` : '';
  return `**Verdict:** ${review.verdict}\n\n${lines.join('\n')}${read}`;
}

/** The question that asks the worker to post the selected findings as one review, the review after the marker. */
export function postingAsk(review: ReviewFindings, selected: readonly SelectedFinding[]): string {
  const n = selected.length;
  const payload = {
    repository: review.repository,
    number: review.number,
    body: reviewBody(
      review,
      selected.map((s) => s.finding),
    ),
    comments: selected.map((s) => ({ path: s.finding.path, line: s.finding.line, body: s.body })),
  };
  return `Post ${n} finding${n === 1 ? '' : 's'} on ${review.repository}#${review.number}${REVIEW_MARKER}${JSON.stringify(payload)}`;
}

/** The verdict the owner gives on a pull request from the cards. */
export type VerdictKind = 'approve' | 'request_changes';

/** Where the owner's verdict on a review stands, read from the conversation: being given, given with its link, or failed. */
export interface VerdictState {
  readonly verdict: VerdictKind;
  readonly phase: 'giving' | 'given' | 'failed';
  readonly url?: string;
}

/**
 * The question that asks the worker to approve the pull request or request changes on it, with the review body
 * and the selected findings as its comments after the marker, as Post sends them.
 */
export function verdictAsk(
  review: ReviewFindings,
  selected: readonly SelectedFinding[],
  verdict: VerdictKind,
): string {
  const target = `${review.repository}#${review.number}`;
  const payload = {
    repository: review.repository,
    number: review.number,
    verdict,
    body: reviewBody(
      review,
      selected.map((s) => s.finding),
    ),
    comments: selected.map((s) => ({ path: s.finding.path, line: s.finding.line, body: s.body })),
  };
  const sentence = verdict === 'approve' ? `Approve ${target}` : `Request changes on ${target}`;
  return `${sentence}${VERDICT_MARKER}${JSON.stringify(payload)}`;
}

/** True when a turn's question is the sentence a verdict click sends, `Approve o/r#n` or `Request changes on o/r#n`. */
export function isVerdictQuestion(question: string): boolean {
  return VERDICT_QUESTION.test(question);
}

/**
 * The verdict given on a review after its findings turn: the first verdict turn that completed with a GitHub
 * link is given; otherwise the latest one is being given while it runs, or failed.
 */
export function verdictOf(
  turns: readonly LocalTurn[],
  turnId: string,
  review: ReviewFindings,
): VerdictState | null {
  if (review.kind === 'audit' || review.number === undefined) return null;
  const target = `${review.repository}#${review.number}`;
  let latest: VerdictState | null = null;
  for (const turn of turns.slice(turns.findIndex((t) => t.id === turnId) + 1)) {
    const match = VERDICT_QUESTION.exec(turn.question);
    if (match === null || match[2] !== target) continue;
    const verdict: VerdictKind = match[1] === 'Approve' ? 'approve' : 'request_changes';
    const url = turn.state === 'done' ? GITHUB_URL.exec(turn.reply)?.[0] : undefined;
    if (url !== undefined) return { verdict, phase: 'given', url: url.replace(/[.,;:!?]+$/, '') };
    latest = { verdict, phase: turn.state === 'running' ? 'giving' : 'failed' };
  }
  return latest;
}

/** The issue a finding of an audit becomes: the owner's edited text when there is one, otherwise the problem, the evidence and the proposed change. */
export function issueBody(review: ReviewFindings, finding: ReviewFinding, edited?: string): string {
  if (edited !== undefined) return edited;
  const context = finding.context ? `\n\n${finding.context}` : '';
  const rule = finding.rule ? `\n\n**Rule:** ${finding.rule}` : '';
  const at = review.commit ? ` at ${review.commit}` : '';
  return (
    `## Problem\n\n${MARKS[finding.severity]} ${finding.title}\n\n${finding.why}\n\n` +
    `## Evidence\n\n\`${finding.path}:${finding.line}\`${context}\n\n` +
    `## Proposed change\n\n${finding.fix}${rule}\n\n` +
    `---\nDrafted by Scout from an audit of ${review.repository}${at}.`
  );
}

/** The question that asks the worker to open one issue per selected finding of an audit, the issues after the marker. */
export function issuesAsk(review: ReviewFindings, selected: readonly SelectedFinding[]): string {
  const n = selected.length;
  const payload = {
    repository: review.repository,
    issues: selected.map((s) => ({ title: s.finding.title, body: s.body })),
  };
  return `Open ${n} issue${n === 1 ? '' : 's'} on ${review.repository}${ISSUES_MARKER}${JSON.stringify(payload)}`;
}

function toReview(data: unknown): ReviewFindings | null {
  if (!isRecord(data)) return null;
  const { kind, repository, number, commit, verdict, read, known, findings } = data;
  const { checks, tests, selfAuthored } = data;
  const audit = kind === 'audit';
  if (
    (kind !== undefined && kind !== 'review' && !audit) ||
    typeof repository !== 'string' ||
    !REPOSITORY.test(repository) ||
    (audit
      ? number !== undefined || !optionalNotes(known)
      : !Number.isInteger(number) || (number as number) < 1) ||
    !isText(verdict) ||
    !optionalString(commit) ||
    !optionalString(read) ||
    !Array.isArray(findings) ||
    findings.length === 0 ||
    findings.length > MAX_FINDINGS
  ) {
    return null;
  }
  const parsed = findings.map(toFinding);
  if (parsed.some((f) => f === null)) return null;
  const valid = parsed as ReviewFinding[];
  if (new Set(valid.map((f) => f.id)).size !== valid.length) return null;
  return {
    ...(kind === undefined ? {} : { kind: kind as 'review' | 'audit' }),
    repository,
    ...(audit ? {} : { number: number as number }),
    ...(commit === undefined ? {} : { commit: commit as string }),
    verdict: verdict as string,
    ...(read === undefined ? {} : { read: read as string }),
    ...(audit && known !== undefined ? { known: known as string[] } : {}),
    findings: valid,
    ...toChecks(checks),
    ...toTests(tests),
    ...(typeof selfAuthored === 'boolean' ? { selfAuthored } : {}),
  };
}

function toChecks(data: unknown): { checks?: readonly ReviewCheck[] } {
  if (!Array.isArray(data)) return {};
  const checks = data.slice(0, MAX_CHECKS).flatMap((item): ReviewCheck[] => {
    if (!isRecord(item)) return [];
    const { name, status, conclusion, url } = item;
    return isText(name) && isText(status) && (conclusion === null || isText(conclusion))
      ? [
          {
            name: name as string,
            status: status as string,
            conclusion: conclusion as string | null,
            url: typeof url === 'string' && HTTPS_URL.test(url) ? url : '',
          },
        ]
      : [];
  });
  return checks.length === 0 ? {} : { checks };
}

function toTests(data: unknown): { tests?: ReviewTests } {
  if (!isRecord(data)) return {};
  const { ran, reason, image, command, exitCode, durationSeconds, output } = data;
  if (ran === false) {
    return isText(reason) ? { tests: { ran, reason: reason as string } } : {};
  }
  if (
    ran !== true ||
    !isText(command) ||
    !Number.isInteger(exitCode) ||
    typeof durationSeconds !== 'number' ||
    !Number.isFinite(durationSeconds) ||
    durationSeconds < 0
  ) {
    return {};
  }
  return {
    tests: {
      ran,
      command: command as string,
      exitCode: exitCode as number,
      durationSeconds,
      ...(isText(image) ? { image: image as string } : {}),
      ...(typeof output === 'string' ? { output: output.slice(-MAX_OUTPUT_CHARS) } : {}),
    },
  };
}

function toFinding(data: unknown): ReviewFinding | null {
  if (!isRecord(data)) return null;
  const { id, severity, title, path, line, context, why, fix, rule } = data;
  if (
    typeof id !== 'string' ||
    !FINDING_ID.test(id) ||
    !SEVERITIES.has(severity) ||
    !isText(title) ||
    (title as string).length > MAX_TITLE_CHARS ||
    !isText(path) ||
    (path as string).includes('..') ||
    !Number.isInteger(line) ||
    (line as number) < 1 ||
    !optionalString(context) ||
    !isText(why) ||
    !isText(fix) ||
    (rule !== undefined && !isNote(rule))
  ) {
    return null;
  }
  return {
    id,
    severity: severity as ReviewFinding['severity'],
    title: title as string,
    path: path as string,
    line: line as number,
    ...(context === undefined ? {} : { context: context as string }),
    why: why as string,
    fix: fix as string,
    ...(rule === undefined ? {} : { rule: rule as string }),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isText(value: unknown): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}

function isNote(value: unknown): boolean {
  return isText(value) && (value as string).length <= MAX_NOTE_CHARS;
}

function optionalNotes(value: unknown): boolean {
  return (
    value === undefined ||
    (Array.isArray(value) && value.length <= MAX_KNOWN && value.every(isNote))
  );
}

function optionalString(value: unknown): boolean {
  return value === undefined || typeof value === 'string';
}
