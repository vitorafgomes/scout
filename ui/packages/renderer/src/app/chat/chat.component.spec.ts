import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalEvent, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ChatComponent } from './chat.component';

const NOT_RUNNING = 'Scout is not running. Start it from the tray or the app.';

/** Main's rule for a progress event: the latest activity, trimmed and cut to 200 characters; a blank one changes nothing. */
function progressed(
  turn: Omit<LocalTurn, 'activity'>,
  activity: string | undefined,
  text: string | undefined,
): LocalTurn {
  const next = (text ?? '').trim().slice(0, 200);
  return next.length === 0
    ? { ...turn, ...(activity === undefined ? {} : { activity }) }
    : { ...turn, activity: next };
}

function makeBridge() {
  let turns: readonly LocalTurn[] = [];
  const listeners: ((turns: readonly LocalTurn[]) => void)[] = [];
  const push = (next: readonly LocalTurn[]) => {
    turns = next;
    listeners.forEach((listener) => listener(turns));
  };
  const append = (req: LocalAskRequest, changes: Partial<LocalTurn> = {}) =>
    push([
      ...turns,
      {
        id: req.id,
        question: 'text' in req ? req.text.trim() : '(voice message)',
        reply: '',
        citations: [],
        state: 'running',
        ...changes,
      },
    ]);
  const localAsk = vi.fn(async (req: LocalAskRequest): Promise<void> => append(req));
  return {
    push,
    emit: (event: LocalEvent) =>
      push(
        turns.map(({ activity, ...turn }) =>
          turn.id !== event.id || turn.state !== 'running' || event.event === 'begin'
            ? { ...turn, ...(activity === undefined ? {} : { activity }) }
            : event.event === 'progress'
              ? progressed(turn, activity, event.text)
              : {
                  ...turn,
                  reply: event.text ?? turn.reply,
                  citations: event.event === 'complete' ? (event.citations ?? []) : turn.citations,
                  state:
                    event.event === 'complete'
                      ? 'done'
                      : event.event === 'fail'
                        ? 'failed'
                        : 'running',
                },
        ),
      ),
    refuseNextAsNotRunning: () =>
      localAsk.mockImplementationOnce(async (req: LocalAskRequest) => {
        append(req, { reply: NOT_RUNNING, state: 'failed', notRunning: true });
        throw new Error(`[scout:local/not-running] ${NOT_RUNNING}`);
      }),
    onLocalTurns: vi.fn((listener: (turns: readonly LocalTurn[]) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    localGetTurns: vi.fn(async (): Promise<readonly LocalTurn[]> => turns),
    localClearTurns: vi.fn(async (): Promise<void> => {
      if (!turns.some((turn) => turn.state === 'running')) push([]);
    }),
    localAsk,
    localCancel: vi.fn(async (_req: { id: string }): Promise<void> => undefined),
    appOpenExternal: vi.fn(async (): Promise<void> => undefined),
    workerStart: vi.fn(async () => ({ pid: 4242 })),
  };
}

const audio = { processor: null as FakeProcessor | null, trackStopped: false };

class FakeProcessor {
  onaudioprocess: ((event: unknown) => void) | null = null;
  connect = vi.fn();
  disconnect = vi.fn();
  hear(samples: number[]): void {
    this.onaudioprocess?.({ inputBuffer: { getChannelData: () => Float32Array.from(samples) } });
  }
}

class FakeAudioContext {
  readonly sampleRate: number;
  readonly destination = {};
  constructor(options: { sampleRate: number }) {
    this.sampleRate = options.sampleRate;
  }
  createMediaStreamSource() {
    return { connect: vi.fn(), disconnect: vi.fn() };
  }
  createScriptProcessor() {
    audio.processor = new FakeProcessor();
    return audio.processor;
  }
  createGain() {
    return { gain: { value: 1 }, connect: vi.fn(), disconnect: vi.fn() };
  }
  async close(): Promise<void> {}
}

function configure(bridge: ReturnType<typeof makeBridge>): void {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
}

async function open(): Promise<ComponentFixture<ChatComponent>> {
  const fixture = TestBed.createComponent(ChatComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<ChatComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<ChatComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function texts(fixture: ComponentFixture<ChatComponent>, test: string): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(`[data-test="${test}"]`) as NodeListOf<HTMLElement>,
  ).map((node) => node.textContent ?? '');
}

async function type(fixture: ComponentFixture<ChatComponent>, text: string): Promise<void> {
  const input = el(fixture, 'input') as HTMLTextAreaElement;
  input.value = text;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

async function press(
  fixture: ComponentFixture<ChatComponent>,
  init: KeyboardEventInit,
): Promise<KeyboardEvent> {
  const event = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, ...init });
  el(fixture, 'input')!.dispatchEvent(event);
  await settle(fixture);
  return event;
}

function lastId(bridge: ReturnType<typeof makeBridge>): string {
  return (bridge.localAsk.mock.lastCall![0] as { id: string }).id;
}

describe('Chat screen', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    audio.processor = null;
    audio.trackStopped = false;
    vi.stubGlobal('AudioContext', FakeAudioContext);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        getUserMedia: vi.fn(async () => ({
          getTracks: () => [
            {
              stop: () => {
                audio.trackStopped = true;
              },
            },
          ],
        })),
      },
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('says who spoke: the question under "You" with its time, the reply under "Scout"', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([
      {
        id: 'q-1',
        question: 'is prod green?',
        reply: 'Yes.',
        citations: [],
        state: 'done',
        askedAt: Date.UTC(2026, 10, 6, 14, 2),
      },
    ]);
    const fixture = await open();

    expect(texts(fixture, 'who-user').map((t) => t.replace(/\s+/g, ' ').trim())).toEqual([
      expect.stringMatching(/^You\s*\d\d:02$/),
    ]);
    expect(texts(fixture, 'who-scout').map((t) => t.trim())).toEqual(['Scout']);
    expect(texts(fixture, 'question')).toEqual(['is prod green?']);
    expect(texts(fixture, 'reply')).toEqual(['Yes.']);
  });

  it('shows three dots and what Scout is doing while the reply is still empty, then the reply as it streams', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'review PR #5');
    await press(fixture, {});
    const request = bridge.localAsk.mock.calls[0]![0];

    expect(el(fixture, 'working')?.textContent?.trim()).toBe('Working on it');

    bridge.emit({
      id: request.id,
      event: 'progress',
      text: 'Reading pull request file src/Checkout.cs',
    });
    await settle(fixture);
    expect(el(fixture, 'working')?.textContent?.trim()).toBe(
      'Reading pull request file src/Checkout.cs',
    );

    bridge.emit({ id: request.id, event: 'progress', text: '   ' });
    await settle(fixture);
    expect(el(fixture, 'working')?.textContent?.trim()).toBe(
      'Reading pull request file src/Checkout.cs',
    );

    bridge.emit({ id: request.id, event: 'update', text: 'One finding so far.' });
    await settle(fixture);
    expect(el(fixture, 'working')).toBeNull();
    expect(texts(fixture, 'reply')).toEqual(['One finding so far.']);

    bridge.emit({ id: request.id, event: 'progress', text: 'Searching code: MenuOrdering' });
    await settle(fixture);
    expect(el(fixture, 'working')?.textContent?.trim()).toBe('Searching code: MenuOrdering');

    bridge.emit({ id: request.id, event: 'complete', text: 'One finding so far.\n\nDone.' });
    await settle(fixture);
    expect(el(fixture, 'working')).toBeNull();
  });

  it('renders the reply as Markdown: headings, emphasis, lists and code, and opens a link in the browser', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([
      {
        id: 'q-1',
        question: 'review it',
        reply:
          '## Review of #5\n\n**Verdict:** needs work.\n\n- `MenuOrdering.Move` has no lock\n\nSee [the run](https://github.com/acme/ci/actions/runs/42).',
        citations: [],
        state: 'done',
      },
    ]);
    const fixture = await open();
    const reply = el(fixture, 'reply')!;

    expect(reply.querySelector('h2')?.textContent).toBe('Review of #5');
    expect(reply.querySelector('strong')?.textContent).toBe('Verdict:');
    expect(reply.querySelector('li code')?.textContent).toBe('MenuOrdering.Move');
    const link = reply.querySelector('a') as HTMLAnchorElement;
    link.click();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://github.com/acme/ci/actions/runs/42',
    });
  });

  it('talks to Scout directly on this computer: Enter sends the typed question and the reply streams in', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();

    await type(fixture, '  is the nightly build green?  ');
    await press(fixture, {});
    const request = bridge.localAsk.mock.lastCall![0] as { id: string; text: string };
    bridge.emit({ id: request.id, event: 'begin' });
    bridge.emit({ id: request.id, event: 'update', text: 'It is' });
    await settle(fixture);
    const partial = texts(fixture, 'reply');
    bridge.emit({
      id: request.id,
      event: 'complete',
      text: 'It is green.',
      citations: [{ label: 'run 42', url: 'https://github.com/acme/ci/actions/runs/42' }],
    });
    await settle(fixture);

    expect(request.text).toBe('is the nightly build green?');
    expect(texts(fixture, 'question')).toEqual(['is the nightly build green?']);
    expect(partial).toEqual(['It is']);
    expect(texts(fixture, 'reply')).toEqual(['It is green.']);
    expect(el(fixture, 'citations')?.querySelector('a')?.textContent?.trim()).toBe('run 42');
    expect((el(fixture, 'input') as HTMLTextAreaElement).value).toBe('');
  });

  it('Shift+Enter adds a line instead of sending', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'first line');

    const event = await press(fixture, { shiftKey: true });

    expect(event.defaultPrevented).toBe(false);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('asks one question at a time: while Scout answers, Enter sends nothing more', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'first');
    await press(fixture, {});

    await type(fixture, 'second');
    await press(fixture, {});

    expect(bridge.localAsk).toHaveBeenCalledTimes(1);
    expect(el(fixture, 'send')).toBeNull();
    expect((el(fixture, 'mic') as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps the conversation when the owner leaves the Chat screen and comes back', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const first = await open();
    await type(first, 'which pods restarted?');
    await press(first, {});
    const id = lastId(bridge);
    first.destroy();

    bridge.emit({ id, event: 'complete', text: 'Two pods restarted.' });
    const again = await open();

    expect(texts(again, 'question')).toEqual(['which pods restarted?']);
    expect(texts(again, 'reply')).toEqual(['Two pods restarted.']);
  });

  it('shows a notice Scout posted on its own as a Scout message with no question', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();

    bridge.push([
      {
        id: 'notice:1',
        question: '',
        reply: 'The script for Span<T> in C# is ready.',
        citations: [],
        state: 'done',
        notice: true,
      },
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(texts(fixture, 'question')).toEqual([]);
    expect(texts(fixture, 'reply')).toEqual(['The script for Span<T> in C# is ready.']);
  });

  it('a turn that carried the transcript shows its question and a with transcript tag', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();

    bridge.push([
      {
        id: 'q-1',
        question: 'what did they ask me?',
        reply: 'They asked about the release.',
        citations: [],
        state: 'done',
        transcript: true,
      },
      { id: 'q-2', question: 'and now?', reply: 'Nothing.', citations: [], state: 'done' },
    ]);
    await settle(fixture);

    expect(texts(fixture, 'question')).toEqual(['what did they ask me?', 'and now?']);
    expect(texts(fixture, 'with-transcript').map((tag) => tag.trim())).toEqual(['with transcript']);
  });

  it('a turn that carried the screen text shows its question and a with screen text tag', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();

    bridge.push([
      {
        id: 'q-1',
        question: 'what is wrong?',
        reply: 'The gateway is down.',
        citations: [],
        state: 'done',
        screenText: true,
      },
      { id: 'q-2', question: 'and now?', reply: 'Nothing.', citations: [], state: 'done' },
    ]);
    await settle(fixture);

    expect(texts(fixture, 'question')).toEqual(['what is wrong?', 'and now?']);
    expect(texts(fixture, 'with-screen-text').map((tag) => tag.trim())).toEqual([
      'with screen text',
    ]);
    expect(texts(fixture, 'with-transcript')).toEqual([]);
  });

  it('Clear empties the conversation', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'hi');
    await press(fixture, {});
    bridge.emit({ id: lastId(bridge), event: 'complete', text: 'Hello.' });
    await settle(fixture);
    const before = texts(fixture, 'reply');

    el(fixture, 'clear')!.click();
    await settle(fixture);

    expect(before).toEqual(['Hello.']);
    expect(texts(fixture, 'question')).toEqual([]);
    expect(texts(fixture, 'reply')).toEqual([]);
  });

  it('Stop cancels the question Scout is answering, and the cancelled reply ends the turn', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'summarise every repo');
    await press(fixture, {});
    const id = lastId(bridge);

    el(fixture, 'stop')!.click();
    bridge.emit({ id, event: 'fail', text: 'Cancelled.' });
    await settle(fixture);

    expect(bridge.localCancel).toHaveBeenCalledExactlyOnceWith({ id });
    expect(texts(fixture, 'reply')).toEqual(['Cancelled.']);
    expect(el(fixture, 'send')).not.toBeNull();
  });

  it('the microphone button is an icon, labelled Hold to talk and Listening while held', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;

    const idle = {
      icon: mic.querySelector('svg') !== null,
      text: mic.textContent?.trim(),
      label: mic.getAttribute('aria-label'),
    };
    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    await settle(fixture);
    const held = mic.getAttribute('aria-label');
    mic.dispatchEvent(new Event('pointerup'));
    await settle(fixture);

    expect(idle).toEqual({ icon: true, text: '', label: 'Hold to talk' });
    expect(held).toBe('Listening');
  });

  it('holding the microphone button sends the recording as a voice question', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;

    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    await settle(fixture);
    const listening = el(fixture, 'recording')?.textContent?.trim();
    audio.processor!.hear([0.5, -0.5]);
    mic.dispatchEvent(new Event('pointerup'));
    await vi.waitFor(() => expect(bridge.localAsk).toHaveBeenCalledTimes(1));
    await settle(fixture);

    const request = bridge.localAsk.mock.lastCall![0] as {
      audio: { contentType: string; base64: string };
    };
    const wav = Uint8Array.from(atob(request.audio.base64), (c) => c.charCodeAt(0));
    expect(listening).toBe('Listening. Release to send.');
    expect(request.audio.contentType).toBe('audio/wav');
    expect(String.fromCharCode(...wav.subarray(0, 4))).toBe('RIFF');
    expect(new DataView(wav.buffer).getUint32(40, true)).toBe(4);
    expect(texts(fixture, 'question')).toEqual(['(voice message)']);
    expect(audio.trackStopped).toBe(true);
  });

  it('leaving the window while the microphone is held discards the recording', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;
    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    audio.processor!.hear([0.5, -0.5]);

    window.dispatchEvent(new Event('blur'));
    await settle(fixture);
    mic.dispatchEvent(new Event('pointerup'));
    await settle(fixture);

    expect(audio.trackStopped).toBe(true);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('leaving the Chat screen while the microphone is held discards the recording', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    (el(fixture, 'mic') as HTMLButtonElement).dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    audio.processor!.hear([0.5, -0.5]);

    fixture.destroy();
    await vi.waitFor(() => expect(audio.trackStopped).toBe(true));

    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('says Scout is not running when the worker is stopped, and lets the owner ask again', async () => {
    const bridge = makeBridge();
    bridge.refuseNextAsNotRunning();
    configure(bridge);
    const fixture = await open();

    await type(fixture, 'hi');
    await press(fixture, {});

    expect(texts(fixture, 'reply')).toEqual([
      'Scout is not running. Start it from the tray or the app.',
    ]);
    expect(el(fixture, 'send')).not.toBeNull();
  });

  it('offers Start Scout on a not-running reply; it starts the worker once and the owner can ask again', async () => {
    const bridge = makeBridge();
    bridge.refuseNextAsNotRunning();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'hi');
    await press(fixture, {});

    expect(el(fixture, 'start-scout')?.textContent?.trim()).toBe('Start Scout');
    const start = el(fixture, 'start-scout') as HTMLButtonElement;
    start.click();
    start.click();
    await settle(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
    expect(el(fixture, 'start-scout')).toBeNull();
    await type(fixture, 'hi again');
    await press(fixture, {});
    expect(bridge.localAsk).toHaveBeenCalledTimes(2);
    expect((bridge.localAsk.mock.lastCall![0] as { text: string }).text).toBe('hi again');
  });

  it('when the worker cannot be started, says why and offers Start Scout again', async () => {
    const bridge = makeBridge();
    bridge.refuseNextAsNotRunning();
    bridge.workerStart.mockRejectedValueOnce(
      new Error('[scout:worker/spawn-failed] The Scout worker binary was not found.'),
    );
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'hi');
    await press(fixture, {});

    el(fixture, 'start-scout')!.click();
    await settle(fixture);

    expect(el(fixture, 'error')?.textContent?.trim()).toBe(
      'The Scout worker binary was not found.',
    );
    expect(el(fixture, 'start-scout')?.textContent?.trim()).toBe('Start Scout');
  });

  it('offers no Start Scout for any other failure', async () => {
    const bridge = makeBridge();
    bridge.localAsk.mockRejectedValueOnce(
      new Error('[scout:local/invalid-request] That question is already running.'),
    );
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'first');
    await press(fixture, {});
    const refusal = el(fixture, 'error')?.textContent?.trim();
    await type(fixture, 'second');
    await press(fixture, {});
    bridge.emit({
      id: lastId(bridge),
      event: 'fail',
      text: 'Scout stopped before it finished answering.',
    });
    await settle(fixture);

    expect(refusal).toBe('That question is already running.');
    expect(el(fixture, 'error')).toBeNull();
    expect(texts(fixture, 'reply')).toEqual(['Scout stopped before it finished answering.']);
    expect(el(fixture, 'start-scout')).toBeNull();
    expect(bridge.workerStart).not.toHaveBeenCalled();
  });
});
