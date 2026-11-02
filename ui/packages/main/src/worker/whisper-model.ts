import { stat } from 'node:fs/promises';
import * as path from 'node:path';
import type { WhisperVariant } from '@scout/shared';

export const WHISPER_MODEL_FILES: Readonly<Record<WhisperVariant, string>> = Object.freeze({
  'large-v3': 'ggml-large-v3.bin',
  'large-v3-turbo': 'ggml-large-v3-turbo.bin',
});

const PREFERENCE: readonly WhisperVariant[] = ['large-v3-turbo', 'large-v3'];

/** The path of the best installed Whisper model in `dir` (large-v3-turbo, else large-v3), or null when none is. */
export async function findBestWhisperModel(dir: string): Promise<string | null> {
  for (const variant of PREFERENCE) {
    const file = path.join(dir, WHISPER_MODEL_FILES[variant]);
    try {
      const s = await stat(file);
      if (s.isFile() && s.size > 0) return file;
    } catch {
      // not installed
    }
  }
  return null;
}
