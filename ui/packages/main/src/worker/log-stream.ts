import readline from 'node:readline';
import type { ChildProcess } from 'node:child_process';
import type { WorkerEvent } from '@scout/shared';

export interface LogStreamDeps {
  emit: (event: WorkerEvent) => void;
  ringBuffer: WorkerEvent[];
  ringLimit: number;
  onReady?: () => void;
  onHttpEndpoint?: (endpoint: string) => void;
  onPaired?: (userId: string) => void;
  onPairingBurned?: () => void;
  /** Called with the JSON of each `SCOUT_LOCAL ` line on stdout; those lines never reach the log. */
  onLocalEvent?: (json: string) => void;
}

const READY_MARKER = 'SCOUT_READY';
const HTTP_MARKER_PREFIX = 'SCOUT_HTTP=';
const PAIRED_MARKER_PREFIX = 'SCOUT_PAIRED=';
const PAIRING_BURNED_MARKER = 'SCOUT_PAIRING_BURNED';
const LOCAL_EVENT_PREFIX = 'SCOUT_LOCAL ';

function pushToRing(ring: WorkerEvent[], limit: number, event: WorkerEvent): void {
  ring.push(event);
  if (ring.length > limit) {
    ring.splice(0, ring.length - limit);
  }
}

export function attachLogStream(child: ChildProcess, deps: LogStreamDeps): void {
  const { emit, ringBuffer, ringLimit, onReady } = deps;

  function handleLine(kind: 'stdout' | 'stderr', line: string): void {
    if (kind === 'stdout' && line.startsWith(LOCAL_EVENT_PREFIX)) {
      deps.onLocalEvent?.(line.slice(LOCAL_EVENT_PREFIX.length));
      return;
    }

    const event: WorkerEvent = { kind, line, ts: new Date().toISOString() };
    pushToRing(ringBuffer, ringLimit, event);
    emit(event);

    if (kind === 'stdout') {
      const trimmed = line.trim();
      if (trimmed === READY_MARKER) {
        onReady?.();
      } else if (trimmed.startsWith(HTTP_MARKER_PREFIX)) {
        const host = trimmed.slice(HTTP_MARKER_PREFIX.length).trim();
        if (host.length > 0) {
          deps.onHttpEndpoint?.(`http://${host}`);
        }
      } else if (trimmed.startsWith(PAIRED_MARKER_PREFIX)) {
        deps.onPaired?.(trimmed.slice(PAIRED_MARKER_PREFIX.length));
      } else if (trimmed === PAIRING_BURNED_MARKER) {
        deps.onPairingBurned?.();
      }
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
