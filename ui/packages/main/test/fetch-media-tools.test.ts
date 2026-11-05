import { afterEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import { dirname, join } from 'node:path';
import {
  TABLE_PATH,
  archiveFor,
  extractArchive,
  extractWithTar,
  fetchMediaTools,
} from '../../../build/scripts/fetch-media-tools.mjs';

const sha = (bytes: string): string => createHash('sha256').update(bytes).digest('hex');

const ARCHIVE = 'archive bytes';
const FONT = 'font bytes';
const LICENCE = 'licence bytes';
const PIPER = 'piper archive bytes';
const MODEL = 'voice model bytes';
const RHUBARB = 'rhubarb archive bytes';

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
  piper: {
    version: '2023.11.14-test',
    project: 'https://github.com/rhasspy/piper',
    release: 'https://github.com/rhasspy/piper/releases/tag/test',
    source: 'https://github.com/rhasspy/piper/tree/test',
    archives: {
      'linux-x64': {
        url: 'https://example.test/piper-linux.tar.gz',
        sha256: sha(PIPER),
        root: 'piper',
      },
      'win32-x64': { url: 'https://example.test/piper-win.zip', sha256: sha(PIPER), root: 'piper' },
      'darwin-arm64': {
        url: 'https://example.test/piper-mac.tar.gz',
        sha256: sha(PIPER),
        root: 'piper',
      },
    },
  },
  rhubarb: {
    version: '1.14.0-test',
    source: 'https://github.com/DanielSWolf/rhubarb-lip-sync',
    license: 'MIT',
    archives: {
      'linux-x64': {
        url: 'https://example.test/rhubarb-Linux.zip',
        sha256: sha(RHUBARB),
        root: 'Rhubarb-Linux',
      },
      'win32-x64': {
        url: 'https://example.test/rhubarb-Windows.zip',
        sha256: sha(RHUBARB),
        root: 'Rhubarb-Windows',
      },
      'darwin-arm64': {
        url: 'https://example.test/rhubarb-macOS.zip',
        sha256: sha(RHUBARB),
        root: 'Rhubarb-macOS',
      },
    },
  },
  voices: {
    repo: 'https://huggingface.co/rhasspy/piper-voices',
    commit: 'abc123',
    items: [
      {
        name: 'en_GB-cori-medium',
        dataset: 'https://librivox.org',
        license: 'public domain',
        files: [
          {
            file: 'en_GB-cori-medium.onnx',
            url: 'https://example.test/cori.onnx',
            sha256: sha(MODEL),
          },
          {
            file: 'en_GB-cori-medium.MODEL_CARD',
            url: 'https://example.test/cori-card',
            sha256: sha(LICENCE),
          },
        ],
      },
      {
        name: 'en_GB-aru-medium',
        dataset: 'https://example.test/aru',
        license: 'CC-BY 4.0 https://creativecommons.org/licenses/by/4.0/',
        attribution: 'University of Liverpool Acoustics Research Unit speech corpus',
        files: [
          {
            file: 'en_GB-aru-medium.onnx',
            url: 'https://example.test/aru.onnx',
            sha256: sha(MODEL),
          },
        ],
      },
    ],
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
  'https://example.test/piper-linux.tar.gz': PIPER,
  'https://example.test/piper-win.zip': PIPER,
  'https://example.test/piper-mac.tar.gz': PIPER,
  'https://example.test/cori.onnx': MODEL,
  'https://example.test/cori-card': LICENCE,
  'https://example.test/aru.onnx': MODEL,
  'https://example.test/rhubarb-Linux.zip': RHUBARB,
  'https://example.test/rhubarb-Windows.zip': RHUBARB,
  'https://example.test/rhubarb-macOS.zip': RHUBARB,
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

const rhubarbMembers: string[] = [];

async function fakeExtract(archive: string, dir: string, members: readonly string[]) {
  if (archive.includes('rhubarb')) {
    rhubarbMembers.push(...members);
    const root = join(dir, members[0]!.split('/')[0]!);
    const exe = archive.includes('Windows') ? 'rhubarb.exe' : 'rhubarb';
    await mkdir(join(root, 'res', 'sphinx'), { recursive: true });
    await mkdir(join(root, 'extras'), { recursive: true });
    await mkdir(join(root, 'tests'), { recursive: true });
    await writeFile(join(root, exe), 'rhubarb exe', { mode: 0o644 });
    await writeFile(join(root, 'res', 'sphinx', 'cmudict-en-us.dict'), 'dictionary');
    await writeFile(join(root, 'LICENSE.md'), 'rhubarb licence');
    await writeFile(join(root, 'README.adoc'), 'readme');
    await writeFile(join(root, 'extras', 'plugin.jsx'), 'plugin');
    await writeFile(join(root, 'tests', 'fixture.wav'), 'fixture');
    return;
  }
  if (archive.includes('piper')) {
    for (const member of members) {
      const exe = archive.includes('win') ? 'piper.exe' : 'piper';
      await mkdir(join(dir, member, 'espeak-ng-data'), { recursive: true });
      await writeFile(join(dir, member, exe), 'piper exe', { mode: 0o644 });
      await writeFile(join(dir, member, 'libonnxruntime.so'), 'lib');
      await writeFile(join(dir, member, 'espeak-ng-data', 'en_dict'), 'dict');
    }
    return;
  }
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
    const piperEntries = Object.values(pinned.piper.archives);
    const voiceFiles = pinned.voices.items.flatMap((v) => v.files);
    expect(Object.keys(pinned.piper.archives).sort()).toEqual([
      'darwin-arm64',
      'darwin-x64',
      'linux-x64',
      'win32-x64',
    ]);
    expect(
      [...piperEntries, ...voiceFiles].filter(
        (e) => !/^https:\/\//.test(e.url) || !/^[0-9a-f]{64}$/.test(e.sha256),
      ),
    ).toEqual([]);
    expect(
      piperEntries.filter(
        (a) => !a.url.includes(`/releases/download/${pinned.piper.version}/`) || a.root !== 'piper',
      ),
    ).toEqual([]);
    expect(pinned.voices.items.map((v) => v.name).sort()).toEqual([
      'en_GB-aru-medium',
      'en_GB-cori-medium',
    ]);
    expect(
      voiceFiles.filter((f) => !f.url.includes(`/resolve/${pinned.voices.commit}/en/en_GB/`)),
    ).toEqual([]);
    expect(pinned.fonts.map((f) => f.file).sort()).toEqual([
      'NotoSans-Bold.ttf',
      'NotoSans-Regular.ttf',
      'OFL.txt',
    ]);
  });

  it('the pinned table names Rhubarb Lip Sync 1.14.0 (MIT) with one release zip and its SHA-256 per target, the macOS zip for both Mac archs', () => {
    const pinned = JSON.parse(readFileSync(TABLE_PATH, 'utf8')) as typeof table;
    const release =
      'https://github.com/DanielSWolf/rhubarb-lip-sync/releases/download/v1.14.0/Rhubarb-Lip-Sync-1.14.0-';
    const { archives: pinnedArchives, ...about } = pinned.rhubarb;
    const archives: Record<string, { url: string; sha256: string; root: string }> = pinnedArchives;

    expect(about).toEqual({
      version: '1.14.0',
      source: 'https://github.com/DanielSWolf/rhubarb-lip-sync',
      license: 'MIT',
    });
    expect(
      Object.entries(archives)
        .map(([target, a]) => [target, a.url, a.root])
        .sort(),
    ).toEqual([
      ['darwin-arm64', `${release}macOS.zip`, 'Rhubarb-Lip-Sync-1.14.0-macOS'],
      ['darwin-x64', `${release}macOS.zip`, 'Rhubarb-Lip-Sync-1.14.0-macOS'],
      ['linux-x64', `${release}Linux.zip`, 'Rhubarb-Lip-Sync-1.14.0-Linux'],
      ['win32-x64', `${release}Windows.zip`, 'Rhubarb-Lip-Sync-1.14.0-Windows'],
    ]);
    expect(Object.values(archives).filter((a) => !/^[0-9a-f]{64}$/.test(a.sha256))).toEqual([]);
    expect(archives['darwin-x64']!.sha256).toBe(archives['darwin-arm64']!.sha256);
    expect(new Set(Object.values(archives).map((a) => a.sha256)).size).toBe(3);
  });

  it('fetches rhubarb for the target and takes its executable, its res folder and its licence, without the editor plugins and test fixtures, with its notice', async () => {
    const { resourcesDir, cacheDir } = await folders();
    rhubarbMembers.splice(0);

    await fetchMediaTools({
      target: 'linux-x64',
      table,
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    });

    const rhubarb = join(resourcesDir, 'media-tools', 'linux-x64', 'rhubarb');
    expect((await readdir(rhubarb)).sort()).toEqual(['LICENSE.md', 'NOTICE.txt', 'res', 'rhubarb']);
    expect(rhubarbMembers).toEqual([
      'Rhubarb-Linux/rhubarb',
      'Rhubarb-Linux/res',
      'Rhubarb-Linux/LICENSE.md',
    ]);
    expect(await readFile(join(rhubarb, 'res', 'sphinx', 'cmudict-en-us.dict'), 'utf8')).toBe(
      'dictionary',
    );
    if (process.platform !== 'win32')
      expect(statSync(join(rhubarb, 'rhubarb')).mode & 0o777).toBe(0o755);
    const notice = await readFile(join(rhubarb, 'NOTICE.txt'), 'utf8');
    expect(notice).toContain('rhubarb 1.14.0-test, Rhubarb Lip Sync (MIT)');
    expect(notice).toContain('https://github.com/DanielSWolf/rhubarb-lip-sync');
    expect(notice).toContain(`Archive SHA-256: ${sha(RHUBARB)}`);
    expect(notice).toContain('separate program');
    expect(notice).toContain('PocketSphinx under its BSD licence');
    expect(notice).toContain('Licence: LICENSE.md beside this file.');
  });

  it('a rhubarb archive whose digest is not the pinned one is refused and nothing of it is kept', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const { fetch } = fakeFetch({
      ...BODIES,
      'https://example.test/rhubarb-Linux.zip': 'tampered',
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
      `media tools: checksum mismatch for https://example.test/rhubarb-Linux.zip: expected ${sha(RHUBARB)}, got ${sha('tampered')}`,
    );
    expect(existsSync(join(cacheDir, 'rhubarb-Linux.zip'))).toBe(false);
    expect(existsSync(join(resourcesDir, 'media-tools', 'linux-x64', 'rhubarb'))).toBe(false);
  });

  it('a new ffmpeg build replaces only its own files and leaves the piper and rhubarb folders in place', async () => {
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
    const extracted: string[] = [];

    await fetchMediaTools({
      target: 'linux-x64',
      table: { ...table, ffmpeg: { ...table.ffmpeg, version: 'n9.0.3-test' } },
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: async (archive: string, dir: string, members: readonly string[]) => {
        extracted.push(archive);
        await fakeExtract(archive, dir, members);
      },
      log: () => undefined,
    });

    const tools = join(resourcesDir, 'media-tools', 'linux-x64');
    expect(extracted).toEqual([join(cacheDir, 'ffmpeg-linux64.tar.xz')]);
    expect(existsSync(join(tools, 'rhubarb', 'rhubarb'))).toBe(true);
    expect(existsSync(join(tools, 'piper', 'piper'))).toBe(true);
    expect(await readFile(join(tools, 'NOTICE.txt'), 'utf8')).toContain('n9.0.3-test');
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
    expect((await readdir(tools)).sort()).toEqual([
      'LICENSE.txt',
      'NOTICE.txt',
      'ffmpeg',
      'piper',
      'rhubarb',
    ]);
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

  it('a macOS target gets piper and the voices and no ffmpeg', async () => {
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

    expect(urls).not.toContain('https://example.test/ffmpeg-linux64.tar.xz');
    expect(urls).toContain('https://example.test/piper-mac.tar.gz');
    expect((await readdir(join(resourcesDir, 'fonts'))).sort()).toEqual([
      'NotoSans-Regular.ttf',
      'OFL.txt',
    ]);
    expect(await readFile(join(resourcesDir, 'fonts', 'NotoSans-Regular.ttf'), 'utf8')).toBe(FONT);
    expect((await readdir(join(resourcesDir, 'voices'))).sort()).toEqual([
      'LICENSES.txt',
      'en_GB-aru-medium.onnx',
      'en_GB-cori-medium.MODEL_CARD',
      'en_GB-cori-medium.onnx',
    ]);
    expect((await readdir(join(resourcesDir, 'media-tools', 'darwin-arm64'))).sort()).toEqual([
      'piper',
      'rhubarb',
    ]);
    expect(lines).toEqual([
      'media tools: no ffmpeg bundled for darwin-arm64; the worker uses ffmpeg from the system path',
      `media tools: darwin-arm64 ready in ${resourcesDir}`,
    ]);
  });

  it('fetches piper for the target and extracts the whole folder with its notice', async () => {
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

    const piper = join(resourcesDir, 'media-tools', 'linux-x64', 'piper');
    expect((await readdir(piper)).sort()).toEqual([
      'NOTICE.txt',
      'espeak-ng-data',
      'libonnxruntime.so',
      'piper',
    ]);
    expect(await readFile(join(piper, 'espeak-ng-data', 'en_dict'), 'utf8')).toBe('dict');
    if (process.platform !== 'win32')
      expect(statSync(join(piper, 'piper')).mode & 0o777).toBe(0o755);
    const notice = await readFile(join(piper, 'NOTICE.txt'), 'utf8');
    expect(notice).toContain('piper 2023.11.14-test');
    expect(notice).toContain('MIT');
    expect(notice).toContain(table.piper.source);
    expect(notice).toContain('espeak-ng (GPL-3.0, source https://github.com/espeak-ng/espeak-ng)');
    expect(notice).toContain(table.piper.archives['linux-x64'].sha256);
    const licenses = await readFile(join(resourcesDir, 'voices', 'LICENSES.txt'), 'utf8');
    expect(licenses).toContain(
      'en_GB-cori-medium\nDataset: https://librivox.org\nLicence: public domain',
    );
    expect(licenses).toContain(
      'Attribution: University of Liverpool Acoustics Research Unit speech corpus',
    );
    expect(licenses).toContain('https://huggingface.co/rhasspy/piper-voices at commit abc123');
  });

  it('a voice whose hash does not match is not kept', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const { fetch } = fakeFetch({ ...BODIES, 'https://example.test/aru.onnx': 'tampered' });

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
      `media tools: checksum mismatch for https://example.test/aru.onnx: expected ${sha(MODEL)}, got ${sha('tampered')}`,
    );
    expect(existsSync(join(cacheDir, 'voices', 'en_GB-aru-medium.onnx'))).toBe(false);
    expect(existsSync(join(resourcesDir, 'voices', 'en_GB-aru-medium.onnx'))).toBe(false);
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

  it.skipIf(process.platform === 'win32')(
    'a zip is extracted whole with the system unzip outside Windows, where tar reads no zip',
    async () => {
      const { cacheDir } = await folders();
      const source = join(cacheDir, 'source');
      const out = join(cacheDir, 'out');
      await mkdir(join(source, 'Rhubarb-Linux', 'res'), { recursive: true });
      await mkdir(out, { recursive: true });
      await writeFile(join(source, 'Rhubarb-Linux', 'rhubarb'), 'rhubarb exe');
      await writeFile(join(source, 'Rhubarb-Linux', 'res', 'model'), 'model');
      const archive = join(cacheDir, 'rhubarb.zip');
      expect(spawnSync('zip', ['-qr', archive, 'Rhubarb-Linux'], { cwd: source }).status).toBe(0);

      extractArchive(archive, out, ['Rhubarb-Linux']);

      expect(await readFile(join(out, 'Rhubarb-Linux', 'rhubarb'), 'utf8')).toBe('rhubarb exe');
      expect(await readFile(join(out, 'Rhubarb-Linux', 'res', 'model'), 'utf8')).toBe('model');
    },
  );

  it.skipIf(process.platform === 'win32')(
    'the system unzip failing on a zip it cannot read fails the build, naming the members and the archive',
    async () => {
      const { cacheDir } = await folders();
      await mkdir(cacheDir, { recursive: true });
      const archive = join(cacheDir, 'not-a-zip.zip');
      await writeFile(archive, 'these bytes are not a zip');

      let failure: Error | null = null;
      try {
        extractArchive(archive, cacheDir, ['Rhubarb-Linux']);
      } catch (err: unknown) {
        failure = err as Error;
      }

      expect(failure?.message).toBe(
        `media tools: unzip could not extract Rhubarb-Linux from ${archive}`,
      );
    },
  );

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

  const rhubarbOfSize = (size: number) => ({
    ...table,
    rhubarb: {
      ...table.rhubarb,
      archives: {
        ...table.rhubarb.archives,
        'linux-x64': { ...table.rhubarb.archives['linux-x64'], size },
      },
    },
  });

  it('every pinned rhubarb archive carries its byte size', () => {
    const pinned = JSON.parse(readFileSync(TABLE_PATH, 'utf8')) as {
      rhubarb: { archives: Record<string, { size?: number }> };
    };

    expect(
      Object.entries(pinned.rhubarb.archives).filter(
        ([, a]) => !Number.isInteger(a.size) || a.size! <= 0,
      ),
    ).toEqual([]);
  });

  it('a download whose byte length is not the pinned size is refused, even with the pinned digest, and nothing of it is kept', async () => {
    const { resourcesDir, cacheDir } = await folders();
    const expected = Buffer.byteLength(RHUBARB) + 1;

    const failure = await fetchMediaTools({
      target: 'linux-x64',
      table: rhubarbOfSize(expected),
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    }).then(
      () => null,
      (err: unknown) => err as Error,
    );

    expect(failure?.message).toBe(
      `media tools: size mismatch for https://example.test/rhubarb-Linux.zip: expected ${expected}, got ${Buffer.byteLength(RHUBARB)}`,
    );
    expect(existsSync(join(cacheDir, 'rhubarb-Linux.zip'))).toBe(false);
    expect(existsSync(join(cacheDir, 'rhubarb-Linux.zip.partial'))).toBe(false);
    expect(existsSync(join(resourcesDir, 'media-tools', 'linux-x64', 'rhubarb'))).toBe(false);
  });

  it('a download of the pinned size and digest is kept and extracted', async () => {
    const { resourcesDir, cacheDir } = await folders();

    await fetchMediaTools({
      target: 'linux-x64',
      table: rhubarbOfSize(Buffer.byteLength(RHUBARB)),
      resourcesDir,
      cacheDir,
      fetch: fakeFetch().fetch,
      extract: fakeExtract,
      log: () => undefined,
    });

    expect(await readFile(join(cacheDir, 'rhubarb-Linux.zip'), 'utf8')).toBe(RHUBARB);
    expect(
      await readFile(join(resourcesDir, 'media-tools', 'linux-x64', 'rhubarb', 'rhubarb'), 'utf8'),
    ).toBe('rhubarb exe');
  });
});
