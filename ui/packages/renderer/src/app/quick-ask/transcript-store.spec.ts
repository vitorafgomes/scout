import { describe, expect, it } from 'vitest';

import { TranscriptStore } from './transcript-store';

const NOW = Date.parse('2026-11-04T10:00:00Z');
const MINUTE = 60_000;

describe('TranscriptStore', () => {
  it('keeps at most 500 lines and the window of the last minutes is cut to 24,000 characters from the newest', () => {
    const store = new TranscriptStore();
    for (let i = 0; i < 501; i++) store.add(i % 2 === 0 ? 'me' : 'others', `line ${i}`, NOW);
    const kept = store.lines();
    store.clear();
    store.add('others', 'said eleven minutes ago', NOW - 11 * MINUTE);
    store.add('others', 'Can you ship it?', NOW - 9 * MINUTE);
    store.add('me', 'Yes, today.', NOW - MINUTE);
    const recent = store.window(10, NOW);
    store.clear();
    for (let i = 0; i < 300; i++) store.add('others', `${i}`.padStart(100, 'x'), NOW);
    const capped = store.window(10, NOW);

    expect(kept).toHaveLength(500);
    expect([kept[0]!.text, kept.at(-1)!.text]).toEqual(['line 1', 'line 500']);
    expect([kept[0]!.label, kept[1]!.label]).toEqual(['Others', 'You']);
    expect(recent).toBe('Others: Can you ship it?\nYou: Yes, today.');
    expect(capped).toHaveLength(24_000);
    expect(capped.endsWith(`Others: ${'299'.padStart(100, 'x')}`)).toBe(true);
    expect(capped).not.toContain(`Others: ${'0'.padStart(100, 'x')}`);
  });

  it('a translation is attached to its final by lane and seq and an error shows as a note', () => {
    const store = new TranscriptStore();
    store.add('me', 'old session line', NOW, 1, 'earlier');
    store.add('me', 'Bom dia.', NOW, 1, 'now');
    store.add('others', 'Tudo bem?', NOW, 1, 'now');
    store.add('others', 'Vamos começar.', NOW, 2, 'now');

    store.translate('me', 'now', 1, { text: 'Good morning.', language: 'en' });
    store.translate('others', 'now', 1, { language: 'en', error: 'skipped' });
    store.translate('others', 'now', 9, { text: 'nowhere', language: 'en' });

    expect(store.lines().map((line) => [line.text, line.translation])).toEqual([
      ['old session line', undefined],
      ['Bom dia.', { text: 'Good morning.', language: 'en' }],
      ['Tudo bem?', { language: 'en', error: 'skipped' }],
      ['Vamos começar.', undefined],
    ]);
  });

  it('a translation lands on the line of its own session when an earlier session on the same lane has the same seq', () => {
    const store = new TranscriptStore();
    store.add('others', 'Primeira sessão.', NOW, 1, 'first');
    store.add('others', 'Segunda sessão.', NOW, 1, 'second');
    store.add('others', 'Depois.', NOW, 2, 'first');

    store.translate('others', 'first', 1, { text: 'First session.', language: 'en' });
    store.translate('others', 'second', 2, { text: 'Not in this session.', language: 'en' });

    expect(store.lines().map((line) => [line.text, line.translation])).toEqual([
      ['Primeira sessão.', { text: 'First session.', language: 'en' }],
      ['Segunda sessão.', undefined],
      ['Depois.', undefined],
    ]);
  });

  it('window() holds the source lines only', () => {
    const store = new TranscriptStore();
    store.add('others', 'Pode enviar hoje?', NOW, 1, 's');
    store.add('me', 'Sim.', NOW, 1, 's');
    store.translate('others', 's', 1, { text: 'Can you send it today?', language: 'en' });
    store.translate('me', 's', 1, { text: 'Yes.', language: 'en' });
    store.note('others', 'Translation paused: more than 20 lines a minute.');

    expect(store.window(10, NOW)).toBe('Others: Pode enviar hoje?\nYou: Sim.');
  });

  it('a session note shows once per text, ends with the next translation of its lane, and Clear drops it', () => {
    const store = new TranscriptStore();
    const none = 'Scout has no AI Source for translation; connect one in AI Sources.';
    store.add('others', 'Olá.', NOW, 1, 's');
    store.note('me', none);
    store.note('others', none);
    const both = store.notes();
    store.note('others', 'Translation paused: more than 20 lines a minute.');
    store.translate('others', 's', 1, { text: 'Hello.', language: 'en' });
    const afterOthers = store.notes();
    store.clear();

    expect(both).toEqual([none]);
    expect(afterOthers).toEqual([none]);
    expect(store.notes()).toEqual([]);
  });

  it('Clear empties the transcript and an empty window is an empty text', () => {
    const store = new TranscriptStore();
    store.add('me', 'hello', NOW);

    store.clear();

    expect(store.lines()).toEqual([]);
    expect(store.window(10, NOW)).toBe('');
  });
});
