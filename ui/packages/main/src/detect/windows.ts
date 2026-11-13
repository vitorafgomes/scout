import type { DetectedClient } from '@scout/shared';
import type { DetectDeps } from './types.js';

async function detectDiscordWindows(deps: DetectDeps): Promise<DetectedClient> {
  const localAppData = deps.env('LOCALAPPDATA');
  if (localAppData === undefined || localAppData === '') {
    return { kind: 'discord', installed: false, method: 'none' };
  }

  const updateExe = `${localAppData}\\Discord\\Update.exe`;
  const pathExists = await deps.exists(updateExe);

  if (pathExists) {
    return { kind: 'discord', installed: true, method: 'registry', location: updateExe };
  }

  const regResult = await deps.exec(
    'reg',
    ['query', 'HKCU\\Software\\Classes\\discord', '/ve'],
    2000,
  );
  if (regResult.exitCode === 0) {
    return { kind: 'discord', installed: true, method: 'uri-scheme' };
  }

  return { kind: 'discord', installed: false, method: 'none' };
}

async function detectSlackWindows(deps: DetectDeps): Promise<DetectedClient> {
  const localAppData = deps.env('LOCALAPPDATA');
  if (localAppData === undefined || localAppData === '') {
    return { kind: 'slack', installed: false, method: 'none' };
  }

  const slackDir = `${localAppData}\\slack\\`;
  if (await deps.exists(slackDir)) {
    return { kind: 'slack', installed: true, method: 'registry', location: slackDir };
  }

  const appxDir = `${localAppData}\\Packages\\91750D7E.Slack`;
  if (await deps.exists(appxDir)) {
    return { kind: 'slack', installed: true, method: 'registry', location: appxDir };
  }

  const regResult = await deps.exec(
    'reg',
    ['query', 'HKCU\\Software\\Classes\\slack', '/ve'],
    2000,
  );
  if (regResult.exitCode === 0) {
    return { kind: 'slack', installed: true, method: 'uri-scheme' };
  }

  return { kind: 'slack', installed: false, method: 'none' };
}

async function detectTeamsWindows(deps: DetectDeps): Promise<DetectedClient> {
  const localAppData = deps.env('LOCALAPPDATA');
  if (localAppData === undefined || localAppData === '') {
    return { kind: 'teams', installed: false, method: 'none' };
  }

  const teamsDir = `${localAppData}\\Microsoft\\Teams\\`;
  if (await deps.exists(teamsDir)) {
    return { kind: 'teams', installed: true, method: 'registry', location: teamsDir };
  }

  const packagesDir = `${localAppData}\\Packages`;
  const packagesExists = await deps.exists(packagesDir);
  if (packagesExists) {
    const msTeamsResult = await deps.exec(
      'reg',
      ['query', 'HKCU\\Software\\Classes\\msteams', '/ve'],
      2000,
    );
    if (msTeamsResult.exitCode === 0) {
      return { kind: 'teams', installed: true, method: 'uri-scheme' };
    }
  }

  return { kind: 'teams', installed: false, method: 'none' };
}

export async function detectKind(
  kind: DetectedClient['kind'],
  deps: DetectDeps,
): Promise<DetectedClient> {
  switch (kind) {
    case 'discord':
      return detectDiscordWindows(deps);
    case 'slack':
      return detectSlackWindows(deps);
    case 'teams':
      return detectTeamsWindows(deps);
  }
}

export async function detect(deps: DetectDeps): Promise<readonly DetectedClient[]> {
  const [discord, slack, teams] = await Promise.all([
    detectDiscordWindows(deps),
    detectSlackWindows(deps),
    detectTeamsWindows(deps),
  ]);
  return [discord, slack, teams];
}
