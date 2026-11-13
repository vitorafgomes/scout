import { describe, expect, it } from 'vitest';

import { MAX_PIECE_CHARS, SentenceStream, speakableSentences } from './sentences';

describe('speakableSentences: what a voice reads of a reply', () => {
  it.each([
    [
      'plain sentences',
      'The build is green. Two checks ran!',
      ['The build is green.', 'Two checks ran!'],
    ],
    [
      'a fenced code block is skipped',
      'Run this:\n```bash\nnpm test. echo done.\n```\nThen push.',
      ['Run this:', 'Then push.'],
    ],
    ['an unclosed fence skips the rest', 'Look.\n```ts\nconst a = 1;', ['Look.']],
    [
      'table rows are skipped',
      'Results:\n| check | state |\n|---|---|\n| build | green. |\nAll good.',
      ['Results:', 'All good.'],
    ],
    ['a bare URL is skipped', 'See https://example.com/a?b=1 for details.', ['See for details.']],
    ['a www address is skipped', 'Open www.example.com now.', ['Open now.']],
    [
      'a Markdown link reads its label',
      'Read [the pull request](https://example.com/pr/1) first.',
      ['Read the pull request first.'],
    ],
    ['a line of only a URL is dropped', 'Here.\nhttps://example.com/x', ['Here.']],
    [
      'headings, bullets and emphasis are dropped',
      '# Summary\n- **Two** checks *failed*.\n1. Fix `lint`.',
      ['Summary', 'Two checks failed.', 'Fix lint.'],
    ],
    ['a piece with no letter or digit is dropped', '---\n...\nDone.', ['Done.']],
  ])('%s', (_name, reply, expected) => {
    expect(speakableSentences(reply)).toEqual(expected);
  });
});

describe('SentenceStream: a streamed reply read sentence by sentence, each once', () => {
  it.each([
    [
      'a sentence is given once it ends with a space after its stop',
      [
        'The build',
        'The build is green.',
        'The build is green. Two',
        'The build is green. Two checks ran.',
      ],
      [[], [], ['The build is green.'], []],
      ['Two checks ran.'],
    ],
    [
      'a line break ends a line without a stop',
      ['Summary', 'Summary\nAll', 'Summary\nAll good'],
      [[], ['Summary'], []],
      ['All good'],
    ],
    [
      'a code block that streams in is never read, and the sentence after it is',
      ['Run:\n```', 'Run:\n```sh\nnpm test. ok.\n', 'Run:\n```sh\nnpm test. ok.\n```\nThen push. '],
      [['Run:'], [], ['Then push.']],
      [],
    ],
    [
      'a table that streams in is never read',
      ['Here:\n| a | b. |\n', 'Here:\n| a | b. |\n| c. | d |\nDone. '],
      [['Here:'], ['Done.']],
      [],
    ],
  ])('%s', (_name, updates, given, rest) => {
    const stream = new SentenceStream();

    expect(updates.map((update) => stream.push(update))).toEqual(given);
    expect(stream.end(updates[updates.length - 1]!)).toEqual(rest);
  });
});

describe('SentenceStream: a list streamed character by character reads each item once, never its marker', () => {
  it.each([
    ['a numbered list', 'Steps:\n1. Fix lint.\n2. Push.', ['Steps:', 'Fix lint.', 'Push.']],
    [
      'a numbered list with a parenthesis',
      'Steps:\n1) Fix lint.\n2) Push.',
      ['Steps:', 'Fix lint.', 'Push.'],
    ],
    ['a dash list', 'Steps:\n- Fix lint.\n- Push.', ['Steps:', 'Fix lint.', 'Push.']],
    ['a star list', 'Steps:\n* Fix lint.\n* Push.', ['Steps:', 'Fix lint.', 'Push.']],
  ])('%s', (_name, reply, expected) => {
    const stream = new SentenceStream();
    const read: string[] = [];

    for (let i = 1; i <= reply.length; i++) read.push(...stream.push(reply.slice(0, i)));
    read.push(...stream.end(reply));

    expect(read).toEqual(expected);
  });
});

describe('speakableSentences: a long piece is cut so no piece is too long for the voice', () => {
  it.each([
    [
      'words with no punctuation, cut at a space',
      Array.from({ length: 500 }, (_, i) => `w${i}`).join(' '),
      ' ',
    ],
    ['one unbroken run of letters, cut at the cap', 'a'.repeat(2500), ''],
  ])(
    '%s: every piece is at most 300 characters and the whole text is read',
    (_name, line, joiner) => {
      const stream = new SentenceStream();

      const read = [...stream.push(`${line}\n`), ...stream.end(`${line}\n`)];

      expect(line.length).toBeGreaterThan(2000);
      expect(read.filter((piece) => piece.length > MAX_PIECE_CHARS)).toEqual([]);
      expect(read.join(joiner)).toBe(line);
    },
  );

  it('a long clause list is cut after its last comma before the cap', () => {
    const clause = 'the build ran on main and every check passed';
    const reply = Array.from({ length: 10 }, () => clause).join(', ');

    const read = speakableSentences(reply);

    expect(read.length).toBeGreaterThan(1);
    expect(read.slice(0, -1).every((piece) => piece.endsWith(','))).toBe(true);
    expect(read.join(' ')).toBe(reply);
  });
});
