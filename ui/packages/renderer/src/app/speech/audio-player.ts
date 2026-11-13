import type { LocalSpeakResponse } from '@scout/shared';

import type { SpeechPlayer } from './speech-queue';

/**
 * Plays a sentence through Web Audio: the bytes are decoded in memory and played from a buffer, so no media URL is
 * made and the page's media policy is untouched. Before each sentence `route` points the context at its output device.
 * A stop while a sentence is still decoding means it never starts.
 */
export class WebAudioPlayer implements SpeechPlayer {
  private context: AudioContext | null = null;
  private source: AudioBufferSourceNode | null = null;
  private generation = 0;

  constructor(
    private readonly create: () => AudioContext = () => new AudioContext(),
    private readonly route: (context: AudioContext) => Promise<void> = async () => undefined,
  ) {}

  async play(audio: LocalSpeakResponse): Promise<void> {
    const generation = this.generation;
    const context = (this.context ??= this.create());
    if (context.state === 'suspended') await context.resume();
    await this.route(context);
    const binary = atob(audio.base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    const buffer = await context.decodeAudioData(bytes.buffer);
    if (generation !== this.generation) return;
    await new Promise<void>((resolve) => {
      const source = context.createBufferSource();
      source.buffer = buffer;
      source.connect(context.destination);
      source.onended = () => {
        if (this.source === source) this.source = null;
        resolve();
      };
      this.source = source;
      source.start();
    });
  }

  stop(): void {
    this.generation += 1;
    const source = this.source;
    this.source = null;
    source?.stop();
  }
}
