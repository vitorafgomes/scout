import { describe, expect, it, jest } from 'bun:test';
import { CHANNEL, readIpcError, type WorkerEvent } from '@scout/shared';
import {
  MAX_SPEAK_CHARS,
  SAMPLE_INVALID_REPLY,
  SPEAK_FAILED_REPLY,
  SPEAK_INVALID_REPLY,
  SPEAK_NOT_RUNNING_REPLY,
  SPEAK_STOPPED_REPLY,
  SPEAK_TIMEOUT_MS,
  SPEAK_TIMEOUT_REPLY,
  VOICES_FAILED_REPLY,
  VOICES_STOPPED_REPLY,
  VOICES_TIMEOUT_MS,
  VOICES_TIMEOUT_REPLY,
  registerSpeech,
} from '../src/quick-ask/speech.js';
import { recordingLogger } from './kubernetes-fakes.js';

type Handler = (event: unknown, req: unknown) => Promise<unknown>;

async function flush(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

function setup(accepting = true, saved: Record<string, string> = {}, vaultFails = false) {
  const handlers = new Map<string, Handler>();
  const written: string[] = [];
  const listeners: Array<(event: WorkerEvent) => void> = [];
  const onErrors: Array<(err: Error) => void> = [];
  const { logger, lines } = recordingLogger();
  const speech = registerSpeech({
    ipcMain: {
      handle: (channel: string, handler: Handler) => void handlers.set(channel, handler),
      removeHandler: (channel: string) => void handlers.delete(channel),
    } as never,
    supervisor: {
      writeStdin: (line: string, onError: (err: Error) => void) => {
        if (!accepting) return false;
        written.push(line);
        onErrors.push(onError);
        return true;
      },
      onEvent: (listener: (event: WorkerEvent) => void) => {
        listeners.push(listener);
        return () => listeners.splice(listeners.indexOf(listener), 1);
      },
    },
    vault: {
      get: (key: string) =>
        vaultFails
          ? Promise.reject(new Error('EACCES: vault file unreadable'))
          : Promise.resolve(saved[key] ?? null),
    } as never,
    logger,
  });
  const invoke = (channel: string, req?: unknown): Promise<unknown> => {
    const handler = handlers.get(channel);
    if (handler === undefined) throw new Error('no handler');
    return handler({}, req);
  };
  const speak = (req: unknown): Promise<unknown> => invoke(CHANNEL.localSpeak, req);
  const voices = (): Promise<unknown> => invoke(CHANNEL.localVoices);
  const reason = async (pending: Promise<unknown>): Promise<string> => {
    try {
      await pending;
    } catch (err: unknown) {
      return readIpcError(err).message;
    }
    throw new Error('expected a refusal');
  };
  return {
    handlers,
    written,
    listeners,
    onErrors,
    lines,
    speech,
    speak,
    voices,
    reason,
    saved,
  };
}

describe('quick ask: reading a sentence aloud', () => {
  it('sends a speak line with its own id and resolves with the audio the worker answers', async () => {
    const t = setup();

    const answer = t.speak({ text: 'The build is green.' });
    await flush();
    expect(t.written).toEqual([
      JSON.stringify({ type: 'speak', id: 'v-1', text: 'The build is green.' }),
    ]);
    expect(
      t.speech.apply({ id: 'v-1', event: 'audio', contentType: 'audio/wav', base64: 'UklGRg==' }),
    ).toBe(true);

    expect(await answer).toEqual({ contentType: 'audio/wav', base64: 'UklGRg==' });
  });

  it.each([
    [
      "the worker's reason",
      { event: 'fail', text: 'No voice: connect an AI Source with speech.' },
      'No voice: connect an AI Source with speech.',
    ],
    ['the could-not sentence for a blank fail', { event: 'fail', text: '' }, SPEAK_FAILED_REPLY],
  ])('a fail from the worker rejects with %s', async (_name, line, expected) => {
    const t = setup();

    const answer = t.reason(t.speak({ text: 'Hello.' }));
    await flush();
    t.speech.apply({ id: 'v-1', ...line });

    expect(await answer).toBe(expected);
  });

  it.each([
    ['empty', ''],
    ['blank', '   '],
    ['not text', 42],
    ['too long', 'a'.repeat(MAX_SPEAK_CHARS + 1)],
  ])('a sentence that is %s is refused before the worker', async (_name, text) => {
    const t = setup();

    expect(await t.reason(t.speak({ text }))).toBe(SPEAK_INVALID_REPLY);
    expect(t.written).toEqual([]);
  });

  it('rejects with the timeout sentence after 60 s, the stopped sentence when the worker exits, and not-running when it is not', async () => {
    jest.useFakeTimers();
    try {
      const t = setup();
      const slow = t.reason(t.speak({ text: 'One.' }));
      await flush();
      jest.advanceTimersByTime(SPEAK_TIMEOUT_MS);
      expect(await slow).toBe(SPEAK_TIMEOUT_REPLY);

      const exited = t.reason(t.speak({ text: 'Two.' }));
      await flush();
      for (const listener of t.listeners)
        listener({ kind: 'exit', code: 1, signal: null } as never);
      expect(await exited).toBe(SPEAK_STOPPED_REPLY);
    } finally {
      jest.useRealTimers();
    }

    const off = setup(false);
    expect(await off.reason(off.speak({ text: 'Three.' }))).toBe(SPEAK_NOT_RUNNING_REPLY);
  });

  it('ignores lines for other ids, never logs the sentence, and dispose removes the handler and settles what waits', async () => {
    const t = setup();
    const waiting = t.reason(t.speak({ text: 'The secret plan is ready.' }));
    await flush();

    expect(t.speech.apply({ id: 'r-1', event: 'text', text: 'other' })).toBe(false);
    t.speech.dispose();

    expect(await waiting).toBe(SPEAK_STOPPED_REPLY);
    expect(t.handlers.has(CHANNEL.localSpeak)).toBe(false);
    expect(t.handlers.has(CHANNEL.localVoices)).toBe(false);
    expect(t.listeners).toEqual([]);
    expect(t.lines.some((line) => line.includes('v-1'))).toBe(true);
    expect(t.lines.some((line) => line.includes('secret plan'))).toBe(false);
  });
});

describe('quick ask: the voice that reads aloud', () => {
  const ARU = 'piper:en_GB-aru-medium#0';

  it('the speak line carries the provider voice and the local voice saved in Models, Speech', async () => {
    const t = setup(true, { 'speech.voice.provider': 'nova', 'speech.voice.local': ARU });

    void t.speak({ text: 'Hello.' });
    await flush();

    expect(t.written.map((line) => JSON.parse(line) as unknown)).toEqual([
      { type: 'speak', id: 'v-1', text: 'Hello.', providerVoice: 'nova', localVoice: ARU },
    ]);
  });

  it('a voice saved between two sentences is used by the next sentence, with no worker restart', async () => {
    const t = setup(true, { 'speech.voice.provider': 'nova' });

    void t.speak({ text: 'One.' });
    await flush();
    t.saved['speech.voice.provider'] = 'onyx';
    void t.speak({ text: 'Two.' });
    await flush();

    expect(
      t.written.map((line) => (JSON.parse(line) as { providerVoice?: string }).providerVoice),
    ).toEqual(['nova', 'onyx']);
  });

  it('a saved value that is not a voice id is left out of the line', async () => {
    const t = setup(true, { 'speech.voice.provider': 'nova; rm -rf', 'speech.voice.local': ARU });

    void t.speak({ text: 'Hello.' });
    await flush();

    expect(JSON.parse(t.written[0] ?? '{}')).toEqual({
      type: 'speak',
      id: 'v-1',
      text: 'Hello.',
      localVoice: ARU,
    });
  });

  it('a vault that cannot be read still sends the sentence, in the default voice, and logs why', async () => {
    const t = setup(true, {}, true);

    void t.speak({ text: 'Hello.' });
    await flush();

    expect(JSON.parse(t.written[0] ?? '{}')).toEqual({ type: 'speak', id: 'v-1', text: 'Hello.' });
    expect(t.lines.some((line) => line.includes('the saved voice could not be read'))).toBe(true);
  });

  it.each([
    ['an AI Source voice', { provider: 'fable' }, { providerVoice: 'fable' }],
    [
      'a local voice, read with the local voice only',
      { local: ARU },
      { localVoice: ARU, localOnly: true },
    ],
  ])(
    'a sample of %s is sent in that voice instead of the saved one',
    async (_name, sample, fields) => {
      const t = setup(true, {
        'speech.voice.provider': 'nova',
        'speech.voice.local': 'piper:en_GB-cori-medium',
      });

      void t.speak({ text: 'Hello.', sample });
      await flush();

      expect(JSON.parse(t.written[0] ?? '{}')).toEqual({
        type: 'speak',
        id: 'v-1',
        text: 'Hello.',
        ...fields,
      });
    },
  );

  it.each([
    ['null', null],
    ['empty', {}],
    ['both voices', { provider: 'nova', local: ARU }],
    ['not a voice id', { provider: 'nova voice' }],
    ['an unknown field', { voice: 'nova' }],
  ])('a sample that is %s is refused before the worker', async (_name, sample) => {
    const t = setup();

    expect(await t.reason(t.speak({ text: 'Hello.', sample }))).toBe(SAMPLE_INVALID_REPLY);
    expect(t.written).toEqual([]);
  });

  it("asks the worker for its voices and resolves with the AI Source's and the local voices it lists", async () => {
    const t = setup();

    const listed = t.voices();
    expect(t.written).toEqual([JSON.stringify({ type: 'voices', id: 'vl-1' })]);
    t.speech.apply({
      id: 'vl-1',
      event: 'voices',
      provider: 'OpenAI',
      providerVoices: ['alloy', 'nova'],
      localVoices: [{ id: ARU, label: 'Aru 03 (medium)' }],
      defaultLocal: 'piper:en_GB-cori-medium',
    });

    expect(await listed).toEqual({
      provider: 'OpenAI',
      providerVoices: ['alloy', 'nova'],
      localVoices: [{ id: ARU, label: 'Aru 03 (medium)' }],
      defaultLocal: 'piper:en_GB-cori-medium',
    });
  });

  it.each([
    [
      "the worker's reason",
      { event: 'fail', text: 'No voice: connect an AI Source with speech.' },
      'No voice: connect an AI Source with speech.',
    ],
    [
      'the could-not-list sentence for a blank fail',
      { event: 'fail', text: '' },
      VOICES_FAILED_REPLY,
    ],
    [
      'the could-not-list sentence for a malformed list',
      { event: 'voices', provider: 'OpenAI', providerVoices: [1] },
      VOICES_FAILED_REPLY,
    ],
  ])('a voices request that fails rejects with %s', async (_name, line, expected) => {
    const t = setup();

    const listed = t.reason(t.voices());
    t.speech.apply({ id: 'vl-1', ...line });

    expect(await listed).toBe(expected);
  });

  it('a voices request rejects with its timeout sentence, its stopped sentence on exit, and not-running when the worker is not', async () => {
    jest.useFakeTimers();
    try {
      const t = setup();
      const slow = t.reason(t.voices());
      jest.advanceTimersByTime(VOICES_TIMEOUT_MS);
      expect(await slow).toBe(VOICES_TIMEOUT_REPLY);

      const exited = t.reason(t.voices());
      for (const listener of t.listeners)
        listener({ kind: 'exit', code: 1, signal: null } as never);
      expect(await exited).toBe(VOICES_STOPPED_REPLY);
    } finally {
      jest.useRealTimers();
    }

    const off = setup(false);
    expect(await off.reason(off.voices())).toBe(SPEAK_NOT_RUNNING_REPLY);
  });
});
