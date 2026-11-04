import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, readFileSync } from 'node:fs';
import {
  chmod,
  copyFile,
  cp,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rename,
  rm,
  writeFile,
} from 'node:fs/promises';
import { basename, dirname, join } from 'node:path';
import { pipeline } from 'node:stream/promises';

const uiRoot = join(import.meta.dirname, '..', '..');

/** The pinned downloads: the ffmpeg and piper archives of each bundled platform, the caption fonts and the piper voices with their licences. */
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

function piperNoticeOf(piper, sha256) {
  return [
    `piper ${piper.version}, the text-to-speech engine of ${piper.project} (MIT):`,
    piper.release,
    `Archive SHA-256: ${sha256}`,
    `Piper source of this build: ${piper.source}`,
    'The archive also contains espeak-ng (GPL-3.0, source https://github.com/espeak-ng/espeak-ng),',
    'onnxruntime (MIT) and piper-phonemize (MIT); Scout runs piper as a separate program.',
    '',
  ].join('\n');
}

function voiceLicensesOf(voices) {
  return [
    ...voices.items.flatMap((voice) => [
      voice.name,
      `Dataset: ${voice.dataset}`,
      `Licence: ${voice.license}`,
      ...(voice.attribution === undefined ? [] : [`Attribution: ${voice.attribution}`]),
      '',
    ]),
    `Models: ${voices.repo} at commit ${voices.commit}`,
    '',
  ].join('\n');
}

async function fetchFfmpeg({ target, table, toolsDir, cacheDir, fetch, extract }) {
  const archive = archiveFor(table, target);
  if (archive === null) return false;
  const notice = noticeOf(table.ffmpeg, archive.sha256);
  const noticeFile = join(toolsDir, 'NOTICE.txt');
  const inPlace =
    existsSync(noticeFile) &&
    (await readFile(noticeFile, 'utf8')) === notice &&
    Object.keys(archive.members).every((name) => existsSync(join(toolsDir, name)));
  if (inPlace) return true;
  const cached = join(cacheDir, basename(new URL(archive.url).pathname));
  await fetchPinned(archive, cached, fetch);
  const scratch = await mkdtemp(join(cacheDir, 'extract-'));
  try {
    await extract(cached, scratch, Object.values(archive.members));
    await mkdir(toolsDir, { recursive: true });
    for (const name of await readdir(toolsDir)) {
      if (name !== 'piper') await rm(join(toolsDir, name), { recursive: true, force: true });
    }
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
  return true;
}

async function fetchPiper({ target, table, toolsDir, cacheDir, fetch, extract }) {
  const archive = table.piper.archives[target];
  if (archive === undefined) return false;
  const piperDir = join(toolsDir, 'piper');
  const exe = join(piperDir, target.startsWith('win32-') ? 'piper.exe' : 'piper');
  const notice = piperNoticeOf(table.piper, archive.sha256);
  const noticeFile = join(piperDir, 'NOTICE.txt');
  if (
    existsSync(noticeFile) &&
    (await readFile(noticeFile, 'utf8')) === notice &&
    existsSync(exe)
  ) {
    return true;
  }
  const cached = join(cacheDir, basename(new URL(archive.url).pathname));
  await fetchPinned(archive, cached, fetch);
  const scratch = await mkdtemp(join(cacheDir, 'extract-'));
  try {
    await extract(cached, scratch, [archive.root]);
    await rm(piperDir, { recursive: true, force: true });
    await mkdir(toolsDir, { recursive: true });
    await cp(join(scratch, archive.root), piperDir, { recursive: true, verbatimSymlinks: true });
    if (!target.startsWith('win32-')) await chmod(exe, 0o755);
    await writeFile(noticeFile, notice);
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
  return true;
}

/**
 * Puts the pinned fonts with their licence into `<resourcesDir>/fonts/` and the pinned piper voices with their
 * licences into `<resourcesDir>/voices/` on every target, and the pinned ffmpeg with its licence and the
 * whole pinned piper folder into `<resourcesDir>/media-tools/<target>/` where the table has an archive for the
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

  const voicesDir = join(resourcesDir, 'voices');
  await mkdir(voicesDir, { recursive: true });
  for (const { file, url, sha256 } of table.voices.items.flatMap((voice) => voice.files)) {
    const dest = join(voicesDir, file);
    if (existsSync(dest) && (await sha256Of(dest)) === sha256) continue;
    const cached = join(cacheDir, 'voices', file);
    await fetchPinned({ url, sha256 }, cached, fetch);
    await copyFile(cached, dest);
  }
  await writeFile(join(voicesDir, 'LICENSES.txt'), voiceLicensesOf(table.voices));

  const toolsDir = join(resourcesDir, 'media-tools', target);
  const options = { target, table, toolsDir, cacheDir, fetch, extract };
  if (!(await fetchFfmpeg(options))) {
    log(
      `media tools: no ffmpeg bundled for ${target}; the worker uses ffmpeg from the system path`,
    );
  }
  if (!(await fetchPiper(options))) {
    log(`media tools: no piper bundled for ${target}; the worker has no voice engine`);
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
