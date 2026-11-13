import { describe, it, expect } from 'bun:test';
import { detect } from '../src/detect/windows.js';
import type { DetectDeps, ExecResult } from '../src/detect/types.js';

function makeExecOk(stdout = ''): () => Promise<ExecResult> {
  return () => Promise.resolve({ stdout, stderr: '', exitCode: 0 });
}

function makeExecFail(): () => Promise<ExecResult> {
  return () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
}

function makeDeps(overrides: Partial<DetectDeps>): DetectDeps {
  return {
    exists: () => Promise.resolve(false),
    exec: makeExecFail(),
    readFile: () => Promise.resolve(''),
    env: (name) => (name === 'LOCALAPPDATA' ? 'C:\\Users\\test\\AppData\\Local' : undefined),
    homedir: () => 'C:\\Users\\test',
    platform: () => 'win32',
    ...overrides,
  };
}

describe('detect/windows', () => {
  it('returns three installed entries when all three clients are present via path', async () => {
    const deps = makeDeps({
      exists: (p) => {
        if (p.includes('Discord\\Update.exe')) return Promise.resolve(true);
        if (p.includes('slack\\')) return Promise.resolve(true);
        if (p.includes('Microsoft\\Teams\\')) return Promise.resolve(true);
        return Promise.resolve(false);
      },
    });

    const results = await detect(deps);

    expect(results).toHaveLength(3);
    expect(results[0]?.kind).toBe('discord');
    expect(results[0]?.installed).toBe(true);
    expect(results[1]?.kind).toBe('slack');
    expect(results[1]?.installed).toBe(true);
    expect(results[2]?.kind).toBe('teams');
    expect(results[2]?.installed).toBe(true);
  });

  it('returns three not-installed entries when nothing is found', async () => {
    const deps = makeDeps({
      exists: () => Promise.resolve(false),
      exec: makeExecFail(),
    });

    const results = await detect(deps);

    expect(results).toHaveLength(3);
    for (const r of results) {
      expect(r.installed).toBe(false);
      expect(r.method).toBe('none');
    }
  });

  it('returns partial install: only discord installed via path', async () => {
    const deps = makeDeps({
      exists: (p) => Promise.resolve(p.includes('Discord\\Update.exe')),
      exec: makeExecFail(),
    });

    const results = await detect(deps);

    expect(results[0]?.kind).toBe('discord');
    expect(results[0]?.installed).toBe(true);
    expect(results[1]?.kind).toBe('slack');
    expect(results[1]?.installed).toBe(false);
    expect(results[2]?.kind).toBe('teams');
    expect(results[2]?.installed).toBe(false);
  });

  it('returns all-false gracefully when LOCALAPPDATA env is missing', async () => {
    const deps = makeDeps({
      env: () => undefined,
      exists: () => Promise.resolve(true),
      exec: makeExecOk(),
    });

    const results = await detect(deps);

    expect(results).toHaveLength(3);
    for (const r of results) {
      expect(r.installed).toBe(false);
    }
  });

  it('discord installed via path even when reg query exits non-zero', async () => {
    const deps = makeDeps({
      exists: (p) => Promise.resolve(p.includes('Discord\\Update.exe')),
      exec: makeExecFail(),
    });

    const results = await detect(deps);

    const discord = results.find((r) => r.kind === 'discord');
    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('registry');
  });

  it('discord detected via URI scheme when Update.exe absent but reg query succeeds', async () => {
    const deps = makeDeps({
      exists: () => Promise.resolve(false),
      exec: (cmd, args) => {
        if (cmd === 'reg' && args.some((a) => a.includes('discord'))) {
          return Promise.resolve({ stdout: '', stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
    });

    const results = await detect(deps);

    const discord = results.find((r) => r.kind === 'discord');
    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('uri-scheme');
  });
});
