import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection, signal, type Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { WhisperDownloadModelRequest, WhisperVariant } from '@scout/shared';

import { ChatComponent } from '../chat/chat.component';
import { LAID_OUT } from '../core/laid-out.testing';
import { ScoutBridgeService } from '../core/scout-bridge';
import { MAIN_WHISPER_TABLE } from '../core/whisper-table.testing';
import { LiveListener } from '../quick-ask/live-listener';
import { QuickAskComponent } from '../quick-ask/quick-ask.component';

type Installed = 'installed' | 'not installed' | 'unknown';

interface BridgeOptions {
  readonly installed: Installed;
  readonly download?: (req: WhisperDownloadModelRequest) => Promise<unknown>;
}

function makeBridge(options: BridgeOptions) {
  return {
    actionsList: vi.fn(async () => ({ actions: [] })),
    writesList: vi.fn(async () => ({ entries: [] })),
    onActionsChanged: vi.fn(() => () => undefined),
    localSpeak: vi.fn(async () => ({ contentType: 'audio/wav', base64: '' })),
    onLocalTranscript: vi.fn(() => () => undefined),
    localListen: vi.fn(async () => ({ id: 'talk-1' })),
    localListening: vi.fn(async () => ({ listening: false })),
    onLocalListening: vi.fn(() => () => undefined),
    localAudio: vi.fn(async () => undefined),
    localStopListening: vi.fn(async () => undefined),
    whisperListInstalled: vi.fn(async () => {
      if (options.installed === 'unknown') {
        throw new Error('[scout:ipc/unavailable] The main process did not answer.');
      }
      return {
        installed:
          options.installed === 'installed'
            ? [{ variant: 'base' as WhisperVariant, verified: true }]
            : [],
      };
    }),
    whisperModelTable: vi.fn(async () => MAIN_WHISPER_TABLE),
    whisperDownloadModel: vi.fn(
      options.download ??
        (async (req: WhisperDownloadModelRequest) => ({ variant: req.variant, path: '/m' })),
    ),
    onWhisperDownloadProgress: vi.fn(() => () => undefined),
    vaultStore: vi.fn(async () => undefined),
    vaultHas: vi.fn(async () => ({ exists: false })),
    vaultGetPublic: vi.fn(async () => ({ value: null })),
    onLocalTurns: vi.fn(() => () => undefined),
    localGetTurns: vi.fn(async () => []),
    onGitHubVerdict: vi.fn(() => () => undefined),
    githubVerdictFor: vi.fn(async () => null),
    localHide: vi.fn(async () => undefined),
    localAsk: vi.fn(async () => undefined),
    appOpenExternal: vi.fn(async () => undefined),
  };
}

function makeLive() {
  const listening = signal(false);
  const othersListening = signal(false);
  return {
    listening,
    othersListening,
    text: signal(''),
    partials: signal({ me: '', others: '' }),
    error: signal<string | null>(null),
    systemAudioAvailable: true,
    listen: vi.fn(async (lane: 'me' | 'others', _translate?: string, _dictation?: boolean) =>
      (lane === 'me' ? listening : othersListening).set(true),
    ),
    finish: vi.fn(async () => {
      listening.set(false);
      return { text: '', error: null };
    }),
    stop: vi.fn(async () => {
      listening.set(false);
      othersListening.set(false);
    }),
    stopAll: vi.fn(async () => {
      listening.set(false);
      othersListening.set(false);
    }),
    clear: vi.fn(),
  };
}

interface World {
  readonly fixture: ComponentFixture<unknown>;
  readonly bridge: ReturnType<typeof makeBridge>;
  readonly live: ReturnType<typeof makeLive>;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  fixture.detectChanges();
}

async function render(component: Type<unknown>, options: BridgeOptions): Promise<World> {
  const bridge = makeBridge(options);
  const live = makeLive();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: LiveListener, useValue: live as unknown as LiveListener },
    ],
  });
  const fixture = TestBed.createComponent(component);
  await settle(fixture);
  return { fixture, bridge, live };
}

function q(world: World, test: string): HTMLElement {
  const found = (world.fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
    `[data-test="${test}"]`,
  );
  if (found === null) throw new Error(`no [data-test="${test}"] on the page`);
  return found;
}

async function press(world: World, test: string): Promise<void> {
  q(world, test).dispatchEvent(new Event('pointerdown'));
  await settle(world.fixture);
  q(world, test).dispatchEvent(new Event('pointerup'));
  await settle(world.fixture);
}

async function trayClick(world: World, test: string): Promise<void> {
  const tray = (world.fixture.nativeElement as HTMLElement).querySelector('[data-test="tools"]');
  if (tray === null) {
    q(world, 'tools-toggle').click();
    await settle(world.fixture);
  }
  q(world, test).click();
  await settle(world.fixture);
}

interface Entry {
  readonly name: string;
  readonly component: Type<unknown>;
  use(world: World): Promise<void>;
  started(world: World): number;
  stop(world: World): Promise<void>;
}

const ENTRIES: readonly Entry[] = [
  {
    name: 'the quick-ask microphone',
    component: QuickAskComponent,
    use: (world) => press(world, 'mic'),
    started: (world) => world.live.listen.mock.calls.filter(([, , dictation]) => dictation).length,
    stop: async () => undefined,
  },
  {
    name: 'Space held in quick ask',
    component: QuickAskComponent,
    use: async (world) => {
      window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', key: ' ' }));
      await settle(world.fixture);
      window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space', key: ' ' }));
      await settle(world.fixture);
    },
    started: (world) => world.live.listen.mock.calls.filter(([, , dictation]) => dictation).length,
    stop: async () => undefined,
  },
  {
    name: 'Listen in quick ask',
    component: QuickAskComponent,
    use: (world) => trayClick(world, 'listen'),
    started: (world) =>
      world.live.listen.mock.calls.filter(([lane, , dictation]) => lane === 'me' && !dictation)
        .length,
    stop: async (world) => {
      if (world.live.listening()) await trayClick(world, 'listen');
    },
  },
  {
    name: 'Others in quick ask',
    component: QuickAskComponent,
    use: (world) => trayClick(world, 'listen-others'),
    started: (world) => world.live.listen.mock.calls.filter(([lane]) => lane === 'others').length,
    stop: async (world) => {
      if (world.live.othersListening()) await trayClick(world, 'listen-others');
    },
  },
  {
    name: 'Meeting in quick ask',
    component: QuickAskComponent,
    use: (world) => trayClick(world, 'meeting-start'),
    started: (world) => world.live.listen.mock.calls.filter(([lane]) => lane === 'me').length,
    stop: async (world) => {
      const host = world.fixture.nativeElement as HTMLElement;
      if (host.querySelector('[data-test="meeting-stop"]') !== null) {
        await trayClick(world, 'meeting-stop');
        const done = host.querySelector<HTMLElement>('[data-test="meeting-done"]');
        done?.click();
        await settle(world.fixture);
      }
    },
  },
  {
    name: 'the Chat microphone',
    component: ChatComponent,
    use: (world) => press(world, 'mic'),
    started: (world) => world.live.listen.mock.calls.filter(([, , dictation]) => dictation).length,
    stop: async () => undefined,
  },
];

function dialogTitle(): string | null {
  return document.querySelector('.mat-mdc-dialog-title')?.textContent?.trim() ?? null;
}

async function answer(world: World, test: 'voice-download' | 'voice-not-now'): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${test}"]`);
  if (button === null) throw new Error('the voice model dialog did not open');
  button.click();
  await settle(world.fixture);
}

function status(world: World): string | null {
  return (
    (world.fixture.nativeElement as HTMLElement)
      .querySelector('[data-test="voice-ask-status"]')
      ?.textContent?.trim() ?? null
  );
}

describe('asks to download when voice is first used', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  for (const entry of ENTRIES) {
    describe(entry.name, () => {
      it('with a model installed: no question, the voice starts at once', async () => {
        const world = await render(entry.component, { installed: 'installed' });

        await entry.use(world);

        expect(dialogTitle()).toBeNull();
        expect(entry.started(world)).toBe(1);
        expect(world.bridge.whisperModelTable).not.toHaveBeenCalled();
      });

      it('with no model installed, Download: the question opens instead of the voice, the picked model downloads through main, and the next use starts the voice without asking', async () => {
        const world = await render(entry.component, { installed: 'not installed' });

        await entry.use(world);
        const asked = dialogTitle();
        const startedWhileAsking = entry.started(world);
        await answer(world, 'voice-download');
        const afterDownload = status(world);
        await entry.use(world);

        expect(asked).toBe('Download a voice model?');
        expect(startedWhileAsking).toBe(0);
        expect(world.bridge.whisperDownloadModel.mock.calls.map(([req]) => req.variant)).toEqual([
          'base',
        ]);
        expect(afterDownload).toBe('The base voice model is installed.');
        expect(dialogTitle()).toBeNull();
        expect(entry.started(world)).toBe(1);
      });

      it('with no model installed, Not now: nothing downloads, and the answer holds for the session, so the next use starts the voice without asking', async () => {
        const world = await render(entry.component, { installed: 'not installed' });

        await entry.use(world);
        const asked = dialogTitle();
        const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
        await answer(world, 'voice-not-now');
        await entry.stop(world);
        await entry.use(world);

        expect([asked, focused]).toEqual(['Download a voice model?', 'voice-not-now']);
        expect(world.bridge.whisperDownloadModel).not.toHaveBeenCalled();
        expect(dialogTitle()).toBeNull();
        expect(entry.started(world)).toBe(1);
      });
    });
  }

  it('when main cannot say what is installed, the quick-ask microphone never asks and starts the voice', async () => {
    const world = await render(QuickAskComponent, { installed: 'unknown' });

    await press(world, 'mic');

    expect(dialogTitle()).toBeNull();
    expect(world.live.listen.mock.calls.filter(([, , dictation]) => dictation)).toHaveLength(1);
  });

  it('a download picked from the question that fails says why, and the next use asks again', async () => {
    const world = await render(QuickAskComponent, {
      installed: 'not installed',
      download: async () => {
        throw new Error('[scout:whisper/download-failed] HuggingFace returned HTTP 503');
      },
    });

    await press(world, 'mic');
    await answer(world, 'voice-download');
    const failed = status(world);
    await press(world, 'mic');

    expect(failed).toBe('Could not download the base voice model: HuggingFace returned HTTP 503');
    expect(dialogTitle()).toBe('Download a voice model?');
    expect(world.live.listen).not.toHaveBeenCalled();
  });

  it('a download picked from the question and cancelled from another window says nothing went wrong, and the next use asks again', async () => {
    const world = await render(QuickAskComponent, {
      installed: 'not installed',
      download: async () => {
        throw new Error(
          "Error invoking remote method 'scout:whisper.downloadModel': Error: [scout:whisper/download-cancelled] whisper: the download was cancelled",
        );
      },
    });

    await press(world, 'mic');
    await answer(world, 'voice-download');
    const after = status(world);
    await press(world, 'mic');

    expect(after).toBeNull();
    expect(dialogTitle()).toBe('Download a voice model?');
  });

  it('Escape in the question closes only the question, never the quick-ask window', async () => {
    const world = await render(QuickAskComponent, { installed: 'not installed' });

    await press(world, 'mic');
    const button = document.querySelector<HTMLElement>('[data-test="voice-not-now"]')!;
    button.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Escape',
        keyCode: 27,
        bubbles: true,
        cancelable: true,
      } as KeyboardEventInit),
    );
    await settle(world.fixture);

    expect(world.bridge.localHide).not.toHaveBeenCalled();
    expect(dialogTitle()).toBeNull();
  });
});
