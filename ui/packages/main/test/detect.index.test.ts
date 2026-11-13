import { describe, it, expect } from 'bun:test';
import { detectAll } from '../src/detect/index.js';
import type { DetectDeps } from '../src/detect/types.js';
import type { Logger } from '../src/util/logger.js';
import type { DetectedClient } from '@scout/shared';

function makeNullLogger(): Logger {
  return {
    debug() {},
    info() {},
    warn() {},
    error() {},
    child() {
      return makeNullLogger();
    },
  };
}

function makeBaseDeps(overrides: Partial<DetectDeps>): DetectDeps {
  return {
    exists: () => Promise.resolve(false),
    exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    readFile: () => Promise.resolve(''),
    env: () => undefined,
    homedir: () => '/home/test',
    platform: () => 'linux',
    ...overrides,
  };
}

const WIN32_INSTALLED: readonly DetectedClient[] = [
  { kind: 'discord', installed: true, method: 'registry' },
  { kind: 'slack', installed: false, method: 'none' },
  { kind: 'teams', installed: false, method: 'none' },
];

describe('detectAll / platform routing', () => {
  it('routes to windows detector on win32 and returns its shape', async () => {
    const deps = makeBaseDeps({
      platform: () => 'win32',
      env: (name) => (name === 'LOCALAPPDATA' ? 'C:\\Users\\test\\AppData\\Local' : undefined),
      exists: (p) => Promise.resolve(p.includes('Discord\\Update.exe')),
    });

    const results = await detectAll(deps, makeNullLogger());

    expect(results).toHaveLength(3);
    const discord = results.find((r) => r.kind === 'discord');
    expect(discord?.installed).toBe(true);
  });

  it('routes to macos detector on darwin', async () => {
    const deps = makeBaseDeps({
      platform: () => 'darwin',
      exec: () =>
        Promise.resolve({ stdout: '/Applications/Discord.app\n', stderr: '', exitCode: 0 }),
    });

    const results = await detectAll(deps, makeNullLogger());

    expect(results).toHaveLength(3);
    expect(results.map((r) => r.kind)).toEqual(['discord', 'slack', 'teams']);
  });

  it('routes to linux detector on linux', async () => {
    const deps = makeBaseDeps({
      platform: () => 'linux',
    });

    const results = await detectAll(deps, makeNullLogger());

    expect(results).toHaveLength(3);
    expect(results.map((r) => r.kind)).toEqual(['discord', 'slack', 'teams']);
  });

  it('returns three not-installed entries for unknown platform without throwing', async () => {
    const deps = makeBaseDeps({
      platform: () => 'aix' as NodeJS.Platform,
    });

    const results = await detectAll(deps, makeNullLogger());

    expect(results).toHaveLength(3);
    for (const r of results) {
      expect(r.installed).toBe(false);
      expect(r.method).toBe('none');
    }
    expect(results.map((r) => r.kind)).toEqual(['discord', 'slack', 'teams']);
  });

  it('always returns exactly three entries matching CLIENT_KINDS order', async () => {
    const deps = makeBaseDeps({ platform: () => 'linux' });

    const results = await detectAll(deps, makeNullLogger());

    expect(results[0]?.kind).toBe('discord');
    expect(results[1]?.kind).toBe('slack');
    expect(results[2]?.kind).toBe('teams');
  });

  it('per-kind error inside platform module downgrades that kind to not-installed', async () => {
    let callCount = 0;
    const deps = makeBaseDeps({
      platform: () => 'linux',
      exists: (p) => {
        callCount++;
        if (callCount === 1 && p.endsWith('.local/share/applications')) {
          throw new Error('simulated fs error');
        }
        return Promise.resolve(false);
      },
      exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    });

    let results: readonly DetectedClient[] | undefined;
    let threw = false;
    try {
      results = await detectAll(deps, makeNullLogger());
    } catch {
      threw = true;
    }

    expect(threw).toBe(false);
    expect(results).toHaveLength(3);
    expect(results?.map((r) => r.kind)).toEqual(['discord', 'slack', 'teams']);
  });

  it('win32 shape passthrough matches CLIENT_KINDS order', async () => {
    const deps = makeBaseDeps({
      platform: () => 'win32',
      env: (name) => (name === 'LOCALAPPDATA' ? 'C:\\Users\\test\\AppData\\Local' : undefined),
      exists: (p) => {
        if (p.includes('Discord\\Update.exe')) return Promise.resolve(true);
        if (p.includes('slack\\')) return Promise.resolve(true);
        return Promise.resolve(false);
      },
    });

    const results = await detectAll(deps, makeNullLogger());

    expect(results[0]?.kind).toBe('discord');
    expect(results[1]?.kind).toBe('slack');
    expect(results[2]?.kind).toBe('teams');
    expect(WIN32_INSTALLED[0]?.installed).toBe(true);
  });
});
