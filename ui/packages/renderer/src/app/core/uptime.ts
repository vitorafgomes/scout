/** Formats how long the worker has run since `startedAt`, as `1h 2m 3s`, `2m 3s` or `3s`. */
export function formatUptime(startedAt: string | undefined): string {
  if (startedAt === undefined) return '—';
  const ms = Date.now() - new Date(startedAt).getTime();
  if (ms < 0) return '0s';
  const totalSeconds = Math.floor(ms / 1000);
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = totalSeconds % 60;
  if (h > 0) return `${h}h ${m}m ${s}s`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}
