import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { ApplicationRef, computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LOCAL_CANCELLED_REPLY } from '@scout/shared';
import type {
  LocalAskRequest,
  LocalEvent,
  LocalSetDecisionsRequest,
  LocalTranscriptEvent,
  LocalTurn,
  ReviewFinding,
} from '@scout/shared';

import { REPLY_STYLES } from '../core/reply-styles';
import { ScoutBridgeService } from '../core/scout-bridge';
import { ConversationMode, type ConversationState } from '../speech/conversation-mode';
import { ReadAloud } from '../speech/read-aloud';

import { ChatComponent } from './chat.component';

const NOT_RUNNING = 'Scout is not running. Start it from the tray or the app.';
const BUSY = 'Scout is queuing cards for your approval; wait until it finishes.';

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
        question: req.text.trim(),
        reply: '',
        citations: [],
        state: 'running',
        ...('voice' in req && req.voice === true ? { voice: true as const } : {}),
        ...changes,
      },
    ]);
  const localAsk = vi.fn(async (req: LocalAskRequest): Promise<void> => append(req));
  const transcriptListeners: ((event: LocalTranscriptEvent) => void)[] = [];
  const vaultPublic: Record<string, string> = {};
  return {
    push,
    vaultPublic,
    vaultHas: vi.fn(async (_req: { key: string }) => ({ exists: false })),
    vaultGetPublic: vi.fn(async (req: { key: string }) => ({
      value: vaultPublic[req.key] ?? null,
    })),
    emit: (event: LocalEvent) =>
      push(
        turns.map(({ activity, ...turn }) =>
          turn.id !== event.id || turn.state !== 'running' || event.event === 'begin'
            ? { ...turn, ...(activity === undefined ? {} : { activity }) }
            : event.event === 'progress'
              ? progressed(turn, activity, event.text)
              : event.event === 'fail' && turn.reply.length > 0
                ? { ...turn, failure: event.text ?? '', state: 'failed' as const }
                : {
                    ...turn,
                    reply: event.text ?? turn.reply,
                    citations:
                      event.event === 'complete' ? (event.citations ?? []) : turn.citations,
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
    actionsList: vi.fn(async () => ({ actions: [] })),
    writesList: vi.fn(async () => ({ entries: [] })),
    onActionsChanged: vi.fn(() => () => undefined),
    localSetDecisions: vi.fn(async (req: LocalSetDecisionsRequest): Promise<void> =>
      push(
        turns.map((turn) =>
          turn.id === req.turnId ? { ...turn, decisions: req.decisions } : turn,
        ),
      ),
    ),
    onGitHubVerdict: vi.fn(() => () => undefined),
    githubVerdictFor: vi.fn(async () => null),
    localClearTurns: vi.fn(async (): Promise<void> => {
      if (turns.some((turn) => turn.state === 'running' && turn.issues)) {
        throw new Error(`[scout:local/busy] ${BUSY}`);
      }
      push([]);
    }),
    localAsk,
    localCancel: vi.fn(async (_req: { id: string }): Promise<void> => undefined),
    hear: (event: LocalTranscriptEvent) => transcriptListeners.forEach((l) => l(event)),
    onLocalTranscript: vi.fn((listener: (event: LocalTranscriptEvent) => void) => {
      transcriptListeners.push(listener);
      return () => undefined;
    }),
    localListen: vi.fn(async (_req?: { lane?: string }) => ({ id: 'live-1' })),
    localListening: vi.fn(async () => ({ listening: false })),
    onLocalListening: vi.fn(
      (_listener: (state: { listening: boolean }) => void) => () => undefined,
    ),
    localAudio: vi.fn(async (_req: { id: string; pcm16: string }): Promise<void> => undefined),
    localStopListening: vi.fn(async (_req: { id: string }): Promise<void> => undefined),
    whisperListInstalled: vi.fn(async () => ({
      installed: [{ variant: 'base' as const, verified: true }],
    })),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    localSpeak: vi.fn(async () => ({ contentType: 'audio/wav', base64: '' })),
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

/** The Chat screen with its top-bar actions drawn before it, as the shell's top bar draws them. */
async function open(): Promise<ComponentFixture<ChatComponent>> {
  const fixture = TestBed.createComponent(ChatComponent);
  await settle(fixture);
  const actions = fixture.componentInstance.topBarActions().createEmbeddedView(undefined);
  TestBed.inject(ApplicationRef).attachView(actions);
  (fixture.nativeElement as HTMLElement).prepend(...(actions.rootNodes as Node[]));
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

function pushListening(bridge: ReturnType<typeof makeBridge>, listening: boolean): void {
  bridge.onLocalListening.mock.calls.forEach(([listener]) => listener({ listening }));
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
    localStorage.removeItem('scout:read-aloud');
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
    expect(texts(fixture, 'who-scout').map((t) => t.replace(/\s+/g, ' ').trim())).toEqual([
      expect.stringMatching(/^Scout\s*\d\d:02$/),
    ]);
    expect(texts(fixture, 'question')).toEqual(['is prod green?']);
    expect(texts(fixture, 'reply')).toEqual(['Yes.']);
  });

  it('shows what Scout is doing while the reply is still empty, then the reply as it streams', async () => {
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

  it('stop and ask: while Scout answers a question the field, Send and Enter stay enabled and a second question is sent to main, which stops the first; the mic stays off', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'analyze the project');
    await press(fixture, {});
    const send = el(fixture, 'send') as HTMLButtonElement;
    const answering = {
      field: (el(fixture, 'input') as HTMLTextAreaElement).disabled,
      send: send.disabled,
      sendTitle: send.title,
      stop: el(fixture, 'stop')?.getAttribute('aria-label'),
      mic: (el(fixture, 'mic') as HTMLButtonElement).disabled,
      hint: el(fixture, 'composer-hint')?.textContent?.trim(),
    };

    await type(fixture, 'stop and review PR 5');
    await press(fixture, {});
    await type(fixture, 'and a third, by the button');
    send.click();
    await settle(fixture);

    expect(answering).toEqual({
      field: false,
      send: false,
      sendTitle: 'Send',
      stop: 'Stop the answer',
      mic: true,
      hint: 'A new question stops this answer · Stop ends it',
    });
    expect(bridge.localAsk.mock.calls.map(([req]) => req.text)).toEqual([
      'analyze the project',
      'stop and review PR 5',
      'and a third, by the button',
    ]);
    expect((el(fixture, 'input') as HTMLTextAreaElement).value).toBe('');
    expect(el(fixture, 'error')).toBeNull();
  });

  it('while a card post runs Send and Clear are disabled with the posting title and Enter sends nothing', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    bridge.push([
      {
        id: 'post-1',
        question: 'Open 1 issue on acme/shop',
        reply: '',
        citations: [],
        state: 'running',
        issues: true,
      },
    ]);
    await settle(fixture);
    const send = el(fixture, 'send') as HTMLButtonElement;
    const clear = el(fixture, 'clear') as HTMLButtonElement;

    await type(fixture, 'is prod green?');
    await press(fixture, {});
    send.click();
    clear.click();
    await settle(fixture);

    expect({
      send: send.disabled,
      sendTitle: send.title,
      clear: clear.disabled,
      clearTitle: clear.title,
      hint: el(fixture, 'composer-hint')?.textContent?.trim(),
    }).toEqual({
      send: true,
      sendTitle: 'Queuing cards; cannot be stopped.',
      clear: true,
      clearTitle: 'Queuing cards; cannot be stopped.',
      hint: 'Queuing cards; cannot be stopped.',
    });
    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(bridge.localClearTurns).not.toHaveBeenCalled();
    expect(texts(fixture, 'question')).toEqual(['Open 1 issue on acme/shop']);
  });

  it('Clear while Scout answers a question is on and asks main to clear, which stops the answer and empties the conversation', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'analyze the project');
    await press(fixture, {});
    const clear = el(fixture, 'clear') as HTMLButtonElement;
    const answering = { disabled: clear.disabled, title: clear.title };

    clear.click();
    await settle(fixture);

    expect(answering).toEqual({ disabled: false, title: 'Clear the conversation' });
    expect(bridge.localClearTurns).toHaveBeenCalledExactlyOnceWith();
    expect(texts(fixture, 'question')).toEqual([]);
    expect(el(fixture, 'error')).toBeNull();
  });

  it('Clear refused by main because a card post from the other window is running shows that reason instead of nothing', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'hi');
    await press(fixture, {});
    bridge.emit({ id: lastId(bridge), event: 'complete', text: 'Hello.' });
    await settle(fixture);
    bridge.localClearTurns.mockRejectedValueOnce(new Error(`[scout:local/busy] ${BUSY}`));

    el(fixture, 'clear')!.click();
    await settle(fixture);
    const refused = el(fixture, 'error')?.textContent?.trim();
    el(fixture, 'clear')!.click();
    await settle(fixture);

    expect(refused).toBe(BUSY);
    expect(el(fixture, 'error')).toBeNull();
    expect(texts(fixture, 'reply')).toEqual([]);
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

  it('a reply with a findings block renders the cards and the text around it; one without renders as before', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const block = JSON.stringify({
      repository: 'acme/shop',
      number: 5,
      verdict: 'merge after the fix',
      findings: [
        {
          id: 'lock',
          severity: 'fix',
          title: 'Move has no lock',
          path: 'src/Cart.cs',
          line: 12,
          why: 'Two moves lose an item.',
          fix: 'Take the lock.',
        },
      ],
    });
    bridge.push([
      {
        id: 'q-1',
        question: 'review acme/shop#5',
        reply: `## Review of acme/shop\n\n\`\`\`scout-findings\n${block}\n\`\`\`\n\nAsk me to post it.`,
        citations: [],
        state: 'done',
      },
      {
        id: 'q-2',
        question: 'and this one?',
        reply: '## Plain\n\n```scout-findings\nnot json\n```',
        citations: [],
        state: 'done',
      },
    ]);
    const fixture = await open();
    const [withCards, plain] = Array.from(
      fixture.nativeElement.querySelectorAll('li.turn') as NodeListOf<HTMLElement>,
    );

    const reply = withCards!.querySelector('[data-test="reply"]')!;
    expect(reply.querySelector('h2')?.textContent).toBe('Review of acme/shop');
    expect(reply.textContent).toContain('Ask me to post it.');
    expect(reply.textContent).not.toContain('scout-findings');
    expect(reply.querySelector('pre')).toBeNull();
    expect(
      Array.from(withCards!.querySelectorAll('[data-test="finding"] h3')).map((h) => h.textContent),
    ).toEqual(['Move has no lock']);
    expect(plain!.querySelector('[data-test="finding"]')).toBeNull();
    expect(plain!.querySelector('[data-test="reply"] h2')?.textContent).toBe('Plain');
    expect(plain!.querySelector('[data-test="reply"] pre code')?.textContent?.trim()).toBe(
      'not json',
    );
  });

  it('a posting question shows the bare question and a with the review tag', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([
      {
        id: 'q-1',
        question: 'Post 1 finding on acme/shop#5',
        reply: '',
        citations: [],
        state: 'running',
        review: true,
      },
      { id: 'q-2', question: 'and now?', reply: 'Nothing.', citations: [], state: 'done' },
    ]);
    const fixture = await open();

    expect(texts(fixture, 'question')).toEqual(['and now?', 'Post 1 finding on acme/shop#5']);
    expect(texts(fixture, 'with-review').map((tag) => tag.trim())).toEqual(['with the review']);
  });

  it('an issues question shows the bare question and a with the issues tag', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([
      {
        id: 'q-1',
        question: 'Open 2 issues on acme/shop',
        reply: '',
        citations: [],
        state: 'running',
        issues: true,
      },
      { id: 'q-2', question: 'and now?', reply: 'Nothing.', citations: [], state: 'done' },
    ]);
    const fixture = await open();

    expect(texts(fixture, 'question')).toEqual(['and now?', 'Open 2 issues on acme/shop']);
    expect(texts(fixture, 'with-issues').map((tag) => tag.trim())).toEqual(['with the issues']);
    expect(texts(fixture, 'with-review')).toEqual([]);
  });

  it("Clear empties the conversation through main's clear, the one request main also turns into the reset of the worker's memory for the local channel", async () => {
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
    expect(bridge.localClearTurns).toHaveBeenCalledExactlyOnceWith();
    expect(texts(fixture, 'question')).toEqual([]);
    expect(texts(fixture, 'reply')).toEqual([]);
  });

  it('the header carries "Read answers aloud" as an icon button that presses on a click', async () => {
    const bridge = makeBridge();
    configure(bridge);
    localStorage.removeItem('scout:read-aloud');
    const fixture = await open();
    const toggle = el(fixture, 'read-aloud-switch')!;
    const before = toggle.getAttribute('aria-pressed');

    toggle.click();
    await settle(fixture);

    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle.getAttribute('aria-label')).toBe('Read answers aloud');
    expect(before).toBe('false');
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
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

  it('a card post cannot be cancelled: Stop is disabled with its reason while an Open issues turn runs, and a plain question can still be stopped', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    bridge.push([
      {
        id: 'post-1',
        question: 'Open 1 issue on acme/shop',
        reply: '',
        citations: [],
        state: 'running',
        issues: true,
      },
    ]);
    await settle(fixture);
    const stop = () => el(fixture, 'stop') as HTMLButtonElement;
    const posting = { disabled: stop().disabled, title: stop().title };
    stop().click();
    await settle(fixture);
    const cancelledWhilePosting = bridge.localCancel.mock.calls.length;

    bridge.push([]);
    await settle(fixture);
    await type(fixture, 'summarise every repo');
    await press(fixture, {});
    const id = lastId(bridge);
    const plain = { disabled: stop().disabled, title: stop().title };
    stop().click();
    await settle(fixture);

    expect(posting).toEqual({ disabled: true, title: 'Queuing cards; cannot be stopped.' });
    expect(cancelledWhilePosting).toBe(0);
    expect(plain).toEqual({ disabled: false, title: 'Stop the answer' });
    expect(bridge.localCancel).toHaveBeenCalledExactlyOnceWith({ id });
  });

  it('Stop after part of the reply keeps it and shows Interrupted under it', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'summarise every repo');
    await press(fixture, {});
    const id = lastId(bridge);
    bridge.emit({ id, event: 'update', text: 'Three repos so far' });

    el(fixture, 'stop')!.click();
    bridge.emit({ id, event: 'fail', text: 'Cancelled.' });
    await settle(fixture);

    expect(texts(fixture, 'reply')).toEqual(['Three repos so far']);
    expect(texts(fixture, 'failure').map((t) => t.trim())).toEqual(['Interrupted']);
  });

  it('the question box starts at one row and grows with the text up to six rows', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const box = el(fixture, 'input') as HTMLTextAreaElement;
    const css = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('\n');
    const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
      .filter(([, selector]) =>
        selector!.split(',').some((one) => {
          try {
            return box.matches(one.trim());
          } catch {
            return false;
          }
        }),
      )
      .map(([, , body]) => body!)
      .join(';');

    expect(box.rows).toBe(1);
    expect(rules).toMatch(/field-sizing:\s*content/);
    expect(rules).toMatch(/max-height:\s*calc\(6lh \+ 20px\)/);
  });

  it('the microphone sits inside the question field, with Send or Stop beside it', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const field = el(fixture, 'field')!;

    expect(field.contains(el(fixture, 'input'))).toBe(true);
    expect(el(fixture, 'mic')!.parentElement).toBe(field);
    expect(field.contains(el(fixture, 'send'))).toBe(false);
    expect(el(fixture, 'send')!.parentElement).toBe(field.parentElement);
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

  it('holding the microphone dictates: what Scout hears fills the box, release sends it as the question with a mic mark, never a recording', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;

    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    await settle(fixture);
    const listening = el(fixture, 'recording')?.textContent?.trim();
    bridge.hear({ id: 'live-1', lane: 'me', text: 'is the nightly', final: false });
    await settle(fixture);
    const box = (fixture.nativeElement as HTMLElement).querySelector('textarea')!.value;
    bridge.hear({
      id: 'live-1',
      lane: 'me',
      text: 'is the nightly build green?',
      final: true,
      seq: 1,
    });
    mic.dispatchEvent(new Event('pointerup'));
    await settle(fixture);
    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true });
    await vi.waitFor(() => expect(bridge.localAsk).toHaveBeenCalledTimes(1));
    await settle(fixture);

    expect(listening).toBe('Listening. Release to send.');
    expect(box).toBe('is the nightly');
    expect(bridge.localAsk.mock.calls.map(([req]) => req)).toEqual([
      { id: expect.any(String), text: 'is the nightly build green?', voice: true },
    ]);
    expect(texts(fixture, 'question')).toEqual(['is the nightly build green?']);
    expect(el(fixture, 'question-voice')).not.toBeNull();
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

  it('when the microphone track ends while the button is held, push-to-talk stops, sends nothing and says "The microphone was disconnected."', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const track: { stop: () => void; onended?: ((event: Event) => void) | null } = {
      stop: () => {
        audio.trackStopped = true;
      },
    };
    vi.mocked(navigator.mediaDevices.getUserMedia).mockResolvedValueOnce({
      getTracks: () => [track],
    } as unknown as MediaStream);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;
    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    audio.processor!.hear([0.5, -0.5]);

    track.onended?.(new Event('ended'));
    await vi.waitFor(() => expect(audio.trackStopped).toBe(true));
    await settle(fixture);
    mic.dispatchEvent(new Event('pointerup'));
    await settle(fixture);
    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true });
    await settle(fixture);

    expect(el(fixture, 'error')?.textContent?.trim()).toBe('The microphone was disconnected.');
    expect(el(fixture, 'recording')).toBeNull();
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it.each([
    ['NotAllowedError', 'Scout was not allowed to use the microphone.'],
    ['NotFoundError', 'No microphone was found.'],
    ['NotReadableError', 'The microphone is busy: another program is using it.'],
  ])(
    'a microphone that fails with %s when the button is held says so in its own sentence and sends nothing',
    async (name, sentence) => {
      const bridge = makeBridge();
      configure(bridge);
      vi.mocked(navigator.mediaDevices.getUserMedia).mockRejectedValueOnce(
        new DOMException('failed', name),
      );
      const fixture = await open();
      const mic = el(fixture, 'mic') as HTMLButtonElement;

      mic.dispatchEvent(new Event('pointerdown'));
      await vi.waitFor(async () => {
        await settle(fixture);
        expect(el(fixture, 'error')?.textContent?.trim()).toBe(sentence);
      });
      mic.dispatchEvent(new Event('pointerup'));
      await settle(fixture);

      expect(el(fixture, 'error')?.textContent?.trim()).toBe(sentence);
      expect(el(fixture, 'recording')).toBeNull();
      expect(bridge.localAsk).not.toHaveBeenCalled();
    },
  );

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

  it.each([
    [
      'main is posting a card the quick-ask window just sent',
      'is the nightly build green?',
      `[scout:local/busy] ${BUSY}`,
      BUSY,
    ],
    [
      'the question is over 4000 characters',
      'x'.repeat(4001),
      '[scout:local/invalid-request] Type a question of 1 to 4000 characters.',
      'Type a question of 1 to 4000 characters.',
    ],
  ])(
    'B1 Chat keeps the typed question when main refuses it: %s',
    async (_label, typed, thrown, shown) => {
      const bridge = makeBridge();
      bridge.localAsk.mockRejectedValueOnce(new Error(thrown));
      configure(bridge);
      const fixture = await open();

      await type(fixture, typed);
      await press(fixture, {});

      const kept = (el(fixture, 'input') as HTMLTextAreaElement).value;
      expect(el(fixture, 'error')?.textContent?.trim()).toBe(shown);
      expect(kept.length).toBe(typed.length);
      expect(kept).toBe(typed);
    },
  );

  it('B5 with no AI Source connected, Chat keeps the field, the microphone and Send off and sends nothing', async () => {
    const bridge = makeBridge();
    bridge.vaultPublic['llm.provider'] = 'None';
    configure(bridge);
    const fixture = await open();
    const controls = () => ({
      field: (el(fixture, 'input') as HTMLTextAreaElement).disabled,
      mic: (el(fixture, 'mic') as HTMLButtonElement).disabled,
      send: (el(fixture, 'send') as HTMLButtonElement | null)?.disabled,
    });

    await vi.waitFor(async () => {
      await settle(fixture);
      expect(controls()).toEqual({ field: true, mic: true, send: true });
    });
    await type(fixture, 'is prod green?');
    await press(fixture, {});

    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('B5 while a question is still on its way to main, Chat keeps the microphone off and does not start recording', async () => {
    const bridge = makeBridge();
    let accept: () => void = () => undefined;
    bridge.localAsk.mockImplementationOnce(
      () =>
        new Promise<void>((resolve) => {
          accept = resolve;
        }),
    );
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'is prod green?');
    await press(fixture, {});

    const mic = el(fixture, 'mic') as HTMLButtonElement;
    const micOffWhileSending = mic.disabled;
    mic.dispatchEvent(new Event('pointerdown'));
    await settle(fixture);
    const recordingWhileSending = el(fixture, 'recording');
    accept();
    await settle(fixture);

    expect(micOffWhileSending).toBe(true);
    expect(recordingWhileSending).toBeNull();
    expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled();
  });

  it('B8 Stop pressed in Chat while the question is still on its way to main cancels it once main takes it', async () => {
    const bridge = makeBridge();
    let accept: () => void = () => undefined;
    bridge.localAsk.mockImplementationOnce(
      (req: LocalAskRequest) =>
        new Promise<void>((resolve) => {
          accept = () => {
            bridge.push([
              {
                id: req.id,
                question: 'summarise every repo',
                reply: '',
                citations: [],
                state: 'running',
              },
            ]);
            resolve();
          };
        }),
    );
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'summarise every repo');
    await press(fixture, {});

    const stop = el(fixture, 'stop') as HTMLButtonElement | null;
    const stopShownWhileSending = stop !== null;
    stop?.click();
    await settle(fixture);
    accept();
    await settle(fixture);

    expect(stopShownWhileSending).toBe(true);
    expect(bridge.localCancel).toHaveBeenCalledExactlyOnceWith({ id: lastId(bridge) });
  });

  it('B7 Chat follows a streaming reply to the bottom of the conversation', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const list = el(fixture, 'turns')!;
    Object.defineProperty(list, 'scrollHeight', { configurable: true, get: () => 1000 });
    Object.defineProperty(list, 'clientHeight', { configurable: true, get: () => 200 });

    await type(fixture, 'summarise every repo');
    await press(fixture, {});
    bridge.emit({ id: lastId(bridge), event: 'update', text: 'Three repos so far' });
    await settle(fixture);

    expect(list.scrollTop).toBe(1000);
  });
  it('closing the Chat screen while a clicked-off conversation drains drops what was in flight: its last line is never asked', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const talk = el(fixture, 'talk') as HTMLButtonElement;
    talk.click();
    await settle(fixture);
    talk.click();
    await settle(fixture);

    fixture.destroy();
    bridge.hear({ id: 'live-1', lane: 'me', text: 'Is main green?', final: true });
    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true });
    for (let i = 0; i < 10; i++) await Promise.resolve();

    expect(bridge.localListen).toHaveBeenCalledExactlyOnceWith({ lane: 'me' });
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('while main reports that another window is listening, the microphone and the talk button stay off', async () => {
    const bridge = makeBridge();
    bridge.localListening.mockResolvedValue({ listening: true });
    configure(bridge);
    const fixture = await open();
    pushListening(bridge, true);
    await settle(fixture);

    expect({
      mic: (el(fixture, 'mic') as HTMLButtonElement).disabled,
      talk: (el(fixture, 'talk') as HTMLButtonElement).disabled,
    }).toEqual({ mic: true, talk: true });
  });

  it("main reporting this screen's own hold as listening keeps the microphone usable through the release, which sends what was heard", async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    const mic = el(fixture, 'mic') as HTMLButtonElement;

    mic.dispatchEvent(new Event('pointerdown'));
    await vi.waitFor(() => expect(audio.processor).not.toBeNull());
    pushListening(bridge, true);
    await settle(fixture);
    const disabledWhileHeld = mic.disabled;
    bridge.hear({ id: 'live-1', lane: 'me', text: 'is prod green?', final: true, seq: 1 });
    mic.dispatchEvent(new Event('pointerup'));
    await settle(fixture);
    bridge.hear({ id: 'live-1', lane: 'me', text: '', final: true, ended: true });
    await vi.waitFor(() => expect(bridge.localAsk).toHaveBeenCalledTimes(1));

    expect(disabledWhileHeld).toBe(false);
    expect(bridge.localAsk.mock.calls.map(([req]) => req.text)).toEqual(['is prod green?']);
  });
});

type AuditStage = 'first batch' | 'later batch with a repeat' | 'final scope';
type FirstBatchPick = 'none' | 'a No on a card';
type IssuesBetween = 'no issues' | 'issues opened after the first batch';

interface AuditRow {
  readonly stage: AuditStage;
  readonly pick: FirstBatchPick;
  readonly issues: IssuesBetween;
}

const AUDIT_FINDING = (id: string, title: string): ReviewFinding => ({
  id,
  severity: 'fix',
  title,
  path: `src/${id}.cs`,
  line: 3,
  why: `${title} is wrong.`,
  fix: `Fix ${title}.`,
});
const TOTALS = AUDIT_FINDING('totals', 'Totals before discounts');
const LOCKS = AUDIT_FINDING('locks', 'Cart has no lock');
const NAMES = AUDIT_FINDING('names', 'Names say the wrong thing');

function auditPost(id: string, area: string, findings: readonly ReviewFinding[]): LocalTurn {
  const block = JSON.stringify({
    kind: 'audit',
    repository: 'acme/shop',
    commit: 'ec7f951',
    verdict: 'sound after the High findings',
    read: `${area}: 2 of 2 files read`,
    findings,
  });
  return {
    id,
    question: '',
    reply: `Audit of acme/shop, ${area}:\n\n\`\`\`scout-findings\n${block}\n\`\`\``,
    citations: [],
    state: 'done',
    notice: true,
    askedAt: ARRIVED_AT,
  };
}

const ARRIVED_AT = new Date(2026, 10, 7, 14, 5).getTime();
const ARRIVED = '14:05';

const SCOPE =
  'Audit of acme/shop at ec7f951 is done. Verdict: sound (0 High, 3 Medium, 0 Low).\nScope: 2 of 2 areas covered (src; tests); 4 of 4 files read.\nEvery area was covered.';

const AUDIT_ROWS: readonly AuditRow[] = (
  ['first batch', 'later batch with a repeat', 'final scope'] as const
).flatMap((stage) =>
  (['none', 'a No on a card'] as const).flatMap((pick) =>
    (['no issues', 'issues opened after the first batch'] as const)
      .filter((issues) => stage !== 'first batch' || issues === 'no issues')
      .map((issues) => ({ stage, pick, issues })),
  ),
);

describe(`Chat screen: an audit job's posts as cards (${AUDIT_ROWS.length} rows)`, () => {
  beforeEach(() => TestBed.resetTestingModule());

  it.each(AUDIT_ROWS.map((row) => [`${row.stage}, ${row.pick}, ${row.issues}`, row] as const))(
    '%s',
    async (_name, row) => {
      const bridge = makeBridge();
      configure(bridge);
      const started: LocalTurn = {
        id: 'q-1',
        question: 'audit acme/shop',
        reply: 'Started the audit of acme/shop; the findings come here batch by batch.',
        citations: [],
        state: 'done',
      };
      bridge.push([started, auditPost('notice:1', 'src (1 of 2)', [TOTALS, LOCKS])]);
      const fixture = await open();
      const turnsNow = async (extra: readonly LocalTurn[]) => {
        bridge.push([...(await bridge.localGetTurns()), ...extra]);
        await settle(fixture);
      };
      if (row.pick === 'a No on a card') {
        (fixture.nativeElement.querySelectorAll('[data-test="no"]')[1] as HTMLElement).click();
        await settle(fixture);
      }
      if (row.issues === 'issues opened after the first batch') {
        await turnsNow([
          {
            id: 'q-2',
            question: 'Open 1 issue on acme/shop',
            reply: 'Opened https://github.com/acme/shop/issues/12',
            citations: [],
            state: 'done',
            issues: true,
          },
        ]);
      }
      if (row.stage !== 'first batch') {
        await turnsNow([auditPost('notice:2', 'tests (2 of 2)', [LOCKS, NAMES])]);
      }
      if (row.stage === 'final scope') {
        await turnsNow([
          {
            id: 'notice:3',
            question: '',
            reply: SCOPE,
            citations: [],
            state: 'done',
            notice: true,
            askedAt: ARRIVED_AT,
          },
        ]);
      }

      const items = Array.from(
        fixture.nativeElement.querySelectorAll('li.turn') as NodeListOf<HTMLElement>,
      );
      const cardsByTurn = items.map((item) =>
        Array.from(item.querySelectorAll('[data-test="finding"]')).map((card) =>
          card.querySelector('[data-test="dismissed"]')
            ? `${card.querySelector('[data-test="dismissed"] .dismissed-title')?.textContent}`
            : (card.querySelector('h3')?.textContent ?? ''),
        ),
      );
      const replies = items.map((item) =>
        (item.querySelector('[data-test="reply"]')?.textContent ?? '').replace(/\s+/g, ' ').trim(),
      );
      const opened = row.issues === 'issues opened after the first batch';
      const lockLabel =
        row.pick === 'a No on a card' ? 'Dismissed: Cart has no lock' : 'Cart has no lock';
      const expectedCards: string[][] =
        row.stage === 'first batch'
          ? [[], [TOTALS.title, lockLabel]]
          : opened
            ? [[], [TOTALS.title, lockLabel], [], [NAMES.title]]
            : [[], [], [TOTALS.title, lockLabel, NAMES.title]];
      if (row.stage === 'final scope') expectedCards.push([]);

      expect(
        items.map((item) =>
          (item.querySelector('[data-test="moved-below"]')?.textContent ?? '').trim(),
        ),
      ).toEqual(
        items.map((_item, i) =>
          i === 1 && row.stage !== 'first batch' && !opened
            ? 'Its findings are in the cards below.'
            : '',
        ),
      );
      expect(cardsByTurn).toEqual(expectedCards);
      expect(replies.every((reply) => !reply.includes('scout-findings'))).toBe(true);
      expect(replies[1]).toBe('Audit of acme/shop, src (1 of 2):');
      expect(
        items.map((item) => item.querySelector('[data-test="arrived-at"]')?.textContent ?? null),
      ).toEqual(items.map((_item, i) => (i === 0 || (i === 2 && opened) ? null : ARRIVED)));
      if (row.stage === 'final scope') {
        expect(replies.at(-1)).toBe(SCOPE.replace(/\s+/g, ' '));
      }
      expect(
        Array.from(
          fixture.nativeElement.querySelectorAll(
            '[data-test="posted"] strong',
          ) as NodeListOf<HTMLElement>,
        ).map((node) => node.textContent?.replace(/\s+/g, ' ').trim()),
      ).toEqual(opened ? ['1 issue opened on acme/shop'] : []);
      const decided = (await bridge.localGetTurns()).find((turn) => turn.id === 'notice:1');
      expect(decided?.decisions ?? {}).toEqual(
        row.pick === 'a No on a card' ? { locks: { pick: 'no', dismissed: true } } : {},
      );
    },
  );
});

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declared(node: Element, property: string): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

describe('the chat as in the prototype', () => {
  const TURN: LocalTurn = {
    id: 'q-1',
    question: 'Review acme/shop#5 and run its tests',
    reply: 'Two findings on acme/shop#5.',
    citations: [],
    state: 'done',
    askedAt: Date.UTC(2026, 10, 7, 14, 5),
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
  });

  async function chatWith(turns: readonly LocalTurn[]) {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push(turns);
    return { fixture: await open(), bridge };
  }

  type Part = "the owner's bubble" | "Scout's card";
  const LOOK: Readonly<Record<Part, Readonly<Record<string, string>>>> = {
    "the owner's bubble": {
      background: 'var(--sc-accent-strong)',
      color: 'var(--sc-on-accent)',
      'border-radius': '14px 14px 4px 14px',
      padding: '10px 14px',
    },
    "Scout's card": {
      background: 'var(--sc-surface)',
      border: '1px solid var(--sc-line)',
      'border-radius': '14px',
      padding: '16px',
    },
  };

  for (const theme of ['light', 'dark'] as const) {
    for (const part of Object.keys(LOOK) as Part[]) {
      it(`in the ${theme} theme ${part} paints as the artboard draws it, through the theme tokens`, async () => {
        document.documentElement.setAttribute('data-theme', theme);
        const { fixture } = await chatWith([TURN]);
        const node =
          part === "the owner's bubble" ? el(fixture, 'question')! : el(fixture, 'scout-reply')!;

        const look = Object.fromEntries(
          Object.keys(LOOK[part]).map((property) => [property, declared(node, property)]),
        );

        expect(look).toEqual(LOOK[part]);
      });
    }
  }

  it('a wide window keeps the column in the middle of the page, at most 880 px wide', async () => {
    const { fixture } = await chatWith([TURN]);
    const column = fixture.nativeElement.querySelector('main.chat') as HTMLElement;

    expect(declared(column, 'max-width')).toBe('880px');
    expect(declared(column, 'margin-inline')).toBe('auto');
  });

  it("the owner's question sits on the right with no avatar; Scout's reply is on the left", async () => {
    const { fixture } = await chatWith([TURN]);
    const user = el(fixture, 'question')!.parentElement!;

    expect(declared(user, 'align-items')).toBe('flex-end');
    expect(user.querySelector('.avatar')).toBeNull();
    expect(declared(el(fixture, 'scout-reply')!, 'align-items')).toBeNull();
  });

  it('the composer: the question field on the page colour, the microphone inside it, Send as the accent icon button, and the keys line under it', async () => {
    const { fixture } = await chatWith([]);
    const send = el(fixture, 'send')!;

    expect(declared(el(fixture, 'input')!, 'background')).toBe('var(--sc-bg)');
    expect(declared(el(fixture, 'input')!, 'border-radius')).toBe('10px');
    expect({
      label: send.getAttribute('aria-label'),
      icon: send.querySelector('svg') !== null,
      background: declared(send, 'background'),
      color: declared(send, 'color'),
    }).toEqual({
      label: 'Send',
      icon: true,
      background: 'var(--sc-accent-strong)',
      color: 'var(--sc-on-accent)',
    });
    expect(el(fixture, 'composer-hint')?.textContent?.trim()).toBe(
      'Enter sends · Shift+Enter for a new line',
    );
  });

  it('the empty state shows the Scout mark, one sentence and a suggestion that fills the question box without sending it', async () => {
    const { fixture, bridge } = await chatWith([]);
    const icon = el(fixture, 'empty')?.querySelector('.empty-mark svg');

    el(fixture, 'empty-suggestion')!.click();
    await settle(fixture);

    expect(icon).not.toBeNull();
    expect(el(fixture, 'empty-text')?.textContent?.trim()).toBe(
      'Ask Scout anything, or hold the microphone button to talk.',
    );
    expect((el(fixture, 'input') as HTMLTextAreaElement).value).toBe('List my open pull requests');
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });
});

/** True when `node` is drawn off screen for a screen reader only: absolutely placed and clipped to nothing. */
function visuallyHidden(node: Element): boolean {
  return declared(node, 'position') === 'absolute' && declared(node, 'clip-path') === 'inset(50%)';
}

function clock(at: number): string {
  const date = new Date(at);
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
}

function fakeVoice(state: ConversationState, heard: string, error: string | null) {
  const current = signal(state);
  return {
    state: current.asReadonly(),
    error: signal(error).asReadonly(),
    heard: signal(heard).asReadonly(),
    on: computed(() => current() !== 'off'),
    microphoneOpen: computed(() => current() === 'listening'),
    toggle: vi.fn(),
    escape: vi.fn(() => false),
    stop: vi.fn(async () => false),
  };
}

describe('what a screen reader hears, and where focus goes, as a turn runs and ends', () => {
  const ASKED: LocalTurn = {
    id: 'q-1',
    question: 'Where are the docs?',
    reply: 'See [the docs](https://example.com/docs).',
    citations: [],
    state: 'done',
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function announced(fixture: ComponentFixture<ChatComponent>): string {
    return el(fixture, 'announce')?.textContent?.trim() ?? '';
  }

  it('the conversation is not a live region: the running region is the only live one', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([{ ...ASKED, state: 'running', reply: '' }]);
    const fixture = await open();

    expect({
      list: el(fixture, 'turns')!.getAttribute('aria-live'),
      running: el(fixture, 'running-turn')!.getAttribute('aria-live'),
      status: el(fixture, 'announce')!.getAttribute('role'),
    }).toEqual({ list: null, running: 'polite', status: 'status' });
  });

  it('a turn that streamed through the running region is announced once: nothing more when it joins the conversation', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const fixture = await open();
    await type(fixture, 'Where are the docs?');
    await press(fixture, {});
    const id = lastId(bridge);
    bridge.emit({ id, event: 'update', text: 'See the docs.' });
    await settle(fixture);
    bridge.emit({ id, event: 'complete', text: 'See the docs.' });
    await settle(fixture);

    expect({
      reply: texts(fixture, 'reply'),
      listLive: el(fixture, 'turns')!.getAttribute('aria-live'),
      announced: announced(fixture),
    }).toEqual({ reply: ['See the docs.'], listLive: null, announced: '' });
  });

  for (const [name, arrive, said] of [
    [
      'a notice Scout posted on its own, which arrives already done,',
      (bridge: ReturnType<typeof makeBridge>) =>
        bridge.push([
          {
            id: 'notice:1',
            question: '',
            reply: 'Area 3 of 8 done.',
            citations: [],
            state: 'done',
            notice: true,
          },
        ]),
      'Scout: Area 3 of 8 done.',
    ],
    [
      'a question refused because Scout is not running, which never ran,',
      async (bridge: ReturnType<typeof makeBridge>, fixture: ComponentFixture<ChatComponent>) => {
        bridge.refuseNextAsNotRunning();
        await type(fixture, 'is prod green?');
        await press(fixture, {});
      },
      NOT_RUNNING,
    ],
  ] as const) {
    it(`${name} is announced once, in the polite status line`, async () => {
      const bridge = makeBridge();
      configure(bridge);
      const fixture = await open();

      await arrive(bridge, fixture);
      await settle(fixture);

      expect(announced(fixture)).toBe(said);
    });
  }

  for (const [name, ended, said] of [
    [
      'failed before any text',
      { reply: 'The AI Source did not answer.', state: 'failed' },
      'The AI Source did not answer.',
    ],
    [
      'failed after part of its reply',
      { reply: 'Half of it', state: 'failed', failure: 'The AI Source did not answer.' },
      'The AI Source did not answer.',
    ],
    [
      'stopped after part of its reply',
      { reply: 'Half of it', state: 'failed', failure: LOCAL_CANCELLED_REPLY },
      'Interrupted',
    ],
    ['finished', { reply: 'All of it.', state: 'done' }, ''],
  ] as const) {
    it(`a running turn that ${name} is told once in the status line by what its card shows, since leaving the running region announces nothing`, async () => {
      const bridge = makeBridge();
      configure(bridge);
      const fixture = await open();
      bridge.push([{ ...ASKED, state: 'running', reply: '' }]);
      await settle(fixture);
      const whileRunning = announced(fixture);

      bridge.push([{ ...ASKED, ...ended }]);
      await settle(fixture);
      const atTheEnd = announced(fixture);
      bridge.push([
        { ...ASKED, ...ended },
        { ...ASKED, id: 'q-2', state: 'running', reply: '' },
      ]);
      await settle(fixture);

      expect([whileRunning, atTheEnd, announced(fixture)]).toEqual(['', said, said]);
    });
  }

  it('the history restored as the Chat screen opens is not announced, then or when a new question starts', async () => {
    const bridge = makeBridge();
    configure(bridge);
    const history: readonly LocalTurn[] = [
      ASKED,
      {
        id: 'notice:1',
        question: '',
        reply: 'Area 3 of 8 done.',
        citations: [],
        state: 'done',
        notice: true,
      },
    ];
    bridge.push(history);
    const fixture = await open();
    bridge.push([...history, { ...ASKED, id: 'q-2', state: 'running', reply: '' }]);
    await settle(fixture);

    expect({
      shown: texts(fixture, 'reply').length,
      status: el(fixture, 'announce')?.getAttribute('role'),
      announced: announced(fixture),
    }).toEqual({ shown: 2, status: 'status', announced: '' });
  });

  for (const [control, find] of [
    [
      'the read-aloud control',
      (root: Element) => root.querySelector<HTMLElement>('[data-test="read-aloud"]'),
    ],
    ['the link in the reply', (root: Element) => root.querySelector<HTMLElement>('.reply a')],
  ] as const) {
    it(`focus on ${control} in the running region moves to the same control in the card that replaces it`, async () => {
      const bridge = makeBridge();
      configure(bridge);
      bridge.push([{ ...ASKED, state: 'running' }]);
      const fixture = await open();
      find(el(fixture, 'running-turn')!)!.focus();

      bridge.push([ASKED]);
      await settle(fixture);
      const card = el(fixture, 'turns')!.querySelector('[data-test="scout-reply"]')!;

      expect(document.activeElement).toBe(find(card));
    });
  }

  it('focus in the composer stays there when the running turn ends', async () => {
    const bridge = makeBridge();
    configure(bridge);
    bridge.push([{ ...ASKED, state: 'running' }]);
    const fixture = await open();
    el(fixture, 'read-aloud')!.focus();
    el(fixture, 'input')!.focus();

    bridge.push([ASKED]);
    await settle(fixture);

    expect(document.activeElement).toBe(el(fixture, 'input'));
  });
});

describe('Chat as Main.dc.html draws it, state by state', () => {
  const AT = Date.UTC(2026, 10, 7, 14, 5);
  const DONE: LocalTurn = {
    id: 'q-1',
    question: 'Review acme/shop#5 and run its tests',
    reply: 'Two findings on acme/shop#5.',
    citations: [],
    state: 'done',
    askedAt: AT,
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem('scout:read-aloud');
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    vi.unstubAllGlobals();
  });

  async function chatWith(
    turns: readonly LocalTurn[],
    options: { noSource?: boolean; voice?: ReturnType<typeof fakeVoice> } = {},
  ) {
    const bridge = makeBridge();
    if (options.noSource === true) bridge.vaultPublic['llm.provider'] = 'None';
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
        ...(options.voice === undefined
          ? []
          : [{ provide: ConversationMode, useValue: options.voice }]),
      ],
    });
    bridge.push(turns);
    const fixture = await open();
    if (options.noSource === true) {
      await vi.waitFor(async () => {
        await settle(fixture);
        expect((el(fixture, 'input') as HTMLTextAreaElement).disabled).toBe(true);
      });
    }
    return { fixture, bridge };
  }

  it('the page carries no title row of its own: the top bar names the screen, and "Chat" stays a heading for a screen reader only', async () => {
    const { fixture } = await chatWith([DONE]);
    const headings = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('h1'),
    ) as HTMLElement[];

    expect(headings.map((h) => h.textContent?.trim())).toEqual(['Chat']);
    expect(visuallyHidden(headings[0]!)).toBe(true);
    expect(fixture.nativeElement.querySelector('header')).toBeNull();
  });

  it("Clear and Read answers aloud are the screen's top-bar actions, not part of the page: Clear first, drawn as the artboard's top-bar Clear on a Material outlined button", async () => {
    const { fixture } = await chatWith([DONE]);
    const clear = el(fixture, 'clear') as HTMLButtonElement;
    const page = fixture.nativeElement.querySelector('main.chat') as HTMLElement;

    expect(
      fixture.componentInstance
        .topBarActions()
        .createEmbeddedView(undefined)
        .rootNodes.map((node: Node) =>
          node instanceof Element
            ? (node.getAttribute('data-test') ?? node.tagName.toLowerCase())
            : null,
        )
        .filter((name: string | null) => name !== null),
    ).toEqual(['clear', 'sc-read-aloud-switch']);
    expect(page.querySelector('[data-test="clear"], [data-test="read-aloud-switch"]')).toBeNull();
    expect({
      material: clear.classList.contains('mat-mdc-outlined-button'),
      icon: clear.querySelector('sc-icon')?.getAttribute('name'),
      title: clear.title,
      disabled: clear.disabled,
      height: declared(clear, '--mat-button-outlined-container-height'),
      padding: declared(clear, '--mat-button-outlined-horizontal-padding'),
      size: declared(clear, '--mat-button-outlined-label-text-size'),
      weight: declared(clear, '--mat-button-outlined-label-text-weight'),
      label: declared(clear, '--mat-button-outlined-label-text-color'),
      outline: declared(clear, '--mat-button-outlined-outline-color'),
      shape: declared(clear, '--mat-button-outlined-container-shape'),
      disabledLabel: declared(clear, '--mat-button-outlined-disabled-label-text-color'),
    }).toEqual({
      material: true,
      icon: 'trash',
      title: 'Clear the conversation',
      disabled: false,
      height: '32px',
      padding: '12px',
      size: '13px',
      weight: '400',
      label: 'var(--sc-fg-2)',
      outline: 'var(--sc-line)',
      shape: 'var(--sc-radius-sm)',
      disabledLabel: 'var(--sc-muted)',
    });
  });

  it("Read answers aloud in the top bar is the round icon button of the Components artboard, the bell's size, on the second surface while pressed", async () => {
    const { fixture } = await chatWith([DONE]);
    const toggle = el(fixture, 'read-aloud-switch') as HTMLButtonElement;
    const look = () => ({
      pressed: toggle.getAttribute('aria-pressed'),
      size: [declared(toggle, 'width'), declared(toggle, 'height')],
      radius: declared(toggle, 'border-radius'),
      border: declared(toggle, 'border'),
      background: declared(toggle, 'background'),
      color: declared(toggle, 'color'),
    });
    const before = look();

    toggle.click();
    await settle(fixture);

    expect([before, look()]).toEqual([
      {
        pressed: 'false',
        size: ['40px', '40px'],
        radius: '50%',
        border: '1px solid var(--sc-line)',
        background: 'transparent',
        color: 'var(--sc-fg)',
      },
      {
        pressed: 'true',
        size: ['40px', '40px'],
        radius: '50%',
        border: '1px solid var(--sc-line)',
        background: 'var(--sc-surface-2)',
        color: 'var(--sc-fg)',
      },
    ]);
  });

  it('a voice that failed to read aloud says so on the page, as an alert over the composer, not in the top bar', async () => {
    const readAloud = {
      enabled: signal(true).asReadonly(),
      setEnabled: vi.fn(),
      error: signal<string | null>('Scout could not read that aloud; see Scout logs.').asReadonly(),
      speakingId: signal<string | null>(null).asReadonly(),
      playing: signal(false).asReadonly(),
      busy: signal(false).asReadonly(),
      toggle: vi.fn(),
      follow: vi.fn(),
      stop: vi.fn(() => false),
    };
    TestBed.overrideProvider(ReadAloud, { useValue: readAloud });
    const { fixture } = await chatWith([DONE]);
    const alerts = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('[data-test="voice-error"]'),
    ) as HTMLElement[];

    expect(
      alerts.map((node) => [
        node.textContent?.trim(),
        node.getAttribute('role'),
        node.closest('main.chat') !== null,
      ]),
    ).toEqual([['Scout could not read that aloud; see Scout logs.', 'alert', true]]);
  });

  it('the owner\'s turn has no visible "You HH:MM" line: the bubble alone, with who spoke and when kept for a screen reader', async () => {
    const { fixture } = await chatWith([DONE]);
    const user = el(fixture, 'question')!.parentElement!;
    const who = el(fixture, 'who-user')!;

    expect(visuallyHidden(who)).toBe(true);
    expect(who.textContent?.replace(/\s+/g, ' ').trim()).toBe(`You ${clock(AT)}`);
    expect(
      Array.from(user.children)
        .filter((child) => !visuallyHidden(child))
        .map((child) => child.getAttribute('data-test')),
    ).toEqual(['question']);
  });

  const REPLIES: readonly {
    readonly name: string;
    readonly turn: LocalTurn;
    readonly header: readonly string[];
    readonly body: Readonly<Record<string, string | null>>;
  }[] = [
    {
      name: 'a finished reply',
      turn: DONE,
      header: ['Scout', clock(AT), 'read aloud'],
      body: { reply: 'Two findings on acme/shop#5.', working: null, failure: null },
    },
    {
      name: 'a reply interrupted after it began',
      turn: { ...DONE, reply: 'Two findings', state: 'failed', failure: LOCAL_CANCELLED_REPLY },
      header: ['Scout', clock(AT), 'read aloud'],
      body: { reply: 'Two findings', working: null, failure: 'Interrupted' },
    },
    {
      name: 'a reply that failed before any text',
      turn: { ...DONE, reply: 'The model did not answer.', state: 'failed' },
      header: ['Scout', clock(AT), 'read aloud'],
      body: { reply: 'The model did not answer.', working: null, failure: null },
    },
    {
      name: 'a notice Scout posted on its own',
      turn: { ...DONE, id: 'notice:1', question: '', notice: true, reply: 'Area 3 of 8 done.' },
      header: ['Scout', clock(AT), 'read aloud'],
      body: { reply: 'Area 3 of 8 done.', working: null, failure: null },
    },
  ];

  for (const row of REPLIES) {
    it(`${row.name}: the header reads "Scout", the time and the read-aloud control at the right, with no glyph`, async () => {
      const { fixture } = await chatWith([row.turn]);
      const header = el(fixture, 'who-scout')!;
      const speaker = header.querySelector('sc-read-aloud-button');

      expect(
        Array.from(header.children).map((child) =>
          child.tagName === 'SC-READ-ALOUD-BUTTON' ? 'read aloud' : child.textContent?.trim(),
        ),
      ).toEqual(row.header);
      expect(header.children[1]?.getAttribute('data-test')).toBe(
        row.turn.notice === true ? 'arrived-at' : 'reply-time',
      );
      expect(header.querySelector('.avatar')).toBeNull();
      expect(
        Array.from(header.querySelectorAll('svg')).filter((svg) => !speaker?.contains(svg)),
      ).toEqual([]);
      expect(speaker === null ? null : declared(speaker, 'margin-left')).toBe(
        row.header.includes('read aloud') ? 'auto' : null,
      );
      expect({
        reply: el(fixture, 'reply')?.textContent?.trim() ?? null,
        working: el(fixture, 'working')?.textContent?.trim() ?? null,
        failure: el(fixture, 'failure')?.textContent?.trim() ?? null,
      }).toEqual(row.body);
    });
  }

  const RUNNING: readonly {
    readonly name: string;
    readonly turn: LocalTurn;
    readonly shown: readonly (readonly [string, string])[];
  }[] = [
    {
      name: 'still empty while Scout works',
      turn: { ...DONE, reply: '', state: 'running' },
      shown: [
        ['question', 'Review acme/shop#5 and run its tests'],
        ['working', 'Working on it'],
      ],
    },
    {
      name: 'saying what Scout is doing',
      turn: { ...DONE, reply: '', state: 'running', activity: 'Reading the repository' },
      shown: [
        ['question', 'Review acme/shop#5 and run its tests'],
        ['working', 'Reading the repository'],
      ],
    },
    {
      name: 'streaming its text',
      turn: { ...DONE, reply: 'Two findings', state: 'running', activity: 'Reading the diff' },
      shown: [
        ['question', 'Review acme/shop#5 and run its tests'],
        ['reply', 'Two findings'],
        ['working', 'Reading the diff'],
        ['read-aloud', ''],
      ],
    },
  ];

  for (const row of RUNNING) {
    it(`a running turn ${row.name} is drawn as the artboard draws it: below the approval strip, the question and a bare line with no card and no dots, announced politely`, async () => {
      const { fixture } = await chatWith([{ ...DONE, id: 'q-0' }, row.turn]);
      const page = fixture.nativeElement.querySelector('main.chat') as HTMLElement;
      const running = el(fixture, 'running-turn')!;

      expect({
        place: Array.from(page.children)
          .map((child) => child.getAttribute('data-test') ?? child.tagName.toLowerCase())
          .filter((name) => ['turns', 'sc-approval-strip', 'running-turn', 'form'].includes(name)),
        inList: texts(fixture, 'question').length,
        listed: Array.from(el(fixture, 'turns')!.querySelectorAll('[data-test="question"]')).map(
          (node) => node.textContent?.trim(),
        ),
        shown: Array.from(
          running.querySelectorAll(
            '[data-test="question"], [data-test="working"], [data-test="read-aloud"], [data-test="reply"]',
          ),
        ).map((node) => [node.getAttribute('data-test'), node.textContent?.trim() ?? '']),
        card: running.querySelector('[data-test="scout-reply"], [data-test="who-scout"]'),
        dots: running.querySelector('.dots'),
        live: running.getAttribute('aria-live'),
        name: running.getAttribute('aria-label'),
        line: [
          declared(el(fixture, 'working')!, 'font-size'),
          declared(el(fixture, 'working')!, 'color'),
        ],
      }).toEqual({
        place: ['turns', 'sc-approval-strip', 'running-turn', 'form'],
        inList: 2,
        listed: ['Review acme/shop#5 and run its tests'],
        shown: row.shown,
        card: null,
        dots: null,
        live: 'polite',
        name: 'Scout is answering',
        line: ['13px', 'var(--sc-fg-2)'],
      });
    });
  }

  it('a running turn that ends joins the conversation as a card, and the bare line leaves', async () => {
    const { fixture, bridge } = await chatWith([{ ...DONE, reply: '', state: 'running' }]);
    const before = el(fixture, 'running-turn') !== null;

    bridge.push([DONE]);
    await settle(fixture);

    expect({
      before,
      after: el(fixture, 'running-turn'),
      cards: texts(fixture, 'reply').map((t) => t.trim()),
      inList: el(fixture, 'turns')!.querySelector('[data-test="scout-reply"]') !== null,
    }).toEqual({
      before: true,
      after: null,
      cards: ['Two findings on acme/shop#5.'],
      inList: true,
    });
  });

  it('Reply "Scout not running": the sentence in the error colour and Start Scout as the artboard draws it, a 34 px button at the left', async () => {
    const { fixture } = await chatWith([
      {
        ...DONE,
        reply: NOT_RUNNING,
        state: 'failed',
        notRunning: true,
      },
    ]);
    const start = el(fixture, 'start-scout')!;

    expect({
      text: start.textContent?.trim(),
      reply: [
        el(fixture, 'reply')?.textContent?.trim(),
        el(fixture, 'reply')?.classList.contains('failed'),
      ],
      height: declared(start, 'height'),
      place: declared(start, 'align-self'),
      look: [
        declared(start, 'background'),
        declared(start, 'border'),
        declared(start, 'border-radius'),
        declared(start, 'color'),
      ],
    }).toEqual({
      text: 'Start Scout',
      reply: [NOT_RUNNING, true],
      height: '34px',
      place: 'flex-start',
      look: [
        'var(--sc-surface)',
        '1px solid var(--sc-line)',
        'var(--sc-radius-sm)',
        'var(--sc-fg)',
      ],
    });
  });

  it('the reply header sets "Scout" at 14 px and 600 over the 12 px muted time, and the speaker is a bare 26 px button with 6 px corners', async () => {
    const { fixture } = await chatWith([DONE]);
    const header = el(fixture, 'who-scout')!;
    const speak = el(fixture, 'read-aloud')!;

    expect({
      name: [
        declared(header.firstElementChild!, 'font-size'),
        declared(header.firstElementChild!, 'font-weight'),
      ],
      time: [declared(header, 'font-size'), declared(header, 'color')],
      speaker: [
        declared(speak, 'width'),
        declared(speak, 'height'),
        declared(speak, 'border'),
        declared(speak, 'border-radius'),
        declared(speak, 'background'),
        declared(speak, 'color'),
      ],
      label: speak.getAttribute('aria-label'),
    }).toEqual({
      name: ['14px', '600'],
      time: ['12px', 'var(--sc-muted)'],
      speaker: ['26px', '26px', '0', '6px', 'transparent', 'var(--sc-muted)'],
      label: 'Read this answer aloud',
    });
  });

  it('the composer is the bare row of the artboard: no card around it, a 40 px field, the talk button centred on it, and the keys line 16 px under it', async () => {
    const { fixture } = await chatWith([]);
    const input = el(fixture, 'input')!;
    const form = input.closest('form')!;
    const talk = fixture.nativeElement.querySelector('sc-talk-button') as HTMLElement;

    expect({
      card: ['background', 'border', 'padding', 'border-radius'].map((p) => declared(form, p)),
      gap: declared(form, 'gap'),
      field: [
        declared(input, 'padding'),
        declared(input, 'line-height'),
        declared(input, 'font-size'),
      ],
      talk: declared(talk, 'margin-bottom'),
      row: Array.from(form.firstElementChild!.children).map(
        (child) => child.getAttribute('data-test') ?? child.tagName.toLowerCase(),
      ),
      hint: el(fixture, 'composer-hint')?.textContent?.trim(),
    }).toEqual({
      card: [null, null, null, null],
      gap: '16px',
      field: ['9px 44px 9px 12px', '20px', '14px'],
      talk: '4px',
      row: ['field', 'sc-talk-button', 'send'],
      hint: 'Enter sends · Shift+Enter for a new line',
    });
    const field =
      /\.ask textarea \{([^}]*)\}/.exec(componentStyles('./chat.component.ts'))?.[1] ?? '';
    expect(field.lastIndexOf('line-height: 20px')).toBeGreaterThan(field.lastIndexOf('font:'));
  });

  it('Stop is drawn as the artboard draws it: a white square on the danger red, and off on the second surface while cards are queued', async () => {
    const { fixture } = await chatWith([{ ...DONE, reply: '', state: 'running' }]);
    const stop = el(fixture, 'stop')!;
    const on = {
      label: stop.getAttribute('aria-label'),
      background: declared(stop, 'background'),
      color: declared(stop, 'color'),
    };
    TestBed.resetTestingModule();
    const queuing = await chatWith([{ ...DONE, reply: '', state: 'running', issues: true }]);
    const off = el(queuing.fixture, 'stop') as HTMLButtonElement;

    expect({
      ...on,
      offBackground: off.disabled ? declared(off, 'background') : 'enabled',
      offColor: declared(off, 'color'),
    }).toEqual({
      label: 'Stop the answer',
      background: 'var(--sc-danger)',
      color: 'var(--sc-on-danger)',
      offBackground: 'var(--sc-surface-2)',
      offColor: 'var(--sc-muted)',
    });
  });

  const ANSWERS = [
    {
      state: 'Idle',
      turns: [DONE],
      hint: 'Enter sends · Shift+Enter for a new line',
      failures: [],
      working: [],
      stop: null,
      send: { disabled: false, title: 'Send' },
      clear: { disabled: false, title: 'Clear the conversation' },
    },
    {
      state: 'Answering',
      turns: [DONE, { ...DONE, id: 'q-2', reply: '', state: 'running' as const }],
      hint: 'A new question stops this answer · Stop ends it',
      failures: [],
      working: ['Working on it'],
      stop: { disabled: false, title: 'Stop the answer' },
      send: { disabled: false, title: 'Send' },
      clear: { disabled: false, title: 'Clear the conversation' },
    },
    {
      state: 'Stopped by a new question',
      turns: [
        {
          ...DONE,
          reply: 'Two findings',
          state: 'failed' as const,
          failure: LOCAL_CANCELLED_REPLY,
        },
        {
          ...DONE,
          id: 'q-2',
          question: 'Stop and review acme/shop#5',
          reply: '',
          state: 'running' as const,
          activity: 'Reading the pull request',
        },
      ],
      hint: 'A new question stops this answer · Stop ends it',
      failures: ['Interrupted'],
      working: ['Reading the pull request'],
      stop: { disabled: false, title: 'Stop the answer' },
      send: { disabled: false, title: 'Send' },
      clear: { disabled: false, title: 'Clear the conversation' },
    },
    {
      state: 'Queuing for approval',
      turns: [
        { ...DONE, id: 'post-1', reply: '', state: 'running' as const, issues: true as const },
      ],
      hint: 'Queuing cards; cannot be stopped.',
      failures: [],
      working: ['Working on it'],
      stop: { disabled: true, title: 'Queuing cards; cannot be stopped.' },
      send: { disabled: true, title: 'Queuing cards; cannot be stopped.' },
      clear: { disabled: true, title: 'Queuing cards; cannot be stopped.' },
    },
  ];

  for (const row of ANSWERS) {
    it(`Answer "${row.state}": the keys line, Stop, Send and Clear read as the artboard draws them`, async () => {
      const { fixture } = await chatWith(row.turns);
      const button = (test: string) => {
        const node = el(fixture, test) as HTMLButtonElement | null;
        return node === null ? null : { disabled: node.disabled, title: node.title };
      };

      expect({
        hint: el(fixture, 'composer-hint')?.textContent?.trim(),
        failures: texts(fixture, 'failure').map((t) => t.trim()),
        working: texts(fixture, 'working').map((t) => t.trim()),
        stop: button('stop'),
        send: button('send'),
        clear: button('clear'),
      }).toEqual({
        hint: row.hint,
        failures: row.failures,
        working: row.working,
        stop: row.stop,
        send: row.send,
        clear: row.clear,
      });
    });
  }

  for (const noSource of [false, true]) {
    it(`Conversation "Empty" ${noSource ? 'with no AI Source' : 'with an AI Source'}: the mark, the sentence, the suggestion, Clear off, and the field ${noSource ? 'off with "Connect an AI Source first", with the microphone, the voice conversation and Send off too' : 'on with "Ask Scout"'}`, async () => {
      const { fixture } = await chatWith([], { noSource });
      const input = el(fixture, 'input') as HTMLTextAreaElement;

      expect({
        mark: el(fixture, 'empty')?.querySelector('.empty-mark svg')?.tagName ?? null,
        text: el(fixture, 'empty-text')?.textContent?.trim(),
        suggestion: el(fixture, 'empty-suggestion')?.textContent?.trim(),
        clear: (el(fixture, 'clear') as HTMLButtonElement).disabled,
        placeholder: input.placeholder,
        field: input.disabled,
        mic: (el(fixture, 'mic') as HTMLButtonElement).disabled,
        send: (el(fixture, 'send') as HTMLButtonElement).disabled,
        talk: (el(fixture, 'talk') as HTMLButtonElement).disabled,
        talkTitle: (el(fixture, 'talk') as HTMLButtonElement).title,
      }).toEqual({
        mark: 'svg',
        text: 'Ask Scout anything, or hold the microphone button to talk.',
        suggestion: 'Try: list my open pull requests',
        clear: true,
        placeholder: noSource ? 'Connect an AI Source first' : 'Ask Scout',
        field: noSource,
        mic: noSource,
        send: noSource,
        talk: noSource,
        talkTitle: noSource ? 'Connect an AI Source first' : 'Start a voice conversation',
      });
    });
  }

  const VOICES: readonly {
    readonly state: string;
    readonly voice: [ConversationState, string, string | null];
    readonly label: string;
    readonly shown: readonly (readonly [string, string])[];
  }[] = [
    { state: 'Off', voice: ['off', '', null], label: 'Start a voice conversation', shown: [] },
    {
      state: 'Listening, partial shown',
      voice: ['listening', 'can you check the failing build on', null],
      label: 'End the voice conversation',
      shown: [
        ['talk-state', 'Listening'],
        ['talk-heard', 'can you check the failing build on'],
      ],
    },
    {
      state: 'Thinking',
      voice: ['thinking', '', null],
      label: 'End the voice conversation',
      shown: [['talk-state', 'Thinking']],
    },
    {
      state: 'Speaking',
      voice: ['speaking', '', null],
      label: 'End the voice conversation',
      shown: [['talk-state', 'Speaking']],
    },
    {
      state: 'Microphone taken',
      voice: ['off', '', 'Another listen took the microphone.'],
      label: 'Start a voice conversation',
      shown: [['talk-error', 'Another listen took the microphone.']],
    },
    {
      state: 'Stopped listening',
      voice: ['off', '', 'Scout stopped listening.'],
      label: 'Start a voice conversation',
      shown: [['talk-error', 'Scout stopped listening.']],
    },
  ];

  for (const row of VOICES) {
    it(`Voice conversation "${row.state}": beside the talk button, between the field and Send, the composer shows ${row.shown.length === 0 ? 'nothing' : row.shown.map(([, text]) => `"${text}"`).join(' then ')}`, async () => {
      const voice = fakeVoice(...row.voice);
      const { fixture } = await chatWith([DONE], { voice });
      const talk = el(fixture, 'talk')!;
      const line = talk.closest('form')!.firstElementChild!;
      const after = Array.from(
        line.querySelectorAll(
          '[data-test="talk-state"], [data-test="talk-heard"], [data-test="talk-error"]',
        ),
      ) as HTMLElement[];

      expect({
        pressed: talk.getAttribute('aria-pressed'),
        label: talk.getAttribute('aria-label'),
        shown: after.map((node) => [node.getAttribute('data-test'), node.textContent?.trim()]),
        alert: after
          .filter((node) => node.getAttribute('data-test') === 'talk-error')
          .map((node) => node.getAttribute('role')),
        order: Array.from(line.children).map(
          (child) => child.getAttribute('data-test') ?? child.tagName.toLowerCase(),
        ),
      }).toEqual({
        pressed: String(row.voice[0] !== 'off'),
        label: row.label,
        shown: row.shown,
        alert: row.shown.some(([test]) => test === 'talk-error') ? ['alert'] : [],
        order: ['field', 'sc-talk-button', 'send'],
      });
    });
  }
});

/** The `styles` of a component source file: everything from its `styles:` key to the end of the decorator. */
function componentStyles(relative: string): string {
  const source = readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
  const start = source.indexOf('  styles:');
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  return source.slice(start, source.indexOf('\n})\n', start));
}

const COLOUR_LITERAL = /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/g;

/** The rules of the GitHub preview, which keeps GitHub's light greys in either theme as approved. */
const GITHUB_PREVIEW_RULE = /\n {4}\.gh[^{}]*\{[^{}]*\}/g;

function literals(css: string): string[] {
  return Array.from(css.matchAll(COLOUR_LITERAL), (match) => match[0]);
}

describe('the chat follows the dark and light theme', () => {
  for (const [part, styles] of [
    ['the Chat screen', () => componentStyles('./chat.component.ts')],
    ['the turns and the rendered reply', () => REPLY_STYLES],
    [
      'the review findings and verdict cards, outside the GitHub preview,',
      () =>
        componentStyles('../core/review-findings.component.ts').replace(GITHUB_PREVIEW_RULE, ''),
    ],
  ] as const) {
    it(`${part} paints only with the theme tokens, no hex, rgb() or hsl() colour left`, () => {
      const css = styles();

      expect(css).toContain('var(--sc-');
      expect(literals(css)).toEqual([]);
    });
  }

  it("the GitHub preview keeps GitHub's light look in either theme", () => {
    const preview = /\n {4}\.gh \{([^}]*)\}/.exec(
      componentStyles('../core/review-findings.component.ts'),
    )?.[1];

    expect(preview).toContain('color-scheme: light;');
    expect(preview).toContain('background: #fff;');
  });

  it('code in a reply uses the code background and the mono font of the theme', () => {
    const code = /\.reply pre \{([^}]*)\}/.exec(REPLY_STYLES)?.[1] ?? '';

    expect(code).toContain('background: var(--sc-code-bg);');
    expect(code).toContain('font-family: var(--sc-mono);');
  });
});
