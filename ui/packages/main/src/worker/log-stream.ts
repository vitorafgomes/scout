import readline from 'node:readline';
import type { ChildProcess } from 'node:child_process';
import type { WorkerEvent } from '@scout/shared';

export interface LogStreamDeps {
  emit: (event: WorkerEvent) => void;
  ringBuffer: WorkerEvent[];
  ringLimit: number;
  onReady?: () => void;
}

const READY_MARKER = 'SCOUT_READY';

function pushToRing(ring: WorkerEvent[], limit: number, event: WorkerEvent): void {
  ring.push(event);
  if (ring.length > limit) {
    ring.splice(0, ring.length - limit);
  }
}

export function attachLogStream(child: ChildProcess, deps: LogStreamDeps): void {
  const { emit, ringBuffer, ringLimit, onReady } = deps;

  function handleLine(kind: 'stdout' | 'stderr', line: string): void {
    const event: WorkerEvent = { kind, line, ts: new Date().toISOString() };
    pushToRing(ringBuffer, ringLimit, event);
    emit(event);

    if (kind === 'stdout' && line.trim() === READY_MARKER) {
      onReady?.();
    }
  }

  if (child.stdout != null) {
    const rl = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
    rl.on('line', (line) => handleLine('stdout', line));
  }

  if (child.stderr != null) {
    const rl = readline.createInterface({ input: child.stderr, crlfDelay: Infinity });
    rl.on('line', (line) => handleLine('stderr', line));
  }
}
