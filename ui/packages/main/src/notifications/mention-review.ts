import { reviewQuestion } from '@scout/shared';
import type { Logger } from '../util/logger.js';
import type { NotificationInput } from './notification-store.js';

/** A mention older than this keeps Review it and starts nothing by itself. */
export const MENTION_MAX_AGE_MS = 60 * 60_000;
/** How long a mention waits before asking again while the chat is answering or refuses the question. */
export const MENTION_RETRY_MS = 60_000;
/** The detail of a mention's entry once its review was asked; an entry that carries it is never asked again. */
export const STARTED_DETAIL = 'Review started by the mention. The answer is in the chat.';

export interface MentionReviewDeps {
  /** The owner's GitHub login, or null while GitHub is not connected. */
  login(): Promise<string | null>;
  /** Asks the chat when no turn runs and answers the turn's id; null while one runs or when the chat refuses. */
  ask(question: string): string | null;
  /** True when the chat was asked `question` at or after `since`, by the owner's click on Review it or by hand. */
  asked(question: string, since: number): boolean;
  /** The entry the bell keeps under `id`, or undefined. */
  kept(id: string): NotificationInput | undefined;
  record(entry: NotificationInput): void;
  now(): number;
  /** Runs `callback` once after `ms`; returns the cancel function. */
  setTimer(ms: number, callback: () => void): () => void;
  logger: Logger;
}

/**
 * Starts a pull request's review by itself when the owner mentions the Scout bot on it: the same question Review it
 * asks, once per mention, one at a time and only while the chat is idle. A mention by anyone else, one older than
 * `MENTION_MAX_AGE_MS` and one with no pull request link keep Review it; one that lost Review it, its pull request
 * merged, closed or approved by the bot, is asked nothing. While the review's turn runs the bell shows it in
 * progress, then ready, or not finished with Review it given back. Nothing is posted to GitHub from here: the
 * review's writes stay the approved actions they are.
 */
export class MentionReviews {
  private readonly waiting = new Map<string, { entry: NotificationInput; question: string }>();
  private readonly started = new Set<string>();
  private cancelRetry: (() => void) | null = null;
  private running: {
    turnId: string;
    mention: NotificationInput;
    review: NotificationInput;
  } | null = null;
  private asking = false;

  constructor(private readonly deps: MentionReviewDeps) {}

  /** Records `entry` in the bell, a started mention as started, and queues the owner's fresh mention for its review. */
  async record(entry: NotificationInput): Promise<void> {
    const { deps } = this;
    if (entry.kind !== 'mention') {
      deps.record(entry);
      return;
    }
    if (this.started.has(entry.id) || deps.kept(entry.id)?.detail === STARTED_DETAIL) {
      this.started.add(entry.id);
      deps.record(asStarted(entry));
      return;
    }
    deps.record(entry);
    let login: string | null;
    try {
      login = await deps.login();
    } catch (err: unknown) {
      deps.logger.warn(
        'mention review: the GitHub login was not read; the mention keeps Review it',
        {
          error: err instanceof Error ? err.name : typeof err,
        },
      );
      return;
    }
    if (this.started.has(entry.id)) return;
    const mine = login !== null && entry.title.startsWith(`@${login} mentioned @`);
    const fresh = deps.now() - (entry.at ?? deps.now()) <= MENTION_MAX_AGE_MS;
    const question = reviewQuestion(entry.url);
    if (!mine || !fresh || question === null || entry.actions === undefined) return;
    this.waiting.set(entry.id, { entry, question });
    this.pump();
  }

  /** Asks for the next waiting review when the chat is idle and no review of a mention runs. */
  pump(): void {
    const { deps } = this;
    this.cancelRetry?.();
    this.cancelRetry = null;
    if (this.running !== null) return;
    for (const [id, { entry, question }] of this.waiting) {
      if (deps.now() - (entry.at ?? deps.now()) > MENTION_MAX_AGE_MS) {
        this.waiting.delete(id);
        continue;
      }
      if (deps.kept(id)?.actions === undefined) {
        this.waiting.delete(id);
        continue;
      }
      if (deps.asked(question, entry.at ?? 0)) {
        this.waiting.delete(id);
        this.started.add(id);
        deps.record(asStarted(entry));
        continue;
      }
      this.asking = true;
      const turnId = deps.ask(question);
      this.asking = false;
      if (turnId === null) break;
      this.waiting.delete(id);
      this.started.add(id);
      const review: NotificationInput = {
        id: `review:${id}`,
        kind: 'running',
        title: `Reviewing ${pullRequestOf(entry)}`,
        detail: 'Started by your mention of the Scout bot',
        indeterminate: true,
        ...(entry.url === undefined ? {} : { url: entry.url }),
        source: 'github',
      };
      this.running = { turnId, mention: entry, review };
      deps.record(asStarted(entry));
      deps.record(review);
      deps.logger.info('mention review: started', { id });
      break;
    }
    if (this.waiting.size > 0) {
      this.cancelRetry = deps.setTimer(MENTION_RETRY_MS, () => this.pump());
    }
  }

  /** The turn `turnId` completed: the review it ran is ready, and the next waiting mention is asked. */
  turnDone(turnId: string): void {
    const { running } = this;
    if (running?.turnId === turnId) {
      this.running = null;
      const { indeterminate: _sweep, ...review } = running.review;
      this.deps.record({
        ...review,
        kind: 'done',
        title: `Review of ${pullRequestOf(running.mention)} is ready`,
        detail: 'The answer is in the chat.',
      });
    }
    this.pump();
  }

  /**
   * No turn runs any more. The review that was running, when its turn did not complete, stopped or failed, is recorded
   * as not finished and its mention gets Review it back; checked after the caller's stack, so a completing turn
   * reaches `turnDone` first, and only for that review, never for one asked meanwhile. A question of this queue the
   * chat refused also ends a turn: that one waits for the retry and asks nothing here.
   */
  idle(): void {
    if (this.asking) return;
    const turnId = this.running?.turnId;
    this.deps.setTimer(0, () => {
      const { running } = this;
      if (running !== null && running.turnId === turnId) {
        this.running = null;
        this.started.delete(running.mention.id);
        const { indeterminate: _sweep, ...review } = running.review;
        this.deps.record({
          ...review,
          kind: 'failed',
          title: `Review of ${pullRequestOf(running.mention)} did not finish`,
          detail: 'It stopped before the answer. Review it asks again.',
        });
        this.deps.record(running.mention);
      }
      this.pump();
    });
  }

  /** GitHub was disconnected: no waiting mention is asked any more, and the entries are the caller's to remove. */
  clear(): void {
    this.cancelRetry?.();
    this.cancelRetry = null;
    this.waiting.clear();
    this.started.clear();
    this.running = null;
  }
}

/** The pull request a mention's title names, "owner/repo#n", or the words for one that names none. */
function pullRequestOf(entry: NotificationInput): string {
  return / on ([^\s:]+#\d+):/.exec(entry.title)?.[1] ?? 'the pull request';
}

function asStarted({ actions: _dropped, ...entry }: NotificationInput): NotificationInput {
  return { ...entry, detail: STARTED_DETAIL };
}
