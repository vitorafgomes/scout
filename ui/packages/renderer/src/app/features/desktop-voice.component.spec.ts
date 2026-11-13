import { InteractivityChecker } from '@angular/cdk/a11y';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type {
  WhisperDownloadModelRequest,
  WhisperDownloadProgressEvent,
  WhisperModelTableResponse,
  WhisperVariant,
} from '@scout/shared';

import { LAID_OUT } from '../core/laid-out.testing';
import { LocalModelsStore } from '../core/local-models.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { MAIN_WHISPER_TABLE, OTHER_WHISPER_TABLE } from '../core/whisper-table.testing';

import { DesktopVoiceComponent } from './desktop-voice.component';
import { askVoiceModel } from './voice-model-dialog.component';

interface Options {
  readonly installed?: readonly WhisperVariant[];
  readonly download?: (req: WhisperDownloadModelRequest) => Promise<unknown>;
  readonly table?: WhisperModelTableResponse;
  readonly saved?: WhisperVariant;
}

const LOAD_ORDER: readonly WhisperVariant[] = [
  'large-v3-turbo',
  'large-v3',
  'small',
  'base',
  'tiny',
];

function makeBridge(options: Options = {}) {
  const listeners: ((event: WhisperDownloadProgressEvent) => void)[] = [];
  const installed = [...(options.installed ?? [])];
  return {
    whisperListInstalled: vi.fn(async () => ({
      installed: installed.map((variant) => ({ variant, verified: true })),
      inUse:
        options.saved !== undefined && installed.includes(options.saved)
          ? options.saved
          : (LOAD_ORDER.find((v) => installed.includes(v)) ?? null),
      preferred: options.saved ?? null,
    })),
    whisperModelTable: vi.fn(
      async (): Promise<WhisperModelTableResponse> => options.table ?? MAIN_WHISPER_TABLE,
    ),
    whisperDownloadModel: vi.fn(async (req: WhisperDownloadModelRequest) => {
      const done = await (options.download ?? (async () => ({ variant: req.variant, path: '/m' })))(
        req,
      );
      installed.push(req.variant);
      return done;
    }),
    onWhisperDownloadProgress: vi.fn((listener: (event: WhisperDownloadProgressEvent) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    vaultStore: vi.fn(async () => undefined),
    vaultDelete: vi.fn(async () => undefined),
    progress: (event: WhisperDownloadProgressEvent) => listeners.forEach((l) => l(event)),
  };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  fixture.detectChanges();
}

async function render(bridge: ReturnType<typeof makeBridge>) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(DesktopVoiceComponent);
  await settle(fixture);
  return fixture;
}

function q(fixture: ComponentFixture<unknown>, test: string): HTMLElement | null {
  return (fixture.nativeElement as HTMLElement).querySelector(`[data-test="${test}"]`);
}

function line(fixture: ComponentFixture<unknown>): string {
  return (q(fixture, 'voice-line')?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function rows(): string[][] {
  return Array.from(
    document.querySelectorAll<HTMLElement>('[data-test^="voice-model-"]'),
    (row) => [
      row.getAttribute('data-test')!.replace('voice-model-', ''),
      row.querySelector('.name')!.childNodes[0]!.textContent!.trim(),
      row.querySelector('[data-test="voice-size"]')!.textContent!.replace(/\s+/g, ' ').trim(),
      row.querySelector('[data-test="voice-recommended"]') === null ? '' : 'Recommended',
      row.querySelector('[data-test="voice-installed"]') === null ? '' : 'Installed',
    ],
  );
}

describe('the Desktop card voice line', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  for (const [installed, expected, download] of [
    [[], 'Voice model: not installed', true],
    [['base'], 'Voice model: base installed', false],
    [['small'], 'Voice model: small installed', false],
    [['tiny', 'small'], 'Voice model: small installed', false],
  ] as const) {
    it(`with ${installed.length === 0 ? 'no model' : installed.join(' and ')} on this computer it reads "${expected}"${download ? ' and offers Download' : ' with no Download'}`, async () => {
      const fixture = await render(makeBridge({ installed }));

      expect(line(fixture)).toBe(expected);
      expect(q(fixture, 'voice-download-open') !== null).toBe(download);
    });
  }

  it('Manage in Models, Transcription goes to the Transcription tab of Models', async () => {
    const fixture = await render(makeBridge({ installed: ['base'] }));

    expect(q(fixture, 'voice-manage')?.getAttribute('href')).toBe('/llm?tab=transcription');
    expect(q(fixture, 'voice-manage')?.textContent?.trim()).toBe('Manage in Models, Transcription');
  });

  it('while a model downloads the line shows its progress, then says it is installed', async () => {
    let finish: () => void = () => undefined;
    const bridge = makeBridge({
      download: (req) =>
        new Promise((resolve) => (finish = () => resolve({ variant: req.variant, path: '/m' }))),
    });
    const fixture = await render(bridge);

    const done = fixture.componentInstance.download('base');
    await settle(fixture);
    const requestId = bridge.whisperDownloadModel.mock.calls[0]![0].requestId;
    bridge.progress({
      requestId,
      variant: 'base',
      percent: 42,
      completedBytes: 1,
      totalBytes: 2,
    });
    bridge.progress({
      requestId: 'another-download',
      variant: 'base',
      percent: 99,
      completedBytes: 1,
      totalBytes: 2,
    });
    fixture.detectChanges();
    const during = line(fixture);
    const bar = q(fixture, 'desktop-voice')?.querySelector('mat-progress-bar') !== null;
    finish();
    await done;
    await settle(fixture);

    expect(during).toBe('Downloading the base voice model... 42%');
    expect(bar).toBe(true);
    expect(line(fixture)).toBe('Voice model: base installed');
    expect(TestBed.inject(LocalModelsStore).isDownloaded('whisper-base')).toBe(true);
  });

  it('a download that fails shows its reason with Retry, and Retry downloads the same model again', async () => {
    let calls = 0;
    const bridge = makeBridge({
      download: async (req) => {
        calls += 1;
        if (calls === 1)
          throw new Error(
            '[scout:whisper/download-failed] The model download stopped: connection reset.',
          );
        return { variant: req.variant, path: '/m' };
      },
    });
    const fixture = await render(bridge);

    await fixture.componentInstance.download('small');
    await settle(fixture);
    const failed = line(fixture);
    q(fixture, 'voice-retry')?.click();
    await settle(fixture);

    expect(failed).toBe(
      'Could not download the small voice model: The model download stopped: connection reset.',
    );
    expect(bridge.whisperDownloadModel.mock.calls.map(([req]) => req.variant)).toEqual([
      'small',
      'small',
    ]);
    expect(line(fixture)).toBe('Voice model: small installed');
  });

  it('a download cancelled from another window shows as cancelled: the line goes back to not installed with Download, never an error', async () => {
    const bridge = makeBridge({
      download: async () => {
        throw new Error(
          "Error invoking remote method 'scout:whisper.downloadModel': Error: [scout:whisper/download-cancelled] whisper: the download was cancelled",
        );
      },
    });
    const fixture = await render(bridge);

    await fixture.componentInstance.download('small');
    await settle(fixture);

    expect({
      line: line(fixture),
      download: q(fixture, 'voice-download-open') !== null,
      retry: q(fixture, 'voice-retry') !== null,
    }).toEqual({ line: 'Voice model: not installed', download: true, retry: false });
  });

  for (const [saved, installed, expected] of [
    ['base', ['large-v3-turbo'], 'Voice model: large-v3-turbo installed'],
    ['base', ['base', 'large-v3-turbo'], 'Voice model: base installed'],
    ['tiny', ['base', 'tiny'], 'Voice model: tiny installed'],
    ['base', [], 'Voice model: not installed'],
  ] as const) {
    it(`the line names the model Scout uses: saved ${saved}, installed ${installed.join(' and ') || 'none'} reads "${expected}"`, async () => {
      const fixture = await render(makeBridge({ installed, saved }));

      expect(line(fixture)).toBe(expected);
    });
  }

  it('a model that just downloaded reads as installed while the worker has not loaded it yet', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);
    bridge.whisperListInstalled.mockResolvedValue({
      installed: [{ variant: 'base', verified: true }],
      inUse: null,
      preferred: null,
    });

    await fixture.componentInstance.download('base');
    await settle(fixture);

    expect(line(fixture)).toBe('Voice model: base installed');
  });

  it('a list that cannot be read says Scout could not read the voice models, with Try again and no Download', async () => {
    const bridge = makeBridge({ installed: ['base'] });
    bridge.whisperListInstalled.mockRejectedValueOnce(new Error('[scout:whisper/list] denied'));
    const fixture = await render(bridge);

    expect({
      line: line(fixture),
      tryAgain: q(fixture, 'voice-try-again') !== null,
      download: q(fixture, 'voice-download-open') !== null,
    }).toEqual({
      line: 'Scout could not read the voice models.',
      tryAgain: true,
      download: false,
    });
  });

  it('a list read that fails and then succeeds on Try again shows the model main reports', async () => {
    const bridge = makeBridge({ installed: ['base'] });
    bridge.whisperListInstalled.mockRejectedValueOnce(new Error('[scout:whisper/list] denied'));
    const fixture = await render(bridge);

    q(fixture, 'voice-try-again')!.click();
    await settle(fixture);

    expect({ line: line(fixture), reads: bridge.whisperListInstalled.mock.calls.length }).toEqual({
      line: 'Voice model: base installed',
      reads: 2,
    });
  });
});

describe('Download a voice model?', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  it('the lists show exactly the models main downloads: the dialog lists main pinned table today, in its order, with its sizes', async () => {
    const fixture = await render(makeBridge());

    q(fixture, 'voice-download-open')!.click();
    await settle(fixture);

    expect(document.querySelector('.mat-mdc-dialog-title')?.textContent?.trim()).toBe(
      'Download a voice model?',
    );
    expect(rows()).toEqual([
      ['tiny', 'Whisper Tiny', '74 MB download · needs 273 MB of memory', '', ''],
      ['base', 'Whisper Base', '141 MB download · needs 388 MB of memory', 'Recommended', ''],
      ['small', 'Whisper Small', '465 MB download · needs 852 MB of memory', '', ''],
      [
        'large-v3-turbo',
        'Whisper Large v3 Turbo',
        '1.5 GB download · needs 3 GB of memory',
        '',
        '',
      ],
      ['large-v3', 'Whisper Large v3', '2.9 GB download · needs 4.7 GB of memory', '', ''],
    ]);
  });

  it('the lists show exactly the models main downloads: another table from main gives the dialog its rows, order and sizes', async () => {
    const fixture = await render(makeBridge({ table: OTHER_WHISPER_TABLE }));

    q(fixture, 'voice-download-open')!.click();
    await settle(fixture);

    expect(rows().map(([variant, , size]) => [variant, size])).toEqual([
      ['large-v3', '2 GB download · needs 4.7 GB of memory'],
      ['small', '286 MB download · needs 852 MB of memory'],
      ['tiny', '48 MB download · needs 273 MB of memory'],
      ['base', '191 MB download · needs 388 MB of memory'],
    ]);
    expect(rows().map(([variant]) => variant)).toEqual(
      OTHER_WHISPER_TABLE.models.map((m) => m.variant),
    );
  });

  it('when main cannot give its Whisper table the dialog says why, lists nothing and offers no Download', async () => {
    const bridge = makeBridge();
    bridge.whisperModelTable.mockRejectedValueOnce(
      new Error('[scout:ipc/unavailable] The main process did not answer.'),
    );
    const fixture = await render(bridge);

    q(fixture, 'voice-download-open')!.click();
    await settle(fixture);

    expect(document.querySelector('[data-test="voice-models-error"]')?.textContent?.trim()).toBe(
      'Scout could not list the voice models: The main process did not answer.',
    );
    expect(rows()).toEqual([]);
    expect(
      document.querySelector<HTMLButtonElement>('[data-test="voice-download"]')?.disabled,
    ).toBe(true);
  });

  it('Not now has the focus, base is picked, and Not now downloads nothing', async () => {
    const bridge = makeBridge();
    const fixture = await render(bridge);

    q(fixture, 'voice-download-open')!.click();
    await settle(fixture);
    const focused = (document.activeElement as HTMLElement | null)?.getAttribute('data-test');
    const picked = document.querySelector<HTMLInputElement>('input[type="radio"]:checked')?.value;
    document.querySelector<HTMLElement>('[data-test="voice-not-now"]')!.click();
    await settle(fixture);

    expect([focused, picked]).toEqual(['voice-not-now', 'base']);
    expect(bridge.whisperDownloadModel).not.toHaveBeenCalled();
    expect(line(fixture)).toBe('Voice model: not installed');
  });

  for (const variant of ['tiny', 'base', 'small', 'large-v3', 'large-v3-turbo'] as const) {
    it(`picking ${variant} and Download downloads ${variant}`, async () => {
      const bridge = makeBridge();
      const fixture = await render(bridge);

      q(fixture, 'voice-download-open')!.click();
      await settle(fixture);
      document.querySelector<HTMLInputElement>(`[data-test="voice-pick-${variant}"]`)!.click();
      await settle(fixture);
      document.querySelector<HTMLElement>('[data-test="voice-download"]')!.click();
      await settle(fixture);

      expect(bridge.whisperDownloadModel.mock.calls.map(([req]) => req.variant)).toEqual([variant]);
      expect(line(fixture)).toBe(`Voice model: ${variant} installed`);
    });
  }

  it('marks the models already on this computer as Installed and does not let them be picked', async () => {
    const fixture = await render(makeBridge());
    const answer = askVoiceModel(TestBed.inject(MatDialog), TestBed.inject(LocalModelsStore), [
      'tiny',
      'base',
    ]);
    await settle(fixture);

    const marks = rows().map(([variant, , , recommended, installed]) => [
      variant,
      recommended,
      installed,
    ]);
    const disabled = Array.from(
      document.querySelectorAll<HTMLInputElement>('input[type="radio"]:disabled'),
      (input) => input.value,
    );
    const picked = document.querySelector<HTMLInputElement>('input[type="radio"]:checked')?.value;
    document.querySelector<HTMLElement>('[data-test="voice-not-now"]')!.click();
    await settle(fixture);

    expect(marks).toEqual([
      ['tiny', '', 'Installed'],
      ['base', 'Recommended', 'Installed'],
      ['small', '', ''],
      ['large-v3-turbo', '', ''],
      ['large-v3', '', ''],
    ]);
    expect(disabled).toEqual(['tiny', 'base']);
    expect(picked).toBe('small');
    expect(await answer).toBeNull();
  });
});
