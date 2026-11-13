import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, createReadStream, existsSync, readFileSync } from 'node:fs';
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

/**
 * The pinned downloads: the ffmpeg, piper and rhubarb archives of each bundled platform, the tesseract AppImage of
 * linux-x64 with its language data, the caption fonts and the piper voices with their licences.
 */
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
 * Downloads `entry.url` into `file` unless the file is there with the pinned SHA-256; a download of another length
 * than the pinned `size`, where the entry has one, or with another hash is never kept and fails.
 */
async function fetchPinned(entry, file, fetch) {
  if (existsSync(file) && (await sha256Of(file)) === entry.sha256) return;
  const response = await fetch(entry.url);
  if (!response.ok) throw new Error(`media tools: ${entry.url} answered ${response.status}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (entry.size !== undefined && bytes.length !== entry.size) {
    throw new Error(
      `media tools: size mismatch for ${entry.url}: expected ${entry.size}, got ${bytes.length}`,
    );
  }
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
 * Extracts `members` of `archive` into `dir`: a zip outside Windows with the system unzip, which takes the whole
 * archive into `dir` since GNU tar reads no zip, so the caller copies only the members; anything else with
 * `extractWithTar`.
 */
export function extractArchive(archive, dir, members) {
  if (!archive.endsWith('.zip') || process.platform === 'win32') {
    extractWithTar(archive, dir, members);
    return;
  }
  const result = spawnSync('unzip', ['-q', '-o', archive, '-d', dir], { stdio: 'inherit' });
  if (result.status !== 0) {
    throw new Error(`media tools: unzip could not extract ${members.join(', ')} from ${archive}`);
  }
}

function rhubarbNoticeOf(rhubarb, sha256) {
  return [
    `rhubarb ${rhubarb.version}, Rhubarb Lip Sync (${rhubarb.license}):`,
    rhubarb.source,
    `Archive SHA-256: ${sha256}`,
    'Scout runs rhubarb as a separate program. Its archive bundles PocketSphinx under its BSD licence',
    '(https://github.com/cmusphinx/pocketsphinx); res/ holds its acoustic model.',
    'Licence: LICENSE.md beside this file.',
    '',
  ].join('\n');
}

/**
 * Runs `appImage` with `--appimage-extract` in `dir`, which writes the AppImage's contents into `dir/squashfs-root`.
 */
export function extractAppImage(appImage, dir) {
  chmodSync(appImage, 0o755);
  const result = spawnSync(appImage, ['--appimage-extract'], {
    cwd: dir,
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  if (result.status !== 0 || !existsSync(join(dir, 'squashfs-root'))) {
    throw new Error(`media tools: ${appImage} could not extract itself into ${dir}`);
  }
}

function tesseractNoticeOf(tesseract, tessdata, sha256) {
  return [
    `tesseract ${tesseract.version}, the OCR engine of ${tesseract.project} (Apache-2.0),`,
    `as the x86_64 AppImage of ${tesseract.release}:`,
    `AppImage SHA-256: ${sha256}`,
    `Tesseract source of this build: ${tesseract.source}`,
    `The AppImage also contains leptonica (BSD-2-Clause, source ${tesseract.leptonica}),`,
    'its image libraries and the GNU C library with its loader; Scout runs tesseract as a separate program through AppRun.',
    `Language data: ${tessdata.repo} at commit ${tessdata.commit} (${tessdata.license}, tessdata/LICENSE).`,
    '',
  ].join('\n');
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
      if (!['piper', 'rhubarb', 'tesseract', 'face'].includes(name)) {
        await rm(join(toolsDir, name), { recursive: true, force: true });
      }
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
 * Takes the rhubarb executable, its `res/` folder, which it finds beside itself, and its licence from the archive
 * into `<toolsDir>/rhubarb/` with a notice; the editor plugins and test fixtures in the archive stay out.
 */
async function fetchRhubarb({ target, table, toolsDir, cacheDir, fetch, extract }) {
  const archive = table.rhubarb.archives[target];
  if (archive === undefined) return false;
  const rhubarbDir = join(toolsDir, 'rhubarb');
  const exeName = target.startsWith('win32-') ? 'rhubarb.exe' : 'rhubarb';
  const taken = [exeName, 'res', 'LICENSE.md'];
  const notice = rhubarbNoticeOf(table.rhubarb, archive.sha256);
  const noticeFile = join(rhubarbDir, 'NOTICE.txt');
  if (
    existsSync(noticeFile) &&
    (await readFile(noticeFile, 'utf8')) === notice &&
    taken.every((name) => existsSync(join(rhubarbDir, name)))
  ) {
    return true;
  }
  const cached = join(cacheDir, basename(new URL(archive.url).pathname));
  await fetchPinned(archive, cached, fetch);
  const scratch = await mkdtemp(join(cacheDir, 'extract-'));
  try {
    await extract(
      cached,
      scratch,
      taken.map((name) => `${archive.root}/${name}`),
    );
    await rm(rhubarbDir, { recursive: true, force: true });
    await mkdir(rhubarbDir, { recursive: true });
    for (const name of taken) {
      await cp(join(scratch, archive.root, name), join(rhubarbDir, name), {
        recursive: true,
        verbatimSymlinks: true,
      });
    }
    if (!target.startsWith('win32-')) await chmod(join(rhubarbDir, exeName), 0o755);
    await writeFile(noticeFile, notice);
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
  return true;
}

function faceNoticeOf(face) {
  return [
    'Face models Scout runs in-process to find the mouth on a presenter picture:',
    '',
    ...face.files.flatMap((model) => [
      `${model.file}: ${model.model}`,
      `Source: ${model.repo} at commit ${model.commit}`,
      `SHA-256: ${model.sha256}`,
      `Licence: ${model.license}`,
      ...(model.outOfScope === undefined ? [] : [model.outOfScope]),
      '',
    ]),
  ].join('\n');
}

/**
 * Puts the pinned face models into `<toolsDir>/face/` under their fixed names, each checked against its size and
 * SHA-256, with a notice naming their sources, commits, digests and licences.
 */
async function fetchFace({ table, toolsDir, cacheDir, fetch }) {
  const faceDir = join(toolsDir, 'face');
  await mkdir(faceDir, { recursive: true });
  for (const model of table.face.files) {
    const dest = join(faceDir, model.file);
    if (existsSync(dest) && (await sha256Of(dest)) === model.sha256) continue;
    const cached = join(cacheDir, 'face', model.file);
    await fetchPinned(model, cached, fetch);
    await copyFile(cached, dest);
  }
  await writeFile(join(faceDir, 'NOTICE.txt'), faceNoticeOf(table.face));
}

async function fetchTesseract({ target, table, toolsDir, cacheDir, fetch, extractAppImage }) {
  const archive = table.tesseract.archives[target];
  if (archive === undefined) return false;
  const tesseractDir = join(toolsDir, 'tesseract');
  const notice = tesseractNoticeOf(table.tesseract, table.tessdata, archive.sha256);
  const noticeFile = join(tesseractDir, 'NOTICE.txt');
  const inPlace =
    existsSync(noticeFile) &&
    (await readFile(noticeFile, 'utf8')) === notice &&
    existsSync(join(tesseractDir, 'AppRun'));
  if (!inPlace) {
    const cached = join(cacheDir, basename(new URL(archive.url).pathname));
    await fetchPinned(archive, cached, fetch);
    const scratch = await mkdtemp(join(cacheDir, 'extract-'));
    try {
      await extractAppImage(cached, scratch);
      await rm(tesseractDir, { recursive: true, force: true });
      await mkdir(toolsDir, { recursive: true });
      await cp(join(scratch, archive.root), tesseractDir, {
        recursive: true,
        verbatimSymlinks: true,
      });
      for (const entry of await readdir(tesseractDir, { recursive: true, withFileTypes: true })) {
        if (entry.isDirectory()) await chmod(join(entry.parentPath, entry.name), 0o755);
      }
      for (const name of ['.', 'AppRun', 'ld-linux-x86-64.so.2', join('usr', 'bin', 'tesseract')]) {
        await chmod(join(tesseractDir, name), 0o755);
      }
      await writeFile(noticeFile, notice);
    } finally {
      await rm(scratch, { recursive: true, force: true });
    }
  }
  const tessdataDir = join(tesseractDir, 'tessdata');
  await mkdir(tessdataDir, { recursive: true });
  for (const entry of table.tessdata.files) {
    const dest = join(tessdataDir, entry.file);
    if (existsSync(dest) && (await sha256Of(dest)) === entry.sha256) continue;
    const cached = join(cacheDir, 'tessdata', entry.file);
    await fetchPinned(entry, cached, fetch);
    await copyFile(cached, dest);
  }
  const licence = table.tesseract.leptonicaLicense;
  const licenceDest = join(tesseractDir, licence.file);
  if (!existsSync(licenceDest) || (await sha256Of(licenceDest)) !== licence.sha256) {
    const cached = join(cacheDir, licence.file);
    await fetchPinned(licence, cached, fetch);
    await copyFile(cached, licenceDest);
  }
  return true;
}

/**
 * Puts the pinned fonts with their licence into `<resourcesDir>/fonts/` and the pinned piper voices with their
 * licences into `<resourcesDir>/voices/` on every target, and the pinned ffmpeg with its licence and the
 * whole pinned piper and rhubarb folders into `<resourcesDir>/media-tools/<target>/` where the table has an
 * archive for the target, the pinned face models into `face/` there on every target, and the whole pinned tesseract AppImage with its language data in `tesseract/tessdata/`
 * there too where the table has one; each download checked against its SHA-256 and kept in `cacheDir`. Nothing is
 * downloaded or extracted again when it is already in place.
 */
export async function fetchMediaTools({
  target,
  table,
  resourcesDir,
  cacheDir,
  fetch,
  extract,
  extractAppImage,
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
  const options = { target, table, toolsDir, cacheDir, fetch, extract, extractAppImage };
  if (!(await fetchFfmpeg(options))) {
    log(
      `media tools: no ffmpeg bundled for ${target}; the worker uses ffmpeg from the system path`,
    );
  }
  if (!(await fetchPiper(options))) {
    log(`media tools: no piper bundled for ${target}; the worker has no voice engine`);
  }
  if (!(await fetchRhubarb(options))) {
    log(`media tools: no rhubarb bundled for ${target}; the worker has no lip sync`);
  }
  await fetchFace(options);
  if (!(await fetchTesseract(options))) {
    log(
      `media tools: no tesseract bundled for ${target}; the worker cannot read text on a screenshot`,
    );
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
    extract: extractArchive,
    extractAppImage,
    log: (line) => console.log(line),
  });
}
