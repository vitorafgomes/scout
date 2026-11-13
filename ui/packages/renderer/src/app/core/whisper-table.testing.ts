import type { WhisperModelTableResponse } from '@scout/shared';

/** Main's pinned Whisper table as `scout:whisper.modelTable` returns it today. */
export const MAIN_WHISPER_TABLE: WhisperModelTableResponse = {
  models: [
    { variant: 'tiny', file: 'ggml-tiny.bin', size: 77_691_713 },
    { variant: 'base', file: 'ggml-base.bin', size: 147_951_465 },
    { variant: 'small', file: 'ggml-small.bin', size: 487_601_967 },
    { variant: 'large-v3-turbo', file: 'ggml-large-v3-turbo.bin', size: 1_624_555_275 },
    { variant: 'large-v3', file: 'ggml-large-v3.bin', size: 3_095_033_483 },
  ],
};

/**
 * A table no renderer copy could produce: another order, one variant missing, and sizes that format differently
 * from any size the renderer once carried, so a list that reads anything but main's answer shows it.
 */
export const OTHER_WHISPER_TABLE: WhisperModelTableResponse = {
  models: [
    { variant: 'large-v3', file: 'ggml-large-v3.bin', size: 2_200_000_000 },
    { variant: 'small', file: 'ggml-small.bin', size: 300_000_000 },
    { variant: 'tiny', file: 'ggml-tiny.bin', size: 50_000_000 },
    { variant: 'base', file: 'ggml-base.bin', size: 200_000_000 },
  ],
};
