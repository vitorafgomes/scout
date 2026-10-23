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

const CONTINUATION = /^(\s|--- End of |$)/;

/**
 * Derives the level of each line in a sequence of worker output lines.
 *
 * The .NET console formatter writes the level once, on a header line, and
 * indents the message, the exception and its stack frames beneath it. A
 * continuation line (indented, blank, or a `--- End of ...` stack marker)
 * therefore takes the level of the most recent header from the same stream,
 * and falls back to its own content when no header precedes it.
 */
export function detectLevels<T extends { readonly kind: string; readonly line: string }>(
  lines: readonly T[],
): { readonly event: T; readonly level: LogLevel }[] {
  const lastHeaderLevel = new Map<string, LogLevel>();
  return lines.map((event) => {
    if (CONTINUATION.test(event.line)) {
      return { event, level: lastHeaderLevel.get(event.kind) ?? detectLevel(event.line) };
    }
    const level = detectLevel(event.line);
    lastHeaderLevel.set(event.kind, level);
    return { event, level };
  });
}
