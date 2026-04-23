import type { DetectedClient } from '@scout/shared';
import type { DetectDeps } from './types.js';
import type { Logger } from '../util/logger.js';

async function mdfindBundleId(
  bundleId: string,
  deps: DetectDeps,
  logger: Logger,
): Promise<string | null> {
  const result = await deps.exec('mdfind', [`kMDItemCFBundleIdentifier == "${bundleId}"`], 2000);

  if (result.exitCode !== 0) {
    logger.warn('mdfind exited with non-zero code', {
      bundleId,
      exitCode: result.exitCode,
      stderr: result.stderr.trim(),
    });
    return null;
  }

  const firstLine = result.stdout.split('\n').find((l) => l.trim() !== '');
  return firstLine?.trim() ?? null;
}

async function detectDiscordMacos(deps: DetectDeps, logger: Logger): Promise<DetectedClient> {
  const found = await mdfindBundleId('com.hnc.Discord', deps, logger);
  if (found !== null) {
    return { kind: 'discord', installed: true, method: 'bundle-id', location: found };
  }

  if (await deps.exists('/Applications/Discord.app')) {
    return {
      kind: 'discord',
      installed: true,
      method: 'bundle-id',
      location: '/Applications/Discord.app',
    };
  }

  return { kind: 'discord', installed: false, method: 'none' };
}

async function detectSlackMacos(deps: DetectDeps, logger: Logger): Promise<DetectedClient> {
  const found = await mdfindBundleId('com.tinyspeck.slackmacgap', deps, logger);
  if (found !== null) {
    return { kind: 'slack', installed: true, method: 'bundle-id', location: found };
  }

  return { kind: 'slack', installed: false, method: 'none' };
}

async function detectTeamsMacos(deps: DetectDeps, logger: Logger): Promise<DetectedClient> {
  const found1 = await mdfindBundleId('com.microsoft.teams', deps, logger);
  if (found1 !== null) {
    return { kind: 'teams', installed: true, method: 'bundle-id', location: found1 };
  }

  const found2 = await mdfindBundleId('com.microsoft.teams2', deps, logger);
  if (found2 !== null) {
    return { kind: 'teams', installed: true, method: 'bundle-id', location: found2 };
  }

  return { kind: 'teams', installed: false, method: 'none' };
}

export async function detectKind(
  kind: DetectedClient['kind'],
  deps: DetectDeps,
  logger: Logger,
): Promise<DetectedClient> {
  switch (kind) {
    case 'discord':
      return detectDiscordMacos(deps, logger);
    case 'slack':
      return detectSlackMacos(deps, logger);
    case 'teams':
      return detectTeamsMacos(deps, logger);
  }
}

export async function detect(deps: DetectDeps, logger: Logger): Promise<readonly DetectedClient[]> {
  const [discord, slack, teams] = await Promise.all([
    detectDiscordMacos(deps, logger),
    detectSlackMacos(deps, logger),
    detectTeamsMacos(deps, logger),
  ]);
  return [discord, slack, teams];
}
