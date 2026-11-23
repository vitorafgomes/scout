import type {
  GitHubVerdictRequest,
  LocalTurn,
  ReviewCheck,
  ReviewCoverage,
  ReviewFinding,
  ReviewFindings,
  ReviewEarlier,
  ReviewEarlierFinding,
  ReviewOffer,
  ReviewTests,
} from './ipc-contract.js';

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
const GITHUB_URL = /^https:\/\/github\.com\/[^\s]+$/;
const OUTCOMES: ReadonlySet<unknown> = new Set(['fixed', 'open', 'answered', 'unknown']);
const MAX_EARLIER = 30;
const PASSING_CONCLUSIONS: ReadonlySet<string | null> = new Set(['success', 'neutral', 'skipped']);

/** A finding the owner chose to post, with the comment or issue body it goes with. */
export interface SelectedFinding {
  readonly finding: ReviewFinding;
  readonly body: string;
}

/**
 * The review or audit in a reply's single `scout-findings` block, validated, and the reply without the
 * block; null when there is no such block, more than one, or the block is not a valid review or audit.
 * A review job's measured `coverage` and `notes` are kept only when `notice` says the block came in a worker
 * notice, the way the job posts its card; in a reply they are dropped, since a model or a pull request's text could write them.
 */
export function parseReviewFindings(
  reply: string,
  notice = false,
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
  const review = toReview(data, notice);
  if (review === null) return null;
  const before = reply.slice(0, block.index).trimEnd();
  const after = reply.slice(block.index + block[0].length).trimStart();
  return { review, markdown: `${before}\n\n${after}`.trim() };
}

/** How a check reads on the card: passed, failed, or still pending. */
export type CheckMark = 'Passed' | 'Failed' | 'Pending';

/** A check is pending until GitHub completes it; then success, neutral and skipped pass and every other conclusion fails. */
export function checkMark(check: ReviewCheck): CheckMark {
  if (check.status !== 'completed' || check.conclusion === null) return 'Pending';
  return PASSING_CONCLUSIONS.has(check.conclusion) ? 'Passed' : 'Failed';
}

/**
 * The checks of the reviewed commit as the worker judges an approval: one is running while its status is not
 * `completed`; one failed once completed with a conclusion other than success, neutral or skipped. `failed` names the
 * first that failed, or is null.
 */
export function checksOutcome(checks: readonly ReviewCheck[] | undefined): {
  readonly running: number;
  readonly failed: string | null;
} {
  const all = checks ?? [];
  return {
    running: all.filter((c) => c.status !== 'completed').length,
    failed:
      all.find((c) => c.status === 'completed' && !PASSING_CONCLUSIONS.has(c.conclusion))?.name ??
      null,
  };
}

/**
 * The line the container run reads as: passed with its time and command, failed with its exit code, could not run with
 * the worker's sentence, or why it did not run.
 */
export function testsLine(tests: ReviewTests): string {
  if (!tests.ran) return `Tests did not run: ${tests.reason}`;
  if (tests.unrunnable !== undefined) return `Tests could not run: ${tests.unrunnable}`;
  return tests.exitCode === 0
    ? `Tests passed in ${Math.round(tests.durationSeconds ?? 0)}s (${tests.command})`
    : `Tests failed (exit ${tests.exitCode})`;
}

/** The tests line a verdict posts after it: the checks with their marks, then the container run; empty when the review carries neither. */
export function verdictTestsLine(review: ReviewFindings): string {
  const checks = review.checks?.map((c) => `${c.name} ${checkMark(c).toLowerCase()}`);
  return [
    ...(checks === undefined ? [] : [`Checks: ${checks.join(', ')}`]),
    ...(review.tests === undefined ? [] : [testsLine(review.tests)]),
  ].join('\n');
}

/**
 * The verdict a click on Approve or Request changes gives, or with `comment` the review a click on Post sends: the
 * review body and the selected findings as its comments, pinned to the reviewed commit, with the tests line on an
 * approval or a request for changes; null on an audit or a review that names no commit.
 */
export function verdictRequest(
  review: ReviewFindings,
  selected: readonly SelectedFinding[],
  verdict: GitHubVerdictRequest['verdict'],
): GitHubVerdictRequest | null {
  if (review.kind === 'audit' || review.number === undefined || !review.commit) return null;
  return {
    repository: review.repository,
    number: review.number,
    verdict,
    findings: selected.map((s) => s.finding),
    ...(review.coverage === undefined ? {} : { coverage: review.coverage }),
    ...(review.notes === undefined ? {} : { notes: review.notes }),
    commit: review.commit,
    testsLine: verdict === 'comment' ? '' : verdictTestsLine(review),
  };
}

/** The review or audit a turn's reply shows as cards: only a done turn's, its notice read as a job's card. */
export function cardOfTurn(
  turn: Pick<LocalTurn, 'state' | 'reply' | 'notice'>,
): ReviewFindings | undefined {
  return turn.state === 'done'
    ? parseReviewFindings(turn.reply, turn.notice === true)?.review
    : undefined;
}

/** The newest review card, never an audit's, of pull request `number` of `repository` (any case) among `turns`. */
export function latestReviewCard(
  turns: readonly Pick<LocalTurn, 'state' | 'reply' | 'notice'>[],
  repository: string,
  number: number,
): ReviewFindings | null {
  for (let i = turns.length - 1; i >= 0; i--) {
    const review = cardOfTurn(turns[i]!);
    if (
      review !== undefined &&
      review.kind !== 'audit' &&
      review.number === number &&
      review.repository.toLowerCase() === repository.toLowerCase()
    ) {
      return review;
    }
  }
  return null;
}

function toReview(data: unknown, notice: boolean): ReviewFindings | null {
  if (!isRecord(data)) return null;
  const { kind, repository, number, commit, verdict, read, known, findings } = data;
  const { checks, tests, selfAuthored, coverage, notes, offer, earlier } = data;
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
    (audit && findings.length === 0) ||
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
    ...(audit || !notice ? {} : toCoverage(coverage, notes)),
    ...(audit || toOffer(offer) === undefined ? {} : { offer: toOffer(offer) as ReviewOffer }),
    ...(audit || !notice ? {} : toEarlier(earlier)),
  };
}

/**
 * A review job's section on Scout's earlier findings, kept only from a worker notice like the coverage: its checked and
 * more flags, why it was not checked, and each item read strictly; an item with an unknown outcome or severity, a title
 * or reason that is not text, or an address that is not an https address on github.com is not shown and is counted as
 * unreadable. No section when the block has none or it is not an object.
 */
function toEarlier(raw: unknown): { earlier?: ReviewEarlier } {
  if (!isRecord(raw) || typeof raw['checked'] !== 'boolean' || !Array.isArray(raw['items']))
    return {};
  const read = (raw['items'] as unknown[]).slice(0, MAX_EARLIER).map(toEarlierFinding);
  const items = read.filter((i): i is ReviewEarlierFinding => i !== null);
  const why = raw['why'];
  return {
    earlier: {
      checked: raw['checked'],
      more: raw['more'] === true,
      ...(typeof why === 'string' && isNote(why) ? { why } : {}),
      items,
      unreadable: read.length - items.length,
    },
  };
}

function toEarlierFinding(raw: unknown): ReviewEarlierFinding | null {
  if (!isRecord(raw)) return null;
  const { title, severity, path, line, outcome, reason, url } = raw;
  if (
    typeof title !== 'string' ||
    title.trim().length === 0 ||
    title.length > MAX_TITLE_CHARS ||
    !SEVERITIES.has(severity) ||
    typeof path !== 'string' ||
    path.length === 0 ||
    (line !== null && line !== undefined && (!Number.isInteger(line) || (line as number) < 1)) ||
    !OUTCOMES.has(outcome) ||
    typeof reason !== 'string' ||
    !isNote(reason) ||
    (url !== null && url !== undefined && (typeof url !== 'string' || !GITHUB_URL.test(url)))
  ) {
    return null;
  }
  return {
    title,
    severity: severity as ReviewEarlierFinding['severity'],
    path,
    line: typeof line === 'number' ? line : null,
    outcome: outcome as ReviewEarlierFinding['outcome'],
    reason,
    url: typeof url === 'string' ? url : null,
  };
}

const OFFERS: ReadonlySet<string> = new Set([
  'approve',
  'request_changes',
  'comment',
  'wait',
  'none',
]);

/** The longest reason of an offer kept. */
export const MAX_OFFER_REASON = 600;

/**
 * The worker's offer when it is one: a known verdict, a reason of 1 to {@link MAX_OFFER_REASON} characters, and
 * commentInstead true only beside request_changes; anything else is no offer. Extra fields are ignored.
 */
export function toOffer(value: unknown): ReviewOffer | undefined {
  if (!isRecord(value)) return undefined;
  const { verdict, reason, commentInstead } = value;
  if (
    typeof verdict !== 'string' ||
    !OFFERS.has(verdict) ||
    typeof reason !== 'string' ||
    reason.trim().length === 0 ||
    reason.length > MAX_OFFER_REASON ||
    typeof commentInstead !== 'boolean' ||
    (commentInstead && verdict !== 'request_changes')
  ) {
    return undefined;
  }
  return { verdict: verdict as ReviewOffer['verdict'], reason, commentInstead };
}

/** The reason a card from before Scout decided the verdict shows: such a card offers Comment only. */
export const OLD_CARD_REASON =
  'This review was made before Scout decided the verdict; review it again to see what Scout offers.';

/** What a review card offers: the worker's offer, or for a card from before it, Comment only, never Approve. */
export function offerOf(review: Pick<ReviewFindings, 'offer'>): ReviewOffer {
  return review.offer ?? { verdict: 'comment', reason: OLD_CARD_REASON, commentInstead: false };
}

/**
 * A review job's measured read line and Scout's notes: both kept only when the coverage is two whole counts, the files
 * read at most the files changed, and the notes a short list of short lines; otherwise the block shows as before.
 */
function toCoverage(
  coverage: unknown,
  notes: unknown,
): { coverage?: ReviewCoverage; notes?: readonly string[] } {
  if (!isRecord(coverage)) return {};
  const { read, of } = coverage;
  if (
    !Number.isInteger(read) ||
    !Number.isInteger(of) ||
    (read as number) < 0 ||
    (read as number) > (of as number)
  )
    return {};
  return {
    coverage: { read: read as number, of: of as number },
    ...(notes !== undefined && optionalNotes(notes) ? { notes: notes as string[] } : {}),
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
  const { ran, reason, image, command, exitCode, durationSeconds, output, unrunnable } = data;
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
      ...(exitCode !== 0 && isText(unrunnable) ? { unrunnable: unrunnable as string } : {}),
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
