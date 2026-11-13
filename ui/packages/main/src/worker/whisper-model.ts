import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import type { WhisperVariant } from '@scout/shared';

/** One Whisper model as published in ggerganov/whisper.cpp: its file name, byte size and SHA-256. */
export interface WhisperModelFile {
  readonly file: string;
  readonly size: number;
  readonly sha256: string;
}

export type WhisperModelTable = Readonly<Record<WhisperVariant, WhisperModelFile>>;

/** The models Scout downloads and runs, pinned by size and SHA-256; a file that does not match is never used. */
export const WHISPER_MODEL_FILES: WhisperModelTable = Object.freeze({
  tiny: {
    file: 'ggml-tiny.bin',
    size: 77_691_713,
    sha256: 'be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21',
  },
  base: {
    file: 'ggml-base.bin',
    size: 147_951_465,
    sha256: '60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe',
  },
  small: {
    file: 'ggml-small.bin',
    size: 487_601_967,
    sha256: '1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b',
  },
  'large-v3-turbo': {
    file: 'ggml-large-v3-turbo.bin',
    size: 1_624_555_275,
    sha256: '1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69',
  },
  'large-v3': {
    file: 'ggml-large-v3.bin',
    size: 3_095_033_483,
    sha256: '64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2',
  },
});

const PREFERENCE: readonly WhisperVariant[] = [
  'large-v3-turbo',
  'large-v3',
  'small',
  'base',
  'tiny',
];

/** The SHA-256 of a file, read as a stream. */
export async function sha256OfFile(file: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk as Buffer);
  return hash.digest('hex');
}

interface FileState {
  readonly size: number;
  readonly mtimeMs: number;
}

function sidecarText(sha256: string, state: FileState): string {
  return JSON.stringify({ sha256, size: state.size, mtimeMs: state.mtimeMs });
}

/** Records that `file`, as it is now, hashed to `sha256`, so the next check skips the hash. */
export async function writeWhisperSidecar(file: string, sha256: string): Promise<void> {
  await writeFile(`${file}.sha256`, sidecarText(sha256, await stat(file)));
}

/** The hash the sidecar records for `file` in its current size and mtime, or null when it records none for this state. */
function recordedDigest(text: string | null, state: FileState): string | null {
  if (text === null) return null;
  try {
    const recorded = JSON.parse(text) as Partial<Record<'sha256' | 'size' | 'mtimeMs', unknown>>;
    return recorded.size === state.size &&
      recorded.mtimeMs === state.mtimeMs &&
      typeof recorded.sha256 === 'string'
      ? recorded.sha256
      : null;
  } catch {
    return null;
  }
}

/**
 * True when `file` has the pinned size and SHA-256. The hash is taken again only when the `<file>.sha256`
 * sidecar is missing or the file's size or mtime changed since it was written; the sidecar records the hash the
 * file has, matching or not, so a file that does not match is not hashed again on every check.
 */
export async function verifyWhisperModel(file: string, pin: WhisperModelFile): Promise<boolean> {
  const current = await stat(file).catch(() => null);
  if (current === null || !current.isFile() || current.size !== pin.size) return false;

  const recorded = recordedDigest(
    await readFile(`${file}.sha256`, 'utf8').catch(() => null),
    current,
  );
  if (recorded !== null) return recorded === pin.sha256;

  const digest = await sha256OfFile(file).catch(() => null);
  if (digest === null) return false;
  await writeFile(`${file}.sha256`, sidecarText(digest, current)).catch(() => undefined);
  return digest === pin.sha256;
}

/** The variant an `stt.model` value names (`whisper-<variant>` or `<variant>`), or null when it names none of `table`. */
export function variantOf(
  preferred: string | null,
  table: WhisperModelTable = WHISPER_MODEL_FILES,
): WhisperVariant | null {
  if (preferred === null) return null;
  const name = preferred.startsWith('whisper-') ? preferred.slice('whisper-'.length) : preferred;
  return Object.hasOwn(table, name) ? (name as WhisperVariant) : null;
}

/**
 * The path of the Whisper model the worker should load from `dir`: `preferred` (the `stt.model` setting, as
 * `whisper-<variant>` or `<variant>`) when it is installed and verified, otherwise the best verified one
 * (large-v3-turbo, large-v3, small, base, tiny), or null when none is.
 */
export async function chooseWhisperModel(
  dir: string,
  preferred: string | null,
  table: WhisperModelTable = WHISPER_MODEL_FILES,
): Promise<string | null> {
  const first = variantOf(preferred, table);
  const order = first === null ? PREFERENCE : [first, ...PREFERENCE.filter((v) => v !== first)];
  for (const variant of order) {
    const file = path.join(dir, table[variant].file);
    if (await verifyWhisperModel(file, table[variant])) return file;
  }
  return null;
}
