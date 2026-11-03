import { afterEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import { dirname, join } from 'node:path';
import {
  TABLE_PATH,
  archiveFor,
  extractWithTar,
  fetchMediaTools,
} from '../../../build/scripts/fetch-media-tools.mjs';

const sha = (bytes: string): string => createHash('sha256').update(bytes).digest('hex');

const ARCHIVE = 'archive bytes';
const FONT = 'font bytes';
const LICENCE = 'licence bytes';

const table = {
  ffmpeg: {
    version: 'n9.0.2-test',
    release: 'https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-test',
    source: 'https://github.com/FFmpeg/FFmpeg/tree/test',
    archives: {
      'linux-x64': {
        url: 'https://example.test/ffmpeg-linux64.tar.xz',
        sha256: sha(ARCHIVE),
        root: 'ffmpeg-linux64',
      },
      'win32-x64': {
        url: 'https://example.test/ffmpeg-win64.zip',
        sha256: sha(ARCHIVE),
        root: 'ffmpeg-win64',
      },
    },
  },
  fonts: [
    { file: 'NotoSans-Regular.ttf', url: 'https://example.test/regular.ttf', sha256: sha(FONT) },
    { file: 'OFL.txt', url: 'https://example.test/LICENSE', sha256: sha(LICENCE) },
  ],
};

const BODIES: Record<string, string> = {
  'https://example.test/ffmpeg-linux64.tar.xz': ARCHIVE,
  'https://example.test/ffmpeg-win64.zip': ARCHIVE,
  'https://example.test/regular.ttf': FONT,
  'https://example.test/LICENSE': LICENCE,
};

function fakeFetch(bodies: Record<string, string> = BODIES) {
  const urls: string[] = [];
  const fetch = async (url: string): Promise<Response> => {
    urls.push(url);
    const body = bodies[url];
    return body === undefined ? new Response('', { status: 404 }) : new Response(body);
  };
  return { fetch, urls };
}

async function fakeExtract(_archive: string, dir: string, members: readonly string[]) {
  for (const member of members) {
    await mkdir(dirname(join(dir, member)), { recursive: true });
    await writeFile(join(dir, member), `extracted ${member}`, { mode: 0o644 });
  }
}

describe('fetch-media-tools', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function folders(): Promise<{ resourcesDir: string; cacheDir: string }> {
    const root = await mkdtemp(join(os.tmpdir(), 'scout-media-tools-'));
    dirs.push(root);
    return { resourcesDir: join(root, 'resources'), cacheDir: join(root, 'cache') };
  }

  it('the pinned table names an https URL and a 64-hex SHA-256 for every archive and font, archives for linux-x64 and win32-x64 only, all from one BtbN release', () => {
    const pinned = JSON.parse(readFileSync(TABLE_PATH, 'utf8')) as typeof table;
    const entries = [...Object.values(pinned.ffmpeg.archives), ...pinned.fonts];
    const tag = /\/releases\/tag\/(autobuild-[0-9-]+)$/.exec(pinned.ffmpeg.release)?.[1];

    expect(Object.keys(pinned.ffmpeg.archives).sort()).toEqual(['linux-x64', 'win32-x64']);
    expect(
      entries.filter((e) => !/^https:\/\//.test(e.url) || !/^[0-9a-f]{64}$/.test(e.sha256)),
    ).toEqual([]);
    expect(tag).toBeDefined();
    expect(
      Object.values(pinned.ffmpeg.archives).filter(
        (a) =>
          !a.url.includes(`/releases/download/${tag}/${a.root}.`) || !a.root.includes('-lgpl-'),
      ),
    ).toEqual([]);
    expect(pinned.fonts.map((f) => f.file).sort()).toEqual([
      'NotoSans-Bold.ttf',
      'NotoSans-Regular.ttf',
      'OFL.txt',
    ]);
  });

  it('selects the archive of the platform and arch, with ffmpeg alone and the licence to take from it; none for macOS', () => {
    expect(archiveFor(table, 'linux-x64')?.members).toEqual({
      ffmpeg: 'ffmpeg-linux64/bin/ffmpeg',
      'LICENSE.txt': 'ffmpeg-linux64/LICENSE.txt',
    });
    expect(archiveFor(table, 'win32-x64')?.members).toEqual({
      'ffmpeg.exe': 'ffmpeg-win64/bin/ffmpeg.exe',
      'LICENSE.txt': 'ffmpeg-win64/LICENSE.txt',
    });
    expect(archiveFor(table, 'darwin-arm64')).toBeNull();
  });

  it('extracts ffmpeg, without ffprobe, with the licence and a notice into media-tools/<platform>-<arch>, and the fonts with their licence into fonts', async () => {
    const { resourcesDir, cacheDir } = await folders();

    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    });

    const tools = join(resourcesDir, 'media-tools', 'linux-x64');
    expect((await readdir(tools)).sort()).toEqual(['LICENSE.txt', 'NOTICE.txt', 'ffmpeg']);
    expect(await readFile(join(tools, 'ffmpeg'), 'utf8')).toBe(
      'extracted ffmpeg-linux64/bin/ffmpeg',
    );
    expect(await readFile(join(tools, 'LICENSE.txt'), 'utf8')).toBe(
      'extracted ffmpeg-linux64/LICENSE.txt',
    );
    const notice = await readFile(join(tools, 'NOTICE.txt'), 'utf8');
    expect(notice).toContain(table.ffmpeg.release);
    expect(notice).toContain(table.ffmpeg.source);
    expect(notice).toContain(table.ffmpeg.archives['linux-x64'].sha256);
    expect((await readdir(join(resourcesDir, 'fonts'))).sort()).toEqual([
      'NotoSans-Regular.ttf',
      'OFL.txt',
    ]);
    expect(await readFile(join(resourcesDir, 'fonts', 'OFL.txt'), 'utf8')).toBe(LICENCE);
  });

  it.skipIf(process.platform === 'win32')(
    'ffmpeg is executable on linux even when the archive gave it no executable bit',
    async () => {
      const { resourcesDir, cacheDir } = await folders();

      await fetchMediaTools({
        target: 'linux-x64',
        table,
        resourcesDir,
        cacheDir,
        fetch: fakeFetch().fetch,
        extract: fakeExtract,
        log: () => undefined,
      });

      const tools = join(resourcesDir, 'media-tools', 'linux-x64');
      expect(statSync(join(tools, 'ffmpeg')).mode & 0o777).toBe(0o755);
    },
  );

  it('fails the build on a checksum mismatch and keeps nothing it downloaded', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const { fetch } = fakeFetch({
      ...BODIES,
      'https://example.test/ffmpeg-linux64.tar.xz': 'tampered',
    });

    const failure = await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch,
      extract: fakeExtract,
      log: () => undefined,
    }).then(
      () => null,
      (err: unknown) => err as Error,
    );

    expect(failure?.message).toBe(
      `media tools: checksum mismatch for https://example.test/ffmpeg-linux64.tar.xz: expected ${sha(ARCHIVE)}, got ${sha('tampered')}`,
    );
    expect(existsSync(join(cacheDir, 'ffmpeg-linux64.tar.xz'))).toBe(false);
    expect(existsSync(join(resourcesDir, 'media-tools'))).toBe(false);
  });

  it('a download that answers an error status fails the build, naming the URL and the status', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const { fetch } = fakeFetch({ 'https://example.test/ffmpeg-linux64.tar.xz': ARCHIVE });

    const failure = await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch,
      extract: fakeExtract,
      log: () => undefined,
    }).then(
      () => null,
      (err: unknown) => err as Error,
    );

    expect(failure?.message).toBe('media tools: https://example.test/regular.ttf answered 404');
  });

  it('macOS gets the fonts and their licence, and no ffmpeg: no archive download and no media-tools folder', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const { fetch, urls } = fakeFetch();
    const lines: string[] = [];

    await fetchMediaTools({
      target: 'darwin-arm64',
      table,
      resourcesDir,
      cacheDir,
      fetch,
      extract: fakeExtract,
      log: (line: string) => lines.push(line),
    });

    expect(urls).toEqual(['https://example.test/regular.ttf', 'https://example.test/LICENSE']);
    expect((await readdir(join(resourcesDir, 'fonts'))).sort()).toEqual([
      'NotoSans-Regular.ttf',
      'OFL.txt',
    ]);
    expect(await readFile(join(resourcesDir, 'fonts', 'NotoSans-Regular.ttf'), 'utf8')).toBe(FONT);
    expect(existsSync(join(resourcesDir, 'media-tools'))).toBe(false);
    expect(lines).toEqual([
      'media tools: none bundled for darwin-arm64; the worker uses ffmpeg from the system path',
    ]);
  });

  it('a second run with everything in place downloads and extracts nothing', async () => {
    const { resourcesDir, cacheDir } = await folders();
    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    });
    const again = fakeFetch();
    const extracted: string[] = [];

    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: again.fetch,
      extract: async (archive: string, dir: string, members: readonly string[]) => {
        extracted.push(archive);
        await fakeExtract(archive, dir, members);
      },
      log: () => undefined,
    });

    expect(again.urls).toEqual([]);
    expect(extracted).toEqual([]);
  });

  it('a cached archive with the pinned hash is extracted again without a download when the tools are gone', async () => {
    const { resourcesDir, cacheDir } = await folders();
    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    });
    await rm(resourcesDir, { recursive: true });
    const again = fakeFetch();

    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: again.fetch,
      extract: fakeExtract,
      log: () => undefined,
    });

    expect(again.urls).toEqual([]);
    expect(existsSync(join(resourcesDir, 'media-tools', 'linux-x64', 'ffmpeg'))).toBe(true);
    expect(existsSync(join(resourcesDir, 'fonts', 'NotoSans-Regular.ttf'))).toBe(true);
  });

  it('the system tar failing on an archive it cannot read fails the build, naming the members and the archive', async () => {
    const { cacheDir } = await folders();
    await mkdir(cacheDir, { recursive: true });
    const archive = join(cacheDir, 'not-an-archive.tar.xz');
    await writeFile(archive, 'these bytes are not an archive');

    let failure: Error | null = null;
    try {
      extractWithTar(archive, cacheDir, ['ffmpeg-linux64/bin/ffmpeg']);
    } catch (err: unknown) {
      failure = err as Error;
    }

    expect(failure?.message).toBe(
      `media tools: tar could not extract ffmpeg-linux64/bin/ffmpeg from ${archive}`,
    );
  });
});
