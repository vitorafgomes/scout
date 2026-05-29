export type LogLevel = 'info' | 'warn' | 'error' | 'debug';

/**
 * Derives the log level from a worker output line.
 *
 * The .NET worker host routes every level to stderr by design (to keep stdout
 * clean for the SCOUT_READY marker), so the stream is not a severity signal.
 * The level must be read from the line content. The default console formatter
 * prefixes lines with `info:`, `warn:`, `fail:`, `crit:`, `dbug:`, `trce:`.
 */
export function detectLevel(line: string): LogLevel {
  const upper = line.toUpperCase();
  if (upper.includes('ERROR') || upper.includes('FAIL') || upper.includes('CRIT')) return 'error';
  if (upper.includes('WARN')) return 'warn';
  if (
    upper.includes('DEBUG') ||
    upper.includes('TRACE') ||
    upper.includes('DBUG') ||
    upper.includes('TRCE')
  ) {
    return 'debug';
  }
  return 'info';
}
