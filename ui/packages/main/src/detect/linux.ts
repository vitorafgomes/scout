import type { DetectedClient } from '@scout/shared';
import type { DetectDeps } from './types.js';
import type { Logger } from '../util/logger.js';

const DESKTOP_DIRS = [
  '.local/share/applications',
  '.local/share/flatpak/exports/share/applications',
] as const;

const SYSTEM_DESKTOP_DIRS = [
  '/usr/share/applications',
  '/var/lib/flatpak/exports/share/applications',
  '/var/lib/snapd/desktop/applications',
] as const;

interface DesktopEntry {
  readonly execLine: string | null;
  readonly name: string | null;
}

function parseDesktopEntry(content: string): DesktopEntry {
  let execLine: string | null = null;
  let name: string | null = null;

  for (const rawLine of content.split('\n')) {
    const line = rawLine.trim();
    if (execLine === null && line.startsWith('Exec=')) {
      execLine = line.slice('Exec='.length).trim();
    }
    if (name === null && line.startsWith('Name=')) {
      name = line.slice('Name='.length).trim();
    }
  }

  return { execLine, name };
}

async function matchesDesktopFiles(
  keyword: RegExp,
  deps: DetectDeps,
  logger: Logger,
  homedir: string,
): Promise<string | null> {
  const userDirs = DESKTOP_DIRS.map((d) => `${homedir}/${d}`);
  const allDirs = [...userDirs, ...SYSTEM_DESKTOP_DIRS];

  for (const dir of allDirs) {
    if (!(await deps.exists(dir))) continue;

    const listResult = await deps.exec('find', [dir, '-maxdepth', '1', '-name', '*.desktop'], 2000);
    if (listResult.exitCode !== 0) continue;

    const files = listResult.stdout
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.endsWith('.desktop'));

    for (const filePath of files) {
      try {
        const content = await deps.readFile(filePath);
        const entry = parseDesktopEntry(content);
        if (entry.execLine !== null && keyword.test(entry.execLine)) {
          return filePath;
        }
        if (entry.name !== null && keyword.test(entry.name)) {
          return filePath;
        }
      } catch (err: unknown) {
        logger.warn('skipping malformed .desktop file', { path: filePath, err });
      }
    }
  }

  return null;
}

async function detectDiscordLinux(deps: DetectDeps, logger: Logger): Promise<DetectedClient> {
  const homedir = deps.homedir();
  const desktopFile = await matchesDesktopFiles(/discord/i, deps, logger, homedir);
  if (desktopFile !== null) {
    return { kind: 'discord', installed: true, method: 'desktop-file', location: desktopFile };
  }

  const whichResult = await deps.exec('which', ['discord'], 2000);
  if (whichResult.exitCode === 0 && whichResult.stdout.trim() !== '') {
    return {
      kind: 'discord',
      installed: true,
      method: 'package-manager',
      location: whichResult.stdout.trim(),
    };
  }

  return { kind: 'discord', installed: false, method: 'none' };
}

async function detectSlackLinux(deps: DetectDeps, logger: Logger): Promise<DetectedClient> {
  const homedir = deps.homedir();
  const desktopFile = await matchesDesktopFiles(/slack/i, deps, logger, homedir);
  if (desktopFile !== null) {
    return { kind: 'slack', installed: true, method: 'desktop-file', location: desktopFile };
  }

  const snapResult = await deps.exec('snap', ['list', 'slack'], 2000);
  if (snapResult.exitCode === 0) {
    return { kind: 'slack', installed: true, method: 'package-manager' };
  }

  const whichResult = await deps.exec('which', ['slack'], 2000);
  if (whichResult.exitCode === 0 && whichResult.stdout.trim() !== '') {
    return {
      kind: 'slack',
      installed: true,
      method: 'package-manager',
      location: whichResult.stdout.trim(),
    };
  }

  return { kind: 'slack', installed: false, method: 'none' };
}

export async function detectKind(
  kind: DetectedClient['kind'],
  deps: DetectDeps,
  logger: Logger,
): Promise<DetectedClient> {
  switch (kind) {
    case 'discord':
      return detectDiscordLinux(deps, logger);
    case 'slack':
      return detectSlackLinux(deps, logger);
    case 'teams':
      return { kind: 'teams', installed: false, method: 'none' };
  }
}

export async function detect(deps: DetectDeps, logger: Logger): Promise<readonly DetectedClient[]> {
  const [discord, slack] = await Promise.all([
    detectDiscordLinux(deps, logger),
    detectSlackLinux(deps, logger),
  ]);

  const teams: DetectedClient = { kind: 'teams', installed: false, method: 'none' };

  return [discord, slack, teams];
}
