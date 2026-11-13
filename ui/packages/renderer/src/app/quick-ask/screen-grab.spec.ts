import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { grabScreenshot } from './screen-grab';

const frame = { width: 0, height: 0 };
const drawn: { width: number; height: number }[] = [];
const encoded: { type: string; quality: unknown }[] = [];
let tracks: { stopped: boolean; stop: () => void }[] = [];
let getDisplayMedia: ReturnType<typeof vi.fn>;

function track() {
  const t = {
    stopped: false,
    stop: () => {
      t.stopped = true;
    },
  };
  return t;
}

describe('grabScreenshot', () => {
  beforeEach(() => {
    frame.width = 3840;
    frame.height = 2160;
    drawn.length = 0;
    encoded.length = 0;
    tracks = [track(), track()];
    getDisplayMedia = vi.fn(async () => ({ getTracks: () => tracks }));
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getDisplayMedia },
    });
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
    Object.defineProperty(HTMLVideoElement.prototype, 'videoWidth', {
      configurable: true,
      get: () => frame.width,
    });
    Object.defineProperty(HTMLVideoElement.prototype, 'videoHeight', {
      configurable: true,
      get: () => frame.height,
    });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(
      () =>
        ({
          drawImage: (_source: unknown, _x: number, _y: number, width: number, height: number) =>
            drawn.push({ width, height }),
        }) as unknown as CanvasRenderingContext2D,
    );
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(function (
      this: HTMLCanvasElement,
      type?: string,
      quality?: unknown,
    ) {
      encoded.push({ type: type ?? '', quality });
      return `data:image/jpeg;base64,${this.width}x${this.height}`;
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('scales to at most 1920x1080 and encodes JPEG at 0.8', async () => {
    const shot = await grabScreenshot();

    expect(getDisplayMedia).toHaveBeenCalledExactlyOnceWith({ video: true, audio: false });
    expect(shot).toEqual({ base64: '1920x1080', width: 1920, height: 1080 });
    expect(drawn).toEqual([{ width: 1920, height: 1080 }]);
    expect(encoded).toEqual([{ type: 'image/jpeg', quality: 0.8 }]);
  });

  it('keeps the aspect of a tall window and never scales a small one up', async () => {
    frame.width = 1200;
    frame.height = 2400;
    const tall = await grabScreenshot();
    frame.width = 800;
    frame.height = 600;
    const small = await grabScreenshot();

    expect([tall?.width, tall?.height]).toEqual([540, 1080]);
    expect([small?.width, small?.height]).toEqual([800, 600]);
  });

  it('stops every track after one frame', async () => {
    await grabScreenshot();

    expect(drawn).toHaveLength(1);
    expect(tracks.map((t) => t.stopped)).toEqual([true, true]);
  });

  it('stops every track when the frame cannot be taken', async () => {
    vi.mocked(HTMLMediaElement.prototype.play).mockRejectedValue(new Error('no frame'));

    await expect(grabScreenshot()).rejects.toThrow('no frame');
    expect(tracks.map((t) => t.stopped)).toEqual([true, true]);
  });

  it.each(['NotAllowedError', 'AbortError'])(
    'resolves null when the owner cancels the picker (%s)',
    async (name) => {
      getDisplayMedia.mockRejectedValue(new DOMException('cancelled', name));

      expect(await grabScreenshot()).toBeNull();
    },
  );

  it('throws any other failure of the picker', async () => {
    getDisplayMedia.mockRejectedValue(new DOMException('no capturer', 'NotReadableError'));

    await expect(grabScreenshot()).rejects.toThrow('no capturer');
  });
});
