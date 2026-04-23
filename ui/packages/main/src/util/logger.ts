export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface Logger {
  debug(msg: string, ctx?: Record<string, unknown>): void;
  info(msg: string, ctx?: Record<string, unknown>): void;
  warn(msg: string, ctx?: Record<string, unknown>): void;
  error(msg: string, err?: unknown, ctx?: Record<string, unknown>): void;
  child(bindings: Record<string, unknown>): Logger;
}

const LEVEL_RANK: Record<LogLevel, number> = {
  debug: 0,
  info: 1,
  warn: 2,
  error: 3,
};

function resolveLevel(opts?: { level?: LogLevel }): LogLevel {
  if (opts?.level != null) return opts.level;
  const env = process.env['SCOUT_LOG_LEVEL'];
  if (env === 'debug' || env === 'info' || env === 'warn' || env === 'error') {
    return env;
  }
  return 'info';
}

function serializeErr(err: unknown): Record<string, string> | undefined {
  if (err instanceof Error) {
    return { name: err.name, message: err.message };
  }
  return undefined;
}

export function createLogger(
  name: string,
  opts?: { level?: LogLevel; sink?: (line: string) => void },
): Logger {
  const minLevel = resolveLevel(opts);
  const sink = opts?.sink ?? ((line: string) => process.stderr.write(line + '\n'));

  function emit(lvl: LogLevel, msg: string, ctx: Record<string, unknown>, err?: unknown): void {
    if (LEVEL_RANK[lvl] < LEVEL_RANK[minLevel]) return;

    const entry: Record<string, unknown> = {
      ts: new Date().toISOString(),
      lvl,
      name,
      msg,
    };

    const errData = serializeErr(err);
    if (errData != null) {
      entry['err'] = errData;
    }

    if (Object.keys(ctx).length > 0) {
      entry['ctx'] = ctx;
    }

    sink(JSON.stringify(entry));
  }

  function makeLogger(bindings: Record<string, unknown>): Logger {
    return {
      debug(msg, ctx = {}) {
        emit('debug', msg, { ...bindings, ...ctx });
      },
      info(msg, ctx = {}) {
        emit('info', msg, { ...bindings, ...ctx });
      },
      warn(msg, ctx = {}) {
        emit('warn', msg, { ...bindings, ...ctx });
      },
      error(msg, err?, ctx = {}) {
        emit('error', msg, { ...bindings, ...ctx }, err);
      },
      child(newBindings) {
        return makeLogger({ ...bindings, ...newBindings });
      },
    };
  }

  return makeLogger({});
}
