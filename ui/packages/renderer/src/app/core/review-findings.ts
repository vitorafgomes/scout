import { REVIEW_MARKER, type ReviewFinding, type ReviewFindings } from '@scout/shared';

const BLOCK = /```scout-findings[ \t]*\r?\n([\s\S]*?)\r?\n```/g;
const REPOSITORY = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;
const FINDING_ID = /^[A-Za-z0-9-]{1,40}$/;
const SEVERITIES: ReadonlySet<unknown> = new Set(['block', 'fix', 'nit']);
const MAX_FINDINGS = 50;
const MAX_TITLE_CHARS = 200;

const MARKS: Readonly<Record<ReviewFinding['severity'], string>> = {
  block: '\u{1F534}',
  fix: '\u{1F7E1}',
  nit: '\u{1F7E2}',
};

/** A finding the owner chose to post, with the comment body it goes with. */
export interface SelectedFinding {
  readonly finding: ReviewFinding;
  readonly body: string;
}

/**
 * The review in a reply's single `scout-findings` block, validated, and the reply without the block;
 * null when there is no such block, more than one, or the block is not a valid review.
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

function toReview(data: unknown): ReviewFindings | null {
  if (!isRecord(data)) return null;
  const { repository, number, commit, verdict, read, findings } = data;
  if (
    typeof repository !== 'string' ||
    !REPOSITORY.test(repository) ||
    !Number.isInteger(number) ||
    (number as number) < 1 ||
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
    repository,
    number: number as number,
    ...(commit === undefined ? {} : { commit: commit as string }),
    verdict: verdict as string,
    ...(read === undefined ? {} : { read: read as string }),
    findings: valid,
  };
}

function toFinding(data: unknown): ReviewFinding | null {
  if (!isRecord(data)) return null;
  const { id, severity, title, path, line, context, why, fix } = data;
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
    !isText(fix)
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
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isText(value: unknown): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}

function optionalString(value: unknown): boolean {
  return value === undefined || typeof value === 'string';
}
