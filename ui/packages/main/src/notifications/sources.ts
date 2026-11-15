import type {
  GitHubVerdictStatus,
  GitHubWaitingApproval,
  NoticeRecordRequest,
  NotificationKind,
} from '@scout/shared';
import { ISSUES_TARGET, issueLinks, signerInWords, type LocalTurn } from '@scout/shared';
import type { NotificationInput } from './notification-store.js';

const MAX_TITLE_CHARS = 200;
const MAX_DETAIL_CHARS = 400;
const REPOSITORY = '([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)';
const SO_FAR = '(\\d+ findings? so far)';
const AUDIT_BATCH = new RegExp(`^Audit of ${REPOSITORY}, .+ \\((\\d+) of (\\d+), ${SO_FAR}\\)`);
const AUDIT_RESUMED = new RegExp(
  `^Audit of ${REPOSITORY} resumed: area (\\d+) of (\\d+) done, ${SO_FAR}\\.`,
);
export const CARDS_ARRIVE = 'cards arrive in the chat as each area ends';
const AUDIT_PAUSED = new RegExp(`^The audit of ${REPOSITORY} paused: (.+)$`, 's');
const AUDIT_DONE = new RegExp(`^Audit of ${REPOSITORY} at [0-9a-f]+ is done\\.\\s*(.*)$`);
const AUDIT_STOPPED = new RegExp(`^The audit of ${REPOSITORY} ended at your request: (.+)$`);
const STOPPED_COUNTS = /^(\d+) of (\d+) areas done, (\d+) findings kept/;
const TESTS_STOPPED = new RegExp(`^The tests of ${REPOSITORY}#(\\d+) ended at your request`);
const PAUSED_BY_OWNER = 'you paused it.';
export const PAUSED_BY_YOU = 'Paused by you';
export const TESTS_GAVE_NO_RESULT = 'The tests gave no result.';
const CONTINUE = 'ask Scout to continue.';
const PAUSE_SENTENCES: readonly [RegExp, (found: RegExpExecArray) => string][] = [
  [
    /^the AI provider's usage limit was reached\b/,
    () =>
      'The AI Source reached its usage limit. Ask Scout to continue when it has capacity again.',
  ],
  [
    /^the AI provider's account has run out of credit\b/,
    () =>
      "The AI Source's account has run out of credit. Ask Scout to continue once it has credit again.",
  ],
  [
    /^the AI provider's rate limit was reached\b/,
    () => 'The AI Source reached its rate limit. Ask Scout to continue when it has capacity again.',
  ],
  [
    /^the AI provider needs the owner to sign in again\b/,
    () => `The AI Source needs you to sign in again. Sign in from AI Sources, then ${CONTINUE}`,
  ],
  [
    /^the AI Source rejected its key\b/,
    () => `The AI Source rejected its key. Give it a working key in AI Sources, then ${CONTINUE}`,
  ],
  [
    /^the AI Source does not have the model (.+?)\. Ask Scout to continue\b/s,
    (found) =>
      `The AI Source does not have the model ${found[1]}. Choose a model it has in Models, then ${CONTINUE}`,
  ],
];
const FENCED = /```[\s\S]*?(```|$)/g;
const URL_IN_TEXT = /https:\/\/[^\s<>()"'`[\]]+/;
const FAILED = /\b(stopped|failed|could not|did not)\b/i;
const PAUSED = /\bpaused\b/i;
const RUNNING = /(\.\.\.|…)$/;

function cut(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

function linesOf(text: string): string[] {
  return text
    .replace(FENCED, '')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function urlIn(text: string): { url?: string } {
  const found = URL_IN_TEXT.exec(text.replace(FENCED, ''))?.[0]?.replace(/[.,;:!?]+$/, '');
  return found === undefined ? {} : { url: found };
}

/** The one entry of a pull request's test run, `owner/repo#N`, its key compared without case. */
export function testsId(pullRequest: string): string {
  return `tests:${pullRequest.toLowerCase()}`;
}

function pauseSentence(reason: string): string {
  for (const [pattern, sentence] of PAUSE_SENTENCES) {
    const found = pattern.exec(reason);
    if (found !== null) return cut(sentence(found), MAX_DETAIL_CHARS);
  }
  return cut(reason.charAt(0).toUpperCase() + reason.slice(1), MAX_DETAIL_CHARS);
}

function kindOf(firstLine: string): NotificationKind {
  if (FAILED.test(firstLine)) return 'failed';
  if (PAUSED.test(firstLine)) return 'paused';
  if (RUNNING.test(firstLine)) return 'running';
  return 'done';
}

/**
 * The entry for one `SCOUT_LOCAL` line when it is a worker notice: an audit's batches, resume, pause, stop and end update
 * one entry per repository, a batch and a resume as running with the areas done, the findings so far and the progress
 * rounded down, the owner's own pause read as Paused by you and the AI
 * Source's in the artboard's sentence for its reason; the stop of a pull
 * request's test run ends that run's entry; any other notice is its own entry, its kind read from the text.
 */
export function workerNotice(json: string, nextId: () => string): NotificationInput | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }
  const p = (typeof parsed === 'object' && parsed !== null ? parsed : {}) as Record<
    string,
    unknown
  >;
  const text = p['text'];
  if (p['event'] !== 'notice' || p['id'] !== null || typeof text !== 'string') return null;
  const lines = linesOf(text);
  const first = lines[0];
  if (first === undefined) return null;

  const batch = AUDIT_BATCH.exec(first) ?? AUDIT_RESUMED.exec(first);
  if (batch !== null) {
    const [, repository, done, total, soFar] = batch as unknown as [
      string,
      string,
      string,
      string,
      string,
    ];
    const progress = Math.min(100, Math.floor((Number(done) * 100) / Math.max(1, Number(total))));
    return {
      id: `audit:${repository}`,
      kind: 'running',
      title: `Audit of ${repository}`,
      detail: `Area ${done} of ${total} done · ${soFar} · ${CARDS_ARRIVE}`,
      progress,
      source: 'worker',
    };
  }
  const paused = AUDIT_PAUSED.exec(text.trim());
  if (paused !== null) {
    const reason = paused[2] ?? '';
    return {
      id: `audit:${paused[1]}`,
      kind: 'paused',
      title: `Audit of ${paused[1]} paused`,
      detail: reason.startsWith(PAUSED_BY_OWNER) ? PAUSED_BY_YOU : pauseSentence(reason),
      source: 'worker',
    };
  }
  const stopped = AUDIT_STOPPED.exec(first);
  if (stopped !== null) {
    const counts = STOPPED_COUNTS.exec(stopped[2] ?? '');
    return {
      id: `audit:${stopped[1]}`,
      kind: 'done',
      title: `Audit of ${stopped[1]} stopped`,
      detail:
        counts === null
          ? cut(stopped[2] ?? '', MAX_DETAIL_CHARS)
          : `${counts[1]} of ${counts[2]} areas · ${counts[3]} findings kept`,
      source: 'worker',
    };
  }
  const testsStopped = TESTS_STOPPED.exec(first);
  if (testsStopped !== null) {
    const pullRequest = `${testsStopped[1]}#${testsStopped[2]}`;
    return {
      id: testsId(pullRequest),
      kind: 'done',
      title: `Tests for ${pullRequest} stopped`,
      detail: TESTS_GAVE_NO_RESULT,
      source: 'worker',
    };
  }
  const done = AUDIT_DONE.exec(first);
  if (done !== null) {
    return {
      id: `audit:${done[1]}`,
      kind: 'done',
      title: `Audit of ${done[1]} is done`,
      detail: cut(done[2] ?? '', MAX_DETAIL_CHARS),
      source: 'worker',
    };
  }
  return {
    id: nextId(),
    kind: kindOf(first),
    title: cut(first.replace(/:$/, ''), MAX_TITLE_CHARS),
    detail: cut(lines[1] ?? '', MAX_DETAIL_CHARS),
    ...urlIn(text),
    source: 'worker',
  };
}

const VERDICT_LABELS: Readonly<Record<GitHubVerdictStatus['verdict'], readonly [string, string]>> =
  {
    approve: ['Approve', 'Approved'],
    request_changes: ['Request changes', 'Changes requested'],
    comment: ['Comment', 'Commented'],
  };

const TESTS_LINE_WORDS: Readonly<Record<string, string>> = {
  posted: 'the tests line was posted as a comment',
  notPosted: 'the tests line was not posted',
};

/** Who signed a given verdict and what became of its tests line, each part left out when not known or not asked for. */
function givenDetail(status: GitHubVerdictStatus): string {
  const tests = TESTS_LINE_WORDS[status.testsLine ?? ''];
  if (status.signedAs === null) {
    return tests === undefined ? '' : tests.charAt(0).toUpperCase() + tests.slice(1);
  }
  const signer = `Signed as ${signerInWords(status.signedAs)}`;
  return tests === undefined ? signer : `${signer} · ${tests}`;
}

/**
 * The entry of one verdict action, updated in place from queued to given, the verdict in its title and who signed it
 * with its tests line in its detail, or failed.
 */
export function verdictNotification(status: GitHubVerdictStatus): NotificationInput {
  const pr = `${status.repository}#${status.number}`;
  const [label, given] = VERDICT_LABELS[status.verdict];
  const id = `verdict:${status.actionId}`;
  switch (status.state) {
    case 'given':
      return {
        id,
        kind: 'done',
        title:
          status.afterChecks === true
            ? `Approved ${pr} after its checks passed`
            : `Verdict given: ${given} ${pr}`,
        detail: givenDetail(status),
        ...(status.url === null ? {} : { url: status.url }),
        source: 'verdict',
      };
    case 'failed':
      return {
        id,
        kind: 'failed',
        title: `Verdict not given on ${pr}`,
        detail: cut(status.reason ?? 'The verdict failed.', MAX_DETAIL_CHARS),
        source: 'verdict',
      };
    default:
      return {
        id,
        kind: 'running',
        title: `Verdict on ${pr}`,
        detail: `${label}: ${status.state}`,
        source: 'verdict',
      };
  }
}

/** The one entry of an approval that waits for the checks of its pull request at its reviewed commit. */
export function waitingApprovalId(status: GitHubWaitingApproval): string {
  return `approval:${status.repository}#${status.number}@${status.commit}`;
}

/**
 * The entry of an approval that waits for the checks: running with Cancel while it waits, failed with the reason once
 * a check failed, a new commit arrived, two hours passed or the pull request was closed; null once cancelled or
 * approved, when the entry is removed and the verdict's own entry tells the rest.
 */
export function waitingApprovalNotification(
  status: GitHubWaitingApproval,
): NotificationInput | null {
  const id = waitingApprovalId(status);
  const pr = `${status.repository}#${status.number}`;
  const failed = (title: string, detail = ''): NotificationInput => ({
    id,
    kind: 'failed',
    title,
    detail,
    source: 'verdict',
  });
  if (status.state === 'waiting') {
    return {
      id,
      kind: 'running',
      title: `Waiting to approve ${pr}`,
      detail:
        status.running === 0
          ? 'Waiting for the checks'
          : `${status.running} ${status.running === 1 ? 'check' : 'checks'} still running`,
      indeterminate: true,
      actions: [{ id: 'cancel-waiting-approval', label: 'Cancel' }],
      source: 'verdict',
    };
  }
  switch (status.reason) {
    case 'check-failed':
      return failed(`Not approved: a check failed on ${pr}`, `${status.detail ?? ''} did not pass`);
    case 'new-commit':
      return failed(`Not approved: ${pr} changed after the review`);
    case 'timed-out':
      return failed('Not approved: the checks did not finish within 2 hours', pr);
    case 'closed':
      return failed(`Not approved: ${pr} was closed`);
    default:
      return null;
  }
}

/** The entry of a notice the renderer showed: an error notice as failed, any other as done. */
export function noticeNotification(req: NoticeRecordRequest, id: string): NotificationInput {
  return {
    id,
    kind: req.kind === 'error' ? 'failed' : 'done',
    title: cut(req.message.trim(), MAX_TITLE_CHARS),
    detail: '',
    source: 'notice',
  };
}

export const ISSUES_DETAIL = 'From the audit findings you chose';

/**
 * The entry of an issues turn that ended done with links to issues of its repository in its reply, one per turn: the
 * count of distinct links in its title, the repository's issues as its link when two or more were opened and the
 * issue's own link when one was; null for any other turn.
 */
export function issuesNotification(turn: LocalTurn): NotificationInput | null {
  if (turn.state !== 'done' || turn.issues !== true) return null;
  const repository = ISSUES_TARGET.exec(turn.question)?.[1];
  const links = issueLinks(repository, turn.reply);
  if (repository === undefined || links.length === 0) return null;
  return {
    id: `issues:${turn.id}`,
    kind: 'done',
    title: `${links.length} ${links.length === 1 ? 'issue' : 'issues'} opened on ${repository}`,
    detail: ISSUES_DETAIL,
    url: links.length === 1 ? links[0]! : `https://github.com/${repository}/issues`,
    source: 'worker',
  };
}
