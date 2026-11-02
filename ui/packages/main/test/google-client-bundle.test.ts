import { afterEach, describe, expect, it } from 'bun:test';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import { join } from 'node:path';
import { DEFAULT_GOOGLE_CLIENT_ID } from '../src/oauth/google-auth.js';

const UI_ROOT = join(import.meta.dirname, '../../..');
const SCRIPT = join(UI_ROOT, 'build/scripts/write-google-client.mjs');

function run(target: string, env: Record<string, string>): { status: number | null; out: string } {
  const result = spawnSync(process.execPath, [SCRIPT, target], {
    encoding: 'utf8',
    env: { PATH: process.env['PATH'] ?? '', ...env },
  });
  return { status: result.status, out: `${result.stdout}${result.stderr}` };
}

describe('the bundled Google client', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function target(): Promise<string> {
    const dir = await mkdtemp(join(os.tmpdir(), 'scout-google-client-'));
    dirs.push(dir);
    return join(dir, 'google-client.json');
  }

  it('writes the client id and the secret from the environment into the app resources', async () => {
    const file = await target();

    const result = run(file, { SCOUT_GOOGLE_CLIENT_SECRET: 'build-secret-value' });

    expect(result.status).toBe(0);
    expect(JSON.parse(await readFile(file, 'utf8'))).toEqual({
      client_id: DEFAULT_GOOGLE_CLIENT_ID,
      client_secret: 'build-secret-value',
    });
    expect(result.out).not.toContain('build-secret-value');
  });

  it('takes SCOUT_GOOGLE_CLIENT_ID over the constant when it is set', async () => {
    const file = await target();

    run(file, {
      SCOUT_GOOGLE_CLIENT_ID: 'other-client.apps.googleusercontent.com',
      SCOUT_GOOGLE_CLIENT_SECRET: 'build-secret-value',
    });

    expect(JSON.parse(await readFile(file, 'utf8'))).toMatchObject({
      client_id: 'other-client.apps.googleusercontent.com',
    });
  });

  it('without the secret removes a stale file and says the client is not bundled', async () => {
    const file = await target();
    await writeFile(file, '{"client_id":"stale","client_secret":"stale"}');

    const result = run(file, {});

    expect(result.status).toBe(0);
    expect(existsSync(file)).toBe(false);
    expect(result.out.trim()).toBe('google client: not bundled');
  });

  it('packaging writes or removes the bundled client before building main', () => {
    const scripts = (
      JSON.parse(readFileSync(join(UI_ROOT, 'package.json'), 'utf8')) as {
        scripts: Record<string, string>;
      }
    ).scripts;

    expect(
      scripts['package:all']?.startsWith('node build/scripts/write-google-client.mjs && '),
    ).toBe(true);
  });

  it('the bundled client file is ignored by git', () => {
    const result = spawnSync(
      'git',
      ['check-ignore', 'ui/packages/main/resources/google-client.json'],
      {
        cwd: join(UI_ROOT, '..'),
        encoding: 'utf8',
      },
    );

    expect(result.status).toBe(0);
  });
});
