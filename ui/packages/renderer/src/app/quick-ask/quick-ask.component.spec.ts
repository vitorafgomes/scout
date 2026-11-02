import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalAskRequest, LocalEvent, LocalTurn } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { QuickAskComponent } from './quick-ask.component';

const NOT_RUNNING = 'Scout is not running. Start it from the tray or the app.';

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
        turns.map((turn) =>
          turn.id !== event.id || turn.state !== 'running' || event.event === 'begin'
            ? turn
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
    localClearTurns: vi.fn(async (): Promise<void> => undefined),
    localAsk,
    localHide: vi.fn(async (): Promise<void> => undefined),
    localCancel: vi.fn(async (_req: { id: string }): Promise<void> => undefined),
    appOpenExternal: vi.fn(async (): Promise<void> => undefined),
  };
}

class FakeProcessor {
  onaudioprocess: ((event: unknown) => void) | null = null;
  connect = vi.fn();
  disconnect = vi.fn();
  hear(samples: number[]): void {
    this.onaudioprocess?.({ inputBuffer: { getChannelData: () => Float32Array.from(samples) } });
  }
}

const audio = {
  processor: null as FakeProcessor | null,
  contextRate: 0,
  closed: false,
  trackStopped: false,
};

class FakeAudioContext {
  readonly sampleRate: number;
  readonly destination = {};
  constructor(options: { sampleRate: number }) {
    this.sampleRate = options.sampleRate;
    audio.contextRate = options.sampleRate;
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
  async close(): Promise<void> {
    audio.closed = true;
  }
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<QuickAskComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(QuickAskComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<QuickAskComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<QuickAskComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

async function key(
  fixture: ComponentFixture<QuickAskComponent>,
  type: 'keydown' | 'keyup',
  init: KeyboardEventInit,
): Promise<void> {
  window.dispatchEvent(new KeyboardEvent(type, { ...init, cancelable: true }));
  await settle(fixture);
}

function region(node: Element): string | null {
  const css = Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
  let value: string | null = null;
  for (const [, selector, body] of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const declared = /-webkit-app-region:\s*([\w-]+)/.exec(body!)?.[1];
    if (declared !== undefined && node.matches(selector!.trim())) value = declared;
  }
  return value;
}

function texts(fixture: ComponentFixture<QuickAskComponent>, test: string): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(`[data-test="${test}"]`) as NodeListOf<HTMLElement>,
  ).map((node) => node.textContent ?? '');
}

async function askTyped(fixture: ComponentFixture<QuickAskComponent>, text: string): Promise<void> {
  const input = el(fixture, 'question') as HTMLInputElement;
  input.value = text;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
  (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
    new Event('submit', { cancelable: true }),
  );
  await settle(fixture);
}

describe('Quick ask', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    audio.processor = null;
    audio.contextRate = 0;
    audio.closed = false;
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

  it('sends a typed question, shows it as a turn and streams the answer under it', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await askTyped(fixture, '  is the nightly build green?  ');
    expect(bridge.localAsk).toHaveBeenCalledTimes(1);
    const request = bridge.localAsk.mock.calls[0]![0] as { id: string; text: string };
    bridge.emit({ id: request.id, event: 'begin' });
    bridge.emit({ id: request.id, event: 'update', text: 'It is' });
    await settle(fixture);
    const partial = texts(fixture, 'reply');
    bridge.emit({
      id: request.id,
      event: 'complete',
      text: 'It is green.\nAll 42 jobs passed.',
      citations: [{ label: 'run 42', url: 'https://github.com/acme/ci/actions/runs/42' }],
    });
    await settle(fixture);

    expect(request.text).toBe('is the nightly build green?');
    expect(texts(fixture, 'asked')).toEqual(['is the nightly build green?']);
    expect(partial).toEqual(['It is']);
    expect(texts(fixture, 'reply')).toEqual(['It is green.\nAll 42 jobs passed.']);
    const link = el(fixture, 'citations')?.querySelector('a') as HTMLAnchorElement;
    expect(link.textContent?.trim()).toBe('run 42');
    link.click();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://github.com/acme/ci/actions/runs/42',
    });
  });

  it('cancels a question still being answered when the next one is asked', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await askTyped(fixture, 'first question');
    const first = bridge.localAsk.mock.calls[0]![0] as { id: string };

    await askTyped(fixture, 'second question');

    expect(bridge.localCancel).toHaveBeenCalledExactlyOnceWith({ id: first.id });
  });

  it('does not cancel a question that already completed', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await askTyped(fixture, 'first question');
    const first = bridge.localAsk.mock.calls[0]![0] as { id: string };
    bridge.emit({ id: first.id, event: 'complete', text: 'done' });

    await askTyped(fixture, 'second question');

    expect(bridge.localCancel).not.toHaveBeenCalled();
  });

  it('keeps earlier questions and answers on screen when the next one is asked', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await askTyped(fixture, 'which pods restarted?');
    const first = bridge.localAsk.mock.calls[0]![0] as { id: string };
    bridge.emit({ id: first.id, event: 'complete', text: 'Two pods restarted.' });

    await askTyped(fixture, 'why?');
    const second = bridge.localAsk.mock.calls[1]![0] as { id: string };
    bridge.emit({ id: second.id, event: 'update', text: 'OOMKilled' });
    await settle(fixture);

    expect(texts(fixture, 'asked')).toEqual(['which pods restarted?', 'why?']);
    expect(texts(fixture, 'reply')).toEqual(['Two pods restarted.', 'OOMKilled']);
  });

  it('shows the turns pushed from main, including ones asked on the Chat screen', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    bridge.push([
      {
        id: 'asked-on-chat-screen',
        question: 'is prod green?',
        reply: 'Prod is green.',
        citations: [],
        state: 'done',
      },
    ]);
    await settle(fixture);

    expect(bridge.localAsk).not.toHaveBeenCalled();
    expect(texts(fixture, 'asked')).toEqual(['is prod green?']);
    expect(texts(fixture, 'reply')).toEqual(['Prod is green.']);
  });

  it('opens on the conversation main already holds', async () => {
    const bridge = makeBridge();
    bridge.localGetTurns.mockResolvedValueOnce([
      { id: 'earlier', question: 'hello', reply: 'Hi.', citations: [], state: 'done' },
    ]);

    const fixture = await render(bridge);

    expect(texts(fixture, 'asked')).toEqual(['hello']);
    expect(texts(fixture, 'reply')).toEqual(['Hi.']);
  });

  it("a voice turn shows what Scout heard from the reply's (voice) quote", async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await key(fixture, 'keydown', { key: ' ', code: 'Space' });
    audio.processor!.hear([0.5, -0.5]);
    await key(fixture, 'keyup', { key: ' ', code: 'Space' });
    const request = bridge.localAsk.mock.calls[0]![0] as { id: string };

    bridge.emit({
      id: request.id,
      event: 'complete',
      text: '> (voice) is the nightly build green?\n\nIt is green.',
    });
    await settle(fixture);

    expect(texts(fixture, 'asked')).toEqual(['(voice message)']);
    expect(texts(fixture, 'reply')).toEqual([
      '> (voice) is the nightly build green?\n\nIt is green.',
    ]);
  });

  it('shows "Scout is not running" as the turn\'s reply when the worker is stopped, with no second error line', async () => {
    const bridge = makeBridge();
    bridge.refuseNextAsNotRunning();
    const fixture = await render(bridge);

    await askTyped(fixture, 'hi');

    expect(texts(fixture, 'asked')).toEqual(['hi']);
    expect(texts(fixture, 'reply')).toEqual([NOT_RUNNING]);
    expect(el(fixture, 'error')).toBeNull();
  });

  it('shows a refusal that never became a turn on the error line', async () => {
    const bridge = makeBridge();
    bridge.localAsk.mockRejectedValueOnce(
      new Error('[scout:local/invalid-request] Type a question of 1 to 4000 characters.'),
    );
    const fixture = await render(bridge);

    await askTyped(fixture, 'hi');

    expect(el(fixture, 'error')?.textContent?.trim()).toBe(
      'Type a question of 1 to 4000 characters.',
    );
    expect(texts(fixture, 'reply')).toEqual([]);
  });

  it('records while Space is held and sends the recording as 16 kHz mono 16-bit WAV', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await key(fixture, 'keydown', { key: ' ', code: 'Space' });
    const listening = el(fixture, 'recording')?.textContent?.trim();
    audio.processor!.hear([0.5, -0.5]);
    await key(fixture, 'keyup', { key: ' ', code: 'Space' });

    expect(listening).toBe('Listening. Release to send.');
    expect(audio.contextRate).toBe(16_000);
    expect(bridge.localAsk).toHaveBeenCalledTimes(1);
    const request = bridge.localAsk.mock.calls[0]![0] as {
      id: string;
      audio: { contentType: string; base64: string };
    };
    expect(request.audio.contentType).toBe('audio/wav');
    const wav = Uint8Array.from(atob(request.audio.base64), (c) => c.charCodeAt(0));
    const view = new DataView(wav.buffer);
    expect(String.fromCharCode(...wav.subarray(0, 4))).toBe('RIFF');
    expect(String.fromCharCode(...wav.subarray(8, 12))).toBe('WAVE');
    expect(view.getUint16(20, true)).toBe(1);
    expect(view.getUint16(22, true)).toBe(1);
    expect(view.getUint32(24, true)).toBe(16_000);
    expect(view.getUint16(34, true)).toBe(16);
    expect(view.getUint32(40, true)).toBe(4);
    expect([view.getInt16(44, true), view.getInt16(46, true)]).toEqual([16383, -16384]);
    expect(audio.trackStopped).toBe(true);
    expect(audio.closed).toBe(true);
    expect(el(fixture, 'recording')).toBeNull();
  });

  it('types a space instead of recording when the question box has text', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    const input = el(fixture, 'question') as HTMLInputElement;
    input.value = 'is prod';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);

    await key(fixture, 'keydown', { key: ' ', code: 'Space' });

    expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled();
    expect(el(fixture, 'recording')).toBeNull();
  });

  it('shows a clear message when the microphone cannot be used', async () => {
    const bridge = makeBridge();
    vi.mocked(navigator.mediaDevices.getUserMedia).mockRejectedValueOnce(
      new DOMException('Permission denied', 'NotAllowedError'),
    );
    const fixture = await render(bridge);

    await key(fixture, 'keydown', { key: ' ', code: 'Space' });

    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(el(fixture, 'error')?.textContent?.trim()).toBe('Scout could not use the microphone.');
    });
    expect(el(fixture, 'recording')).toBeNull();
  });

  it('a quick press of the microphone button leaves no live microphone and sends nothing', async () => {
    const bridge = makeBridge();
    let grant: (stream: unknown) => void = () => undefined;
    vi.mocked(navigator.mediaDevices.getUserMedia).mockImplementationOnce(
      () => new Promise((resolve) => (grant = resolve as (stream: unknown) => void)) as never,
    );
    const fixture = await render(bridge);
    const mic = el(fixture, 'mic') as HTMLButtonElement;

    mic.dispatchEvent(new Event('pointerdown'));
    mic.dispatchEvent(new Event('pointerup'));
    await vi.waitFor(() => expect(navigator.mediaDevices.getUserMedia).toHaveBeenCalledTimes(1));
    grant({
      getTracks: () => [
        {
          stop: () => {
            audio.trackStopped = true;
          },
        },
      ],
    });
    await settle(fixture);

    expect(audio.trackStopped).toBe(true);
    expect(audio.closed).toBe(true);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('losing focus while Space is held discards the recording and sends nothing', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await key(fixture, 'keydown', { key: ' ', code: 'Space' });
    audio.processor!.hear([0.5, -0.5]);

    window.dispatchEvent(new Event('blur'));
    await settle(fixture);
    await key(fixture, 'keyup', { key: ' ', code: 'Space' });

    expect(audio.trackStopped).toBe(true);
    expect(el(fixture, 'recording')).toBeNull();
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('the quick-ask window has a grip to drag it', async () => {
    const fixture = await render(makeBridge());

    const grip = el(fixture, 'grip');

    expect(grip?.textContent?.trim()).toBe('Scout');
    expect(region(grip!)).toBe('drag');
    const controls = Array.from(
      fixture.nativeElement.querySelectorAll('input, button') as NodeListOf<HTMLElement>,
    );
    expect(controls.map(region)).toEqual(controls.map(() => 'no-drag'));
  });

  it('hides the window on Escape', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    await key(fixture, 'keydown', { key: 'Escape', code: 'Escape' });

    expect(bridge.localHide).toHaveBeenCalledTimes(1);
  });

  it('Escape while holding Space stops the microphone and sends nothing', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await key(fixture, 'keydown', { key: ' ', code: 'Space' });

    await key(fixture, 'keydown', { key: 'Escape', code: 'Escape' });

    expect(bridge.localHide).toHaveBeenCalledTimes(1);
    expect(audio.trackStopped).toBe(true);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });

  it('hides the window from the Close button', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    await key(fixture, 'keydown', { key: ' ', code: 'Space' });

    el(fixture, 'close')!.click();
    await settle(fixture);

    expect(bridge.localHide).toHaveBeenCalledTimes(1);
    expect(audio.trackStopped).toBe(true);
    expect(bridge.localAsk).not.toHaveBeenCalled();
  });
});
