import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, stat, utimes, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  WHISPER_MODEL_FILES,
  chooseWhisperModel,
  verifyWhisperModel,
  type WhisperModelTable,
} from '../src/worker/whisper-model.js';

const sha256 = (text: string) => createHash('sha256').update(text).digest('hex');

function pin(file: string, content: string) {
  return { file, size: Buffer.byteLength(content), sha256: sha256(content) };
}

const TABLE: WhisperModelTable = {
  tiny: pin('ggml-tiny.bin', 'tiny model'),
  base: pin('ggml-base.bin', 'base model'),
  small: pin('ggml-small.bin', 'small model'),
  'large-v3-turbo': pin('ggml-large-v3-turbo.bin', 'turbo model'),
  'large-v3': pin('ggml-large-v3.bin', 'large model'),
};

describe('Whisper model table', () => {
  it('the table pins the offered models by size and sha256', () => {
    expect(WHISPER_MODEL_FILES).toEqual({
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
  });
});

describe('chooseWhisperModel', () => {
  let dir: string;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(os.tmpdir(), 'scout-whisper-'));
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  const install = (variant: keyof WhisperModelTable, content: string) =>
    writeFile(path.join(dir, TABLE[variant].file), content);

  it('stt.model is preferred when installed and verified', async () => {
    await install('large-v3-turbo', 'turbo model');
    await install('base', 'base model');

    expect(await chooseWhisperModel(dir, 'whisper-base', TABLE)).toBe(
      path.join(dir, 'ggml-base.bin'),
    );
    expect(await chooseWhisperModel(dir, 'base', TABLE)).toBe(path.join(dir, 'ggml-base.bin'));
  });

  it('without a usable stt.model the best verified model is chosen: large-v3-turbo, large-v3, small, base, tiny', async () => {
    await install('tiny', 'tiny model');
    await install('small', 'small model');

    expect(await chooseWhisperModel(dir, null, TABLE)).toBe(path.join(dir, 'ggml-small.bin'));
    expect(await chooseWhisperModel(dir, 'whisper-large-v3', TABLE)).toBe(
      path.join(dir, 'ggml-small.bin'),
    );
    expect(await chooseWhisperModel(dir, 'whisper-medium', TABLE)).toBe(
      path.join(dir, 'ggml-small.bin'),
    );
  });

  it('a file with a wrong hash is never chosen, even when it is stt.model and has the pinned size', async () => {
    await install('base', 'BASE MODEL');
    await install('tiny', 'tiny model');

    expect(await chooseWhisperModel(dir, 'whisper-base', TABLE)).toBe(
      path.join(dir, 'ggml-tiny.bin'),
    );
    await rm(path.join(dir, 'ggml-tiny.bin'));
    expect(await chooseWhisperModel(dir, 'whisper-base', TABLE)).toBeNull();
  });

  it('a file of another size is never chosen, and an empty or missing folder chooses nothing', async () => {
    await install('large-v3-turbo', 'turbo model, truncated');
    await install('large-v3', '');

    expect(await chooseWhisperModel(dir, null, TABLE)).toBeNull();
    expect(await chooseWhisperModel(path.join(dir, 'missing'), null, TABLE)).toBeNull();
  });

  it('a verified file gets a sidecar and is trusted without hashing again until its size or mtime changes', async () => {
    const file = path.join(dir, 'ggml-base.bin');
    const verifiedAt = new Date('2026-11-01T10:00:00Z');
    await install('base', 'base model');
    await utimes(file, verifiedAt, verifiedAt);
    expect(await verifyWhisperModel(file, TABLE.base)).toBe(true);
    const sidecar = JSON.parse(await readFile(`${file}.sha256`, 'utf8')) as Record<string, unknown>;

    await writeFile(file, 'BASE MODEL');
    await utimes(file, verifiedAt, verifiedAt);
    const trusted = await verifyWhisperModel(file, TABLE.base);
    await utimes(file, verifiedAt, new Date(verifiedAt.getTime() + 5_000));
    const rehashed = await verifyWhisperModel(file, TABLE.base);

    expect(sidecar).toEqual({
      sha256: TABLE.base.sha256,
      size: TABLE.base.size,
      mtimeMs: verifiedAt.getTime(),
    });
    expect(trusted).toBe(true);
    expect(rehashed).toBe(false);
  });

  it('a good file is still verified when its sidecar cannot be written', async () => {
    const file = path.join(dir, 'ggml-base.bin');
    await install('base', 'base model');
    await mkdir(`${file}.sha256`);

    expect(await verifyWhisperModel(file, TABLE.base)).toBe(true);
  });

  it('a sidecar recorded for another hash is not trusted', async () => {
    const file = path.join(dir, 'ggml-base.bin');
    await install('base', 'BASE MODEL');
    const s = await stat(file);
    await writeFile(
      `${file}.sha256`,
      JSON.stringify({ sha256: sha256('BASE MODEL'), size: s.size, mtimeMs: s.mtimeMs }),
    );

    expect(await verifyWhisperModel(file, TABLE.base)).toBe(false);
  });
});
