import { afterEach, describe, expect, it } from 'bun:test';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import * as os from 'node:os';
import { join } from 'node:path';
import { load } from 'js-yaml';

const REPO_ROOT = join(import.meta.dirname, '../../../..');

interface Step {
  name?: string;
  run?: string;
}

interface Workflow {
  jobs: Record<string, { steps: Step[] }>;
}

const workflow = (name: string) =>
  load(readFileSync(join(REPO_ROOT, '.github/workflows', name), 'utf8')) as Workflow;

const steps = (name: string) => Object.values(workflow(name).jobs).flatMap((job) => job.steps);

const DIST: Record<string, string[]> = {
  linux: [
    'Scout-Desktop-0.2.0-linux-x86_64.AppImage',
    'Scout-Desktop-0.2.0-linux-amd64.deb',
    'latest-linux.yml',
    'builder-debug.yml',
    'builder-effective-config.yaml',
  ],
  windows: [
    'Scout-Desktop-0.2.0-win-x64.exe',
    'Scout-Desktop-0.2.0-win-x64.exe.blockmap',
    'latest.yml',
    'builder-debug.yml',
    'builder-effective-config.yaml',
  ],
  mac: [
    'Scout-Desktop-0.2.0-mac-arm64.dmg',
    'Scout-Desktop-0.2.0-mac-arm64.dmg.blockmap',
    'Scout-Desktop-0.2.0-mac-x64.dmg',
    'Scout-Desktop-0.2.0-mac-x64.dmg.blockmap',
    'latest-mac.yml',
    'builder-debug.yml',
    'builder-effective-config.yaml',
  ],
};

describe('the release workflow', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  it.skipIf(process.platform === 'win32').each([
    [
      'linux',
      [
        'SHA256SUMS-linux.txt',
        'Scout-Desktop-0.2.0-linux-amd64.deb',
        'Scout-Desktop-0.2.0-linux-x86_64.AppImage',
        'latest-linux.yml',
      ],
    ],
    [
      'windows',
      [
        'SHA256SUMS-windows.txt',
        'Scout-Desktop-0.2.0-win-x64.exe',
        'Scout-Desktop-0.2.0-win-x64.exe.blockmap',
        'latest.yml',
      ],
    ],
    ['mac', ['SHA256SUMS-mac.txt', 'Scout-Desktop-0.2.0-mac-arm64.dmg']],
  ])(
    'the prune step keeps latest*.yml and the Windows blockmap for the updater, and checksums only the packages (%s)',
    async (platform, survivors) => {
      const prune = steps('release.yml').find((step) => step.name?.startsWith('Keep the packages'));
      const dist = await mkdtemp(join(os.tmpdir(), 'scout-dist-'));
      dirs.push(dist);
      for (const file of DIST[platform]!) await writeFile(join(dist, file), file);

      const run = spawnSync(
        'bash',
        ['-eo', 'pipefail', '-c', prune!.run!.replaceAll('${{ matrix.platform }}', platform)],
        { cwd: dist, encoding: 'utf8' },
      );

      expect(run.status).toBe(0);
      expect((await readdir(dist)).sort()).toEqual(survivors);
      const sums = await readFile(join(dist, `SHA256SUMS-${platform}.txt`), 'utf8');
      expect(
        sums
          .trim()
          .split('\n')
          .map((line) => line.split(/\s+\*?/)[1])
          .sort(),
      ).toEqual(
        survivors.filter(
          (name) => name.startsWith('Scout-Desktop-') && !name.endsWith('.blockmap'),
        ),
      );
    },
  );

  it.each(['release.yml', 'ui-ci.yml'])(
    'every packaging step in %s runs electron-builder with --publish never',
    (name) => {
      const packaging = steps(name).filter((step) => step.run?.includes('matrix.dist_script'));

      expect(packaging.map((step) => step.run!.trim())).toEqual([
        'bun run ${{ matrix.dist_script }} -- --publish never',
      ]);
    },
  );
});
