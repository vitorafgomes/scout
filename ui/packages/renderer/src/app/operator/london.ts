const LONDON = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Europe/London',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

const LOCAL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

function wallClock(utcMs: number): Record<string, string> {
  return Object.fromEntries(LONDON.formatToParts(new Date(utcMs)).map((p) => [p.type, p.value]));
}

function offsetMs(utcMs: number): number {
  const p = wallClock(utcMs);
  const minute = Math.floor(utcMs / 60_000) * 60_000;
  return Date.UTC(+p['year']!, +p['month']! - 1, +p['day']!, +p['hour']!, +p['minute']!) - minute;
}

export function londonToUtcIso(local: string): string | null {
  const m = LOCAL.exec(local);
  if (m === null) return null;
  const wall = Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!, +m[4]!, +m[5]!);
  if (!Number.isFinite(wall)) return null;
  const guess = wall - offsetMs(wall);
  return new Date(wall - offsetMs(guess)).toISOString();
}

export function formatLondon(iso: string): string {
  const p = wallClock(Date.parse(iso));
  return `${p['year']}-${p['month']}-${p['day']} ${p['hour']}:${p['minute']}`;
}
