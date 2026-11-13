import { describe, it, expect } from 'bun:test';
import { detect } from '../src/detect/linux.js';
import type { DetectDeps } from '../src/detect/types.js';
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

const HOME = '/home/testuser';

function makeDeps(overrides: Partial<DetectDeps>): DetectDeps {
  return {
    exists: () => Promise.resolve(false),
    exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    readFile: () => Promise.resolve(''),
    env: () => undefined,
    homedir: () => HOME,
    platform: () => 'linux',
    ...overrides,
  };
}

const DISCORD_DESKTOP_CONTENT = `[Desktop Entry]
Name=Discord
Exec=/usr/bin/discord --no-sandbox %U
Type=Application
`;

const MALFORMED_DESKTOP_CONTENT = `[Desktop Entry]
Name=Something
\x00\x01\x02 binary garbage
`;

describe('detect/linux', () => {
  it('discord detected from ~/.local/share/applications desktop file', async () => {
    const discordDesktop = `${HOME}/.local/share/applications/discord.desktop`;
    const deps = makeDeps({
      exists: (p) => {
        if (p.endsWith('.local/share/applications')) return Promise.resolve(true);
        return Promise.resolve(false);
      },
      exec: (cmd, args) => {
        if (cmd === 'find' && args[0]?.endsWith('.local/share/applications')) {
          return Promise.resolve({ stdout: `${discordDesktop}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
      readFile: (p) => {
        if (p === discordDesktop) return Promise.resolve(DISCORD_DESKTOP_CONTENT);
        return Promise.resolve('');
      },
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('desktop-file');
    expect(discord?.location).toBe(discordDesktop);
  });

  it('discord detected from flatpak system path', async () => {
    const flatpakDesktop =
      '/var/lib/flatpak/exports/share/applications/com.discordapp.Discord.desktop';
    const deps = makeDeps({
      exists: (p) => {
        if (p === '/var/lib/flatpak/exports/share/applications') return Promise.resolve(true);
        return Promise.resolve(false);
      },
      exec: (cmd, args) => {
        if (cmd === 'find' && args[0] === '/var/lib/flatpak/exports/share/applications') {
          return Promise.resolve({ stdout: `${flatpakDesktop}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
      readFile: (p) => {
        if (p === flatpakDesktop)
          return Promise.resolve(
            '[Desktop Entry]\nExec=/usr/bin/flatpak run com.discordapp.Discord\n',
          );
        return Promise.resolve('');
      },
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('desktop-file');
  });

  it('discord detected from snap path', async () => {
    const snapDesktop = '/var/lib/snapd/desktop/applications/discord_discord.desktop';
    const deps = makeDeps({
      exists: (p) => {
        if (p === '/var/lib/snapd/desktop/applications') return Promise.resolve(true);
        return Promise.resolve(false);
      },
      exec: (cmd, args) => {
        if (cmd === 'find' && args[0] === '/var/lib/snapd/desktop/applications') {
          return Promise.resolve({ stdout: `${snapDesktop}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
      readFile: (p) => {
        if (p === snapDesktop)
          return Promise.resolve('[Desktop Entry]\nExec=/snap/bin/discord\nName=Discord\n');
        return Promise.resolve('');
      },
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(true);
    expect(discord?.method).toBe('desktop-file');
  });

  it('discord not installed when no desktop file and which exits non-zero', async () => {
    const deps = makeDeps({
      exists: () => Promise.resolve(false),
      exec: () => Promise.resolve({ stdout: '', stderr: '', exitCode: 1 }),
    });

    const results = await detect(deps, makeNullLogger());
    const discord = results.find((r) => r.kind === 'discord');

    expect(discord?.installed).toBe(false);
    expect(discord?.method).toBe('none');
  });

  it('malformed .desktop file does not throw and is skipped', async () => {
    const malformedPath = `${HOME}/.local/share/applications/broken.desktop`;
    const warnings: string[] = [];
    const logger = makeNullLogger();
    logger.warn = (msg: string) => {
      warnings.push(msg);
    };

    const deps = makeDeps({
      exists: (p) => {
        if (p.endsWith('.local/share/applications')) return Promise.resolve(true);
        return Promise.resolve(false);
      },
      exec: (cmd, args) => {
        if (cmd === 'find' && args[0]?.endsWith('.local/share/applications')) {
          return Promise.resolve({ stdout: `${malformedPath}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
      readFile: (p) => {
        if (p === malformedPath) return Promise.reject(new Error('invalid utf-8'));
        return Promise.resolve('');
      },
    });

    const results = await detect(deps, logger);

    expect(results).toHaveLength(3);
    const discord = results.find((r) => r.kind === 'discord');
    expect(discord?.installed).toBe(false);
    expect(warnings.some((w) => w.includes('malformed') || w.includes('skipping'))).toBe(true);
  });

  it('teams is always not-installed on Linux regardless of filesystem', async () => {
    const deps = makeDeps({
      exists: () => Promise.resolve(true),
      exec: (cmd, args) => {
        const str = args.join(' ');
        if (str.includes('teams') || str.includes('Teams')) {
          return Promise.resolve({ stdout: '/usr/bin/teams\n', stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
    });

    const results = await detect(deps, makeNullLogger());
    const teams = results.find((r) => r.kind === 'teams');

    expect(teams?.installed).toBe(false);
    expect(teams?.method).toBe('none');
  });

  it('slack detected via which fallback when no desktop files found', async () => {
    const deps = makeDeps({
      exists: () => Promise.resolve(false),
      exec: (cmd, args) => {
        if (cmd === 'which' && args[0] === 'slack') {
          return Promise.resolve({ stdout: '/usr/bin/slack\n', stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
    });

    const results = await detect(deps, makeNullLogger());
    const slack = results.find((r) => r.kind === 'slack');

    expect(slack?.installed).toBe(true);
    expect(slack?.method).toBe('package-manager');
    expect(slack?.location).toBe('/usr/bin/slack');
  });

  it('desktop file with missing Exec= line does not match and is skipped gracefully', async () => {
    const noExecDesktop = `${HOME}/.local/share/applications/nodiscord.desktop`;
    const deps = makeDeps({
      exists: (p) => {
        if (p.endsWith('.local/share/applications')) return Promise.resolve(true);
        return Promise.resolve(false);
      },
      exec: (cmd, args) => {
        if (cmd === 'find' && args[0]?.endsWith('.local/share/applications')) {
          return Promise.resolve({ stdout: `${noExecDesktop}\n`, stderr: '', exitCode: 0 });
        }
        return Promise.resolve({ stdout: '', stderr: '', exitCode: 1 });
      },
      readFile: () => Promise.resolve(MALFORMED_DESKTOP_CONTENT),
    });

    const results = await detect(deps, makeNullLogger());
    expect(results).toHaveLength(3);
    const discord = results.find((r) => r.kind === 'discord');
    expect(discord?.installed).toBe(false);
  });
});
