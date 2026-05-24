type Level = 'debug' | 'info' | 'warn' | 'error';

export interface Logger {
  debug(msg: string, data?: unknown): void;
  info(msg: string, data?: unknown): void;
  warn(msg: string, data?: unknown): void;
  error(msg: string, data?: unknown): void;
}

export function logger(scope: string): Logger {
  const emit = (level: Level, msg: string, data?: unknown): void => {
    const line = `[anchor:${scope}] ${msg}`;
    if (data !== undefined) {
      console[level](line, data);
    } else {
      console[level](line);
    }
  };
  return {
    debug: (msg, data) => emit('debug', msg, data),
    info: (msg, data) => emit('info', msg, data),
    warn: (msg, data) => emit('warn', msg, data),
    error: (msg, data) => emit('error', msg, data),
  };
}
