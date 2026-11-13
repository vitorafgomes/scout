import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { LocalSpeakRequest, LocalVoicesResponse } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import type { SpeechPlayer } from '../speech/speech-queue';
import {
  LOCAL_VOICE_KEY,
  PROVIDER_VOICE_KEY,
  SAMPLE_PLAYER,
  SAMPLE_SENTENCE,
  SpeechVoicesComponent,
  languageOf,
} from './speech-voices.component';

const CORI = 'piper:en_GB-cori-medium';
const aru = (n: number): string => `piper:en_GB-aru-medium#${n}`;

const OPENAI_AND_LOCAL: LocalVoicesResponse = {
  provider: 'OpenAI',
  providerVoices: ['alloy', 'echo', 'fable', 'onyx', 'nova', 'shimmer'],
  localVoices: [
    ...Array.from({ length: 12 }, (_, i) => ({
      id: aru(i),
      label: `Aru ${String(i + 1).padStart(2, '0')} (medium)`,
    })).sort((a, b) => (a.id < b.id ? -1 : 1)),
    { id: CORI, label: 'Cori (medium)' },
  ],
  defaultLocal: CORI,
};

const LOCAL_ONLY: LocalVoicesResponse = {
  ...OPENAI_AND_LOCAL,
  provider: 'Ollama',
  providerVoices: [],
};

const flush = async (): Promise<void> => {
  for (let i = 0; i < 20; i++) {
    TestBed.tick();
    await Promise.resolve();
  }
};

function setup(
  opts: {
    voices?: LocalVoicesResponse | Error;
    saved?: Partial<Record<string, string>>;
  } = {},
) {
  const voices = opts.voices ?? OPENAI_AND_LOCAL;
  const saved = opts.saved ?? {};
  const bridge = {
    localVoices: vi.fn(async () => {
      if (voices instanceof Error) throw voices;
      return voices;
    }),
    vaultGetPublic: vi.fn(async ({ key }: { key: string }) => ({ value: saved[key] ?? null })),
    vaultStore: vi.fn(async (_req: { key: string; value: string }) => undefined),
    workerRestart: vi.fn(async () => ({ outcome: 'now' })),
    localSpeak: vi.fn(async (req: LocalSpeakRequest) => ({
      contentType: 'audio/wav',
      base64: JSON.stringify(req.sample),
    })),
  };
  const ends: (() => void)[] = [];
  const player = {
    played: [] as string[],
    play: vi.fn((audio: { base64: string }) => {
      player.played.push(audio.base64);
      return new Promise<void>((resolve) => ends.push(resolve));
    }),
    stop: vi.fn(() => ends.splice(0).forEach((end) => end())),
  };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: SAMPLE_PLAYER, useValue: player as unknown as SpeechPlayer },
    ],
  });
  const fixture: ComponentFixture<SpeechVoicesComponent> =
    TestBed.createComponent(SpeechVoicesComponent);
  const el = fixture.nativeElement as HTMLElement;
  const q = (selector: string): HTMLElement | null => el.querySelector<HTMLElement>(selector);
  const all = (selector: string): HTMLElement[] =>
    Array.from(el.querySelectorAll<HTMLElement>(selector));
  const text = (node: Element | null | undefined): string =>
    (node?.textContent ?? '').replace(/\s+/g, ' ').trim();
  const row = (id: string): HTMLElement | null => q(`[data-test="speech-row-${id}"]`);
  const radio = (id: string): HTMLElement | null =>
    row(id)?.querySelector<HTMLElement>('[role="radio"]') ?? null;
  const sample = (id: string): HTMLButtonElement =>
    q(`[data-test="speech-sample-${id}"]`) as HTMLButtonElement;
  const names = (group: string): string[] =>
    all(`[data-test="${group}"] [role="radio"] .name-text`).map((n) => text(n));
  const render = async (): Promise<void> => {
    await flush();
    fixture.detectChanges();
  };
  const endPlaying = async (): Promise<void> => {
    ends.splice(0).forEach((end) => end());
    await render();
  };
  return { fixture, bridge, player, q, all, text, row, radio, sample, names, render, endPlaying };
}

async function opened(opts?: Parameters<typeof setup>[0]) {
  const t = setup(opts);
  t.fixture.detectChanges();
  await t.render();
  return t;
}

describe('SpeechVoicesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.style.removeProperty('--sc-muted');
    document.documentElement.style.removeProperty('--sc-error-container');
  });

  it("lists the AI Source's voices as the worker sends them, under the voice of your AI Source", async () => {
    const t = await opened();

    expect(t.names('speech-provider-voices')).toEqual([
      'alloy',
      'echo',
      'fable',
      'onyx',
      'nova',
      'shimmer',
    ]);
    expect(t.text(t.q('[data-test="speech-provider-via"]'))).toBe('via OpenAI API');
    expect(t.q('[data-test="speech-no-provider"]')).toBeNull();
  });

  it('lists only the local voices this machine has, Cori first and marked Default, the Aru list collapsed to 4', async () => {
    const t = await opened();

    expect(t.names('speech-local-voices')).toEqual([
      'Cori (medium)',
      'Aru 01 (medium)',
      'Aru 02 (medium)',
      'Aru 03 (medium)',
      'Aru 04 (medium)',
    ]);
    expect(
      t.all('[data-test="speech-default"]').map((tag) => t.text(tag.closest('.name'))),
    ).toEqual([expect.stringContaining('Cori (medium)')]);
    expect(t.text(t.row(CORI)?.querySelector(`[id$="-desc"]`))).toBe('English (UK)');
    const toggle = t.q('[data-test="speech-show-all"]');
    expect([t.text(toggle), toggle?.getAttribute('aria-expanded')]).toEqual([
      'Show all 12',
      'false',
    ]);
  });

  it('Show all 12 lists every local voice and Show fewer collapses the list again', async () => {
    const t = await opened();
    const toggle = (): HTMLButtonElement =>
      t.q('[data-test="speech-show-all"]') as HTMLButtonElement;

    toggle().click();
    await t.render();
    expect(t.names('speech-local-voices')).toEqual([
      'Cori (medium)',
      ...Array.from({ length: 12 }, (_, i) => `Aru ${String(i + 1).padStart(2, '0')} (medium)`),
    ]);
    expect([t.text(toggle()), toggle().getAttribute('aria-expanded')]).toEqual([
      'Show fewer',
      'true',
    ]);

    toggle().click();
    await t.render();
    expect(t.names('speech-local-voices')).toHaveLength(5);
  });

  it('a saved local voice beyond the first four stays listed while the list is collapsed', async () => {
    const t = await opened({ saved: { [LOCAL_VOICE_KEY]: aru(8) } });

    expect(t.names('speech-local-voices')).toEqual([
      'Cori (medium)',
      'Aru 01 (medium)',
      'Aru 02 (medium)',
      'Aru 03 (medium)',
      'Aru 04 (medium)',
      'Aru 09 (medium)',
    ]);
  });

  it('with an AI Source without speech the provider voices are not offered and Scout says it uses its local voice', async () => {
    const t = await opened({ voices: LOCAL_ONLY });

    expect(t.q('[data-test="speech-provider-voices"]')).toBeNull();
    expect(t.text(t.q('[data-test="speech-no-provider"]'))).toBe(
      'Your AI Source has no voice; Scout uses its local voice.',
    );
    expect(t.text(t.q('[data-test="speech-status"]'))).toBe(
      "Scout reads aloud with Cori (medium), Scout's local voice.",
    );
  });

  it.each([
    ['nothing saved', {}, 'alloy', CORI],
    ['saved voices', { [PROVIDER_VOICE_KEY]: 'nova', [LOCAL_VOICE_KEY]: aru(2) }, 'nova', aru(2)],
    [
      'a saved voice this machine no longer has',
      { [LOCAL_VOICE_KEY]: 'piper:gone' },
      'alloy',
      CORI,
    ],
  ])(
    'In use and the chosen radio mark the voice Scout reads with: %s',
    async (_name, saved, provider, local) => {
      const t = await opened({ saved });

      const inUse = t
        .all('[data-test="speech-in-use"]')
        .map((tag) => tag.closest('[data-test^="speech-row-"]')?.getAttribute('data-test'));
      expect(inUse).toEqual([`speech-row-${provider}`, `speech-row-${local}`]);
      expect(
        t
          .all('[role="radio"][aria-checked="true"]')
          .map((r) => r.closest('[data-test^="speech-row-"]')?.getAttribute('data-test')),
      ).toEqual([`speech-row-${provider}`, `speech-row-${local}`]);
      expect(t.text(t.q('[data-test="speech-status"]'))).toBe(
        `Scout reads aloud with ${provider}, the voice of your AI Source.`,
      );
    },
  );

  it('Save stores the chosen voices in the vault, says the next sentence uses the new voice, and never asks for a worker restart', async () => {
    const t = await opened();

    t.radio('nova')?.click();
    t.radio(aru(2))?.click();
    await t.render();
    (t.q('[data-test="speech-save"]') as HTMLButtonElement).click();
    await t.render();

    expect(t.bridge.vaultStore.mock.calls.map(([req]) => req)).toEqual([
      { key: PROVIDER_VOICE_KEY, value: 'nova' },
      { key: LOCAL_VOICE_KEY, value: aru(2) },
    ]);
    expect(t.bridge.workerRestart).not.toHaveBeenCalled();
    expect(t.text(t.q('[data-test="speech-status"]'))).toBe(
      'Saved. The next sentence read uses nova, the voice of your AI Source.',
    );
    expect(t.text(t.fixture.nativeElement as HTMLElement)).toContain(
      'Saving does not restart anything; the next sentence read uses the new voice.',
    );
    expect(t.row('nova')?.querySelector('[data-test="speech-in-use"]')).not.toBeNull();
  });

  it('with an AI Source without speech, Save stores only the local voice and keeps the saved provider voice', async () => {
    const t = await opened({ voices: LOCAL_ONLY, saved: { [PROVIDER_VOICE_KEY]: 'nova' } });

    t.radio(aru(0))?.click();
    await t.render();
    (t.q('[data-test="speech-save"]') as HTMLButtonElement).click();
    await t.render();

    expect(t.bridge.vaultStore.mock.calls.map(([req]) => req)).toEqual([
      { key: LOCAL_VOICE_KEY, value: aru(0) },
    ]);
    expect(t.text(t.q('[data-test="speech-status"]'))).toBe(
      "Saved. The next sentence read uses Aru 01 (medium), Scout's local voice.",
    );
  });

  it('a save the vault refuses says why and marks nothing as saved', async () => {
    const t = await opened();
    t.bridge.vaultStore.mockRejectedValueOnce(
      new Error('[scout:vault/unavailable] The keychain is locked.'),
    );

    t.radio('onyx')?.click();
    (t.q('[data-test="speech-save"]') as HTMLButtonElement).click();
    await t.render();

    expect(t.text(t.q('[data-test="speech-status"]'))).toBe(
      'Could not save the voice: The keychain is locked.',
    );
    expect(t.row('onyx')?.querySelector('[data-test="speech-in-use"]')).toBeNull();
  });

  it.each([
    ['an AI Source voice', 'fable', { provider: 'fable' }],
    ['a local voice', aru(1), { local: aru(1) }],
  ])(
    'Hear a sample speaks the fixed sentence in %s through the speak channel and plays it',
    async (_name, id, sample) => {
      const t = await opened();

      t.sample(id).click();
      await t.render();

      expect(t.bridge.localSpeak.mock.calls).toEqual([[{ text: SAMPLE_SENTENCE, sample }]]);
      expect(t.player.played).toEqual([JSON.stringify(sample)]);
      expect([t.text(t.sample(id)), t.sample(id).getAttribute('aria-pressed')]).toEqual([
        'Stop sample',
        'true',
      ]);
      expect(t.sample(id).getAttribute('aria-label')).toMatch(/^Stop the sample of /);

      await t.endPlaying();
      expect([t.text(t.sample(id)), t.sample(id).getAttribute('aria-pressed')]).toEqual([
        'Hear a sample',
        'false',
      ]);
    },
  );

  it('playing a sample while another plays stops the first', async () => {
    const t = await opened();

    t.sample('fable').click();
    await t.render();
    t.sample(aru(0)).click();
    await t.render();

    expect(t.player.stop).toHaveBeenCalled();
    expect(t.player.played).toEqual([
      JSON.stringify({ provider: 'fable' }),
      JSON.stringify({ local: aru(0) }),
    ]);
    expect(t.all('[aria-pressed="true"]').map((b) => b.getAttribute('data-test'))).toEqual([
      `speech-sample-${aru(0)}`,
    ]);
  });

  it('Stop sample stops the sample at once', async () => {
    const t = await opened();

    t.sample('nova').click();
    await t.render();
    t.sample('nova').click();
    await t.render();

    expect(t.player.stop).toHaveBeenCalled();
    expect(t.sample('nova').getAttribute('aria-pressed')).toBe('false');
  });

  it("a failed sample shows the worker's reason on its row only", async () => {
    const t = await opened();
    t.bridge.localSpeak.mockRejectedValueOnce(
      new Error(
        "[scout:local/speak-failed] Scout's local voice could not read that aloud; see Scout logs.",
      ),
    );

    t.sample(aru(1)).click();
    await t.render();

    expect(t.all('[role="alert"]').map((a) => [a.getAttribute('data-test'), t.text(a)])).toEqual([
      [
        `speech-sample-error-${aru(1)}`,
        "Scout's local voice could not read that aloud; see Scout logs.",
      ],
    ]);
    expect(t.sample(aru(1)).getAttribute('aria-pressed')).toBe('false');
  });

  it('a listing that fails shows its reason on the tab, and Try again lists the voices again', async () => {
    const t = await opened({
      voices: new Error(
        '[scout:local/not-running] Scout is not running. Start it from the tray or the app.',
      ),
    });

    expect(t.text(t.q('[data-test="speech-list-error"]'))).toContain(
      'Scout is not running. Start it from the tray or the app.',
    );
    expect(t.q('[data-test="speech-local-voices"]')).toBeNull();

    t.bridge.localVoices.mockResolvedValueOnce(OPENAI_AND_LOCAL);
    (t.q('[data-test="speech-list-retry"]') as HTMLButtonElement).click();
    await t.render();

    expect(t.q('[data-test="speech-list-error"]')).toBeNull();
    expect(t.names('speech-local-voices')).toHaveLength(5);
  });

  it('Loading voices: until the worker lists them the card shows its heading and one status line, Loading voices..., in the small muted text, with no voice, error or Save', async () => {
    document.documentElement.style.setProperty('--sc-muted', 'rgb(2, 2, 2)');
    const t = setup();
    t.bridge.localVoices.mockReturnValue(new Promise<LocalVoicesResponse>(() => undefined));
    t.fixture.detectChanges();
    await t.render();
    const line = t.q('[data-test="speech-loading"]');
    const style = line === null ? null : getComputedStyle(line);
    const look = [line?.getAttribute('role'), style?.fontSize, style?.color, style?.marginTop];

    expect([t.text(t.q('[data-test="panel-speech"] h2')), t.text(line)]).toEqual([
      'Speech',
      'Loading voices...',
    ]);
    expect(look).toEqual(['status', '13px', 'rgb(2, 2, 2)', '0px']);
    expect(
      t.all('[role="radio"], [data-test="speech-list-error"], [data-test="speech-save"]'),
    ).toEqual([]);
  });

  it("No local voice: with no local voice on this computer the column says so and offers no local row, and the AI Source's voices stay listed", async () => {
    const t = await opened({ voices: { ...OPENAI_AND_LOCAL, localVoices: [] } });

    expect(t.text(t.q('[data-test="speech-no-local"]'))).toBe(
      "Scout's local voice is not on this computer.",
    );
    expect(t.q('[data-test="speech-local-voices"]')).toBeNull();
    expect(t.q('[data-test="speech-show-all"]')).toBeNull();
    expect(t.names('speech-provider-voices')).toEqual([
      'alloy',
      'echo',
      'fable',
      'onyx',
      'nova',
      'shimmer',
    ]);
  });

  it('Voices did not load: the reason shows as an alert on the error colours with Try again beside it', async () => {
    document.documentElement.style.setProperty('--sc-error-container', 'rgb(7, 7, 7)');
    const t = await opened({
      voices: new Error('[scout:local/not-running] Scout is not running.'),
    });
    const alert = t.q('[data-test="speech-list-error"]') as HTMLElement;
    const background = getComputedStyle(alert).backgroundColor;

    expect([alert.getAttribute('role'), background]).toEqual(['alert', 'rgb(7, 7, 7)']);
    expect(t.text(alert)).toBe('Scout is not running. Try again');
    expect(t.q('[data-test="speech-loading"]')).toBeNull();
  });

  it('the arrow keys move the choice within a group, wrap around, and keep one radio in the tab order', async () => {
    const t = await opened();

    t.radio('alloy')?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }),
    );
    await t.render();
    expect(t.radio('echo')?.getAttribute('aria-checked')).toBe('true');

    t.radio('echo')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
    t.radio('alloy')?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }),
    );
    await t.render();

    expect(t.radio('shimmer')?.getAttribute('aria-checked')).toBe('true');
    expect(
      t.all('[data-test="speech-provider-voices"] [role="radio"][tabindex="0"]').map((r) => r.id),
    ).toEqual(['sp-provider-shimmer']);
    expect(t.radio(CORI)?.getAttribute('aria-checked')).toBe('true');
  });
});

describe('languageOf', () => {
  it.each([
    [CORI, 'English (UK)'],
    ['piper:en_US-lessac-medium', 'English (US)'],
    ['piper:orphan', ''],
    ['nova', ''],
  ])('the language of %s is "%s"', (id, language) => {
    expect(languageOf(id)).toBe(language);
  });
});
