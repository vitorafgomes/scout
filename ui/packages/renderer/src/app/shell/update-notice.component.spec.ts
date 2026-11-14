import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { UpdateNotice, UpdateProgress, UpdateState } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';
import { UpdateNoticeComponent } from './update-notice.component';

const NOTICE: UpdateNotice = {
  currentVersion: '0.1.0',
  latestVersion: '0.2.0',
  notes: [
    "## What's Changed",
    '* Desktop update notice by @someone',
    '- Tray item for a new version',
    '',
    'Look: <img src=x onerror=alert(1)>',
    '',
    '**Full Changelog**: v0.1.0...v0.2.0',
  ].join('\n'),
  releaseUrl: 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0',
  downloadUrl:
    'https://github.com/vitorafgomes/scout/releases/download/v0.2.0/Scout-Desktop-0.2.0-linux-x86_64.AppImage',
  publishedAt: '2026-11-01T18:00:00Z',
  canInstall: false,
};

const INSTALLABLE: UpdateNotice = { ...NOTICE, canInstall: true };

const progress = (
  phase: UpdateProgress['phase'],
  extra: Partial<UpdateProgress> = {},
): UpdateProgress => ({
  phase,
  percent: null,
  transferred: null,
  total: null,
  message: null,
  ...extra,
});

const state = (notice: UpdateNotice | null): UpdateState => ({
  checkEnabled: true,
  channel: 'stable',
  lastCheckAt: '2026-11-02T09:30:00.000Z',
  lastResult: notice === null ? 'up-to-date' : `available ${notice.latestVersion}`,
  notice,
  packaged: true,
});

function makeUpdateBridge(initial: UpdateNotice | null = NOTICE) {
  const listeners: ((notice: UpdateNotice | null) => void)[] = [];
  const progressListeners: ((progress: UpdateProgress) => void)[] = [];
  let finishInstall: (state: UpdateState) => void = () => undefined;
  return {
    push: (notice: UpdateNotice | null) => listeners.forEach((listener) => listener(notice)),
    pushProgress: (next: UpdateProgress) => progressListeners.forEach((listener) => listener(next)),
    finishInstall: (next: UpdateState) => finishInstall(next),
    updateInstall: vi.fn(
      () =>
        new Promise<UpdateState>((resolve) => {
          finishInstall = resolve;
        }),
    ),
    onUpdateProgress: vi.fn((listener: (progress: UpdateProgress) => void) => {
      progressListeners.push(listener);
      return () => undefined;
    }),
    updateGetState: vi.fn(async (): Promise<UpdateState> => state(initial)),
    onUpdateNotice: vi.fn((listener: (notice: UpdateNotice | null) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
    updateRemindLater: vi.fn(async (): Promise<UpdateState> => state(null)),
    updateSkipVersion: vi.fn(async (_req: { version: string }) => state(null)),
    appOpenExternal: vi.fn(async (_req: { url: string }) => undefined),
  };
}

async function render(
  bridge: ReturnType<typeof makeUpdateBridge>,
): Promise<ComponentFixture<UpdateNoticeComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(UpdateNoticeComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<UpdateNoticeComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<UpdateNoticeComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

async function click(fixture: ComponentFixture<UpdateNoticeComponent>, test: string) {
  el(fixture, test)!.click();
  await settle(fixture);
}

describe('UpdateNoticeComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows the new and current versions', async () => {
    const fixture = await render(makeUpdateBridge());

    expect(el(fixture, 'update-notice')?.getAttribute('role')).toBe('status');
    expect(el(fixture, 'update-headline')?.textContent?.trim()).toBe(
      'Scout 0.2.0 is available. You have 0.1.0.',
    );
  });

  it('shows nothing while main knows no newer version, and the notice main pushes later', async () => {
    const bridge = makeUpdateBridge(null);
    const fixture = await render(bridge);
    expect(el(fixture, 'update-notice')).toBeNull();

    bridge.push(NOTICE);
    await settle(fixture);

    expect(el(fixture, 'update-headline')?.textContent?.trim()).toBe(
      'Scout 0.2.0 is available. You have 0.1.0.',
    );
  });

  it('renders the release notes as text, never as HTML', async () => {
    const fixture = await render(makeUpdateBridge());
    const notes = el(fixture, 'update-notes')!;

    expect(notes.tagName).toBe('DETAILS');
    expect(notes.querySelector('summary')?.textContent?.trim()).toBe("What's new");
    expect(notes.querySelector('img')).toBeNull();
    expect(Array.from(notes.querySelectorAll('li')).map((li) => li.textContent?.trim())).toEqual([
      'Desktop update notice by @someone',
      'Tray item for a new version',
    ]);
    expect(Array.from(notes.querySelectorAll('h4')).map((h) => h.textContent?.trim())).toEqual([
      "What's Changed",
    ]);
    expect(Array.from(notes.querySelectorAll('p')).map((p) => p.textContent?.trim())).toEqual([
      'Look: <img src=x onerror=alert(1)>',
      'Full Changelog: v0.1.0...v0.2.0',
    ]);
  });

  it('renders a generated body with its heading and bold label as plain text', async () => {
    const bridge = makeUpdateBridge();
    bridge.updateGetState.mockResolvedValueOnce(
      state({
        ...NOTICE,
        notes: [
          "## What's Changed",
          '* Desktop update notice by @someone in https://github.com/vitorafgomes/scout/pull/71',
          '* Tray item for a new version',
          '',
          '**Full Changelog**: https://github.com/vitorafgomes/scout/compare/v0.0.1...v0.1.0',
        ].join('\n'),
      }),
    );
    const fixture = await render(bridge);
    const notes = el(fixture, 'update-notes')!;

    expect(Array.from(notes.querySelectorAll('h4')).map((h) => h.textContent?.trim())).toEqual([
      "What's Changed",
    ]);
    expect(Array.from(notes.querySelectorAll('li')).map((li) => li.textContent?.trim())).toEqual([
      'Desktop update notice by @someone in https://github.com/vitorafgomes/scout/pull/71',
      'Tray item for a new version',
    ]);
    expect(Array.from(notes.querySelectorAll('p')).map((p) => p.textContent?.trim())).toEqual([
      'Full Changelog: https://github.com/vitorafgomes/scout/compare/v0.0.1...v0.1.0',
    ]);
    expect(el(fixture, 'update-notice')?.textContent).not.toContain('#');
    expect(el(fixture, 'update-notice')?.textContent).not.toContain('*');
  });

  it('Download opens the asset for this machine in the browser', async () => {
    const bridge = makeUpdateBridge();
    const fixture = await render(bridge);

    await click(fixture, 'update-download');

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: NOTICE.downloadUrl });
    expect(el(fixture, 'update-notice')).not.toBeNull();
  });

  it('Release notes opens the release page', async () => {
    const bridge = makeUpdateBridge();
    const fixture = await render(bridge);

    await click(fixture, 'update-release-notes');

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: NOTICE.releaseUrl });
  });

  it('Later hides the bar', async () => {
    const bridge = makeUpdateBridge();
    const fixture = await render(bridge);

    await click(fixture, 'update-later');

    expect(bridge.updateRemindLater).toHaveBeenCalledTimes(1);
    expect(bridge.updateSkipVersion).not.toHaveBeenCalled();
    expect(el(fixture, 'update-notice')).toBeNull();
  });

  it('Skip this version hides the bar and stores the version', async () => {
    const bridge = makeUpdateBridge();
    const fixture = await render(bridge);

    await click(fixture, 'update-skip');

    expect(bridge.updateSkipVersion).toHaveBeenCalledWith({ version: '0.2.0' });
    expect(el(fixture, 'update-notice')).toBeNull();
  });

  it('a skip main refuses keeps the bar and says why', async () => {
    const bridge = makeUpdateBridge();
    bridge.updateSkipVersion.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:update.skipVersion': Error: [scout:vault/unavailable] Secret storage is unavailable.",
      ),
    );
    const fixture = await render(bridge);

    await click(fixture, 'update-skip');

    expect(el(fixture, 'update-notice')).not.toBeNull();
    expect(el(fixture, 'update-error')?.textContent?.trim()).toBe('Secret storage is unavailable.');
  });

  it('every action button is at least 32 px tall', async () => {
    const fixture = await render(makeUpdateBridge());

    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="update-notice"] button'),
    ) as HTMLElement[];
    expect(buttons.map((b) => b.textContent?.trim())).toEqual([
      'Download',
      'Later',
      'Skip this version',
    ]);
    expect(buttons.map((b) => getComputedStyle(b).minHeight)).toEqual(['32px', '32px', '32px']);
  });

  it('offers Update and restart when the package can self-update, Download otherwise', async () => {
    const installable = makeUpdateBridge(INSTALLABLE);
    const updating = await render(installable);
    const updateButtons = Array.from(
      updating.nativeElement.querySelectorAll('[data-test="update-notice"] button'),
    ) as HTMLElement[];
    el(updating, 'update-install')?.click();
    await settle(updating);
    TestBed.resetTestingModule();
    const downloading = await render(makeUpdateBridge(NOTICE));

    expect(updateButtons.map((b) => b.textContent?.trim())).toEqual([
      'Update and restart',
      'Later',
      'Skip this version',
    ]);
    expect(el(updating, 'update-download')).toBeNull();
    expect(installable.updateInstall).toHaveBeenCalledTimes(1);
    expect(installable.appOpenExternal).not.toHaveBeenCalled();
    expect(el(downloading, 'update-install')).toBeNull();
    expect(el(downloading, 'update-download')?.textContent?.trim()).toBe('Download');
  });

  it('shows download progress and then the restart message', async () => {
    const bridge = makeUpdateBridge(INSTALLABLE);
    const fixture = await render(bridge);
    await click(fixture, 'update-install');

    bridge.pushProgress(progress('checking'));
    await settle(fixture);
    const checking = el(fixture, 'update-progress')?.textContent?.trim();
    bridge.pushProgress(progress('downloading', { percent: 42, transferred: 42, total: 100 }));
    await settle(fixture);
    const downloading = el(fixture, 'update-progress');
    const downloadingText = downloading?.textContent?.trim();
    const bar = downloading?.querySelector('progress');
    const barValue = bar?.value;
    const barMax = bar?.max;
    const disabledWhileDownloading = (
      Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="update-notice"] button'),
      ) as HTMLButtonElement[]
    ).map((b) => b.disabled);
    bridge.pushProgress(progress('ready'));
    await settle(fixture);
    const ready = el(fixture, 'update-progress');

    expect(checking).toBe('Checking the release');
    expect(downloadingText).toBe('Downloading 42%');
    expect(barValue).toBe(42);
    expect(barMax).toBe(100);
    expect(disabledWhileDownloading).toEqual([true, true, true]);
    expect(ready?.textContent?.trim()).toBe('Restarting to install');
    expect(ready?.querySelector('progress')).toBeNull();
  });

  it('a failed install shows why and brings the buttons back', async () => {
    const bridge = makeUpdateBridge(INSTALLABLE);
    const fixture = await render(bridge);
    await click(fixture, 'update-install');
    bridge.pushProgress(progress('checking'));
    await settle(fixture);

    bridge.pushProgress(
      progress('failed', {
        message: 'The download did not match the release; nothing was installed',
      }),
    );
    bridge.finishInstall(state(INSTALLABLE));
    await settle(fixture);

    expect(el(fixture, 'update-progress')?.textContent?.trim()).toBe(
      'The download did not match the release; nothing was installed',
    );
    expect(el(fixture, 'update-progress')?.getAttribute('role')).toBe('alert');
    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="update-notice"] button'),
    ) as HTMLButtonElement[];
    expect(buttons.map((b) => [b.textContent?.trim(), b.disabled])).toEqual([
      ['Update and restart', false],
      ['Download', false],
      ['Later', false],
      ['Skip this version', false],
    ]);
  });

  it('after a failed install the bar also offers Download', async () => {
    const bridge = makeUpdateBridge(INSTALLABLE);
    const fixture = await render(bridge);
    const downloadBeforeFailure = el(fixture, 'update-download');
    await click(fixture, 'update-install');
    bridge.pushProgress(
      progress('failed', { message: 'The update failed (network); nothing was installed' }),
    );
    bridge.finishInstall(state(INSTALLABLE));
    await settle(fixture);

    el(fixture, 'update-download')?.click();
    await settle(fixture);

    expect(downloadBeforeFailure).toBeNull();
    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('[data-test="update-notice"] button'),
    ) as HTMLElement[];
    expect(buttons.map((b) => [b.textContent?.trim(), b.classList.contains('primary')])).toEqual([
      ['Update and restart', true],
      ['Download', false],
      ['Later', false],
      ['Skip this version', false],
    ]);
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INSTALLABLE.downloadUrl });
  });
});
