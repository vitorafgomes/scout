export const MAX_WIDTH = 1920;
export const MAX_HEIGHT = 1080;
export const JPEG_QUALITY = 0.8;
const JPEG_DATA_URL = 'data:image/jpeg;base64,';

/** One frame of the screen or window the owner picked, as base64 JPEG. */
export interface Screenshot {
  readonly base64: string;
  readonly width: number;
  readonly height: number;
}

/**
 * Asks the system for one screen or window, takes a single frame of it, scaled to at most 1920x1080 and
 * encoded as JPEG at 0.8, and stops the capture. Resolves null when the owner cancels the picker.
 */
export async function grabScreenshot(): Promise<Screenshot | null> {
  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
  } catch (err: unknown) {
    if (
      err instanceof DOMException &&
      (err.name === 'NotAllowedError' || err.name === 'AbortError')
    ) {
      return null;
    }
    throw err;
  }
  try {
    const video = document.createElement('video');
    video.muted = true;
    video.srcObject = stream;
    await video.play();
    const { videoWidth, videoHeight } = video;
    if (videoWidth === 0 || videoHeight === 0) throw new Error('The capture has no frame.');
    const scale = Math.min(1, MAX_WIDTH / videoWidth, MAX_HEIGHT / videoHeight);
    const width = Math.round(videoWidth * scale);
    const height = Math.round(videoHeight * scale);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d');
    if (context === null) throw new Error('The canvas cannot draw.');
    context.drawImage(video, 0, 0, width, height);
    video.srcObject = null;
    const url = canvas.toDataURL('image/jpeg', JPEG_QUALITY);
    if (!url.startsWith(JPEG_DATA_URL)) throw new Error('The frame could not be encoded as JPEG.');
    return { base64: url.slice(JPEG_DATA_URL.length), width, height };
  } finally {
    stream.getTracks().forEach((track) => track.stop());
  }
}
