import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { findBestWhisperModel } from '../src/worker/whisper-model.js';

describe('findBestWhisperModel', () => {
  let dir: string;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(os.tmpdir(), 'scout-whisper-'));
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  it('prefers large-v3-turbo when both models are installed', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3.bin'), 'model');
    await writeFile(path.join(dir, 'ggml-large-v3-turbo.bin'), 'model');

    expect(await findBestWhisperModel(dir)).toBe(path.join(dir, 'ggml-large-v3-turbo.bin'));
  });

  it('falls back to large-v3 when turbo is not installed', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3.bin'), 'model');

    expect(await findBestWhisperModel(dir)).toBe(path.join(dir, 'ggml-large-v3.bin'));
  });

  it('ignores an empty file left by an interrupted download', async () => {
    await writeFile(path.join(dir, 'ggml-large-v3-turbo.bin'), '');

    expect(await findBestWhisperModel(dir)).toBeNull();
  });

  it('returns null when no model is installed or the folder does not exist', async () => {
    expect(await findBestWhisperModel(dir)).toBeNull();
    expect(await findBestWhisperModel(path.join(dir, 'missing'))).toBeNull();
  });
});
