import { readFileSync } from 'node:fs';

import { InteractivityChecker } from '@angular/cdk/a11y';
import { formatDate } from '@angular/common';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneInvalidArgumentError,
  encodeIpcErrorMessage,
  serializeError,
  type OperatorProjectDto,
  type PublicationDto,
  type SceneDto,
  type VideoPackageDto,
} from '@scout/shared';

import { LAID_OUT } from '../core/laid-out.testing';
import { ScoutBridgeService } from '../core/scout-bridge';

import { AssemblyComponent } from './assembly.component';

function scene(overrides: Partial<SceneDto> = {}): SceneDto {
  return {
    id: 's1',
    order: 1,
    narration: 'Span is a view over memory.',
    visualPrompt: 'A ruler over bytes',
    locked: false,
    status: 'ready',
    error: null,
    audioSource: 'generated',
    visualSource: 'generated',
    visualQuery: '',
    visualCredit: null,
    audioFile: 'scene-s1.mp3',
    visualFile: 'scene-s1.png',
    durationMs: 4200,
    clipStartMs: 0,
    clipEndMs: 0,
    ...overrides,
  };
}

function videoPackage(overrides: Partial<VideoPackageDto> = {}): VideoPackageDto {
  return {
    status: 'assembled',
    videoFile: 'video.mp4',
    shorts: [
      { file: 'short-1.mp4', startMs: 0, durationMs: 40000, hook: 'Span never allocates.' },
      { file: 'short-2.mp4', startMs: 40000, durationMs: 30000, hook: 'Stop copying arrays.' },
    ],
    thumbnailFile: 'thumbnail.png',
    checks: [
      { name: 'Loudness', result: 'pass', detail: '-14 LUFS' },
      { name: 'Captions', result: 'warn', detail: '2 lines over 42 characters' },
    ],
    rightsConfirmed: false,
    approvedAt: null,
    title: 'Span<T> explained',
    description: 'A tour of Span<T>.',
    tags: ['dotnet', 'span'],
    durationMs: 98000,
    error: null,
    metadataEdited: false,
    ...overrides,
  };
}

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: 'Span<T> in C#',
    status: 'media_ready',
    origin: 'desktop',
    createdAt: '2026-11-02T09:00:00Z',
    updatedAt: '2026-11-03T09:05:00Z',
    candidates: [],
    script: null,
    claims: [
      {
        id: 'c1',
        text: 'Span<T> is a ref struct.',
        risk: 'low',
        sourceUrls: [],
        status: 'supported',
        note: null,
      },
    ],
    error: null,
    scenes: [scene(), scene({ id: 's2', order: 2 })],
    package: null,
    publication: null,
    narrationVoice: '',
    backgroundFile: null,
    delegated: false,
    direction: '',
    motion: 'gentle',
    format: 'generated_pictures',
    recordingFile: null,
    recordingMs: 0,
    presenter: '',
    presenterFile: null,
    mouthBox: null,
    presenterVersions: [],
    presenterPreviewFile: null,
    presenterRequest: '',
    presenterSource: '',
    ...overrides,
  };
}

function assembled(
  packageOverrides: Partial<VideoPackageDto> = {},
  overrides: Partial<OperatorProjectDto> = {},
) {
  return project({ status: 'assembled', package: videoPackage(packageOverrides), ...overrides });
}

function refusedWith(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneInvalidArgumentError(message)),
    )}`,
  );
}

function makeBridge(projects: OperatorProjectDto[], overrides: Record<string, unknown> = {}) {
  return {
    operatorProjects: vi.fn(async () => ({ projects })),
    operatorAssemble: vi.fn(async () =>
      project({
        status: 'assembly_queued',
        package: videoPackage({
          status: 'assembly_queued',
          videoFile: null,
          shorts: [],
          thumbnailFile: null,
          checks: [],
        }),
      }),
    ),
    operatorConfirmMediaRights: vi.fn(async () => assembled({ rightsConfirmed: true })),
    operatorEditPackageMetadata: vi.fn(async () => assembled({ metadataEdited: true })),
    operatorApprovePackage: vi.fn(async () =>
      assembled(
        { status: 'approved', rightsConfirmed: true, approvedAt: '2026-11-03T11:00:00Z' },
        { status: 'approved' },
      ),
    ),
    operatorPublishPackage: vi.fn(async (): Promise<OperatorProjectDto> => project()),
    appOpenExternal: vi.fn(async (_req: { url: string }) => undefined),
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

async function settle(fixture: ComponentFixture<AssemblyComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  fixture.detectChanges();
}

async function mount(
  bridge: Bridge,
  selectId?: string,
): Promise<ComponentFixture<AssemblyComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      { provide: InteractivityChecker, useValue: LAID_OUT },
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(AssemblyComponent);
  await settle(fixture);
  if (selectId !== undefined) {
    const picker = el<HTMLSelectElement>(fixture, 'assembly-project')!;
    picker.value = selectId;
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
  }
  return fixture;
}

function el<T extends HTMLElement = HTMLElement>(
  fixture: ComponentFixture<AssemblyComponent>,
  testId: string,
): T | null {
  const selector = `[data-test="${testId}"]`;
  return ((fixture.nativeElement as HTMLElement).querySelector<T>(selector) ??
    document.querySelector<T>(selector)) as T | null;
}

function text(fixture: ComponentFixture<AssemblyComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

async function type(fixture: ComponentFixture<AssemblyComponent>, testId: string, value: string) {
  const input = el<HTMLInputElement>(fixture, testId)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

async function click(fixture: ComponentFixture<AssemblyComponent>, testId: string): Promise<void> {
  el(fixture, testId)!.click();
  await settle(fixture);
}

describe('AssemblyComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
    vi.useRealTimers();
  });

  it('the project picker lists only videos with their media ready or further on', async () => {
    const statuses = [
      'media_ready',
      'assembly_queued',
      'assembling',
      'assembled',
      'approved',
      'failed',
      'script_ready',
      'producing',
      'queued',
    ];
    const fixture = await mount(
      makeBridge(
        statuses.map((status, i) => project({ id: `p${i}`, topic: `Video ${i}`, status })),
      ),
    );

    const options = Array.from(
      el<HTMLSelectElement>(fixture, 'assembly-project')!.querySelectorAll('option'),
    ).map((option) => [option.value, option.textContent?.replace(/\s+/g, ' ').trim()]);
    expect(options).toEqual([
      ['', 'Choose a video'],
      ['p0', 'Video 0 · Waiting to assemble'],
      ['p1', 'Video 1 · Queued for assembly'],
      ['p2', 'Video 2 · Assembling'],
      ['p3', 'Video 3 · Assembled'],
      ['p4', 'Video 4 · Approved'],
      ['p5', 'Video 5 · Failed'],
    ]);
  });

  it('says there is nothing to assemble while no video has its media ready', async () => {
    const fixture = await mount(makeBridge([project({ status: 'producing' })]));

    expect(text(fixture, 'assembly-empty')).toBe(
      'No video is ready for assembly yet. Make its media on the Production page first.',
    );
    expect(el(fixture, 'assembly-project')).toBeNull();
  });

  it('a failed load shows the error and keeps the last list', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const bridge = makeBridge([project()]);
    const fixture = await mount(bridge, 'p1');
    bridge.operatorProjects.mockImplementation(async () => {
      throw new Error(
        `Error invoking remote method 'scout:operator.projects': Error: [scout:control-plane/unavailable] The control plane is not running.`,
      );
    });

    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(text(fixture, 'assembly-error')).toBe('The control plane is not running.');
    expect(
      Array.from(
        el<HTMLSelectElement>(fixture, 'assembly-project')!.querySelectorAll('option'),
      ).map((option) => option.value),
    ).toEqual(['', 'p1']);
    expect(el(fixture, 'assemble')).not.toBeNull();
  });

  it('Assemble sends the project id and shows the queued project with its progress text', async () => {
    const bridge = makeBridge([project()]);
    const fixture = await mount(bridge, 'p1');

    await click(fixture, 'assemble');

    expect(bridge.operatorAssemble.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(text(fixture, 'assembly-progress')).toBe(
      'Queued for assembly; the Scout worker starts it next.',
    );
  });

  it('a project being assembled shows that it is assembling and cannot be assembled again', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          status: 'assembling',
          package: videoPackage({ status: 'assembling', videoFile: null }),
        }),
      ]),
      'p1',
    );

    expect(text(fixture, 'assembly-progress')).toBe(
      'Assembling the video, its Shorts and its thumbnail.',
    );
    expect(el<HTMLButtonElement>(fixture, 'assemble')!.disabled).toBe(true);
  });

  it('Assemble is enabled on media ready, assembled, and a failed project only when every scene is ready', async () => {
    const enabledFor = async (p: OperatorProjectDto): Promise<boolean> => {
      TestBed.resetTestingModule();
      const fixture = await mount(makeBridge([p]), 'p1');
      return !el<HTMLButtonElement>(fixture, 'assemble')!.disabled;
    };

    expect([
      await enabledFor(project()),
      await enabledFor(assembled()),
      await enabledFor(project({ status: 'failed', error: 'ffmpeg exited with 1' })),
      await enabledFor(
        project({ status: 'failed', scenes: [scene(), scene({ id: 's2', status: 'failed' })] }),
      ),
      await enabledFor(project({ status: 'failed', scenes: [] })),
      await enabledFor(assembled({ status: 'approved' }, { status: 'approved' })),
    ]).toEqual([true, true, true, false, false, false]);
  });

  it('an assembly the control plane refuses shows its reason', async () => {
    const reason = '1 scene is not ready; regenerate it or attach its media before assembly.';
    const fixture = await mount(
      makeBridge([project()], {
        operatorAssemble: vi.fn(async () => {
          throw refusedWith('scout:operator.assembly', reason);
        }),
      }),
      'p1',
    );

    await click(fixture, 'assemble');

    expect(text(fixture, 'assemble-error')).toBe(reason);
  });

  it('an assembled package plays the video and each Short with its hook from scout-media, refreshed on every update, and previews the thumbnail', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');
    const v = `?v=${encodeURIComponent('2026-11-03T09:05:00Z')}`;

    const video = el<HTMLVideoElement>(fixture, 'package-video')!;
    expect([video.getAttribute('src'), video.hasAttribute('controls')]).toEqual([
      `scout-media://p1/video.mp4${v}`,
      true,
    ]);
    expect(
      [0, 1].map((i) => [
        el(fixture, `short-video-${i}`)?.getAttribute('src'),
        text(fixture, `short-hook-${i}`),
      ]),
    ).toEqual([
      [`scout-media://p1/short-1.mp4${v}`, 'Span never allocates.'],
      [`scout-media://p1/short-2.mp4${v}`, 'Stop copying arrays.'],
    ]);
    const thumbnail = el<HTMLImageElement>(fixture, 'package-thumbnail')!;
    expect([thumbnail.getAttribute('src'), thumbnail.alt]).toEqual([
      `scout-media://p1/thumbnail.png${v}`,
      'Thumbnail of the video',
    ]);

    bridge.operatorProjects.mockImplementation(async () => ({
      projects: [assembled({}, { updatedAt: '2026-11-03T10:00:00Z' })],
    }));
    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    const updated = `?v=${encodeURIComponent('2026-11-03T10:00:00Z')}`;
    expect(
      ['package-video', 'short-video-0', 'short-video-1', 'package-thumbnail'].map((testId) =>
        el(fixture, testId)?.getAttribute('src'),
      ),
    ).toEqual([
      `scout-media://p1/video.mp4${updated}`,
      `scout-media://p1/short-1.mp4${updated}`,
      `scout-media://p1/short-2.mp4${updated}`,
      `scout-media://p1/thumbnail.png${updated}`,
    ]);
  });

  it('the checks table shows each check with its name, a Passed, Warning or Failed chip on its tone, and its detail', async () => {
    const fixture = await mount(
      makeBridge([
        assembled({
          checks: [
            { name: 'Loudness', result: 'pass', detail: '-14 LUFS' },
            { name: 'Captions', result: 'warn', detail: '2 lines over 42 characters' },
            { name: 'Length', result: 'fail', detail: 'Shorter than 30 s' },
          ],
        }),
      ]),
      'p1',
    );

    const rows = [0, 1, 2].map((i) => [
      text(fixture, `check-name-${i}`),
      text(fixture, `check-result-${i}`),
      el(fixture, `check-result-${i}`)?.getAttribute('data-tone'),
      text(fixture, `check-detail-${i}`),
    ]);
    expect(rows).toEqual([
      ['Loudness', 'Passed', 'ok', '-14 LUFS'],
      ['Captions', 'Warning', 'warn', '2 lines over 42 characters'],
      ['Length', 'Failed', 'error', 'Shorter than 30 s'],
    ]);
  });

  it('the metadata editor counts the title to 100, the description to 5000 and the tags to 500 characters as YouTube does, and adds and removes tag chips', async () => {
    const fixture = await mount(makeBridge([assembled()]), 'p1');

    expect([
      text(fixture, 'title-count'),
      text(fixture, 'description-count'),
      text(fixture, 'tags-count'),
    ]).toEqual(['17 / 100', '18 / 5000', '11 / 500']);
    await type(fixture, 'metadata-title', 'x'.repeat(101));
    await type(fixture, 'tag-input', ' performance ');
    await click(fixture, 'tag-add');
    await click(fixture, 'tag-remove-0');

    expect(text(fixture, 'title-count')).toBe('101 / 100');
    expect(el<HTMLButtonElement>(fixture, 'metadata-save')!.disabled).toBe(true);
    expect([text(fixture, 'tag-0'), text(fixture, 'tag-1')]).toEqual(['span', 'performance']);
    expect(text(fixture, 'tags-count')).toBe('16 / 500');
  });

  it('counts a tag with a space with its two quotes and a comma between tags, and a count over 500 that way disables Save', async () => {
    const fixture = await mount(makeBridge([assembled()]), 'p1');

    await type(fixture, 'tag-input', 'memory safety');
    await click(fixture, 'tag-add');

    expect(text(fixture, 'tags-count')).toBe('27 / 500');
    expect(el<HTMLButtonElement>(fixture, 'metadata-save')!.disabled).toBe(false);

    for (const letter of ['a', 'b', 'c', 'd']) {
      await type(fixture, 'tag-input', letter.repeat(100));
      await click(fixture, 'tag-add');
    }
    await click(fixture, 'tag-remove-0');
    await click(fixture, 'tag-remove-0');
    await click(fixture, 'tag-remove-0');
    await type(fixture, 'tag-input', `${'e'.repeat(90)} bends`);
    await click(fixture, 'tag-add');

    expect(text(fixture, 'tags-count')).toBe('502 / 500');
    expect(el<HTMLButtonElement>(fixture, 'metadata-save')!.disabled).toBe(true);
  });

  it("a refresh that brings the same metadata keeps the owner's unsaved edits, and new metadata from the control plane replaces them", async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');

    await type(fixture, 'metadata-title', 'An unsaved title');
    vi.advanceTimersByTime(10_000);
    await settle(fixture);
    const kept = el<HTMLInputElement>(fixture, 'metadata-title')!.value;
    bridge.operatorProjects.mockImplementation(async () => ({
      projects: [assembled({ title: 'Span<T>, explained again' })],
    }));
    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(kept).toBe('An unsaved title');
    expect(el<HTMLInputElement>(fixture, 'metadata-title')!.value).toBe('Span<T>, explained again');
  });

  it('Save sends the trimmed title and description and the tags to EditPackageMetadata, and a refusal shows its reason', async () => {
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');

    await type(fixture, 'metadata-title', '  Span<T> without fear ');
    await type(fixture, 'metadata-description', ' Why Span<T> never allocates. ');
    await click(fixture, 'metadata-save');
    const errorAfterSave = el(fixture, 'metadata-error');
    bridge.operatorEditPackageMetadata.mockImplementation(async () => {
      throw refusedWith('scout:operator.packageMetadata', 'The title is 1 to 100 characters.');
    });
    await click(fixture, 'metadata-save');

    expect(bridge.operatorEditPackageMetadata.mock.calls[0]).toEqual([
      {
        projectId: 'p1',
        title: 'Span<T> without fear',
        description: 'Why Span<T> never allocates.',
        tags: ['dotnet', 'span'],
      },
    ]);
    expect(errorAfterSave).toBeNull();
    expect(text(fixture, 'metadata-error')).toBe('The title is 1 to 100 characters.');
  });

  it('ticking the media rights box and confirming the step confirms them with ConfirmMediaRights, after which it stays ticked and cannot be unticked', async () => {
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');
    const box = el<HTMLInputElement>(fixture, 'media-rights')!;

    expect(text(fixture, 'media-rights-label')).toBe(
      'I own or have the rights to every image, recording and music in this video',
    );
    box.click();
    await settle(fixture);
    await click(fixture, 'rights-confirm');

    expect(bridge.operatorConfirmMediaRights.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect([
      el<HTMLInputElement>(fixture, 'media-rights')!.checked,
      el<HTMLInputElement>(fixture, 'media-rights')!.disabled,
    ]).toEqual([true, true]);
  });

  it('a media rights confirmation that fails leaves the box unticked and shows the reason', async () => {
    const fixture = await mount(
      makeBridge([assembled()], {
        operatorConfirmMediaRights: vi.fn(async () => {
          throw refusedWith(
            'scout:operator.mediaRights',
            'Media rights are confirmed on an assembled package; this one is assembling.',
          );
        }),
      }),
      'p1',
    );

    el<HTMLInputElement>(fixture, 'media-rights')!.click();
    await settle(fixture);
    await click(fixture, 'rights-confirm');

    expect(el<HTMLInputElement>(fixture, 'media-rights')!.checked).toBe(false);
    expect(text(fixture, 'rights-error')).toBe(
      'Media rights are confirmed on an assembled package; this one is assembling.',
    );
  });

  it('Approve package is disabled with every reason and its count: not assembled, rights not confirmed, failing checks, claims not supported or waived', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          status: 'failed',
          package: videoPackage({
            status: 'failed',
            checks: [
              { name: 'Loudness', result: 'fail', detail: '-30 LUFS' },
              { name: 'Length', result: 'fail', detail: 'Too short' },
            ],
          }),
          claims: [
            { id: 'c1', text: 'A', risk: 'low', sourceUrls: [], status: 'pending', note: null },
            { id: 'c2', text: 'B', risk: 'low', sourceUrls: [], status: 'unsupported', note: null },
            { id: 'c3', text: 'C', risk: 'low', sourceUrls: [], status: 'waived', note: 'Known' },
          ],
        }),
      ]),
      'p1',
    );

    expect(el<HTMLButtonElement>(fixture, 'package-approve')!.disabled).toBe(true);
    expect(text(fixture, 'approve-blocked')).toBe(
      'Before approving: the video is not assembled. Confirm the media rights first. 2 checks failed. 2 claims are not supported or waived.',
    );
  });

  it('Approve package sends the project id once nothing blocks it, then shows when it was approved', async () => {
    const bridge = makeBridge([assembled({ rightsConfirmed: true })]);
    const fixture = await mount(bridge, 'p1');

    expect(el(fixture, 'approve-blocked')).toBeNull();
    await click(fixture, 'package-approve');

    expect(bridge.operatorApprovePackage.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(text(fixture, 'package-approved')).toBe(
      `Approved ${formatDate('2026-11-03T11:00:00Z', 'yyyy-MM-dd', 'en-US')} at ${formatDate('2026-11-03T11:00:00Z', 'HH:mm', 'en-US')}`,
    );
    expect(el(fixture, 'package-approve')).toBeNull();
  });

  it('an approval the control plane refuses shows its reason', async () => {
    const fixture = await mount(
      makeBridge([assembled({ rightsConfirmed: true })], {
        operatorApprovePackage: vi.fn(async () => {
          throw refusedWith(
            'scout:operator.packageApproval',
            '1 claim is not supported or waived.',
          );
        }),
      }),
      'p1',
    );

    await click(fixture, 'package-approve');

    expect(text(fixture, 'approve-error')).toBe('1 claim is not supported or waived.');
  });

  it('a failed assembly shows the package error', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          status: 'failed',
          error: 'Assembly failed.',
          package: videoPackage({
            status: 'failed',
            videoFile: null,
            error: 'ffmpeg exited with code 1: Invalid data',
          }),
        }),
      ]),
      'p1',
    );

    expect(text(fixture, 'package-error')).toBe('ffmpeg exited with code 1: Invalid data');
  });

  it('reads the projects again every 10 s, so the status the worker moves shows without a reload', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const bridge = makeBridge([project({ status: 'assembly_queued' })]);
    const fixture = await mount(bridge, 'p1');
    bridge.operatorProjects.mockImplementation(async () => ({ projects: [assembled()] }));

    vi.advanceTimersByTime(10_000);
    await settle(fixture);

    expect(bridge.operatorProjects.mock.calls.length).toBe(2);
    expect(el(fixture, 'package-video')).not.toBeNull();
    expect(el(fixture, 'assembly-progress')).toBeNull();
  });

  describe('publishing an approved package', () => {
    function approved(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
      return assembled(
        { status: 'approved', rightsConfirmed: true, approvedAt: '2026-11-03T11:00:00Z' },
        { status: 'approved', ...overrides },
      );
    }

    function publication(overrides: Partial<PublicationDto> = {}): PublicationDto {
      return {
        status: 'publish_approved',
        videoId: null,
        privacyStatus: 'private',
        publishAt: null,
        madeForKids: false,
        syntheticMedia: true,
        uploadedAt: null,
        error: null,
        snapshots: [],
        proposals: [],
        experiment: null,
        ...overrides,
      };
    }

    async function choose(fixture: ComponentFixture<AssemblyComponent>, testId: string) {
      const input = el<HTMLInputElement>(fixture, testId)!;
      input.checked = true;
      input.dispatchEvent(new Event('change'));
      await settle(fixture);
    }

    function blocks(fixture: ComponentFixture<AssemblyComponent>): string[] {
      return Array.from(el(fixture, 'publish-blocked')?.querySelectorAll('li') ?? []).map(
        (li) => li.textContent?.trim() ?? '',
      );
    }

    it('only an approved package without a publication offers Publish', async () => {
      const fixture = await mount(
        makeBridge([
          assembled({ rightsConfirmed: true }, { id: 'p1' }),
          approved({ id: 'p2' }),
          approved({ id: 'p3', publication: publication() }),
        ]),
        'p1',
      );
      const offered = [el(fixture, 'publish-open') !== null];
      for (const id of ['p2', 'p3']) {
        const picker = el<HTMLSelectElement>(fixture, 'assembly-project')!;
        picker.value = id;
        picker.dispatchEvent(new Event('change'));
        await settle(fixture);
        offered.push(el(fixture, 'publish-open') !== null);
      }

      expect(offered).toEqual([false, true, false]);
    });

    it('the dialog requires a privacy choice and a made for kids answer, with no privacy chosen at first', async () => {
      const fixture = await mount(makeBridge([approved()]), 'p1');
      await click(fixture, 'publish-open');

      const radios = ['public', 'private', 'unlisted'].map(
        (v) => el<HTMLInputElement>(fixture, `privacy-${v}`)!.checked,
      );
      const before = blocks(fixture);
      const disabledBefore = el<HTMLButtonElement>(fixture, 'publish-confirm')!.disabled;
      await choose(fixture, 'privacy-unlisted');
      await choose(fixture, 'kids-no');

      expect(radios).toEqual([false, false, false]);
      expect(before).toEqual([
        'Choose public, private or unlisted.',
        'Say whether the video is made for kids.',
      ]);
      expect(disabledBefore).toBe(true);
      expect(blocks(fixture)).toEqual([]);
      expect(el<HTMLButtonElement>(fixture, 'publish-confirm')!.disabled).toBe(false);
    });

    it('the schedule is enabled only with private, and choosing public clears it', async () => {
      const fixture = await mount(makeBridge([approved()]), 'p1');
      await click(fixture, 'publish-open');

      const disabledAtFirst = el<HTMLInputElement>(fixture, 'publish-schedule')!.disabled;
      await choose(fixture, 'privacy-private');
      const enabledWithPrivate = !el<HTMLInputElement>(fixture, 'publish-schedule')!.disabled;
      await type(fixture, 'publish-schedule', '2099-07-01T10:00');
      await choose(fixture, 'privacy-public');

      expect([disabledAtFirst, enabledWithPrivate]).toEqual([true, true]);
      expect(el<HTMLInputElement>(fixture, 'publish-schedule')!.disabled).toBe(true);
      expect(el<HTMLInputElement>(fixture, 'publish-schedule')!.value).toBe('');
    });

    it('a schedule in the past is refused, and a London summer time is shown and converted to UTC', async () => {
      const fixture = await mount(makeBridge([approved()]), 'p1');
      await click(fixture, 'publish-open');
      await choose(fixture, 'privacy-private');
      await choose(fixture, 'kids-no');

      await type(fixture, 'publish-schedule', '2020-07-01T10:00');
      const past = blocks(fixture);
      await type(fixture, 'publish-schedule', '2099-07-01T10:00');

      expect(past).toEqual(['The scheduled time must be in the future.']);
      expect(blocks(fixture)).toEqual([]);
      expect(text(fixture, 'publish-schedule-utc')).toBe(
        'Publishes 2099-07-01 10:00 Europe/London (2099-07-01T09:00:00.000Z UTC).',
      );
    });

    it('lists the actions that will run, and Confirm and publish sends the choices with the schedule in UTC', async () => {
      const bridge = makeBridge([approved()], {
        operatorPublishPackage: vi.fn(async () => approved({ publication: publication() })),
      });
      const fixture = await mount(bridge, 'p1');
      await click(fixture, 'publish-open');
      await choose(fixture, 'privacy-private');
      await choose(fixture, 'kids-yes');
      await type(fixture, 'publish-schedule', '2099-12-01T18:30');

      const actions = Array.from(document.querySelectorAll('[data-test="publish-actions"] li')).map(
        (li) => (li as HTMLElement).textContent?.replace(/\s+/g, ' ').trim(),
      );
      await click(fixture, 'publish-confirm');

      expect(actions).toEqual([
        'Upload the video "Span<T> explained" as Private',
        'Set the thumbnail shown above (thumbnail.png)',
        'Schedule: public at 2099-12-01 18:30 Europe/London',
      ]);
      expect(bridge.operatorPublishPackage).toHaveBeenCalledWith({
        projectId: 'p1',
        privacyStatus: 'private',
        publishAt: '2099-12-01T18:30:00.000Z',
        madeForKids: true,
        syntheticMedia: true,
      });
      expect(el(fixture, 'publish-dialog')).toBeNull();
      expect(text(fixture, 'publication-status')).toBe('publish_approved');
    });

    it('synthetic media is ticked when a scene used generated narration or pictures, unticked otherwise, and the owner can change it', async () => {
      const owned = scene({ audioSource: 'owner', visualSource: 'owner' });
      const bridge = makeBridge(
        [
          approved(),
          approved({
            id: 'p2',
            scenes: [owned, { ...owned, id: 's2', visualSource: 'text_card' }],
          }),
        ],
        { operatorPublishPackage: vi.fn(async () => approved({ id: 'p2' })) },
      );
      const fixture = await mount(bridge, 'p1');
      await click(fixture, 'publish-open');
      const generated = el<HTMLInputElement>(fixture, 'publish-synthetic')!.checked;
      await click(fixture, 'publish-cancel');
      const picker = el<HTMLSelectElement>(fixture, 'assembly-project')!;
      picker.value = 'p2';
      picker.dispatchEvent(new Event('change'));
      await settle(fixture);
      await click(fixture, 'publish-open');
      const ownMedia = el<HTMLInputElement>(fixture, 'publish-synthetic')!.checked;
      const box = el<HTMLInputElement>(fixture, 'publish-synthetic')!;
      box.checked = true;
      box.dispatchEvent(new Event('change'));
      await choose(fixture, 'privacy-public');
      await choose(fixture, 'kids-no');
      await click(fixture, 'publish-confirm');

      expect([generated, ownMedia]).toEqual([true, false]);
      expect(bridge.operatorPublishPackage).toHaveBeenCalledWith(
        expect.objectContaining({ projectId: 'p2', publishAt: null, syntheticMedia: true }),
      );
    });

    it('a refused publish keeps the dialog open with the reason', async () => {
      const reason =
        'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
      const bridge = makeBridge([approved()], {
        operatorPublishPackage: vi.fn(async () => {
          throw refusedWith('scout:operator.publication', reason);
        }),
      });
      const fixture = await mount(bridge, 'p1');
      await click(fixture, 'publish-open');
      await choose(fixture, 'privacy-unlisted');
      await choose(fixture, 'kids-no');
      await click(fixture, 'publish-confirm');

      expect(text(fixture, 'publish-error')).toBe(reason);
      expect(el(fixture, 'publish-dialog')).not.toBeNull();
    });

    it('the publication panel shows the status, the video link opened through the allow-listed open, the scheduled time in London and the error', async () => {
      const bridge = makeBridge(
        [
          approved({
            publication: publication({
              status: 'scheduled',
              videoId: 'dQw4w9WgXcQ',
              publishAt: '2099-07-01T09:00:00.000Z',
              error: 'The thumbnail was not set: YouTube answered 403.',
            }),
          }),
        ],
        { appOpenExternal: vi.fn(async () => undefined) },
      );
      const fixture = await mount(bridge, 'p1');

      await click(fixture, 'publication-video');

      expect(text(fixture, 'publication-status')).toBe('scheduled');
      expect(text(fixture, 'publication-video')).toBe(
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
      );
      expect(bridge.appOpenExternal).toHaveBeenCalledWith({
        url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
      });
      expect(text(fixture, 'publication-scheduled')).toBe('2099-07-01 10:00 Europe/London');
      expect(text(fixture, 'publication-error')).toBe(
        'The thumbnail was not set: YouTube answered 403.',
      );
    });

    it('a project keeps its place in the picker once it has a publication, whatever its status', async () => {
      const fixture = await mount(
        makeBridge([approved({ status: 'published', publication: publication() })]),
        'p1',
      );

      expect(text(fixture, 'publication-status')).toBe('publish_approved');
    });
  });
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe('Assembly follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/assembly/assembly.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Assemble video shows its icon before its text, as the prototype draws it', async () => {
    const fixture = await mount(makeBridge([project()]), 'p1');

    expect([
      el(fixture, 'assemble')
        ?.querySelector('.mdc-button__label')
        ?.firstElementChild?.tagName.toLowerCase(),
      text(fixture, 'assemble'),
    ]).toEqual(['sc-icon', 'Assemble video']);
  });

  it('the video and its Shorts sit on the media tile of the theme, not on a black box', () => {
    const source = readFileSync('src/app/assembly/assembly.component.ts', 'utf8');

    for (const rule of ['.player', '.short']) {
      expect(
        new RegExp(`\\n {6}\\${rule} \\{[^}]*background: var\\(--sc-media\\);`).test(source),
        rule,
      ).toBe(true);
    }
  });
});

/** The text of a node as read aloud: its text pieces trimmed and joined by one space. */
function spoken(node: Node | null | undefined): string {
  if (node === null || node === undefined) return '';
  const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
  const pieces: string[] = [];
  for (let n = walker.nextNode(); n !== null; n = walker.nextNode()) {
    const piece = (n.textContent ?? '').replace(/\s+/g, ' ').trim();
    if (piece !== '') pieces.push(piece);
  }
  return pieces.join(' ');
}

/** What the owner sees, in order: the headings, buttons, links, form fields by their label, and status lines, skipping anything hidden. */
function seen(fixture: ComponentFixture<AssemblyComponent>): string[] {
  const roots = [
    (fixture.nativeElement as HTMLElement).querySelector('main')!,
    ...Array.from(document.querySelectorAll<HTMLElement>('.cdk-overlay-container')),
  ];
  const out: string[] = [];
  for (const root of roots) {
    for (const node of Array.from(
      root.querySelectorAll<HTMLElement>(
        'h1, h2, button, a, select, input, textarea, [role="status"], [data-test="assembly-empty"], [data-test="approve-blocked"], [data-test="assembly-progress"], [data-test="assemble-again-help"], [data-test="media-rights-help"], [data-test="package-approved"], sc-status-chip',
      ),
    )) {
      if (node.closest('[hidden]') !== null) continue;
      const tag = node.tagName.toLowerCase();
      const words = spoken(node) || (node.getAttribute('aria-label') ?? '');
      if (tag === 'input' || tag === 'select' || tag === 'textarea') {
        const input = node as HTMLInputElement;
        const label =
          (input.id && spoken(document.querySelector(`label[for="${input.id}"]`))) ||
          spoken(input.closest('label')) ||
          input.getAttribute('aria-label') ||
          '';
        const flags = [
          input.type === 'checkbox' || input.type === 'radio' ? (input.checked ? 'on' : 'off') : '',
          input.disabled ? 'disabled' : '',
        ].filter(Boolean);
        out.push(
          `${tag}${flags.length > 0 ? `(${flags.join(',')})` : ''}: ${label.replace(/\s+/g, ' ').trim()}`,
        );
      } else if (tag === 'button') {
        out.push(`button${(node as HTMLButtonElement).disabled ? '(disabled)' : ''}: ${words}`);
      } else if (tag === 'a') {
        out.push(`link ${node.getAttribute('href')}: ${words}`);
      } else if (tag === 'sc-status-chip') {
        out.push(`chip ${node.getAttribute('data-tone')}: ${words}`);
      } else if (/^h[12]$/.test(tag)) {
        out.push(`${tag}: ${words}`);
      } else {
        out.push(
          `${node.getAttribute('role') === 'status' ? `status ${node.getAttribute('aria-label')}` : 'text'}: ${words}`,
        );
      }
    }
  }
  return out;
}

describe('Assembly as Assembly.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
  });

  const head = ['h1: Assembly', 'link /operator/production: Open in Production'];

  function approvedProject(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
    return assembled(
      { status: 'approved', rightsConfirmed: true, approvedAt: '2026-11-03T11:00:00Z' },
      { status: 'approved', ...overrides },
    );
  }

  it('Loading: the title, Open in Production and the loading card named "Loading videos", nothing else', async () => {
    const fixture = await mount(
      makeBridge([], { operatorProjects: vi.fn(() => new Promise<never>(() => undefined)) }),
    );

    expect(seen(fixture)).toEqual([...head, 'status Loading videos: Loading videos']);
  });

  it('Nothing to assemble: the sentence and Go to Production, which opens the Production page', async () => {
    const fixture = await mount(makeBridge([]));

    expect(seen(fixture)).toEqual([
      ...head,
      'text: No video is ready for assembly yet. Make its media on the Production page first.',
      'link /operator/production: Go to Production',
    ]);
  });

  it('Waiting to assemble: the picker with each video and its status in words, the neutral chip, Assemble video on, and the scenes with their start times; no package, details or approval yet', async () => {
    const fixture = await mount(
      makeBridge([
        project({
          scenes: [
            scene({ durationMs: 48_000 }),
            scene({ id: 's2', order: 2, durationMs: 82_000 }),
          ],
        }),
      ]),
      'p1',
    );

    expect(seen(fixture)).toEqual([
      ...head,
      'select: Video',
      'chip neutral: Waiting to assemble',
      'button: Assemble video',
      'h2: Scenes 2 scenes · 2 min 10 s',
    ]);
    expect(
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('[data-test="scenes"] li'),
        (li) => [
          li.querySelector('[role="img"]')?.getAttribute('aria-label'),
          li.querySelector('.meta')?.textContent,
        ],
      ),
    ).toEqual([
      ['Scene 1, starts at 0:00', '0:00'],
      ['Scene 2, starts at 0:48', '0:48'],
    ]);
  });

  it('Queued for assembly: the info chip, Assemble video off, and the line saying the worker starts it next', async () => {
    const fixture = await mount(makeBridge([project({ status: 'assembly_queued' })]), 'p1');

    expect(seen(fixture).slice(2, 7)).toEqual([
      'select: Video',
      'chip info: Queued for assembly',
      'button(disabled): Assemble video',
      'text: Queued for assembly; the Scout worker starts it next.',
      'h2: Scenes 2 scenes · 8 s',
    ]);
  });

  it('Assembled: Assemble again described by its line, the scenes, video, thumbnail, Shorts and checks, YouTube details with Save details, and the approval blocked until the rights are confirmed', async () => {
    const fixture = await mount(makeBridge([assembled()]), 'p1');
    const again = el(fixture, 'assemble')!;

    expect(seen(fixture)).toEqual([
      ...head,
      'select: Video',
      'chip ok: Assembled',
      'button: Assemble again',
      'text: Assembling again replaces the video, Shorts and thumbnail below.',
      'h2: Scenes 2 scenes · 8 s',
      'h2: Video',
      'h2: Thumbnail',
      'h2: Shorts',
      'h2: Checks',
      'chip ok: Passed',
      'chip warn: Warning',
      'h2: YouTube details',
      'input: Title',
      'textarea: Description',
      'button: Remove tag dotnet',
      'button: Remove tag span',
      'input: Tags',
      'button: Add tag',
      'button: Save details',
      'h2: Approval',
      'input(off): I own or have the rights to every image, recording and music in this video',
      'text: You confirm this once per video, in a confirm step. It cannot be undone.',
      'text: Before approving: confirm the media rights first.',
      'button(disabled): Approve package',
    ]);
    expect([
      again.getAttribute('aria-describedby'),
      el(fixture, 'tag-input')!.getAttribute('placeholder'),
      text(fixture, 'metadata-saved'),
    ]).toEqual(['asm-again', 'Add tags, separated by commas', '']);
  });

  it('Rights confirm step: ticking the box opens "Confirm the media rights?" as an alert dialog on the theme card, with Cancel focused and Confirm the rights', async () => {
    const fixture = await mount(makeBridge([assembled()]), 'p1');

    el<HTMLInputElement>(fixture, 'media-rights')!.click();
    await settle(fixture);
    const dialog = document.querySelector<HTMLElement>('[role="alertdialog"]')!;

    expect({
      panel: document.querySelector('.cdk-overlay-pane')?.classList.contains('sc-dialog'),
      title: document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent,
      body: document
        .getElementById(dialog.getAttribute('aria-describedby') ?? '')
        ?.textContent?.replace(/\s+/g, ' ')
        .trim(),
      focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      buttons: Array.from(dialog.querySelectorAll('button'), (b) => b.textContent?.trim()),
      box: el<HTMLInputElement>(fixture, 'media-rights')!.checked,
    }).toEqual({
      panel: true,
      title: 'Confirm the media rights?',
      body: 'You confirm that you own or may use every image, recording and piece of music in "Span<T> explained". Scout keeps this with the package and it cannot be undone.',
      focused: 'rights-cancel',
      buttons: ['Cancel', 'Confirm the rights'],
      box: false,
    });
  });

  it('Approved, publish dialog: Approved with its time and Publish; the dialog asks who can watch it with nothing chosen, keeps the schedule off, lists what will run and keeps Confirm and publish off', async () => {
    const fixture = await mount(makeBridge([approvedProject()]), 'p1');
    const page = seen(fixture);

    await click(fixture, 'publish-open');
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;

    expect(page.slice(-5)).toEqual([
      'h2: Approval',
      'input(on,disabled): I own or have the rights to every image, recording and music in this video',
      'text: Confirmed. This cannot be undone.',
      `text: Approved ${formatDate('2026-11-03T11:00:00Z', 'yyyy-MM-dd', 'en-US')} at ${formatDate('2026-11-03T11:00:00Z', 'HH:mm', 'en-US')}`,
      'button: Publish',
    ]);
    expect(seen(fixture).slice(page.length)).toEqual([
      'h2: Publish "Span<T> explained"',
      'input(off): Public Anyone can find and watch it.',
      'input(off): Unlisted Only people with the link can watch it.',
      'input(off): Private Only you can watch it. Needed to schedule it.',
      'input(disabled): Publish at (London time)',
      'input(off): Yes',
      'input(off): No',
      'input(on): Contains realistic synthetic or altered media',
      'button: Cancel',
      'button(disabled): Confirm and publish',
    ]);
    expect({
      title: document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent,
      focused: (document.activeElement as HTMLElement | null)?.getAttribute('data-test'),
      run: Array.from(dialog.querySelectorAll('[data-test="publish-actions"] li'), (li) =>
        li.textContent?.replace(/\s+/g, ' ').trim(),
      ),
    }).toEqual({
      title: 'Publish "Span<T> explained"',
      focused: 'publish-cancel',
      run: [
        'Upload the video "Span<T> explained" as (choose who can watch it)',
        'Set the thumbnail shown above (thumbnail.png)',
      ],
    });
  });

  it('Published: the Publication card with its status, link and London time, and no Publish', async () => {
    const fixture = await mount(
      makeBridge([
        approvedProject({
          publication: {
            status: 'scheduled',
            videoId: 'Xb2kq9Lr0aQ',
            privacyStatus: 'private',
            publishAt: '2099-07-01T09:00:00.000Z',
            madeForKids: false,
            syntheticMedia: true,
            uploadedAt: null,
            error: null,
            snapshots: [],
            proposals: [],
            experiment: null,
          },
        }),
      ]),
      'p1',
    );

    expect(seen(fixture).slice(-3)).toEqual([
      `text: Approved ${formatDate('2026-11-03T11:00:00Z', 'yyyy-MM-dd', 'en-US')} at ${formatDate('2026-11-03T11:00:00Z', 'HH:mm', 'en-US')}`,
      'h2: Publication',
      'button: https://www.youtube.com/watch?v=Xb2kq9Lr0aQ',
    ]);
    expect([text(fixture, 'publication-status'), text(fixture, 'publication-scheduled')]).toEqual([
      'scheduled',
      '2099-07-01 10:00 Europe/London',
    ]);
  });
});

describe('Assembly: what each action sends, and its confirmation path', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
  });

  it('ticking the media rights box sends nothing until the owner confirms the step: Cancel leaves the box unticked and the rights unconfirmed, Confirm the rights sends the project id once', async () => {
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');

    el<HTMLInputElement>(fixture, 'media-rights')!.click();
    await settle(fixture);
    const sentOnTick = bridge.operatorConfirmMediaRights.mock.calls.length;
    await click(fixture, 'rights-cancel');
    const afterCancel = {
      sent: bridge.operatorConfirmMediaRights.mock.calls.length,
      ticked: el<HTMLInputElement>(fixture, 'media-rights')!.checked,
    };
    el<HTMLInputElement>(fixture, 'media-rights')!.click();
    await settle(fixture);
    await click(fixture, 'rights-confirm');

    expect({ sentOnTick, afterCancel }).toEqual({
      sentOnTick: 0,
      afterCancel: { sent: 0, ticked: false },
    });
    expect(bridge.operatorConfirmMediaRights.mock.calls).toEqual([[{ id: 'p1' }]]);
  });

  it('Add tag takes several tags separated by commas, skips one already listed, and Save details sends each tag once', async () => {
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');

    await type(fixture, 'tag-input', ' memory, span ,performance,, memory ');
    await click(fixture, 'tag-add');
    await click(fixture, 'metadata-save');

    expect(bridge.operatorEditPackageMetadata.mock.calls).toEqual([
      [
        {
          projectId: 'p1',
          title: 'Span<T> explained',
          description: 'A tour of Span<T>.',
          tags: ['dotnet', 'span', 'memory', 'performance'],
        },
      ],
    ]);
  });
});
