import { formatDate } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
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
    audioFile: 'scene-s1.mp3',
    visualFile: 'scene-s1.png',
    durationMs: 4200,
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
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
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
      ['p0', 'Video 0 (media_ready)'],
      ['p1', 'Video 1 (assembly_queued)'],
      ['p2', 'Video 2 (assembling)'],
      ['p3', 'Video 3 (assembled)'],
      ['p4', 'Video 4 (approved)'],
      ['p5', 'Video 5 (failed)'],
    ]);
  });

  it('says there is nothing to assemble while no video has its media ready', async () => {
    const fixture = await mount(makeBridge([project({ status: 'producing' })]));

    expect(text(fixture, 'assembly-empty')).toBe(
      'No video is ready for assembly yet. Produce its media on the Production page.',
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
    const fixture = await mount(makeBridge([assembled()]), 'p1');
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
  });

  it('the checks table shows each check with its name, a pass, warn or fail badge, and its detail', async () => {
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
      el(fixture, `check-result-${i}`)?.className,
      text(fixture, `check-detail-${i}`),
    ]);
    expect(rows).toEqual([
      ['Loudness', 'pass', 'badge badge--pass', '-14 LUFS'],
      ['Captions', 'warn', 'badge badge--warn', '2 lines over 42 characters'],
      ['Length', 'fail', 'badge badge--fail', 'Shorter than 30 s'],
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
    expect(text(fixture, 'metadata-error')).toBe('The title is 1 to 100 characters.');
  });

  it('ticking the media rights box confirms them with ConfirmMediaRights, after which it stays ticked and cannot be unticked', async () => {
    const bridge = makeBridge([assembled()]);
    const fixture = await mount(bridge, 'p1');
    const box = el<HTMLInputElement>(fixture, 'media-rights')!;

    expect(text(fixture, 'media-rights-label')).toBe(
      'I own or have the rights to every image, recording and music in this video',
    );
    box.click();
    await settle(fixture);

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

    const reasons = Array.from(
      el(fixture, 'approve-blocked')!.querySelectorAll('li') as NodeListOf<HTMLElement>,
    ).map((li) => li.textContent?.trim());
    expect(el<HTMLButtonElement>(fixture, 'package-approve')!.disabled).toBe(true);
    expect(reasons).toEqual([
      'The video is not assembled.',
      'Confirm the media rights first.',
      '2 checks failed.',
      '2 claims are not supported or waived.',
    ]);
  });

  it('Approve package sends the project id once nothing blocks it, then shows when it was approved', async () => {
    const bridge = makeBridge([assembled({ rightsConfirmed: true })]);
    const fixture = await mount(bridge, 'p1');

    expect(el(fixture, 'approve-blocked')).toBeNull();
    await click(fixture, 'package-approve');

    expect(bridge.operatorApprovePackage.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(text(fixture, 'package-approved')).toBe(
      `Approved ${formatDate('2026-11-03T11:00:00Z', 'yyyy-MM-dd HH:mm', 'en-US')}`,
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
      return Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="publish-blocked"] li'),
      ).map((li) => (li as HTMLElement).textContent?.trim() ?? '');
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

      const actions = Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="publish-actions"] li'),
      ).map((li) => (li as HTMLElement).textContent?.replace(/\s+/g, ' ').trim());
      await click(fixture, 'publish-confirm');

      expect(actions).toEqual([
        'Upload video: "Span<T> explained" as private',
        'Set thumbnail: thumbnail.png',
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
