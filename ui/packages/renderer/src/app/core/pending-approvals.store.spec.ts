import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  actionInWords,
  approverInWords,
  outcomeInWords,
  whenInWords,
} from './pending-approvals.store';

describe('Approvals wording', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  describe('an action in words', () => {
    for (const [action, summary, words] of [
      ['videos.insert', undefined, 'Upload a video'],
      ['thumbnails.set', undefined, 'Set a video thumbnail'],
      ['comments.insert', undefined, 'Publish a reply to a comment'],
      ['Pull request reviews', undefined, 'Post a review with comments'],
      [
        'Pull request verdicts',
        'give_pull_request_verdict(body, number=12, owner=acme, repo=api, verdict=approve)',
        'Approve a pull request',
      ],
      ['Pull request verdicts', 'Approve acme/api#12', 'Approve a pull request'],
      ['New issues', undefined, 'Open an issue'],
      ['Pull request comments', undefined, 'Comment on a pull request'],
      [
        'Pull request verdicts',
        'give_pull_request_verdict(body, number=12, owner=acme, repo=api, verdict=request_changes)',
        'Request changes on a pull request',
      ],
      [
        'Pull request verdicts',
        'Request changes on acme/api#12',
        'Request changes on a pull request',
      ],
      ['Pull request verdicts', undefined, 'Give a verdict on a pull request'],
      [
        'Pull request verdicts',
        'give_pull_request_verdict(body, number=12, owner=acme, repo=api, verdict=)',
        'Give a verdict on a pull request',
      ],
      ['Issue comments', undefined, 'Comment on an issue'],
      ['videos.update', undefined, "Update a video's details"],
      ['playlistItems.insert', undefined, 'Add a video to a playlist'],
      ['comments.setModerationStatus', undefined, 'Hold, publish or reject a comment'],
      ['youtubeReporting jobs.create', undefined, 'Create the reach reporting job'],
      ["Scout's data folder", undefined, "Scout's data folder"],
      ['Local container', undefined, 'Local container'],
      ['Pause audit', undefined, 'Pause audit'],
      ['Continue audit', undefined, 'Continue audit'],
      ['Stop audit', undefined, 'Stop audit'],
      ['Stop tests', undefined, 'Stop tests'],
      ['Upload video', 'Upload "Intro" as private', 'Upload video'],
      ['constructor', undefined, 'constructor'],
    ] as const) {
      it(`${action}${summary === undefined ? '' : ` with "${summary}"`} reads "${words}"`, () => {
        expect(actionInWords(action, summary)).toBe(words);
      });
    }
  });

  describe('an outcome in words', () => {
    for (const [outcome, words] of [
      ['succeeded', 'Done'],
      ['succeeded: posted as review 41', 'Done: posted as review 41'],
      ['rejected', 'Rejected'],
      ['rejected: the title is not final', 'Rejected: the title is not final'],
      ['failed: the head commit changed', 'Failed: the head commit changed'],
      ['approved', 'Approved'],
      ['approved: checked the title', 'Approved: checked the title'],
      ['saved script_ready', 'Saved script_ready'],
      [
        'Not applied: no test run of acme/api#41 is running.',
        'Not applied: no test run of acme/api#41 is running.',
      ],
    ] as const) {
      it(`"${outcome}" reads "${words}"`, () => {
        expect(outcomeInWords(outcome)).toBe(words);
      });
    }
  });

  describe('who approved, in words', () => {
    for (const [approvedBy, words] of [
      ['owner', 'You'],
      [null, 'Automatic (writes allowed)'],
      ['ops-team', 'ops-team'],
    ] as const) {
      it(`${JSON.stringify(approvedBy)} reads "${words}" under Approved by`, () => {
        expect(approverInWords(approvedBy)).toBe(words);
      });
    }
  });

  describe('a time in words, on the local calendar days of this computer', () => {
    type Parts = readonly [year: number, month: number, day: number, hour: number, minute: number];
    const at = ([year, month, day, hour, minute]: Parts): Date =>
      new Date(year, month - 1, day, hour, minute);

    for (const [when, now, then, history, card] of [
      [
        'earlier today',
        [2026, 11, 12, 15, 0],
        [2026, 11, 12, 14, 2],
        'Today 14:02',
        'Requested today 14:02',
      ],
      [
        'just after midnight today',
        [2026, 11, 12, 15, 0],
        [2026, 11, 12, 0, 1],
        'Today 00:01',
        'Requested today 00:01',
      ],
      [
        'just before midnight today, a minute ago',
        [2026, 11, 12, 23, 59],
        [2026, 11, 12, 23, 58],
        'Today 23:58',
        'Requested today 23:58',
      ],
      [
        'yesterday at 23:59, seen at 00:01',
        [2026, 11, 12, 0, 1],
        [2026, 11, 11, 23, 59],
        'Yesterday 23:59',
        'Requested yesterday 23:59',
      ],
      [
        'yesterday afternoon',
        [2026, 11, 12, 15, 0],
        [2026, 11, 11, 17, 55],
        'Yesterday 17:55',
        'Requested yesterday 17:55',
      ],
      [
        'two days ago',
        [2026, 11, 12, 15, 0],
        [2026, 11, 10, 9, 5],
        '2026-11-10 09:05',
        'Requested 2026-11-10 09:05',
      ],
      [
        'later today, a time still to come',
        [2026, 11, 12, 15, 0],
        [2026, 11, 12, 18, 0],
        'Today 18:00',
        'Requested today 18:00',
      ],
      [
        'tomorrow, a time still to come',
        [2026, 11, 12, 15, 0],
        [2026, 11, 13, 0, 0],
        '2026-11-13 00:00',
        'Requested 2026-11-13 00:00',
      ],
      [
        'the last day of the month before',
        [2026, 12, 1, 8, 0],
        [2026, 11, 30, 22, 0],
        'Yesterday 22:00',
        'Requested yesterday 22:00',
      ],
      [
        'the last day of the year before',
        [2027, 1, 1, 8, 0],
        [2026, 12, 31, 22, 0],
        'Yesterday 22:00',
        'Requested yesterday 22:00',
      ],
      [
        'the same day a year earlier',
        [2026, 11, 12, 15, 0],
        [2025, 11, 12, 14, 2],
        '2025-11-12 14:02',
        'Requested 2025-11-12 14:02',
      ],
    ] as const) {
      it(`${when} reads "${history}" in History and "${card}" on a waiting card`, () => {
        vi.useFakeTimers({ toFake: ['Date'] });
        vi.setSystemTime(at(now));
        const iso = at(then).toISOString();

        expect([whenInWords(iso, 'history'), whenInWords(iso, 'requested')]).toEqual([
          history,
          card,
        ]);
      });
    }
  });
});
