import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, readFileSync } from 'node:fs';
import { chmod, copyFile, mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, join } from 'node:path';
import { pipeline } from 'node:stream/promises';

const uiRoot = join(import.meta.dirname, '..', '..');

/** The pinned downloads: the ffmpeg archive of each bundled platform and the caption fonts with their licence. */
export const TABLE_PATH = join(uiRoot, 'build', 'media-tools.json');

/**
 * The archive bundled for `target` (`<platform>-<arch>`) and the archive members taken from it, keyed by the
 * name each gets beside the app; null where nothing is bundled.
 */
export function archiveFor(table, target) {
  const archive = table.ffmpeg.archives[target];
  if (archive === undefined) return null;
  const exe = target.startsWith('win32-') ? '.exe' : '';
  return {
    ...archive,
    members: {
      [`ffmpeg${exe}`]: `${archive.root}/bin/ffmpeg${exe}`,
      'LICENSE.txt': `${archive.root}/LICENSE.txt`,
    },
  };
}

async function sha256Of(file) {
  const hash = createHash('sha256');
  await pipeline(createReadStream(file), hash);
  return hash.digest('hex');
}

/**
 * Downloads `entry.url` into `file` unless the file is there with the pinned SHA-256; a download with another hash
 * is never kept and fails.
 */
async function fetchPinned(entry, file, fetch) {
  if (existsSync(file) && (await sha256Of(file)) === entry.sha256) return;
  const response = await fetch(entry.url);
  if (!response.ok) throw new Error(`media tools: ${entry.url} answered ${response.status}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  const actual = createHash('sha256').update(bytes).digest('hex');
  if (actual !== entry.sha256) {
    throw new Error(
      `media tools: checksum mismatch for ${entry.url}: expected ${entry.sha256}, got ${actual}`,
    );
  }
  await mkdir(dirname(file), { recursive: true });
  await writeFile(`${file}.partial`, bytes);
  await rename(`${file}.partial`, file);
}

function noticeOf(ffmpeg, sha256) {
  return [
    `ffmpeg ${ffmpeg.version}, the LGPL static build of BtbN/FFmpeg-Builds:`,
    ffmpeg.release,
    `Archive SHA-256: ${sha256}`,
    `FFmpeg source of this build: ${ffmpeg.source}`,
    'Licence: LICENSE.txt beside this file.',
    '',
  ].join('\n');
}

/**
 * Extracts `members` of `archive` into `dir` with the system tar; on Windows the tar.exe of System32, which reads zip.
 */
export function extractWithTar(archive, dir, members) {
  const tar =
    process.platform === 'win32'
      ? join(process.env['SystemRoot'] ?? 'C:\\Windows', 'System32', 'tar.exe')
      : 'tar';
  const result = spawnSync(tar, ['-xf', archive, '-C', dir, ...members], { stdio: 'inherit' });
  if (result.status !== 0) {
    throw new Error(`media tools: tar could not extract ${members.join(', ')} from ${archive}`);
  }
}

/**
 * Puts the pinned fonts with their licence into `<resourcesDir>/fonts/` on every target, and the pinned ffmpeg
 * and its licence into `<resourcesDir>/media-tools/<target>/` where the table has an archive for the
 * target; each download checked against its SHA-256 and kept in `cacheDir`. Nothing is downloaded or extracted
 * again when it is already in place.
 */
export async function fetchMediaTools({
  target,
  table,
  resourcesDir,
  cacheDir,
  fetch,
  extract,
  log,
}) {
  const fontsDir = join(resourcesDir, 'fonts');
  await mkdir(fontsDir, { recursive: true });
  for (const font of table.fonts) {
    const dest = join(fontsDir, font.file);
    if (existsSync(dest) && (await sha256Of(dest)) === font.sha256) continue;
    const cached = join(cacheDir, font.file);
    await fetchPinned(font, cached, fetch);
    await copyFile(cached, dest);
  }

  const archive = archiveFor(table, target);
  if (archive === null) {
    log(`media tools: none bundled for ${target}; the worker uses ffmpeg from the system path`);
    return;
  }

  const toolsDir = join(resourcesDir, 'media-tools', target);
  const notice = noticeOf(table.ffmpeg, archive.sha256);
  const noticeFile = join(toolsDir, 'NOTICE.txt');
  const inPlace =
    existsSync(noticeFile) &&
    (await readFile(noticeFile, 'utf8')) === notice &&
    Object.keys(archive.members).every((name) => existsSync(join(toolsDir, name)));
  if (!inPlace) {
    const cached = join(cacheDir, basename(new URL(archive.url).pathname));
    await fetchPinned(archive, cached, fetch);
    const scratch = await mkdtemp(join(cacheDir, 'extract-'));
    try {
      await extract(cached, scratch, Object.values(archive.members));
      await rm(toolsDir, { recursive: true, force: true });
      await mkdir(toolsDir, { recursive: true });
      for (const [name, member] of Object.entries(archive.members)) {
        await copyFile(join(scratch, member), join(toolsDir, name));
        if (name !== 'LICENSE.txt' && !target.startsWith('win32-')) {
          await chmod(join(toolsDir, name), 0o755);
        }
      }
      await writeFile(noticeFile, notice);
    } finally {
      await rm(scratch, { recursive: true, force: true });
    }
  }

  log(`media tools: ${target} ready in ${resourcesDir}`);
}

if (import.meta.main) {
  await fetchMediaTools({
    target: process.argv[2] ?? `${process.platform}-${process.arch}`,
    table: JSON.parse(readFileSync(TABLE_PATH, 'utf8')),
    resourcesDir: join(uiRoot, 'packages', 'main', 'resources'),
    cacheDir: join(uiRoot, 'build', 'media-cache'),
    fetch: (url) => globalThis.fetch(url),
    extract: extractWithTar,
    log: (line) => console.log(line),
  });
}
