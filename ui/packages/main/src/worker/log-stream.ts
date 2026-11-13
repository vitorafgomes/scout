import readline from 'node:readline';
import type { ChildProcess } from 'node:child_process';
import type { WorkerEvent } from '@scout/shared';

export interface LogStreamDeps {
  /** Receives each output line once; the supervisor keeps it in its ring and forwards it. */
  emit: (event: WorkerEvent) => void;
  onReady?: () => void;
  onPaired?: (userId: string) => void;
  onPairingBurned?: () => void;
  /** Called with the JSON of each `SCOUT_LOCAL ` line on stdout; those lines never reach the log. */
  onLocalEvent?: (json: string) => void;
}

const READY_MARKER = 'SCOUT_READY';
const PAIRED_MARKER_PREFIX = 'SCOUT_PAIRED=';
const PAIRING_BURNED_MARKER = 'SCOUT_PAIRING_BURNED';
const LOCAL_EVENT_PREFIX = 'SCOUT_LOCAL ';

export function attachLogStream(child: ChildProcess, deps: LogStreamDeps): void {
  const { emit, onReady } = deps;

  function handleLine(kind: 'stdout' | 'stderr', line: string): void {
    if (kind === 'stdout' && line.startsWith(LOCAL_EVENT_PREFIX)) {
      deps.onLocalEvent?.(line.slice(LOCAL_EVENT_PREFIX.length));
      return;
    }

    const event: WorkerEvent = { kind, line, ts: new Date().toISOString() };
    emit(event);

    if (kind === 'stdout') {
      const trimmed = line.trim();
      if (trimmed === READY_MARKER) {
        onReady?.();
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
