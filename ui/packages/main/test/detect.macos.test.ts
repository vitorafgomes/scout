import { describe, it, expect } from 'bun:test';
import { detect } from '../src/detect/macos.js';
import type { DetectDeps, ExecResult } from '../src/detect/types.js';
import type { Logger } from '../src/util/logger.js';

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

function makeDeps(overrides: Partial<DetectDeps>): DetectDeps {
  return {
    exists: () => Promise.resolve(false),
    exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    readFile: () => Promise.resolve(''),
    env: () => undefined,
    homedir: () => '/Users/test',
    platform: () => 'darwin',
    ...overrides,
  };
}

function mdfindHit(path: string): () => Promise<ExecResult> {
  return () => Promise.resolve({ stdout: `${path}\n`, stderr: '', exitCode: 0 });
}

function mdfindMiss(): () => Promise<ExecResult> {
  return () => Promise.resolve({ stdout: '', stderr: '', exitCode: 0 });
}

function mdfindError(): () => Promise<ExecResult> {
  return () => Promise.resolve({ stdout: '', stderr: 'Spotlight error', exitCode: 1 });
}

describe('detect/macos', () => {
  it('discord detected via mdfind bundle-id hit', async () => {
    const appPath = '/Applications/Discord.app';
    const deps = makeDeps({
      exec: mdfindHit(appPath),
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('bundle-id');
    expect(discord?.location).toBe(appPath);
  });

  it('discord detected via /Applications fallback when mdfind returns empty', async () => {
    const deps = makeDeps({
      exec: mdfindMiss(),
      exists: (p) => Promise.resolve(p === '/Applications/Discord.app'),
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('bundle-id');
  });

  it('discord not installed when mdfind exits with code 1 and /Applications absent', async () => {
    const warnings: string[] = [];
    const logger = makeNullLogger();
    logger.warn = (msg: string) => {
      warnings.push(msg);
    };

    const deps = makeDeps({
      exec: mdfindError(),
      exists: () => Promise.resolve(false),
    });

    const results = await detect(deps, logger);
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(false);
    expect(discord?.method).toBe('none');
    expect(warnings.length).toBeGreaterThan(0);
  });

  it('teams detected under com.microsoft.teams2 when com.microsoft.teams absent', async () => {
    const teams2Path = '/Applications/Microsoft Teams (work or school).app';
    const deps = makeDeps({
      exec: (cmd, args) => {
        const query = args[0] ?? '';
        if (query.includes('com.microsoft.teams2')) {
          return Promise.resolve({ stdout: `${teams2Path}\n`, stderr: '', exitCode: 0 });
        }
        if (query.includes('com.microsoft.teams')) {
          return Promise.resolve({ stdout: '', stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 0 });
      },
    });

    const results = await detect(deps, makeNullLogger());
    const teams = results.find((r) => r.kind === 'teams');

    expect(teams?.installed).toBe(true);
    expect(teams?.method).toBe('bundle-id');
    expect(teams?.location).toBe(teams2Path);
  });

  it('partial install: only slack detected, discord and teams not found', async () => {
    const slackPath = '/Applications/Slack.app';
    const deps = makeDeps({
      exec: (cmd, args) => {
        const query = args[0] ?? '';
        if (query.includes('slackmacgap')) {
          return Promise.resolve({ stdout: `${slackPath}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 0 });
      },
      exists: () => Promise.resolve(false),
    });

    const results = await detect(deps, makeNullLogger());

    const discord = results.find((r) => r.kind === 'discord');
    const slack = results.find((r) => r.kind === 'slack');
    const teams = results.find((r) => r.kind === 'teams');

    expect(discord?.installed).toBe(false);
    expect(slack?.installed).toBe(true);
    expect(slack?.location).toBe(slackPath);
    expect(teams?.installed).toBe(false);
  });
});
