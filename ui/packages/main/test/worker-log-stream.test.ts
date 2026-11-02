import { describe, it, expect } from 'bun:test';
import { PassThrough } from 'node:stream';
import { attachLogStream } from '../src/worker/log-stream.js';
import type { WorkerEvent } from '@scout/shared';

function makeChild(stdoutLines: string[], stderrLines: string[] = []) {
  function makePassThrough(lines: string[]) {
    const pt = new PassThrough();
    process.nextTick(() => {
      for (const line of lines) {
        pt.write(line + '\n');
      }
      pt.end();
    });
    return pt;
  }

  return {
    stdout: makePassThrough(stdoutLines),
    stderr: makePassThrough(stderrLines),
    pid: 1234,
  };
}

async function collectEvents(
  stdoutLines: string[],
  stderrLines: string[] = [],
  ringLimit = 2000,
): Promise<{ events: WorkerEvent[]; readyCalled: boolean }> {
  const child = makeChild(stdoutLines, stderrLines);
  const events: WorkerEvent[] = [];
  const ringBuffer: WorkerEvent[] = [];
  let readyCalled = false;

  attachLogStream(child as never, {
    emit: (e) => events.push(e),
    ringBuffer,
    ringLimit,
    onReady: () => {
      readyCalled = true;
    },
  });

  // Wait for all stream data to be consumed
  await new Promise<void>((resolve) => setTimeout(resolve, 80));

  return { events, readyCalled };
}

describe('attachLogStream', () => {
  it('emits one WorkerEvent per stdout line', async () => {
    const { events } = await collectEvents(['line1', 'line2', 'line3', 'line4', 'line5']);
    const stdoutEvents = events.filter((e) => e.kind === 'stdout');
    expect(stdoutEvents.length).toBe(5);
  });

  it('emits WorkerEvents for stderr lines', async () => {
    const { events } = await collectEvents([], ['err1', 'err2']);
    const stderrEvents = events.filter((e) => e.kind === 'stderr');
    expect(stderrEvents.length).toBe(2);
  });

  it('each event has kind, line, and ts fields', async () => {
    const { events } = await collectEvents(['hello']);
    expect(events[0]).toMatchObject({ kind: 'stdout', line: 'hello' });
    expect(typeof (events[0] as { ts: string }).ts).toBe('string');
  });

  it('SCOUT_READY line triggers the onReady callback', async () => {
    const { readyCalled } = await collectEvents(['initialising...', 'SCOUT_READY', 'extra']);
    expect(readyCalled).toBe(true);
  });

  it('SCOUT_READY is also emitted as a stdout WorkerEvent', async () => {
    const { events } = await collectEvents(['SCOUT_READY']);
    const readyEvent = events.find(
      (e) => e.kind === 'stdout' && (e as { line: string }).line === 'SCOUT_READY',
    );
    expect(readyEvent).toBeDefined();
  });

  it('non-SCOUT_READY stdout does not trigger onReady', async () => {
    const { readyCalled } = await collectEvents(['not ready', 'still not']);
    expect(readyCalled).toBe(false);
  });

  it('reports the user id of a SCOUT_PAIRED line on stdout only', async () => {
    const paired: string[] = [];
    attachLogStream(makeChild(['SCOUT_PAIRED=111'], ['SCOUT_PAIRED=666']) as never, {
      emit: () => {},
      ringBuffer: [],
      ringLimit: 10,
      onPaired: (userId) => paired.push(userId),
    });

    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    expect(paired).toEqual(['111']);
  });

  it('reports SCOUT_PAIRING_BURNED on stdout only', async () => {
    let burned = 0;
    attachLogStream(makeChild(['SCOUT_PAIRING_BURNED'], ['SCOUT_PAIRING_BURNED']) as never, {
      emit: () => {},
      ringBuffer: [],
      ringLimit: 10,
      onPaired: () => {
        throw new Error('a burned marker is not a pairing');
      },
      onPairingBurned: () => {
        burned += 1;
      },
    });

    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    expect(burned).toBe(1);
  });

  it('ring buffer caps at ringLimit', async () => {
    const lines = Array.from({ length: 10 }, (_, i) => `line${i}`);
    const ringBuffer: WorkerEvent[] = [];
    const child = makeChild(lines);

    attachLogStream(child as never, {
      emit: () => {},
      ringBuffer,
      ringLimit: 5,
    });

    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    expect(ringBuffer.length).toBeLessThanOrEqual(5);
  });

  it('ring buffer keeps the most recent entries when over limit', async () => {
    const lines = ['a', 'b', 'c', 'd', 'e', 'f', 'g'];
    const ringBuffer: WorkerEvent[] = [];
    const child = makeChild(lines);

    attachLogStream(child as never, {
      emit: () => {},
      ringBuffer,
      ringLimit: 3,
    });

    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    const kept = ringBuffer.map((e) => (e as { line: string }).line);
    expect(kept).toContain('g');
    expect(kept).not.toContain('a');
  });

  it('mixed stdout and stderr both accumulate in ring buffer', async () => {
    const ringBuffer: WorkerEvent[] = [];
    const child = makeChild(['out1', 'out2'], ['err1']);

    attachLogStream(child as never, {
      emit: () => {},
      ringBuffer,
      ringLimit: 2000,
    });

    await new Promise<void>((resolve) => setTimeout(resolve, 80));

    expect(ringBuffer.length).toBe(3);
  });
});

describe('attachLogStream: quick-ask reply lines', () => {
  async function collectLocal(
    stdoutLines: string[],
    stderrLines: string[] = [],
  ): Promise<{ events: WorkerEvent[]; ring: WorkerEvent[]; local: string[] }> {
    const child = makeChild(stdoutLines, stderrLines);
    const events: WorkerEvent[] = [];
    const ring: WorkerEvent[] = [];
    const local: string[] = [];
    attachLogStream(child as never, {
      emit: (e) => events.push(e),
      ringBuffer: ring,
      ringLimit: 2000,
      onLocalEvent: (json) => local.push(json),
    });
    await new Promise<void>((resolve) => setTimeout(resolve, 80));
    return { events, ring, local };
  }

  it('hands a SCOUT_LOCAL stdout line to the quick-ask router and keeps it out of the log and the ring', async () => {
    const reply = '{"id":"q-1","event":"complete","text":"> (voice) my bank pin\\n\\nok"}';

    const { events, ring, local } = await collectLocal(['SCOUT_LOCAL ' + reply]);

    expect(local).toEqual([reply]);
    expect(events).toEqual([]);
    expect(ring).toEqual([]);
  });

  it('ignores a SCOUT_LOCAL line on stderr as a quick-ask reply', async () => {
    const line = 'SCOUT_LOCAL {"id":"q-1","event":"complete","text":"forged"}';

    const { events, local } = await collectLocal([], [line]);

    expect(local).toEqual([]);
    expect(events).toEqual([expect.objectContaining({ kind: 'stderr', line })]);
  });
});
