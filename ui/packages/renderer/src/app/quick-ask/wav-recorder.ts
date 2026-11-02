import { Injectable } from '@angular/core';

export const SAMPLE_RATE = 16_000;
export const MAX_SAMPLES = SAMPLE_RATE * 5 * 60;

/** Encodes mono float samples as a 16-bit PCM WAV file. */
export function encodeWav(chunks: readonly Float32Array[], sampleRate: number): Uint8Array {
  const count = chunks.reduce((total, chunk) => total + chunk.length, 0);
  const bytes = new Uint8Array(44 + count * 2);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, text: string): void => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + count * 2, true);
  ascii(8, 'WAVE');
  ascii(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, 'data');
  view.setUint32(40, count * 2, true);
  let offset = 44;
  for (const chunk of chunks) {
    for (const sample of chunk) {
      const clamped = Math.max(-1, Math.min(1, sample));
      view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
      offset += 2;
    }
  }
  return bytes;
}

export function toBase64(bytes: Uint8Array): string {
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

/**
 * Records the microphone at 16 kHz mono into memory while held, up to 5 minutes;
 * nothing is written to disk and the stream stops when recording ends.
 */
@Injectable({ providedIn: 'root' })
export class WavRecorder {
  private stream: MediaStream | null = null;
  private context: AudioContext | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private processor: ScriptProcessorNode | null = null;
  private mute: GainNode | null = null;
  private starting: Promise<void> | null = null;
  private chunks: Float32Array[] = [];
  private count = 0;

  /** Opens the microphone; a recording still open is released first. */
  async start(onLimit: () => void): Promise<void> {
    const starting = this.release().then(() => {
      this.chunks = [];
      this.count = 0;
      return this.open(onLimit);
    });
    this.starting = starting;
    try {
      await starting;
    } finally {
      if (this.starting === starting) this.starting = null;
    }
  }

  /**
   * Stops the microphone, waiting for a start still in progress so no stream is left open,
   * and returns the WAV recorded, or null when nothing was heard.
   */
  async stop(): Promise<Uint8Array | null> {
    await this.starting?.catch(() => undefined);
    const sampleRate = this.context?.sampleRate ?? SAMPLE_RATE;
    const wav = this.count === 0 ? null : encodeWav(this.chunks, sampleRate);
    await this.release();
    this.chunks = [];
    this.count = 0;
    return wav;
  }

  private async open(onLimit: () => void): Promise<void> {
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
    });
    this.context = new AudioContext({ sampleRate: SAMPLE_RATE });
    this.source = this.context.createMediaStreamSource(this.stream);
    this.processor = this.context.createScriptProcessor(4096, 1, 1);
    this.processor.onaudioprocess = (event: AudioProcessingEvent) => {
      if (this.count >= MAX_SAMPLES) return;
      const input = event.inputBuffer.getChannelData(0);
      const take = Math.min(input.length, MAX_SAMPLES - this.count);
      this.chunks.push(input.slice(0, take));
      this.count += take;
      if (this.count >= MAX_SAMPLES) onLimit();
    };
    this.mute = this.context.createGain();
    this.mute.gain.value = 0;
    this.source.connect(this.processor);
    this.processor.connect(this.mute);
    this.mute.connect(this.context.destination);
  }

  private async release(): Promise<void> {
    const context = this.context;
    if (this.processor !== null) this.processor.onaudioprocess = null;
    this.source?.disconnect();
    this.processor?.disconnect();
    this.mute?.disconnect();
    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = null;
    this.context = null;
    this.source = null;
    this.processor = null;
    this.mute = null;
    await context?.close();
  }
}
