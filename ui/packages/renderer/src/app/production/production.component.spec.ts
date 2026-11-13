import { readFileSync } from 'node:fs';

import { formatDate } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ControlPlaneNotFoundError,
  ControlPlanePermissionDeniedError,
  ControlPlaneUnavailableError,
  encodeIpcErrorMessage,
  serializeError,
  ControlPlaneInvalidArgumentError,
  ScoutError,
  type MandateDto,
  type MandateSaveRequest,
  type OperatorProjectDto,
  type ReadinessDto,
  type SceneDto,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import { ProductionComponent } from './production.component';

const mandate: MandateDto = {
  objective: 'Grow a .NET channel',
  audience: 'Backend developers',
  pillars: ['Performance', 'Testing'],
  cadence: 'weekly',
  language: 'en',
  voiceAndStyle: 'calm',
  blockedTopics: ['politics', 'crypto'],
  targetMetric: 'watch_hours',
  targetValue: '500',
  defaultFormat: 'generated_pictures',
  updatedAt: '2026-11-03T09:00:00Z',
};

function project(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
  return {
    id: 'p1',
    topic: 'Span<T> in C#',
    status: 'script_ready',
    origin: 'desktop',
    createdAt: '2026-11-02T09:00:00Z',
    updatedAt: '2026-11-03T09:05:00Z',
    candidates: [{ title: 'Span<T> without fear', reason: 'Fits Performance', fit: 0.82 }],
    script: {
      hook: 'Your allocations are lying to you.',
      sections: [
        { heading: 'Why Span', narration: 'Span is a view over memory.', startMs: 0, endMs: 0 },
      ],
      callToAction: 'Subscribe for more.',
      titleVariants: ['Span<T> explained', 'Stop allocating'],
      description: 'A tour of Span<T>.',
      tags: ['dotnet', 'span'],
      chapters: [{ at: '00:00', title: 'Intro' }],
    },
    claims: [
      {
        id: 'c1',
        text: 'Span<T> is a ref struct.',
        risk: 'low',
        sourceUrls: ['https://learn.microsoft.com/dotnet/api/system.span-1'],
        status: 'pending',
        note: null,
      },
    ],
    error: null,
    scenes: [],
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

function scene(overrides: Partial<SceneDto> = {}): SceneDto {
  return {
    id: 's1',
    order: 1,
    narration: 'Span is a view over memory.',
    visualPrompt: 'A ruler laid over a row of bytes',
    locked: false,
    status: 'ready',
    error: null,
    audioSource: 'generated',
    visualSource: 'owner',
    visualQuery: '',
    visualCredit: null,
    audioFile: 'scene-s1.mp3',
    visualFile: 'owner-s1-0a1b2c3d4e5f.png',
    durationMs: 4200,
    clipStartMs: 0,
    clipEndMs: 0,
    ...overrides,
  };
}

const readiness: ReadinessDto = {
  speechAvailable: false,
  imagesAvailable: true,
  stockPictures: false,
  provider: 'openai',
  ffmpegAvailable: true,
  youtubeSignedIn: false,
  notes: ['Narration comes from your own audio files with this provider.'],
  voices: [
    {
      id: 'piper:en_GB-cori-medium',
      label: 'Cori (medium)',
      source: 'piper',
      sampleFile: 'piper-en_GB-cori-medium.wav',
    },
    {
      id: 'piper:en_GB-aru-medium#3',
      label: 'Aru <speaker> (medium)',
      source: 'piper',
      sampleFile: 'piper-en_GB-aru-medium-3.wav',
    },
    { id: 'OpenAI:nova', label: 'nova (OpenAI)', source: 'OpenAI', sampleFile: null },
  ],
  reportedAt: '2026-11-03T09:00:00Z',
  transcriptionAvailable: false,
  lipSyncAvailable: false,
  faceDetectionAvailable: false,
  picturesLeftToday: 40,
  builtinPresenters: [],
};

const fullReadiness: ReadinessDto = {
  ...readiness,
  speechAvailable: true,
  imagesAvailable: true,
};

function refusedWith(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneInvalidArgumentError(message)),
    )}`,
  );
}

function goneWith(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneNotFoundError(message)),
    )}`,
  );
}

function ipcFailure(channel: string, message: string): Error {
  return new Error(
    `Error invoking remote method '${channel}': Error: ${encodeIpcErrorMessage(
      serializeError(new ControlPlaneUnavailableError(message)),
    )}`,
  );
}

function makeBridge(overrides: Record<string, unknown> = {}) {
  return {
    operatorMandate: vi.fn(async () => ({ mandate: null as MandateDto | null })),
    operatorSaveMandate: vi.fn(async () => mandate),
    operatorProjects: vi.fn(async () => ({ projects: [] as OperatorProjectDto[] })),
    operatorProject: vi.fn(async () => project()),
    operatorStartProject: vi.fn(async () =>
      project({ id: 'p9', topic: 'Records in C#', status: 'queued', script: null, claims: [] }),
    ),
    operatorDecideClaim: vi.fn(async () => project()),
    operatorReadiness: vi.fn(async () => readiness),
    operatorProduce: vi.fn(async () => project({ status: 'production_queued' })),
    operatorDelegate: vi.fn(async () => project({ status: 'production_queued', delegated: true })),
    operatorDeleteProject: vi.fn(async () => ({ deleted: true as const })),
    operatorReorderScenes: vi.fn(async () => project()),
    operatorSetSceneLocked: vi.fn(async () => project()),
    operatorRegenerateScene: vi.fn(async () => project()),
    operatorEditSceneText: vi.fn(async () => project()),
    operatorRegenerateNarration: vi.fn(async () => project()),
    operatorSetMouthBox: vi.fn(async () => project()),
    operatorRegeneratePresenter: vi.fn(async () => project()),
    operatorRequestPresenter: vi.fn(async () => project()),
    operatorUsePresenterVersion: vi.fn(async () => project()),
    operatorRemovePresenterVersion: vi.fn(async () => project()),
    operatorEditScenePicturePrompt: vi.fn(async () => project()),
    operatorRegenerateScenePicture: vi.fn(async () => project()),
    operatorAttachSceneMedia: vi.fn(async () => ({
      project: project() as OperatorProjectDto | null,
    })),
    ...overrides,
  };
}

type Bridge = ReturnType<typeof makeBridge>;

function listing(...projects: OperatorProjectDto[]) {
  return {
    operatorProjects: vi.fn(async () => ({ projects })),
    operatorProject: vi.fn(async ({ id }: { id: string }) => projects.find((p) => p.id === id)!),
  };
}

async function mount(bridge: Bridge): Promise<ComponentFixture<ProductionComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(ProductionComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<ProductionComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el<T extends HTMLElement = HTMLElement>(
  fixture: ComponentFixture<ProductionComponent>,
  testId: string,
): T | null {
  return fixture.nativeElement.querySelector(`[data-test="${testId}"]`) as T | null;
}

function text(fixture: ComponentFixture<ProductionComponent>, testId: string): string {
  return (el(fixture, testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function type(fixture: ComponentFixture<ProductionComponent>, testId: string, value: string): void {
  const input = el<HTMLInputElement>(fixture, testId)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

async function click(
  fixture: ComponentFixture<ProductionComponent>,
  testId: string,
): Promise<void> {
  el(fixture, testId)!.click();
  await settle(fixture);
}

let captured: Element | null = null;

function pointer(
  fixture: ComponentFixture<ProductionComponent>,
  type: 'pointerdown' | 'pointermove' | 'pointerup' | 'pointercancel',
  clientX: number,
  clientY: number,
  target: Element = el(fixture, 'presenter-image')!,
): void {
  const area = el(fixture, 'mouth-area')!;
  area.setPointerCapture = (pointerId: number) => {
    if (pointerId === 1) captured = area;
  };
  (captured ?? target).dispatchEvent(
    new PointerEvent(type, { pointerId: 1, clientX, clientY, bubbles: true }),
  );
  if (type === 'pointerup' || type === 'pointercancel') captured = null;
}

function boxStyle(fixture: ComponentFixture<ProductionComponent>, testId: string): string[] {
  const box = el(fixture, testId)!.style;
  return [box.left, box.top, box.width, box.height];
}

const BLOCK_HELP: readonly (readonly [string, string])[] = [
  [
    'mandate',
    'What the channel is for: who it speaks to, what it covers and how it sounds. Scout reads it before every topic, script and picture.',
  ],
  [
    'topic-candidates',
    'The angles Scout found for this topic before writing. They stay here for reference once the script exists.',
  ],
  [
    'script',
    "The hook, the sections and the close, in the words the narration will say. Edit a scene's text below to change it.",
  ],
  [
    'claims',
    'Facts the script states that a viewer could dispute. Scout looked for a source for each one. Supported keeps the line; Not supported rewrites without it; Needs no source keeps it without a source, with your reason. Production waits until every claim is decided.',
  ],
  [
    'presenter',
    'The character that speaks the whole video. Scout makes it from the script as soon as the script is ready, finds the mouth on the picture and shows a short preview. Ask for changes in plain words; every request makes a new version you can switch back to.',
  ],
  [
    'scenes',
    'Each scene is a picture with its narration. Assembly, on the next page, joins them into the video.',
  ],
  [
    'background',
    'The picture behind any scene that has none of its own. Without it, that scene is a dark card with its text.',
  ],
  [
    'recording',
    'The video you recorded. Scout transcribes it on this machine and cuts the scenes from it; nothing is uploaded.',
  ],
];

/** The value the last rule matching `node` in the page's style elements declares for `property`; null when none does. */
function declared(node: Element, property: string): string | null {
  const pattern = new RegExp(`(?:^|[;\\s])${property}:\\s*([^;]+)`);
  let value: string | null = null;
  for (const [, selector, body] of styleText().matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const found = pattern.exec(body!)?.[1]?.trim();
    if (found === undefined) continue;
    const matches = selector!.split(',').some((one) => {
      try {
        return node.matches(one.trim());
      } catch {
        return false;
      }
    });
    if (matches) value = found;
  }
  return value;
}

function styleText(): string {
  return Array.from(document.querySelectorAll('style'))
    .map((style) => style.textContent ?? '')
    .join('\n');
}

describe('ProductionComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    captured = null;
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('explains that the operator needs a mandate while none is saved, and says there are no videos yet', async () => {
    const fixture = await mount(makeBridge());

    expect(text(fixture, 'mandate-empty')).toBe(
      'The operator needs a mandate before it makes a video: what the channel is for, who it is for and its language. Fill it in and save it.',
    );
    expect(text(fixture, 'projects-empty-text')).toBe(
      'No videos yet. Start one above, or ask Scout in chat.',
    );
    expect(el(fixture, 'mandate-saved')).toBeNull();
  });

  it('save mandate sends the trimmed fields, the pillars filled in and the blocked topics split on commas, and shows the saved time', async () => {
    const bridge = makeBridge();
    const fixture = await mount(bridge);

    type(fixture, 'mandate-objective', '  Grow a .NET channel ');
    type(fixture, 'mandate-audience', ' Backend developers ');
    type(fixture, 'mandate-pillar-0', ' Performance ');
    type(fixture, 'mandate-pillar-2', 'Testing');
    type(fixture, 'mandate-cadence', ' weekly');
    type(fixture, 'mandate-language', 'en ');
    type(fixture, 'mandate-style', ' calm ');
    type(fixture, 'mandate-blocked', ' politics, , crypto ');
    const metric = el<HTMLSelectElement>(fixture, 'mandate-metric')!;
    metric.value = 'watch_hours';
    metric.dispatchEvent(new Event('change'));
    type(fixture, 'mandate-target', ' 500 ');
    await click(fixture, 'mandate-save');

    const { updatedAt: _stamp, ...fields } = mandate;
    expect(bridge.operatorSaveMandate.mock.calls).toEqual([[fields]]);
    expect(text(fixture, 'mandate-saved')).toBe(
      `Saved ${formatDate(mandate.updatedAt!, 'd MMM, HH:mm', 'en-US')}`,
    );
    expect(el(fixture, 'mandate-empty')).toBeNull();
  });

  it('the Mandate has no narration voice select, keeps Voice and style, and saves no narration voice', async () => {
    const save = vi.fn(async (_req: MandateSaveRequest) => mandate);
    const fixture = await mount(makeBridge({ operatorSaveMandate: save }));

    const labels = Array.from(
      el(fixture, 'mandate')!.querySelectorAll('label') as NodeListOf<HTMLLabelElement>,
    ).map((l) => l.firstChild?.textContent?.trim());
    type(fixture, 'mandate-objective', 'Grow a .NET channel');
    type(fixture, 'mandate-style', 'calm');
    type(fixture, 'mandate-language', 'en');
    await click(fixture, 'mandate-save');

    expect(el(fixture, 'mandate-voice')).toBeNull();
    expect(labels).not.toContain('Narration voice');
    expect(labels).toContain('Voice and style');
    expect(save.mock.calls.map(([sent]) => [Object.keys(sent), sent.voiceAndStyle])).toEqual([
      [
        [
          'objective',
          'audience',
          'pillars',
          'cadence',
          'language',
          'voiceAndStyle',
          'blockedTopics',
          'targetMetric',
          'targetValue',
          'defaultFormat',
        ],
        'calm',
      ],
    ]);
  });

  it('a saved mandate fills the form and shows when it was saved', async () => {
    const fixture = await mount(makeBridge({ operatorMandate: vi.fn(async () => ({ mandate })) }));

    expect(el<HTMLInputElement>(fixture, 'mandate-objective')!.value).toBe('Grow a .NET channel');
    expect(el<HTMLInputElement>(fixture, 'mandate-pillar-1')!.value).toBe('Testing');
    expect(el<HTMLInputElement>(fixture, 'mandate-blocked')!.value).toBe('politics, crypto');
    expect(el<HTMLSelectElement>(fixture, 'mandate-metric')!.value).toBe('watch_hours');
    expect(text(fixture, 'mandate-saved')).toBe(
      `Saved ${formatDate(mandate.updatedAt!, 'd MMM, HH:mm', 'en-US')}`,
    );
    expect(el(fixture, 'mandate-empty')).toBeNull();
  });

  it('a refused mandate shows the control plane reason', async () => {
    const bridge = makeBridge({
      operatorSaveMandate: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:operator.saveMandate': Error: ${encodeIpcErrorMessage(
            serializeError(
              new ScoutError('operator/invalid-request', 'The objective is required.'),
            ),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    await click(fixture, 'mandate-save');

    expect(text(fixture, 'mandate-error')).toBe('The objective is required.');
    expect(el(fixture, 'mandate-saved')).toBeNull();
  });

  it('start project sends the trimmed topic and shows the queued project, which joins the list', async () => {
    const queued = project({
      id: 'p9',
      topic: 'Records in C#',
      status: 'queued',
      createdAt: '2026-11-03T10:00:00Z',
      script: null,
      claims: [],
    });
    let listed: OperatorProjectDto[] = [];
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: listed })),
      operatorStartProject: vi.fn(async () => {
        listed = [queued];
        return queued;
      }),
    });
    const fixture = await mount(bridge);

    type(fixture, 'topic', '  Records in C# ');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(bridge.operatorStartProject.mock.calls).toEqual([
      [{ topic: 'Records in C#', format: 'generated_pictures', direction: '', presenter: '' }],
    ]);
    expect(text(fixture, 'queued')).toBe('Records in C# queued');
    expect(text(fixture, 'project-status-p9')).toBe('Queued');
    expect(el<HTMLInputElement>(fixture, 'topic')!.value).toBe('');
  });

  it('a start the owner has not granted shows the write gate reason in the Make a video area', async () => {
    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    const bridge = makeBridge({
      operatorStartProject: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:operator.startProject': Error: ${encodeIpcErrorMessage(
            serializeError(new ControlPlanePermissionDeniedError(reason)),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);

    type(fixture, 'topic', 'Records in C#');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(text(fixture, 'start-error')).toBe(reason);
    expect(el(fixture, 'queued')).toBeNull();
    expect(el(fixture, 'production-error')).toBeNull();
  });

  it('Make a video stays disabled until the topic has 3 characters', async () => {
    const fixture = await mount(makeBridge());

    type(fixture, 'topic', ' ab ');
    await settle(fixture);
    const short = el<HTMLButtonElement>(fixture, 'start-project')!.disabled;
    type(fixture, 'topic', 'abc');
    await settle(fixture);

    expect([short, el<HTMLButtonElement>(fixture, 'start-project')!.disabled]).toEqual([
      true,
      false,
    ]);
  });

  it('Make a video sends the topic, the chosen format and the trimmed direction', async () => {
    const bridge = makeBridge();
    const fixture = await mount(bridge);
    const checkedAtFirst = [
      el<HTMLInputElement>(fixture, 'format-generated_pictures')!.checked,
      el<HTMLInputElement>(fixture, 'format-own_pictures')!.checked,
    ];

    type(fixture, 'topic', 'Records in C#');
    el<HTMLInputElement>(fixture, 'format-own_pictures')!.click();
    type(fixture, 'start-direction', '  Explain it to a junior developer, no jargon  ');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(checkedAtFirst).toEqual([true, false]);
    expect(bridge.operatorStartProject.mock.calls).toEqual([
      [
        {
          topic: 'Records in C#',
          format: 'own_pictures',
          direction: 'Explain it to a junior developer, no jargon',
          presenter: '',
        },
      ],
    ]);
    expect([
      el<HTMLInputElement>(fixture, 'format-generated_pictures')!.checked,
      el<HTMLTextAreaElement>(fixture, 'start-direction')!.value,
    ]).toEqual([true, '']);
  });

  it("the AI presenter is shown disabled with 'The connected AI Source has no video model.'", async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...readiness, transcriptionAvailable: true })),
      }),
    );

    const radios = Array.from(
      fixture.nativeElement.querySelectorAll(
        'input[name="start-format"]',
      ) as NodeListOf<HTMLInputElement>,
    ).map((r) => [r.dataset['test'], r.disabled]);

    expect(radios).toEqual([
      ['format-generated_pictures', false],
      ['format-own_pictures', false],
      ['format-animated_character', true],
      ['format-podcast', true],
      ['format-ai_presenter', true],
      ['format-own_video', false],
    ]);
    expect(text(fixture, 'format-ai_presenter-reason')).toBe(
      'The connected AI Source has no video model.',
    );
  });

  it('the Animated character format is offered while lip sync is available and sends the presenter description with the start', async () => {
    const without = await mount(makeBridge());
    const greyed = [
      el<HTMLInputElement>(without, 'format-animated_character')!.disabled,
      text(without, 'format-animated_character-reason'),
    ];
    TestBed.resetTestingModule();
    const bridge = makeBridge({
      operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable: true })),
    });
    const fixture = await mount(bridge);
    const radio = el<HTMLInputElement>(fixture, 'format-animated_character')!;
    const offered = [
      radio.disabled,
      radio.closest('label')!.querySelector('.format-card__name')!.textContent!.trim(),
      el(fixture, 'format-animated_character-reason'),
      el(fixture, 'start-presenter'),
    ];

    type(fixture, 'topic', 'Span<T> in C#');
    radio.click();
    await settle(fixture);
    type(fixture, 'start-presenter', 'r'.repeat(301));
    await settle(fixture);
    const overLimit = [
      text(fixture, 'start-presenter-count'),
      el<HTMLButtonElement>(fixture, 'start-project')!.disabled,
    ];
    type(fixture, 'start-presenter', '  A friendly robot in a workshop  ');
    await settle(fixture);
    const count = text(fixture, 'start-presenter-count');
    await click(fixture, 'start-project');

    expect(greyed).toEqual([
      true,
      'Needs the lip-sync tool, which is not installed on this computer.',
    ]);
    expect(offered).toEqual([false, 'Animated character', null, null]);
    expect(overLimit).toEqual(['301 / 300', true]);
    expect(count).toBe('30 / 300');
    expect(bridge.operatorStartProject.mock.calls).toEqual([
      [
        {
          topic: 'Span<T> in C#',
          format: 'animated_character',
          direction: '',
          presenter: 'A friendly robot in a workshop',
        },
      ],
    ]);
    expect([
      el<HTMLInputElement>(fixture, 'format-generated_pictures')!.checked,
      el(fixture, 'start-presenter'),
    ]).toEqual([true, null]);
  });

  it('Your own video is offered when Scout can transcribe and greyed out with the Local Models reason when it cannot', async () => {
    const without = await mount(makeBridge());
    const greyed = [
      el<HTMLInputElement>(without, 'format-own_video')!.disabled,
      text(without, 'format-own_video-reason'),
    ];
    TestBed.resetTestingModule();
    const bridge = makeBridge({
      operatorReadiness: vi.fn(async () => ({ ...readiness, transcriptionAvailable: true })),
    });
    const fixture = await mount(bridge);
    const radio = el<HTMLInputElement>(fixture, 'format-own_video')!;
    const offered = [
      radio.disabled,
      radio.closest('label')!.querySelector('.format-card__name')!.textContent!.trim(),
      el(fixture, 'format-own_video-reason'),
    ];

    type(fixture, 'topic', 'My talk at the meetup');
    radio.click();
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(greyed).toEqual([
      true,
      'Scout has no speech-to-text model on this machine; download one in Models, Transcription.',
    ]);
    expect(offered).toEqual([false, 'Your own video', null]);
    expect(bridge.operatorStartProject.mock.calls).toEqual([
      [{ topic: 'My talk at the meetup', format: 'own_video', direction: '', presenter: '' }],
    ]);
  });

  it('a project waiting for its recording offers Attach recording, and a transcribing one shows how much is transcribed', async () => {
    const waiting = project({
      format: 'own_video',
      status: 'recording_waiting',
      candidates: [],
      script: null,
      claims: [],
    });
    const transcribing = project({
      id: 'p2',
      format: 'own_video',
      status: 'transcribing',
      candidates: [],
      claims: [],
      recordingFile: 'owner-recording-0a1b2c3d4e5f.mp4',
      recordingMs: 605_000,
      script: {
        ...project().script!,
        hook: '',
        sections: [
          { heading: '', narration: 'Hello everyone.', startMs: 0, endMs: 62_000 },
          { heading: '', narration: 'Today we look at Span.', startMs: 62_000, endMs: 125_500 },
        ],
      },
    });
    const bridge = makeBridge({
      ...listing(waiting, transcribing),
      operatorAttachSceneMedia: vi.fn(async () => ({
        project: {
          ...waiting,
          status: 'queued',
          recordingFile: 'owner-recording-0a1b2c3d4e5f.mp4',
        } as OperatorProjectDto | null,
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const before = [
      text(fixture, 'project-format'),
      text(fixture, 'recording-empty'),
      text(fixture, 'recording-attach'),
      el(fixture, 'background'),
    ];

    await click(fixture, 'recording-attach');
    const after = [el(fixture, 'recording-empty'), text(fixture, 'recording-attach')];
    await click(fixture, 'project-p2');

    expect(before).toEqual([
      'Your own video',
      'No recording yet: attach the video you recorded; Scout transcribes it on this machine.',
      'Attach recording',
      null,
    ]);
    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: '', kind: 'recording' }],
    ]);
    expect(after).toEqual([null, 'Replace recording']);
    expect(text(fixture, 'transcription-progress')).toBe('Transcribed 2:05 of 10:05');
    expect(
      Array.from(
        fixture.nativeElement.querySelectorAll('[data-test="script-section-time"]'),
        (e: Element) => e.textContent!.trim(),
      ),
    ).toEqual(['0:00 to 1:02', '1:02 to 2:05']);
    expect(el(fixture, 'recording-attach')).toBeNull();
  });

  it('the direction counter shows n / 1,000 and Make a video is disabled past it', async () => {
    const fixture = await mount(makeBridge());
    type(fixture, 'topic', 'Records in C#');

    type(fixture, 'start-direction', ` ${'d'.repeat(1000)} `);
    await settle(fixture);
    const atLimit = [
      text(fixture, 'start-direction-count'),
      el<HTMLButtonElement>(fixture, 'start-project')!.disabled,
    ];
    type(fixture, 'start-direction', 'd'.repeat(1001));
    await settle(fixture);

    expect(atLimit).toEqual(['1,000 / 1,000', false]);
    expect([
      text(fixture, 'start-direction-count'),
      el<HTMLButtonElement>(fixture, 'start-project')!.disabled,
    ]).toEqual(['1,001 / 1,000', true]);
  });

  it('lists projects newest first with topic, status and updated time, and a selection shows candidates, script and claims', async () => {
    const older = project({ id: 'p0', topic: 'Older video', createdAt: '2026-11-01T09:00:00Z' });
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [older, project()] })),
    });
    const fixture = await mount(bridge);

    const order = Array.from(
      fixture.nativeElement.querySelectorAll('.project__topic') as NodeListOf<HTMLElement>,
    ).map((n) => n.textContent?.trim());
    const row = ['project-p1', 'project-status-p1', 'project-updated-p1'].map((id) =>
      id === 'project-p1'
        ? el(fixture, id)!.querySelector('.project__topic')!.textContent!.trim()
        : text(fixture, id),
    );
    await click(fixture, 'project-p1');
    const detail = text(fixture, 'project-detail');

    expect(order).toEqual(['Span<T> in C#', 'Older video']);
    expect(row).toEqual([
      'Span<T> in C#',
      'Script ready for review',
      formatDate('2026-11-03T09:05:00Z', 'd MMM, HH:mm', 'en-US'),
    ]);
    expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }]]);
    for (const part of [
      'Span<T> without fear',
      '82%',
      'Fits Performance',
      'Hook: Your allocations are lying to you.',
      'Why Span',
      'Span is a view over memory.',
      'Call to action: Subscribe for more.',
      'Span<T> explained',
      'Stop allocating',
      'Description: A tour of Span<T>.',
      'Tags: dotnet, span',
      '00:00 Intro',
      'Span<T> is a ref struct.',
      'Low risk',
      'learn.microsoft.com',
    ]) {
      expect(detail).toContain(part);
    }
    expect(text(fixture, 'claim-status-c1')).toBe('pending');
  });

  it('a selected project with nothing yet shows the empty candidates, script and claims states', async () => {
    const bare = project({ candidates: [], script: null, claims: [] });
    const fixture = await mount(
      makeBridge({
        operatorProjects: vi.fn(async () => ({ projects: [bare] })),
        operatorProject: vi.fn(async () => bare),
      }),
    );

    await click(fixture, 'project-p1');

    expect([
      text(fixture, 'candidates-empty'),
      text(fixture, 'script-empty'),
      text(fixture, 'claims-empty'),
    ]).toEqual([
      'No topic candidates yet.',
      'The script is not written yet.',
      'No claims to check.',
    ]);
  });

  it('a source is a chip with its domain and a Copy link that copies the whole URL, never a link the window could follow', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const odd = project({
      claims: [
        { ...project().claims[0]!, sourceUrls: [project().claims[0]!.sourceUrls[0]!, 'not a url'] },
      ],
    });
    const fixture = await mount(makeBridge(listing(odd)));
    await click(fixture, 'project-p1');

    const claim = el(fixture, 'claim-c1')!;
    const chips = [0, 1].map((i) => [
      text(fixture, `claim-source-c1-${i}`),
      el(fixture, `claim-source-c1-${i}`)!.getAttribute('title'),
      text(fixture, `claim-copy-c1-${i}`),
    ]);
    await click(fixture, 'claim-copy-c1-0');

    expect(claim.querySelector('a')).toBeNull();
    expect(chips).toEqual([
      ['learn.microsoft.com', 'https://learn.microsoft.com/dotnet/api/system.span-1', 'Copy link'],
      ['not a url', 'not a url', 'Copy link'],
    ]);
    expect(writeText.mock.calls).toEqual([
      ['https://learn.microsoft.com/dotnet/api/system.span-1'],
    ]);
  });

  it('Supported and Unsupported send the claim status and show the decided claim', async () => {
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [project()] })),
      operatorDecideClaim: vi.fn(async (req: { status: string }) =>
        project({ claims: [{ ...project().claims[0]!, status: req.status }] }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'supported-c1');
    const afterSupported = text(fixture, 'claim-status-c1');
    await click(fixture, 'claim-change-c1');
    await click(fixture, 'unsupported-c1');

    expect(bridge.operatorDecideClaim.mock.calls).toEqual([
      [{ projectId: 'p1', claimId: 'c1', status: 'supported' }],
      [{ projectId: 'p1', claimId: 'c1', status: 'unsupported' }],
    ]);
    expect([afterSupported, text(fixture, 'claim-status-c1')]).toEqual([
      'supported',
      'unsupported',
    ]);
  });

  it('Waive needs a note: the confirm stays disabled while the note is empty, and the trimmed note is sent', async () => {
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [project()] })),
      operatorDecideClaim: vi.fn(async () =>
        project({
          claims: [{ ...project().claims[0]!, status: 'waived', note: 'Common knowledge' }],
        }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'waive-c1');
    const emptyDisabled = el<HTMLButtonElement>(fixture, 'waive-confirm-c1')!.disabled;
    type(fixture, 'waive-note-c1', '   ');
    await settle(fixture);
    const blankDisabled = el<HTMLButtonElement>(fixture, 'waive-confirm-c1')!.disabled;
    type(fixture, 'waive-note-c1', '  Common knowledge ');
    await settle(fixture);
    await click(fixture, 'waive-confirm-c1');

    expect([emptyDisabled, blankDisabled]).toEqual([true, true]);
    expect(bridge.operatorDecideClaim.mock.calls).toEqual([
      [{ projectId: 'p1', claimId: 'c1', status: 'waived', note: 'Common knowledge' }],
    ]);
    expect(text(fixture, 'claim-c1')).toContain('waived · Common knowledge');
    expect(el(fixture, 'waive-note-c1')).toBeNull();
  });

  it('refreshes the list every 10 seconds while open and takes the selected project from it, with one list call and no project call per tick', async () => {
    vi.useFakeTimers();
    let listed = project({ status: 'researching' });
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [listed] })),
      operatorProject: vi.fn(async () => project({ status: 'researching' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    listed = project({ status: 'failed', error: 'The AI model did not answer.' });
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(text(fixture, 'project-status-p1')).toBe('Failed');
    expect(text(fixture, 'project-failure')).toBe('The AI model did not answer.');
    expect(bridge.operatorProjects).toHaveBeenCalledTimes(2);
    expect(bridge.operatorProject).toHaveBeenCalledTimes(1);
  });

  it('a selected project that left the list is read on its own at the next tick', async () => {
    vi.useFakeTimers();
    let listed = [project()];
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: listed })),
      operatorProject: vi.fn(async () => project({ status: 'failed', error: 'Read on its own.' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    listed = [project({ id: 'p2', topic: 'Newer video' })];
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }], [{ id: 'p1' }]]);
    expect(text(fixture, 'project-failure')).toBe('Read on its own.');
  });

  it('a control plane that goes down shows the error and keeps the last list and project', async () => {
    vi.useFakeTimers();
    let down = false;
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => {
        if (down) throw ipcFailure('scout:operator.projects', 'socket reset');
        return { projects: [project()] };
      }),
      operatorProject: vi.fn(async () => {
        if (down) throw ipcFailure('scout:operator.project', 'socket reset');
        return project();
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    down = true;
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    expect(text(fixture, 'production-error')).toBe('The Scout control plane did not answer.');
    expect(el(fixture, 'project-p1')).not.toBeNull();
    expect(text(fixture, 'project-detail')).toContain('Span<T> is a ref struct.');
  });

  it('opened while the control plane host is missing, the page banner alone says the control plane did not answer and nothing shows the spawn failure', async () => {
    const spawnFailure =
      'The control-plane host did not start: spawn /tmp/scout-e2e/no-control-plane ENOENT';
    const fixture = await mount(
      makeBridge({
        operatorMandate: vi.fn(async () => {
          throw ipcFailure('scout:operator.mandate', spawnFailure);
        }),
        operatorReadiness: vi.fn(async () => {
          throw ipcFailure('scout:operator.readiness', spawnFailure);
        }),
        operatorProjects: vi.fn(async () => {
          throw ipcFailure('scout:operator.projects', spawnFailure);
        }),
      }),
    );
    const banners = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.err, .alert'),
    ].map((banner) => [banner.dataset['test'], banner.textContent?.trim()]);

    expect(banners).toEqual([['production-error', 'The Scout control plane did not answer.']]);
    expect((fixture.nativeElement as HTMLElement).textContent).not.toMatch(/\/tmp|spawn|ENOENT/);
  });

  it('a mandate read the control plane did not answer is read again by the poll, and the mandate form never shows empty to save over it in between', async () => {
    vi.useFakeTimers();
    let down = true;
    const fixture = await mount(
      makeBridge({
        operatorMandate: vi.fn(async () => {
          if (down) {
            throw ipcFailure(
              'scout:operator.mandate',
              'The control-plane host did not start: spawn /tmp/scout-e2e/no-control-plane ENOENT',
            );
          }
          return { mandate };
        }),
      }),
    );
    const seen = (): Record<string, unknown> => ({
      form: el(fixture, 'mandate-objective') !== null,
      save: el(fixture, 'mandate-save') !== null,
      banner: el(fixture, 'production-error') === null ? null : text(fixture, 'production-error'),
    });
    const atOpen = seen();
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const afterAPollStillDown = seen();

    down = false;
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);

    const whileDown = {
      form: false,
      save: false,
      banner: 'The Scout control plane did not answer.',
    };
    expect([atOpen, afterAPollStillDown]).toEqual([whileDown, whileDown]);
    expect(seen()).toEqual({ form: true, save: true, banner: null });
    expect(el<HTMLInputElement>(fixture, 'mandate-objective')!.value).toBe('Grow a .NET channel');
  });

  it('a mandate or readiness read the control plane refuses for another reason keeps its own banner, and the page banner stays empty', async () => {
    const fixture = await mount(
      makeBridge({
        operatorMandate: vi.fn(async () => {
          throw refusedWith('scout:operator.mandate', 'The mandate file is not valid.');
        }),
        operatorReadiness: vi.fn(async () => {
          throw refusedWith('scout:operator.readiness', 'The readiness report is not valid.');
        }),
      }),
    );
    const banners = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.err, .alert'),
    ].map((banner) => [banner.dataset['test'], banner.textContent?.trim()]);

    expect(banners).toEqual([
      ['channel-error', 'The readiness report is not valid.'],
      ['mandate-error', 'The mandate file is not valid.'],
      ['readiness-error', 'The readiness report is not valid.'],
    ]);
  });
  it('the readiness banner says whether narration and pictures can be generated, the provider, the YouTube sign-in and the notes', async () => {
    const fixture = await mount(makeBridge());

    expect(text(fixture, 'readiness-speech')).toBe('Narration: not available');
    expect(text(fixture, 'readiness-images')).toBe('Generated pictures: available');
    expect(text(fixture, 'readiness-provider')).toBe('Provider: openai');
    expect(text(fixture, 'readiness-youtube')).toBe('YouTube: not signed in');
    expect(text(fixture, 'readiness-note')).toBe(
      'Narration comes from your own audio files with this provider.',
    );
    expect(text(fixture, 'readiness-voices')).toBe('Voices: 3 available');
  });

  it('the readiness banner says pictures are generated by the provider', async () => {
    const fixture = await mount(makeBridge());

    expect(text(fixture, 'readiness-stock')).toBe('Pictures: generated by openai');
  });

  it('the readiness banner says pictures come from Openverse when only stock pictures are available', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({
          ...readiness,
          imagesAvailable: false,
          stockPictures: true,
        })),
      }),
    );

    expect(text(fixture, 'readiness-stock')).toBe(
      'Pictures: from Openverse (CC0, public domain or CC BY, credited in the description)',
    );
  });

  it('the readiness banner says text cards when no picture source is available', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...readiness, imagesAvailable: false })),
      }),
    );

    expect(text(fixture, 'readiness-stock')).toBe(
      'Pictures: text cards unless you attach your own',
    );
  });

  it('an Openverse scene shows its source label and the credit line under the picture', async () => {
    const stocked = project({
      scenes: [
        scene({
          visualSource: 'stock',
          visualQuery: 'ruler',
          visualCredit: 'Ruler by Jane Doe, CC BY 4.0, https://example.org/ruler',
        }),
        scene({ id: 's2', order: 2 }),
      ],
    });
    const fixture = await mount(makeBridge(listing(stocked)));
    await click(fixture, 'project-p1');

    expect(text(fixture, 'scene-sources-s1')).toContain('Stock picture');
    expect(text(fixture, 'scene-credit-s1')).toBe(
      'Credit: Ruler by Jane Doe, CC BY 4.0, https://example.org/ruler',
    );
    expect(el(fixture, 'scene-credit-s2')).toBeNull();
  });

  it('the readiness banner says there are no voices when the worker reported none', async () => {
    const fixture = await mount(
      makeBridge({ operatorReadiness: vi.fn(async () => ({ ...readiness, voices: [] })) }),
    );

    expect(text(fixture, 'readiness-voices')).toBe('Voices: none');
  });

  it('Produce video media is disabled with the reason while claims are pending', async () => {
    const pending = project({
      claims: [
        { ...project().claims[0]!, id: 'c1' },
        { ...project().claims[0]!, id: 'c2' },
      ],
    });
    const bridge = makeBridge(listing(pending));
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el<HTMLButtonElement>(fixture, 'produce')!.disabled).toBe(true);
    expect(text(fixture, 'produce-blocked')).toBe(
      '2 claims are still waiting; decide every claim before producing the media.',
    );
    expect(el(fixture, 'produce-blocked')!.classList.contains('warn-line')).toBe(true);
  });

  it('Produce video media is enabled on a script_ready or media_ready project with every claim decided, and its panel sends the project id and shows the new status', async () => {
    const decided = project({
      claims: [{ ...project().claims[0]!, status: 'supported' }],
    });
    const bridge = makeBridge({
      ...listing(decided, project({ id: 'p2', status: 'media_ready', claims: [] })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const onMediaReady = el<HTMLButtonElement>(fixture, 'produce')?.disabled;
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    const sentOnOpen = bridge.operatorProduce.mock.calls.length;
    await click(fixture, 'produce-confirm');

    expect(onMediaReady).toBe(false);
    expect(sentOnOpen).toBe(0);
    expect(el(fixture, 'produce-blocked')).toBeNull();
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
    expect(text(fixture, 'project-status-p1')).toBe('production queued');
    expect(el(fixture, 'produce-panel')).toBeNull();
  });

  it('an assembled project offers Produce video media again', async () => {
    const bridge = makeBridge({
      ...listing(project({ id: 'p3', status: 'assembled', claims: [] })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p3');

    const disabled = el<HTMLButtonElement>(fixture, 'produce')?.disabled;
    await click(fixture, 'produce');
    await click(fixture, 'produce-confirm');

    expect(disabled).toBe(false);
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p3', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
  });

  it('the Produce panel offers the voices and sends the chosen voice with the project id', async () => {
    vi.useFakeTimers();
    const decided = project({ claims: [{ ...project().claims[0]!, status: 'supported' }] });
    const bridge = makeBridge({
      ...listing(decided),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    const select = el<HTMLSelectElement>(fixture, 'produce-voice')!;
    const groups = Array.from(select.querySelectorAll('optgroup'));
    select.value = 'OpenAI:nova';
    select.dispatchEvent(new Event('change'));
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const afterRefresh = el<HTMLSelectElement>(fixture, 'produce-voice')?.value;
    await click(fixture, 'produce-confirm');

    expect(Array.from(select.children).map((c) => c.tagName)).toEqual([
      'OPTION',
      'OPTGROUP',
      'OPTGROUP',
    ]);
    expect([select.options[0]!.textContent!.trim(), select.options[0]!.value]).toEqual([
      "Scout's choice",
      '',
    ]);
    expect(groups.map((g) => g.label)).toEqual(['piper', 'OpenAI']);
    expect(
      groups.map((g) => Array.from(g.querySelectorAll('option')).map((o) => o.textContent!.trim())),
    ).toEqual([['Cori (medium)', 'Aru <speaker> (medium)'], ['nova (OpenAI)']]);
    expect(el(fixture, 'produce-warning')).toBeNull();
    expect(bridge.operatorProjects).toHaveBeenCalledTimes(2);
    expect(afterRefresh).toBe('OpenAI:nova');
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: 'OpenAI:nova', direction: '', motion: 'gentle', presenter: '' }],
    ]);
  });

  it("the Produce panel preselects the project's voice and the detail shows it", async () => {
    const voiced = project({ claims: [], narrationVoice: 'piper:en_GB-aru-medium#3' });
    const unvoiced = project({ id: 'p2', claims: [] });
    const gone = project({ id: 'p3', claims: [], narrationVoice: 'piper:en_US-gone-high' });
    const bridge = makeBridge({
      ...listing(voiced, unvoiced, gone),
      operatorReadiness: vi.fn(async () => fullReadiness),
      operatorProduce: vi.fn(async ({ id }: { id: string }) =>
        project({ id, status: 'production_queued', claims: [] }),
      ),
    });
    const fixture = await mount(bridge);

    await click(fixture, 'project-p2');
    const unvoicedLabel = text(fixture, 'project-voice');
    await click(fixture, 'project-p3');
    const goneLabel = text(fixture, 'project-voice');
    await click(fixture, 'produce');
    const goneSelect = el<HTMLSelectElement>(fixture, 'produce-voice')!;
    const goneOption = Array.from(goneSelect.options).find(
      (o) => o.value === 'piper:en_US-gone-high',
    )!;
    const goneState = [
      goneSelect.value,
      goneOption.textContent!.trim(),
      goneOption.disabled,
      goneOption.selected,
    ];
    await click(fixture, 'produce-confirm');
    await click(fixture, 'project-p1');
    const voicedLabel = text(fixture, 'project-voice');
    await click(fixture, 'produce');
    const voicedValue = el<HTMLSelectElement>(fixture, 'produce-voice')!.value;
    await click(fixture, 'produce-confirm');

    expect([unvoicedLabel, goneLabel, voicedLabel]).toEqual([
      "Voice: Scout's choice",
      'Voice: piper:en_US-gone-high',
      'Voice: Aru <speaker> (medium)',
    ]);
    expect(goneState).toEqual([
      'piper:en_US-gone-high',
      'piper:en_US-gone-high (not available on this machine)',
      true,
      true,
    ]);
    expect(voicedValue).toBe('piper:en_GB-aru-medium#3');
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [
        {
          id: 'p3',
          narrationVoice: 'piper:en_US-gone-high',
          direction: '',
          motion: 'gentle',
          presenter: '',
        },
      ],
      [
        {
          id: 'p1',
          narrationVoice: 'piper:en_GB-aru-medium#3',
          direction: '',
          motion: 'gentle',
          presenter: '',
        },
      ],
    ]);
  });

  async function mountDecided(
    speech: boolean,
    images: boolean,
    provider = 'ChatGptPlan',
    stock = false,
    format: OperatorProjectDto['format'] = 'generated_pictures',
  ) {
    const decided = project({
      claims: [{ ...project().claims[0]!, status: 'supported' }],
      format,
    });
    const bridge = makeBridge({
      ...listing(decided),
      operatorReadiness: vi.fn(async () => ({
        ...readiness,
        speechAvailable: speech,
        imagesAvailable: images,
        stockPictures: stock,
        provider,
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    return { bridge, fixture };
  }

  it('the Produce panel warns when the provider has no speech and no image service, and Produce still sends the project', async () => {
    const { bridge, fixture } = await mountDecided(false, false);

    await click(fixture, 'produce');

    expect(bridge.operatorProduce).not.toHaveBeenCalled();
    expect(text(fixture, 'produce-warning')).toBe(
      'ChatGptPlan has no speech or image service in Scout: every scene will be a silent text card. Replace the narration on each scene and attach a background afterwards, or connect an AI Source with speech and pictures first.',
    );
    expect(text(fixture, 'produce-confirm')).toBe('Produce');
    expect(text(fixture, 'produce-cancel')).toBe('Cancel');

    await click(fixture, 'produce-confirm');

    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
    expect(el(fixture, 'produce-panel')).toBeNull();
  });

  it('the Produce panel warns when only narration is unavailable', async () => {
    const { bridge, fixture } = await mountDecided(false, true, 'openai');

    await click(fixture, 'produce');

    expect(bridge.operatorProduce).not.toHaveBeenCalled();
    expect(text(fixture, 'produce-warning')).toBe(
      'openai has no speech service in Scout: every scene will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.',
    );
  });

  it('the Produce panel warns when only pictures are unavailable', async () => {
    const { bridge, fixture } = await mountDecided(true, false, '');

    await click(fixture, 'produce');

    expect(bridge.operatorProduce).not.toHaveBeenCalled();
    expect(text(fixture, 'produce-warning')).toBe(
      'The connected AI Source has no image service in Scout: every scene will be a text card. Attach a background afterwards, or connect an AI Source with pictures first.',
    );
  });

  it('the Produce panel says pictures come from Openverse, credited, when the provider has no image service', async () => {
    const { fixture } = await mountDecided(false, false, 'ChatGptPlan', true);

    await click(fixture, 'produce');

    expect(text(fixture, 'produce-warning')).toBe(
      'ChatGptPlan has no speech or image service in Scout: every scene will be silent and pictures come from Openverse, credited. Replace the narration on each scene afterwards, or connect an AI Source with speech first.',
    );
  });

  it('the Produce panel shows no warning when speech is available and pictures come from Openverse', async () => {
    const { fixture } = await mountDecided(true, false, 'openai', true);

    await click(fixture, 'produce');

    expect(el(fixture, 'produce-warning')).toBeNull();
  });

  it('the Produce panel warning of an own pictures project speaks of narration only', async () => {
    const noSpeech = await mountDecided(false, false, 'ChatGptPlan', true, 'own_pictures');
    await click(noSpeech.fixture, 'produce');
    const silent = text(noSpeech.fixture, 'produce-warning');
    await click(noSpeech.fixture, 'delegate');
    const delegated = text(noSpeech.fixture, 'delegate-warning');
    TestBed.resetTestingModule();
    const noPictures = await mountDecided(true, false, 'openai', false, 'own_pictures');
    await click(noPictures.fixture, 'produce');

    const sentence =
      'ChatGptPlan has no speech service in Scout: every scene will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.';
    expect([silent, delegated]).toEqual([sentence, sentence]);
    expect(el(noPictures.fixture, 'produce-warning')).toBeNull();
  });

  it('the Produce panel of an own video project asks for no voice or motion and the Background block is hidden', async () => {
    const { bridge, fixture } = await mountDecided(false, false, 'ChatGptPlan', false, 'own_video');
    await click(fixture, 'produce');
    const panel = [
      el(fixture, 'produce-voice'),
      el(fixture, 'produce-voice-preview'),
      el(fixture, 'produce-motion'),
      el(fixture, 'produce-warning'),
      el(fixture, 'produce-brief') !== null,
      el(fixture, 'background'),
      el(fixture, 'background-attach'),
    ];

    await click(fixture, 'produce-confirm');

    expect(panel).toEqual([null, null, null, null, true, null, null]);
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
  });

  it('Cancel on the Produce panel closes it and produces nothing', async () => {
    const { bridge, fixture } = await mountDecided(false, false);
    await click(fixture, 'produce');

    await click(fixture, 'produce-cancel');

    expect(el(fixture, 'produce-panel')).toBeNull();
    expect(bridge.operatorProduce).not.toHaveBeenCalled();
  });

  it('a project still being scripted offers no Produce video media button', async () => {
    const bridge = makeBridge({
      ...listing(project({ status: 'scripting', claims: [] })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el(fixture, 'produce')).toBeNull();
  });

  it('a failed project offers Produce video media again when it has a script, and not without one', async () => {
    const failed = project({ status: 'failed', error: 'Speech timed out.', claims: [] });
    const unscripted = project({ id: 'p2', status: 'failed', script: null, claims: [] });
    const bridge = makeBridge({
      ...listing(failed, unscripted),
      operatorProduce: vi.fn(async () => project({ status: 'production_queued', claims: [] })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const withoutScript = el(fixture, 'produce');
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    await click(fixture, 'produce-confirm');

    expect(withoutScript).toBeNull();
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
  });

  it('a refused produce shows the control plane reason under the panel and keeps it open', async () => {
    const reason = 'The owner has not allowed youtube to write.';
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorProduce: vi.fn(async () => {
        throw refusedWith('scout:operator.production', reason);
      }),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    await click(fixture, 'produce-confirm');

    expect(text(fixture, 'produce-error')).toBe(reason);
    expect(el(fixture, 'produce-panel')).not.toBeNull();
  });

  it('a project without a background says so and Attach background sends the background kind', async () => {
    const attached = project({ backgroundFile: 'owner-background-0a1b2c3d4e5f.png' });
    const bridge = makeBridge({
      ...listing(project(), project({ id: 'p2', status: 'queued', script: null })),
      operatorAttachSceneMedia: vi.fn(async () => ({
        project: attached as OperatorProjectDto | null,
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const queuedBlock = [text(fixture, 'background-empty'), el(fixture, 'background-attach')];
    await click(fixture, 'project-p1');
    const before = [text(fixture, 'background-empty'), text(fixture, 'background-attach')];

    await click(fixture, 'background-attach');

    expect(queuedBlock).toEqual([
      'No background yet: every scene without its own picture is a dark text card.',
      null,
    ]);
    expect(before).toEqual([
      'No background yet: every scene without its own picture is a dark text card.',
      'Attach background',
    ]);
    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: '', kind: 'background' }],
    ]);
    expect(el(fixture, 'background-image')!.getAttribute('src')).toBe(
      'scout-media://p1/owner-background-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
    );
  });

  it('a project with a background shows it and offers Replace background', async () => {
    const statuses = [
      'script_ready',
      'media_ready',
      'assembled',
      'approved',
      'assembly_queued',
      'assembling',
      'failed',
      'producing',
      'production_queued',
    ];
    const projects = statuses.map((status, i) =>
      project({ id: `p${i}`, status, backgroundFile: 'owner-background-0a1b2c3d4e5f.jpg' }),
    );
    let refuse = false;
    const bridge = makeBridge({
      ...listing(...projects),
      operatorAttachSceneMedia: vi.fn(async () => {
        if (refuse) {
          throw refusedWith('scout:operator.sceneMedia', 'Pictures are at most 20 MB.');
        }
        return { project: null as OperatorProjectDto | null };
      }),
    });
    const fixture = await mount(bridge);
    const offered: string[] = [];
    for (const p of projects) {
      await click(fixture, `project-${p.id}`);
      if (el(fixture, 'background-attach') !== null) offered.push(p.status);
    }
    await click(fixture, 'project-p0');
    const image = el<HTMLImageElement>(fixture, 'background-image')!;
    const label = text(fixture, 'background-attach');
    refuse = true;
    await click(fixture, 'background-attach');

    expect(offered).toEqual(statuses.slice(0, 7));
    expect([image.getAttribute('src'), image.getAttribute('alt')]).toEqual([
      'scout-media://p0/owner-background-0a1b2c3d4e5f.jpg?v=2026-11-03T09%3A05%3A00Z',
      'Background of the video',
    ]);
    expect(label).toBe('Replace background');
    expect(el(fixture, 'background-empty')).toBeNull();
    expect(text(fixture, 'background-error')).toBe('Pictures are at most 20 MB.');
  });

  it('a scene without a picture previews the dark text card with the first sentence', async () => {
    const scenes = [
      scene({
        id: 's1',
        narration: '  Span is a view over memory. It never allocates!  ',
        visualSource: 'text_card',
        visualFile: null,
      }),
      scene({
        id: 's2',
        order: 2,
        narration: 'Does it copy? No.',
        visualSource: 'text_card',
        visualFile: null,
      }),
      scene({
        id: 's3',
        order: 3,
        narration: ' A ref struct lives on the stack ',
        visualSource: 'text_card',
        visualFile: null,
      }),
      scene({ id: 's4', order: 4 }),
      scene({
        id: 's5',
        order: 5,
        narration: 'Version 1.5 ships .NET 10 support. Next sentence.',
        visualSource: 'text_card',
        visualFile: null,
      }),
      scene({
        id: 's6',
        order: 6,
        narration: 'Span<T> in .NET is fast. More.',
        visualSource: 'text_card',
        visualFile: null,
      }),
    ];
    const fixture = await mount(makeBridge(listing(project({ scenes }))));
    await click(fixture, 'project-p1');

    expect(
      ['s1', 's2', 's3', 's5', 's6'].map((id) => text(fixture, `scene-preview-text-${id}`)),
    ).toEqual([
      'Span is a view over memory.',
      'Does it copy?',
      'A ref struct lives on the stack',
      'Version 1.5 ships .NET 10 support.',
      'Span<T> in .NET is fast.',
    ]);
    expect(el(fixture, 'scene-preview-s1')!.classList.contains('card--background')).toBe(false);
    expect(el(fixture, 'scene-preview-background-s1')).toBeNull();
    expect(el(fixture, 'scene-image-s1')).toBeNull();
    expect([el(fixture, 'scene-preview-s4'), el(fixture, 'scene-image-s4')?.tagName]).toEqual([
      null,
      'IMG',
    ]);
  });

  it('a scene without a picture previews the background with the first sentence', async () => {
    const scenes = [
      scene({
        narration: 'Span is a view over memory. It never allocates.',
        visualSource: 'text_card',
        visualFile: null,
      }),
    ];
    const fixture = await mount(
      makeBridge(listing(project({ scenes, backgroundFile: 'owner-background-0a1b2c3d4e5f.png' }))),
    );
    await click(fixture, 'project-p1');

    expect(el(fixture, 'scene-preview-background-s1')?.getAttribute('src')).toBe(
      'scout-media://p1/owner-background-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
    );
    expect(el(fixture, 'scene-preview-s1')!.classList.contains('card--background')).toBe(true);
    expect(text(fixture, 'scene-preview-text-s1')).toBe('Span is a view over memory.');
    expect(el(fixture, 'scene-image-s1')).toBeNull();
  });

  it('the Delegate dialog lists the steps and sends the id and the voice', async () => {
    vi.useFakeTimers();
    const pending = project({ narrationVoice: 'piper:en_GB-cori-medium' });
    const delegated = project({
      status: 'production_queued',
      delegated: true,
      narrationVoice: 'OpenAI:nova',
    });
    let listed = pending;
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: [listed] })),
      operatorProject: vi.fn(async () => listed),
      operatorDelegate: vi.fn(async () => {
        listed = delegated;
        return delegated;
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const badgeBefore = el(fixture, 'project-delegated');
    const delegateDisabled = el<HTMLButtonElement>(fixture, 'delegate')!.disabled;

    await click(fixture, 'delegate');
    const preselected = el<HTMLSelectElement>(fixture, 'delegate-voice')!.value;
    const steps = Array.from(
      el(fixture, 'delegate-steps')!.querySelectorAll('li') as NodeListOf<HTMLElement>,
    ).map((li) => li.textContent!.trim());
    const warning = text(fixture, 'delegate-warning');
    const voice = el<HTMLSelectElement>(fixture, 'delegate-voice')!;
    voice.value = 'OpenAI:nova';
    voice.dispatchEvent(new Event('change'));
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const keptOpen = [
      el(fixture, 'delegate-dialog') !== null,
      el<HTMLSelectElement>(fixture, 'delegate-voice')?.value,
    ];
    const sentBeforeConfirm = bridge.operatorDelegate.mock.calls.length;
    const buttons = [text(fixture, 'delegate-confirm'), text(fixture, 'delegate-cancel')];
    await click(fixture, 'delegate-confirm');

    expect([badgeBefore, delegateDisabled, preselected]).toEqual([
      null,
      false,
      'piper:en_GB-cori-medium',
    ]);
    expect(steps).toEqual([
      'Produce media',
      'Assemble the video',
      'Stop at ready to approve: publishing needs your approval',
    ]);
    expect(warning).toBe(
      'openai has no speech service in Scout: every scene will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.',
    );
    expect(keptOpen).toEqual([true, 'OpenAI:nova']);
    expect(sentBeforeConfirm).toBe(0);
    expect(buttons).toEqual(['Confirm and delegate', 'Cancel']);
    expect(bridge.operatorDelegate.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: 'OpenAI:nova', direction: '', motion: 'gentle', presenter: '' }],
    ]);
    expect(el(fixture, 'delegate-dialog')).toBeNull();
    expect(text(fixture, 'project-delegated')).toBe('Delegated');
    expect(text(fixture, 'project-status-p1')).toBe('production queued');
  });

  it('the Delegate dialog keeps a voice the readiness does not list selected and sends it back', async () => {
    const gone = project({ narrationVoice: 'piper:en_US-gone-high' });
    const bridge = makeBridge({
      ...listing(gone),
      operatorReadiness: vi.fn(async () => fullReadiness),
      operatorDelegate: vi.fn(async () =>
        project({ status: 'production_queued', delegated: true }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    await click(fixture, 'delegate');
    const select = el<HTMLSelectElement>(fixture, 'delegate-voice')!;
    const option = Array.from(select.options).find((o) => o.value === 'piper:en_US-gone-high')!;
    const state = [select.value, option.textContent!.trim(), option.disabled, option.selected];
    await click(fixture, 'delegate-confirm');

    expect(state).toEqual([
      'piper:en_US-gone-high',
      'piper:en_US-gone-high (not available on this machine)',
      true,
      true,
    ]);
    expect(bridge.operatorDelegate.mock.calls).toEqual([
      [
        {
          id: 'p1',
          narrationVoice: 'piper:en_US-gone-high',
          direction: '',
          motion: 'gentle',
          presenter: '',
        },
      ],
    ]);
  });

  it('Delegate is offered on a script_ready or media_ready project and on a failed one with a script, whatever claims are pending', async () => {
    const statuses = [
      'script_ready',
      'media_ready',
      'failed',
      'assembled',
      'approved',
      'producing',
    ];
    const projects = [
      ...statuses.map((status, i) => project({ id: `p${i}`, status })),
      project({ id: 'p9', status: 'failed', script: null }),
    ];
    const fixture = await mount(makeBridge(listing(...projects)));

    const offered: string[] = [];
    for (const p of projects) {
      await click(fixture, `project-${p.id}`);
      if (el(fixture, 'delegate') !== null) offered.push(p.id);
    }

    expect(offered).toEqual(['p0', 'p1', 'p2']);
  });

  it('a refused delegation keeps the dialog with its reason', async () => {
    const reason =
      'The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.';
    const bridge = makeBridge({
      ...listing(project()),
      operatorDelegate: vi.fn(async () => {
        throw new Error(
          `Error invoking remote method 'scout:operator.delegation': Error: ${encodeIpcErrorMessage(
            serializeError(new ControlPlanePermissionDeniedError(reason)),
          )}`,
        );
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    await click(fixture, 'delegate');

    await click(fixture, 'delegate-confirm');
    const refused = [el(fixture, 'delegate-dialog') !== null, text(fixture, 'delegate-error')];
    el(fixture, 'delegate-cancel')?.click();
    await settle(fixture);

    expect(refused).toEqual([true, reason]);
    expect(el(fixture, 'delegate-dialog')).toBeNull();
    expect(el(fixture, 'project-delegated')).toBeNull();
  });

  it('Delete project is hidden while a project is researching, scripting, producing, assembling, presenting or uploading, and offered otherwise', async () => {
    const statuses = [
      'researching',
      'scripting',
      'producing',
      'assembling',
      'presenting',
      'script_ready',
      'failed',
      'approved',
    ];
    const projects = [
      ...statuses.map((status, i) => project({ id: `p${i}`, status })),
      project({
        id: 'p8',
        status: 'approved',
        publication: {
          status: 'uploading',
          videoId: null,
          privacyStatus: 'private',
          publishAt: null,
          madeForKids: false,
          syntheticMedia: false,
          uploadedAt: null,
          error: null,
          snapshots: [],
          proposals: [],
          experiment: null,
        },
      }),
    ];
    const fixture = await mount(makeBridge(listing(...projects)));

    const offered: string[] = [];
    for (const p of projects) {
      if (el(fixture, `project-delete-${p.id}`) !== null) offered.push(p.id);
    }

    expect(offered).toEqual(['p5', 'p6', 'p7']);
  });

  it('Delete project asks first, says what goes and what stays, and Cancel sends nothing', async () => {
    const bridge = makeBridge({ ...listing(project()) });
    const fixture = await mount(bridge);
    const label = el(fixture, 'project-delete-p1')!.getAttribute('aria-label');

    await click(fixture, 'project-delete-p1');
    const dialog = [
      text(fixture, 'delete-dialog'),
      text(fixture, 'delete-confirm'),
      text(fixture, 'delete-cancel'),
    ];
    await click(fixture, 'delete-cancel');

    expect(label).toBe('Delete Span<T> in C#');
    expect(dialog[0]).toContain(
      "This removes the project, its script, scenes and media files from Scout's data folder. A video already on YouTube stays there. It cannot be undone.",
    );
    expect(dialog.slice(1)).toEqual(['Delete project', 'Cancel']);
    expect(el(fixture, 'delete-dialog')).toBeNull();
    expect(bridge.operatorDeleteProject).not.toHaveBeenCalled();
  });

  it('confirming a delete sends the project id, then the project leaves the list and the detail closes', async () => {
    let remaining = [project(), project({ id: 'p2', topic: 'Records in C#' })];
    const bridge = makeBridge({
      operatorProjects: vi.fn(async () => ({ projects: remaining })),
      operatorProject: vi.fn(async ({ id }: { id: string }) => remaining.find((p) => p.id === id)!),
      operatorDeleteProject: vi.fn(async ({ id }: { id: string }) => {
        remaining = remaining.filter((p) => p.id !== id);
        return { deleted: true as const };
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    await click(fixture, 'project-delete-p1');

    await click(fixture, 'delete-confirm');

    expect(bridge.operatorDeleteProject.mock.calls).toEqual([[{ id: 'p1' }]]);
    expect(el(fixture, 'project-p1')).toBeNull();
    expect(el(fixture, 'project-p2')).not.toBeNull();
    expect(el(fixture, 'project-detail')).toBeNull();
    expect(el(fixture, 'delete-dialog')).toBeNull();
  });

  it('the trash on a project row opens the delete dialog for that project without selecting it, and is absent on a running project', async () => {
    const fixture = await mount(
      makeBridge(
        listing(
          project(),
          project({ id: 'p2', topic: 'Records in C#' }),
          project({ id: 'p3', topic: 'Busy one', status: 'producing' }),
        ),
      ),
    );

    await click(fixture, 'project-delete-p2');

    expect([
      text(fixture, 'delete-dialog').includes('Records in C#'),
      fixture.componentInstance.selectedId(),
      el(fixture, 'project-delete-p3'),
    ]).toEqual([true, null, null]);
  });

  it('a refused delete keeps the dialog with its reason and the project in the list', async () => {
    const reason = 'The project has pending YouTube actions; finish or cancel them first.';
    const bridge = makeBridge({
      ...listing(project()),
      operatorDeleteProject: vi.fn(async () => {
        throw refusedWith('scout:operator.projectDeletion', reason);
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    await click(fixture, 'project-delete-p1');

    await click(fixture, 'delete-confirm');

    expect([el(fixture, 'delete-dialog') !== null, text(fixture, 'delete-error')]).toEqual([
      true,
      reason,
    ]);
    expect(el(fixture, 'project-p1')).not.toBeNull();
    expect(el(fixture, 'project-detail')).not.toBeNull();
  });

  it('lists the scenes in their order with narration, picture prompt, status and where the audio and picture come from', async () => {
    const scenes = [
      scene({
        id: 's2',
        order: 2,
        narration: 'It never allocates.',
        status: 'failed',
        error: 'Speech is not available.',
        audioSource: 'none',
        visualSource: 'text_card',
        audioFile: null,
        visualFile: null,
      }),
      scene(),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    const order = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="scene-narration"]',
      ) as NodeListOf<HTMLElement>,
    ).map((n) => n.textContent?.trim());

    expect(order).toEqual(['Span is a view over memory.', 'It never allocates.']);
    expect(text(fixture, 'scene-picture-prompt-line-s1')).toBe(
      'Picture prompt: A ruler laid over a row of bytes',
    );
    expect(text(fixture, 'scene-status-s1')).toBe('Ready');
    expect(text(fixture, 'scene-sources-s1')).toBe('Your picture · narration: generated');
    expect(text(fixture, 'scene-status-s2')).toBe('Failed');
    expect(text(fixture, 'scene-sources-s2')).toBe('Text card · narration: none');
    expect(text(fixture, 'scene-failure-s2')).toBe('Speech is not available.');
  });

  it('shows the error of a failed scene as a failure and the note of any other scene as a plain note', async () => {
    const scenes = [
      scene({ error: 'No picture: the connected AI Source has no image service in Scout.' }),
      scene({ id: 's2', order: 2, status: 'failed', error: 'Speech timed out.' }),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(text(fixture, 'scene-note-s1')).toBe(
      'No picture: the connected AI Source has no image service in Scout.',
    );
    expect(el(fixture, 'scene-note-s1')!.classList.contains('err')).toBe(false);
    expect(el(fixture, 'scene-failure-s1')).toBeNull();
    expect(text(fixture, 'scene-failure-s2')).toBe('Speech timed out.');
    expect(el(fixture, 'scene-failure-s2')!.classList.contains('err')).toBe(true);
    expect(el(fixture, 'scene-note-s2')).toBeNull();
  });

  it('previews a scene image and audio through scout-media:// with the project id and the file name, never a file path', async () => {
    const scenes = [scene(), scene({ id: 's2', order: 2, audioFile: null, visualFile: null })];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(el(fixture, 'scene-image-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/owner-s1-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
    );
    expect(el(fixture, 'scene-image-s1')!.getAttribute('alt')).toBe('Picture of scene 1');
    expect(el(fixture, 'scene-audio-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/scene-s1.mp3?v=2026-11-03T09%3A05%3A00Z',
    );
    expect(el<HTMLAudioElement>(fixture, 'scene-audio-s1')!.controls).toBe(true);
    expect([el(fixture, 'scene-image-s2'), el(fixture, 'scene-audio-s2')]).toEqual([null, null]);
  });

  it('move up and move down send the scene ids in the new order', async () => {
    const scenes = [
      scene({ id: 's1', order: 1 }),
      scene({ id: 's2', order: 2 }),
      scene({ id: 's3', order: 3 }),
    ];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorReorderScenes: vi.fn(async () => project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const edges = [
      el<HTMLButtonElement>(fixture, 'scene-up-s1')!.disabled,
      el<HTMLButtonElement>(fixture, 'scene-down-s3')!.disabled,
    ];

    await click(fixture, 'scene-down-s1');
    await click(fixture, 'scene-up-s3');

    expect(edges).toEqual([true, true]);
    expect(bridge.operatorReorderScenes.mock.calls).toEqual([
      [{ projectId: 'p1', sceneIds: ['s2', 's1', 's3'] }],
      [{ projectId: 'p1', sceneIds: ['s1', 's3', 's2'] }],
    ]);
  });

  it('the lock toggle sends locked and unlocked, and Regenerate is disabled on a locked scene and sends the scene otherwise', async () => {
    const scenes = [scene({ id: 's1', locked: true }), scene({ id: 's2', order: 2 })];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorSetSceneLocked: vi.fn(async () => project({ scenes })),
      operatorRegenerateScene: vi.fn(async () => project({ scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const labels = [text(fixture, 'scene-lock-s1'), text(fixture, 'scene-lock-s2')];

    await click(fixture, 'scene-lock-s1');
    await click(fixture, 'scene-lock-s2');
    await click(fixture, 'scene-regenerate-s2');

    expect(labels).toEqual(['Unlock', 'Lock']);
    expect(el<HTMLButtonElement>(fixture, 'scene-regenerate-s1')!.disabled).toBe(true);
    expect(bridge.operatorSetSceneLocked.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', locked: false }],
      [{ projectId: 'p1', sceneId: 's2', locked: true }],
    ]);
    expect(bridge.operatorRegenerateScene.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's2' }],
    ]);
  });

  it('Replace narration and Attach image send the project, the scene and the kind, and show the attached file', async () => {
    const before = [scene({ audioFile: null, visualFile: null, audioSource: 'none' })];
    const after = [scene({ audioFile: 'owner-s1-aaaaaaaaaaaa.mp3', audioSource: 'owner' })];
    const bridge = makeBridge({
      ...listing(project({ scenes: before, format: 'own_pictures' })),
      operatorAttachSceneMedia: vi.fn(async () => ({
        project: project({ scenes: after, format: 'own_pictures' }) as OperatorProjectDto | null,
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-narration-replace-s1');
    await click(fixture, 'scene-attach-visual-s1');

    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', kind: 'audio' }],
      [{ projectId: 'p1', sceneId: 's1', kind: 'visual' }],
    ]);
    expect(el(fixture, 'scene-audio-s1')!.getAttribute('src')).toBe(
      'scout-media://p1/owner-s1-aaaaaaaaaaaa.mp3?v=2026-11-03T09%3A05%3A00Z',
    );
  });

  it('a generated pictures project shows no Attach image and offers Regenerate scene; an own pictures project shows Attach image and no Regenerate scene', async () => {
    const generated = project({ scenes: [scene()] });
    const own = project({ id: 'p2', scenes: [scene()], format: 'own_pictures' });
    const fixture = await mount(makeBridge(listing(generated, own)));

    await click(fixture, 'project-p1');
    const generatedControls = [
      el(fixture, 'scene-attach-visual-s1'),
      text(fixture, 'scene-regenerate-s1'),
      text(fixture, 'project-format'),
    ];
    await click(fixture, 'project-p2');

    expect(generatedControls).toEqual([null, 'Regenerate scene', 'Generated pictures']);
    expect([
      text(fixture, 'scene-attach-visual-s1'),
      el(fixture, 'scene-regenerate-s1'),
      text(fixture, 'project-format'),
    ]).toEqual(['Attach image', null, 'Your own pictures']);
  });

  it('a clip past the first hour of an own video reads as h:mm:ss, the way the chapters do', async () => {
    const late = scene({
      audioSource: 'recording',
      visualSource: 'recording',
      audioFile: null,
      visualFile: null,
      clipStartMs: 3_599_000,
      clipEndMs: 3_725_400,
    });
    const fixture = await mount(
      makeBridge(listing(project({ format: 'own_video', scenes: [late] }))),
    );
    await click(fixture, 'project-p1');

    expect(text(fixture, 'scene-clip-s1')).toBe('Clip: 59:59 to 1:02:05');
  });

  it('an own video scene shows its clip and offers Edit text, with no narration row, Regenerate scene or Attach image', async () => {
    const recorded = scene({
      narration: 'Today we look at Span.',
      audioSource: 'recording',
      visualSource: 'recording',
      audioFile: null,
      visualFile: null,
      clipStartMs: 62_000,
      clipEndMs: 125_500,
    });
    const own = project({ format: 'own_video', scenes: [recorded] });
    const generated = project({ id: 'p2', scenes: [scene()] });
    const bridge = makeBridge(listing(own, generated));
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const generatedClip = el(fixture, 'scene-clip-s1');
    await click(fixture, 'project-p1');
    const controls = [
      text(fixture, 'scene-clip-s1'),
      text(fixture, 'scene-sources-s1'),
      el(fixture, 'scene-audio-row-s1'),
      el(fixture, 'scene-narration-regenerate-s1'),
      el(fixture, 'scene-regenerate-s1'),
      el(fixture, 'scene-attach-visual-s1'),
      text(fixture, 'scene-edit-s1'),
    ];
    const pictureLines = (el(fixture, 'scene-s1')!.textContent ?? '').includes('Picture prompt');

    await click(fixture, 'scene-edit-s1');
    const hint = text(fixture, 'scene-text-hint-s1');
    type(fixture, 'scene-text-s1', ' Today we look at Span<T>. ');
    await settle(fixture);
    await click(fixture, 'scene-text-save-s1');

    expect(generatedClip).toBeNull();
    expect(controls).toEqual([
      'Clip: 1:02 to 2:05',
      'Your recording · narration: your recording',
      null,
      null,
      null,
      null,
      'Edit text',
    ]);
    expect(pictureLines).toBe(false);
    expect(hint).toBe("The new text replaces this scene's captions; the recording is not changed.");
    expect(bridge.operatorEditSceneText.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', text: 'Today we look at Span<T>.' }],
    ]);
  });

  it('the narration row plays the audio, regenerates the narration and replaces it with a file, and no Attach audio is left', async () => {
    const scenes = [scene(), scene({ id: 's2', order: 2, locked: true })];
    const renarrated = project({
      status: 'production_queued',
      scenes: [scene({ status: 'planned', audioSource: 'none', audioFile: null }), scenes[1]!],
    });
    const bridge = makeBridge({
      ...listing(project({ scenes }), project({ id: 'p2', scenes, format: 'own_pictures' })),
      operatorRegenerateNarration: vi.fn(async () => renarrated),
      operatorAttachSceneMedia: vi.fn(async () => ({ project: null as OperatorProjectDto | null })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const ownAttachAudio = el(fixture, 'scene-attach-audio-s1');
    await click(fixture, 'project-p1');
    const row = el(fixture, 'scene-audio-row-s1')!;
    const menu = el(fixture, 'scene-narration-menu-s1')!;
    const inRow = [
      row.querySelector('[data-test="scene-audio-s1"]')?.getAttribute('src'),
      menu.querySelector('[data-test="scene-narration-regenerate-s1"]')?.textContent?.trim(),
      menu.querySelector('[data-test="scene-narration-replace-s1"]')?.textContent?.trim(),
    ];
    const lockedRegenerate = el<HTMLButtonElement>(fixture, 'scene-narration-regenerate-s2')!;

    await click(fixture, 'scene-narration-replace-s1');
    await click(fixture, 'scene-narration-regenerate-s1');

    expect(inRow).toEqual([
      'scout-media://p1/scene-s1.mp3?v=2026-11-03T09%3A05%3A00Z',
      'Regenerate narration',
      'Replace narration',
    ]);
    expect([ownAttachAudio, el(fixture, 'scene-attach-audio-s1')]).toEqual([null, null]);
    expect(fixture.nativeElement.textContent).not.toContain('Attach audio');
    expect(lockedRegenerate.disabled).toBe(true);
    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', kind: 'audio' }],
    ]);
    expect(bridge.operatorRegenerateNarration.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1' }],
    ]);
    expect([text(fixture, 'scene-status-s1'), el(fixture, 'scene-audio-s1')]).toEqual([
      'Not made yet',
      null,
    ]);
  });

  it("Regenerate narration on a scene with the owner's audio asks first and Cancel sends nothing", async () => {
    const bridge = makeBridge({
      ...listing(project({ scenes: [scene({ audioSource: 'owner' })] })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-narration-regenerate-s1');
    const dialog = [
      text(fixture, 'narration-replace-dialog'),
      text(fixture, 'narration-replace-confirm'),
      text(fixture, 'narration-replace-cancel'),
    ];
    const sentWhileAsking = bridge.operatorRegenerateNarration.mock.calls.length;
    await click(fixture, 'narration-replace-cancel');

    expect(dialog[0]).toContain(
      "Replace your own narration on this scene with Scout's voice? Your file stays on disk but the video will use the generated narration.",
    );
    expect(dialog.slice(1)).toEqual(['Confirm', 'Cancel']);
    expect(sentWhileAsking).toBe(0);
    expect(el(fixture, 'narration-replace-dialog')).toBeNull();
    expect(bridge.operatorRegenerateNarration).not.toHaveBeenCalled();
  });

  it("confirming the replacement of the owner's narration sends the regeneration once", async () => {
    const bridge = makeBridge({
      ...listing(project({ scenes: [scene({ audioSource: 'owner' })] })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-narration-regenerate-s1');
    await click(fixture, 'narration-replace-confirm');

    expect(bridge.operatorRegenerateNarration.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1' }],
    ]);
    expect(el(fixture, 'narration-replace-dialog')).toBeNull();
  });

  it('Edit text saves the trimmed text, Cancel sends nothing, and an empty or over-700 text shows the sentence and disables Save', async () => {
    const edited = project({
      status: 'production_queued',
      scenes: [scene({ narration: 'Span is a window over memory.', status: 'planned' })],
    });
    const bridge = makeBridge({
      ...listing(project({ scenes: [scene()] })),
      operatorEditSceneText: vi.fn(async () => edited),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const save = () => el<HTMLButtonElement>(fixture, 'scene-text-save-s1')!;

    await click(fixture, 'scene-edit-s1');
    const opened = [
      el<HTMLTextAreaElement>(fixture, 'scene-text-s1')!.value,
      text(fixture, 'scene-text-count-s1'),
      save().disabled,
      el(fixture, 'scene-text-invalid-s1'),
    ];
    type(fixture, 'scene-text-s1', '   ');
    await settle(fixture);
    const empty = [text(fixture, 'scene-text-invalid-s1'), save().disabled];
    type(fixture, 'scene-text-s1', 't'.repeat(701));
    await settle(fixture);
    const tooLong = [
      text(fixture, 'scene-text-count-s1'),
      text(fixture, 'scene-text-invalid-s1'),
      save().disabled,
    ];
    await click(fixture, 'scene-text-cancel-s1');
    const cancelled = [
      el(fixture, 'scene-text-s1'),
      bridge.operatorEditSceneText.mock.calls.length,
    ];
    await click(fixture, 'scene-edit-s1');
    type(fixture, 'scene-text-s1', '  Span is a window over memory.  ');
    await settle(fixture);
    const valid = [text(fixture, 'scene-text-count-s1'), save().disabled];
    await click(fixture, 'scene-text-save-s1');

    expect(opened).toEqual(['Span is a view over memory.', '27 / 700', true, null]);
    expect(empty).toEqual(["A scene's text is 1 to 700 characters.", true]);
    expect(tooLong).toEqual(['701 / 700', "A scene's text is 1 to 700 characters.", true]);
    expect(cancelled).toEqual([null, 0]);
    expect(valid).toEqual(['29 / 700', false]);
    expect(bridge.operatorEditSceneText.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', text: 'Span is a window over memory.' }],
    ]);
    expect(el(fixture, 'scene-text-s1')).toBeNull();
    expect(text(fixture, 'scene-s1')).toContain('Span is a window over memory.');
    expect(text(fixture, 'scene-status-s1')).toBe('Not made yet');
  });

  it('Edit text is disabled on a locked scene and a refusal shows its reason', async () => {
    const reason = "Scene 's1' is locked; unlock it before editing its text.";
    const scenes = [scene(), scene({ id: 's2', order: 2, locked: true })];
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorEditSceneText: vi.fn(async () => {
        throw refusedWith('scout:operator.sceneText', reason);
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const lockedEdit = el<HTMLButtonElement>(fixture, 'scene-edit-s2')!.disabled;

    await click(fixture, 'scene-edit-s1');
    type(fixture, 'scene-text-s1', 'New text.');
    await settle(fixture);
    await click(fixture, 'scene-text-save-s1');

    expect(lockedEdit).toBe(true);
    expect(text(fixture, 'scene-error')).toBe(reason);
    expect(el<HTMLTextAreaElement>(fixture, 'scene-text-s1')!.value).toBe('New text.');
  });

  it('a refused attach shows the reason, and a closed file dialog changes nothing', async () => {
    const scenes = [scene({ audioFile: null })];
    let refuse = true;
    const bridge = makeBridge({
      ...listing(project({ scenes })),
      operatorAttachSceneMedia: vi.fn(async () => {
        if (refuse)
          throw refusedWith('scout:operator.sceneMedia', 'Narration files are at most 100 MB.');
        return { project: null as OperatorProjectDto | null };
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'scene-narration-replace-s1');
    const refused = text(fixture, 'scene-error');
    refuse = false;
    await click(fixture, 'scene-narration-replace-s1');

    expect(refused).toBe('Narration files are at most 100 MB.');
    expect(el(fixture, 'scene-error')).toBeNull();
    expect(el(fixture, 'scene-audio-s1')).toBeNull();
    expect(text(fixture, 'scene-s1')).toContain('Span is a view over memory.');
  });

  it('a producing project shows how many of its scenes are ready', async () => {
    const scenes = [
      scene({ id: 's1' }),
      scene({ id: 's2', order: 2, status: 'generating' }),
      scene({ id: 's3', order: 3, status: 'planned' }),
    ];
    const bridge = makeBridge({
      ...listing(project({ status: 'producing', scenes })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    expect(text(fixture, 'production-progress')).toBe('1 of 3 scenes ready');
  });
  function recordPlays(): { element: HTMLMediaElement; src: string }[] {
    const plays: { element: HTMLMediaElement; src: string }[] = [];
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(function (
      this: HTMLMediaElement,
    ) {
      plays.push({ element: this, src: this.src });
      return Promise.resolve();
    });
    return plays;
  }

  function choose(fixture: ComponentFixture<ProductionComponent>, testId: string, value: string) {
    const select = el<HTMLSelectElement>(fixture, testId)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it("Preview plays the selected voice's sample", async () => {
    const plays = recordPlays();
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    choose(fixture, 'produce-voice', 'piper:en_GB-aru-medium#3');
    const produceButton = el<HTMLButtonElement>(fixture, 'produce-voice-preview')!;
    const produceState = [
      produceButton.textContent!.trim(),
      produceButton.disabled,
      produceButton.title,
    ];
    await click(fixture, 'produce-voice-preview');
    await click(fixture, 'produce-cancel');
    await click(fixture, 'delegate');
    choose(fixture, 'delegate-voice', 'piper:en_GB-cori-medium');
    const delegateButton = el<HTMLButtonElement>(fixture, 'delegate-voice-preview')!;
    const delegateState = [delegateButton.textContent!.trim(), delegateButton.disabled];
    await click(fixture, 'delegate-voice-preview');

    expect(produceState).toEqual(['Preview', false, '']);
    expect(delegateState).toEqual(['Preview', false]);
    expect(plays.map((p) => p.src)).toEqual([
      'scout-media://voice-samples/piper-en_GB-aru-medium-3.wav',
      'scout-media://voice-samples/piper-en_GB-cori-medium.wav',
    ]);
    expect(plays.every((p) => p.element === el(fixture, 'voice-sample-player'))).toBe(true);
  });

  it('Preview is disabled while the selected voice has no sample', async () => {
    const plays = recordPlays();
    const noCoriSample: ReadinessDto = {
      ...fullReadiness,
      voices: fullReadiness.voices.map((v) =>
        v.id === 'piper:en_GB-cori-medium' ? { ...v, sampleFile: null } : v,
      ),
    };
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorReadiness: vi.fn(async () => noCoriSample),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    choose(fixture, 'produce-voice', 'OpenAI:nova');
    const button = el<HTMLButtonElement>(fixture, 'produce-voice-preview')!;
    const withoutSample = [button.disabled, button.title];
    choose(fixture, 'produce-voice', '');
    const scoutsChoice = [button.disabled, button.title];
    button.click();
    await click(fixture, 'produce-cancel');
    await click(fixture, 'delegate');
    choose(fixture, 'delegate-voice', 'OpenAI:nova');
    const delegateButton = el<HTMLButtonElement>(fixture, 'delegate-voice-preview')!;

    expect(withoutSample).toEqual([true, 'No sample yet']);
    expect(scoutsChoice).toEqual([true, 'No sample yet']);
    expect([delegateButton.disabled, delegateButton.title]).toEqual([true, 'No sample yet']);
    expect(plays).toEqual([]);
  });

  it("Scout's choice previews no sample while a provider voice is listed, else Cori on Piper, else the first Piper voice", async () => {
    const plays = recordPlays();
    const piperOnly: ReadinessDto = {
      ...fullReadiness,
      voices: fullReadiness.voices.filter((v) => v.source === 'piper'),
    };
    const piperWithoutCori: ReadinessDto = {
      ...piperOnly,
      voices: piperOnly.voices.filter((v) => v.id !== 'piper:en_GB-cori-medium'),
    };
    const firstPiperWithoutSample: ReadinessDto = {
      ...piperWithoutCori,
      voices: [
        ...piperWithoutCori.voices.map((v) => ({ ...v, sampleFile: null })),
        {
          id: 'piper:en_US-amy-medium',
          label: 'Amy (medium)',
          source: 'piper',
          sampleFile: 'piper-en_US-amy-medium.wav',
        },
      ],
    };
    let current = fullReadiness;
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorReadiness: vi.fn(async () => current),
    });
    const scoutsChoice = async () => {
      TestBed.resetTestingModule();
      const fixture = await mount(bridge);
      await click(fixture, 'project-p1');
      await click(fixture, 'produce');
      const button = el<HTMLButtonElement>(fixture, 'produce-voice-preview')!;
      const state = [
        el<HTMLSelectElement>(fixture, 'produce-voice')!.value,
        button.disabled,
        button.title,
      ];
      await click(fixture, 'produce-voice-preview');
      return state;
    };

    const withProviderVoice = await scoutsChoice();
    current = piperOnly;
    const withoutProviderVoices = await scoutsChoice();
    current = piperWithoutCori;
    const withoutCori = await scoutsChoice();
    current = firstPiperWithoutSample;
    const firstPiperHasNoSample = await scoutsChoice();

    expect(withProviderVoice).toEqual(['', true, 'No sample yet']);
    expect(withoutProviderVoices).toEqual(['', false, '']);
    expect(withoutCori).toEqual(['', false, '']);
    expect(firstPiperHasNoSample).toEqual(['', true, 'No sample yet']);
    expect(plays.map((p) => p.src)).toEqual([
      'scout-media://voice-samples/piper-en_GB-cori-medium.wav',
      'scout-media://voice-samples/piper-en_GB-aru-medium-3.wav',
    ]);
  });

  it('Produce sends the brief and the motion with the voice', async () => {
    vi.useFakeTimers();
    const bridge = makeBridge({
      ...listing(project({ claims: [] })),
      operatorMandate: vi.fn(async () => ({ mandate })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'produce');
    const motion = el<HTMLSelectElement>(fixture, 'produce-motion')!;
    const motionOptions = Array.from(motion.options).map((o) => [o.value, o.textContent!.trim()]);
    const opened = [
      el<HTMLTextAreaElement>(fixture, 'produce-brief')!.value,
      text(fixture, 'produce-brief-count'),
      motion.value,
    ];
    type(fixture, 'produce-brief', '  Chalk sketches on a dark slate  ');
    choose(fixture, 'produce-motion', 'dynamic');
    choose(fixture, 'produce-voice', 'piper:en_GB-aru-medium#3');
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const afterRefresh = [
      el<HTMLTextAreaElement>(fixture, 'produce-brief')!.value,
      text(fixture, 'produce-brief-count'),
      el<HTMLSelectElement>(fixture, 'produce-motion')!.value,
    ];
    await click(fixture, 'produce-confirm');

    expect(motionOptions).toEqual([
      ['still', 'Still'],
      ['gentle', 'Gentle (recommended)'],
      ['dynamic', 'Dynamic'],
    ]);
    expect(opened).toEqual(['calm', '4 / 1000', 'gentle']);
    expect(afterRefresh).toEqual(['  Chalk sketches on a dark slate  ', '30 / 1000', 'dynamic']);
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [
        {
          id: 'p1',
          narrationVoice: 'piper:en_GB-aru-medium#3',
          direction: 'Chalk sketches on a dark slate',
          motion: 'dynamic',
          presenter: '',
        },
      ],
    ]);
  });

  it('Delegate sends the brief and the motion with the voice', async () => {
    const briefed = project({ direction: 'Watercolour diagrams', motion: 'still' });
    const bridge = makeBridge({
      ...listing(briefed),
      operatorMandate: vi.fn(async () => ({ mandate })),
      operatorReadiness: vi.fn(async () => fullReadiness),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'delegate');
    const opened = [
      el<HTMLTextAreaElement>(fixture, 'delegate-brief')!.value,
      text(fixture, 'delegate-brief-count'),
      el<HTMLSelectElement>(fixture, 'delegate-motion')!.value,
    ];
    type(fixture, 'delegate-brief', 'Watercolour diagrams, slow pans');
    choose(fixture, 'delegate-motion', 'dynamic');
    choose(fixture, 'delegate-voice', 'OpenAI:nova');
    const counted = text(fixture, 'delegate-brief-count');
    await click(fixture, 'delegate-confirm');

    expect(opened).toEqual(['Watercolour diagrams', '20 / 1000', 'still']);
    expect(counted).toBe('31 / 1000');
    expect(bridge.operatorDelegate.mock.calls).toEqual([
      [
        {
          id: 'p1',
          narrationVoice: 'OpenAI:nova',
          direction: 'Watercolour diagrams, slow pans',
          motion: 'dynamic',
          presenter: '',
        },
      ],
    ]);
  });

  it("the direction is prefilled from the project, else from the mandate's voice and style", async () => {
    const briefed = project({ claims: [], direction: 'Neon terminal glyphs', motion: 'dynamic' });
    const unbriefed = project({ id: 'p2', claims: [] });
    const bridge = makeBridge({
      ...listing(briefed, unbriefed),
      operatorMandate: vi.fn(async () => ({ mandate })),
    });
    const fixture = await mount(bridge);
    const opened = async (button: string, brief: string, motion: string) => {
      await click(fixture, button);
      return [
        el<HTMLTextAreaElement>(fixture, brief)!.value,
        el<HTMLSelectElement>(fixture, motion)!.value,
      ];
    };

    await click(fixture, 'project-p1');
    const produceBriefed = await opened('produce', 'produce-brief', 'produce-motion');
    const delegateBriefed = await opened('delegate', 'delegate-brief', 'delegate-motion');
    await click(fixture, 'project-p2');
    const produceUnbriefed = await opened('produce', 'produce-brief', 'produce-motion');
    const delegateUnbriefed = await opened('delegate', 'delegate-brief', 'delegate-motion');

    expect([produceBriefed, delegateBriefed]).toEqual([
      ['Neon terminal glyphs', 'dynamic'],
      ['Neon terminal glyphs', 'dynamic'],
    ]);
    expect([produceUnbriefed, delegateUnbriefed]).toEqual([
      ['calm', 'gentle'],
      ['calm', 'gentle'],
    ]);
  });

  it('the detail shows the motion and the direction', async () => {
    const briefed = project({ direction: 'Chalk on slate', motion: 'still' });
    const unbriefed = project({ id: 'p2' });
    const fixture = await mount(makeBridge(listing(briefed, unbriefed)));

    await click(fixture, 'project-p1');
    const shown = [text(fixture, 'project-visual'), text(fixture, 'project-direction')];
    await click(fixture, 'project-p2');

    expect(shown).toEqual(['Visual: Still motion', 'Direction: Chalk on slate']);
    expect(text(fixture, 'project-visual')).toBe('Visual: Gentle motion');
    expect(el(fixture, 'project-direction')).toBeNull();
  });

  it('the Delegate dialog of an own video project asks for no voice or motion', async () => {
    const { bridge, fixture } = await mountDecided(false, false, 'ChatGptPlan', false, 'own_video');
    await click(fixture, 'delegate');
    const dialog = [
      el(fixture, 'delegate-voice'),
      el(fixture, 'delegate-voice-preview'),
      el(fixture, 'delegate-motion'),
      el(fixture, 'delegate-warning'),
      el(fixture, 'delegate-brief') !== null,
    ];

    await click(fixture, 'delegate-confirm');

    expect(dialog).toEqual([null, null, null, null, true]);
    expect(bridge.operatorDelegate.mock.calls).toEqual([
      [{ id: 'p1', narrationVoice: '', direction: '', motion: 'gentle', presenter: '' }],
    ]);
  });
  function character(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
    return project({
      format: 'animated_character',
      presenter: 'A friendly robot in a workshop',
      claims: [{ ...project().claims[0]!, status: 'supported' }],
      ...overrides,
    });
  }

  it('the Produce panel and the Delegate dialog carry the presenter description of an animated character project', async () => {
    const animated = character();
    const generated = project({
      id: 'p2',
      claims: [{ ...project().claims[0]!, status: 'supported' }],
    });
    const bridge = makeBridge({
      ...listing(animated, generated),
      operatorReadiness: vi.fn(async () => fullReadiness),
      operatorProduce: vi.fn(async () => animated),
      operatorDelegate: vi.fn(async () => animated),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const generatedPanel = await (async () => {
      await click(fixture, 'produce');
      return el(fixture, 'produce-presenter');
    })();
    await click(fixture, 'project-p1');
    const detail = [text(fixture, 'project-format'), text(fixture, 'project-presenter')];

    await click(fixture, 'produce');
    const prefilled = el<HTMLTextAreaElement>(fixture, 'produce-presenter')!.value;
    type(fixture, 'produce-presenter', ' A cartoon owl on a bookshelf ');
    await settle(fixture);
    const count = text(fixture, 'produce-presenter-count');
    await click(fixture, 'produce-confirm');
    await click(fixture, 'delegate');
    const delegatePrefilled = el<HTMLTextAreaElement>(fixture, 'delegate-presenter')!.value;
    type(fixture, 'delegate-presenter', '  A paper fox in a forest  ');
    await settle(fixture);
    await click(fixture, 'delegate-confirm');

    expect(generatedPanel).toBeNull();
    expect(detail).toEqual(['Animated character', 'Presenter: A friendly robot in a workshop']);
    expect([prefilled, count, delegatePrefilled]).toEqual([
      'A friendly robot in a workshop',
      '28 / 300',
      'A friendly robot in a workshop',
    ]);
    expect(bridge.operatorProduce.mock.calls).toEqual([
      [
        {
          id: 'p1',
          narrationVoice: '',
          direction: '',
          motion: 'gentle',
          presenter: 'A cartoon owl on a bookshelf',
        },
      ],
    ]);
    expect(bridge.operatorDelegate.mock.calls).toEqual([
      [
        {
          id: 'p1',
          narrationVoice: '',
          direction: '',
          motion: 'gentle',
          presenter: 'A paper fox in a forest',
        },
      ],
    ]);
  });

  it('the Presenter block attaches and replaces the presenter picture, and Generate takes the place of Regenerate picture', async () => {
    const attached = character({ presenterFile: 'owner-presenter-0a1b2c3d4e5f.png' });
    let refuse = false;
    const bridge = makeBridge({
      ...listing(character(), character({ id: 'p2', status: 'queued', script: null })),
      operatorReadiness: vi.fn(async () => fullReadiness),
      operatorAttachSceneMedia: vi.fn(async () => {
        if (refuse) throw refusedWith('scout:operator.sceneMedia', 'Pictures are at most 20 MB.');
        return { project: attached as OperatorProjectDto | null };
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const queued = [text(fixture, 'presenter-empty'), el(fixture, 'presenter-attach')];
    await click(fixture, 'project-p1');
    const before = [
      text(fixture, 'presenter-empty'),
      text(fixture, 'presenter-attach'),
      el(fixture, 'presenter-image'),
      el(fixture, 'background') !== null,
    ];

    await click(fixture, 'presenter-attach');
    const image = el<HTMLImageElement>(fixture, 'presenter-image')!;
    const after = [
      image.getAttribute('src'),
      image.getAttribute('alt'),
      text(fixture, 'presenter-attach'),
      el(fixture, 'presenter-empty'),
    ];
    refuse = true;
    await click(fixture, 'presenter-attach');
    const refused = [text(fixture, 'presenter-error'), el(fixture, 'background-error')];
    const buttons = [el(fixture, 'presenter-regenerate'), text(fixture, 'presenter-generate')];

    expect(queued).toEqual([
      'No presenter picture yet: Scout makes one when it produces the video if the connected AI Source makes pictures; or attach your own.',
      null,
    ]);
    expect(before).toEqual([
      'No presenter picture yet: Scout makes one when it produces the video if the connected AI Source makes pictures; or attach your own.',
      'Attach picture',
      null,
      true,
    ]);
    expect(after).toEqual([
      'scout-media://p1/owner-presenter-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
      'Presenter of the video',
      'Replace picture',
      null,
    ]);
    expect(refused).toEqual(['Pictures are at most 20 MB.', null]);
    expect(bridge.operatorAttachSceneMedia.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: '', kind: 'presenter' }],
      [{ projectId: 'p1', sceneId: '', kind: 'presenter' }],
    ]);
    expect(buttons).toEqual([null, 'Generate']);
    expect(bridge.operatorRegeneratePresenter).not.toHaveBeenCalled();
  });

  it('Generate and Ask for a change are disabled with their reason while the connected AI Source makes no pictures', async () => {
    const bridge = makeBridge({
      ...listing(character()),
      operatorReadiness: vi.fn(async () => ({ ...fullReadiness, imagesAvailable: false })),
      operatorRequestPresenter: vi.fn(async () => character()),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    type(fixture, 'presenter-feedback', 'Older');
    await settle(fixture);
    const generate = el<HTMLButtonElement>(fixture, 'presenter-generate')!;
    const change = el<HTMLButtonElement>(fixture, 'presenter-change')!;

    await click(fixture, 'presenter-generate');
    await click(fixture, 'presenter-change');

    expect([generate.disabled, generate.getAttribute('title')]).toEqual([
      true,
      'The connected AI Source makes no pictures',
    ]);
    expect([change.disabled, change.getAttribute('title')]).toEqual([
      true,
      'The connected AI Source makes no pictures',
    ]);
    expect(bridge.operatorRequestPresenter).not.toHaveBeenCalled();
  });

  it('dragging a box over the presenter picture and saving sends it as fractions of the picture', async () => {
    const marked = character({
      presenterFile: 'owner-presenter-0a1b2c3d4e5f.png',
      mouthBox: { x: 0.1, y: 0.2, width: 0.3, height: 0.05 },
    });
    const saved = { ...marked, mouthBox: { x: 0.4, y: 0.6, width: 0.2, height: 0.1 } };
    const bridge = makeBridge({
      ...listing(marked),
      operatorSetMouthBox: vi.fn(async () => saved),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    vi.spyOn(el(fixture, 'presenter-image')!, 'getBoundingClientRect').mockReturnValue(
      new DOMRect(100, 50, 320, 160),
    );
    const current = boxStyle(fixture, 'mouth-current');
    const hint = text(fixture, 'mouth-hint');
    const saveBeforeDrawing = el<HTMLButtonElement>(fixture, 'mouth-save')!.disabled;

    pointer(fixture, 'pointerdown', 228, 146);
    pointer(fixture, 'pointermove', 236, 148);
    await settle(fixture);
    const tooSmall = el<HTMLButtonElement>(fixture, 'mouth-save')!.disabled;
    await click(fixture, 'mouth-clear');
    const cleared = el(fixture, 'mouth-drawn');
    pointer(fixture, 'pointerdown', 490, 260);
    pointer(fixture, 'pointerup', 260, 180);
    await settle(fixture);
    const clamped = boxStyle(fixture, 'mouth-drawn');
    pointer(fixture, 'pointerdown', 228, 146);
    pointer(fixture, 'pointermove', 280, 154);
    pointer(fixture, 'pointerup', 292, 162);
    pointer(fixture, 'pointermove', 400, 200);
    await settle(fixture);
    const drawn = boxStyle(fixture, 'mouth-drawn');
    await click(fixture, 'mouth-save');

    expect(current).toEqual(['10%', '20%', '30%', '5%']);
    expect(hint).toBe('Drag a box around the mouth so the character speaks.');
    expect([saveBeforeDrawing, tooSmall, cleared]).toEqual([true, true, null]);
    expect(clamped).toEqual(['50%', '81.3%', '50%', '18.7%']);
    expect(drawn).toEqual(['40%', '60%', '20%', '10%']);
    expect(bridge.operatorSetMouthBox.mock.calls).toEqual([
      [{ projectId: 'p1', mouthBox: { x: 0.4, y: 0.6, width: 0.2, height: 0.1 } }],
    ]);
    expect([el(fixture, 'mouth-drawn'), boxStyle(fixture, 'mouth-current')]).toEqual([
      null,
      ['40%', '60%', '20%', '10%'],
    ]);
  });

  it('a mouth drag released outside the picture ends there, clamped to the picture, and a pointercancel ends it with the box of the last move, so a later move without a button changes nothing', async () => {
    const fixture = await mount(
      makeBridge(listing(character({ presenterFile: 'owner-presenter-0a1b2c3d4e5f.png' }))),
    );
    await click(fixture, 'project-p1');
    vi.spyOn(el(fixture, 'presenter-image')!, 'getBoundingClientRect').mockReturnValue(
      new DOMRect(100, 50, 320, 160),
    );
    const outside = fixture.nativeElement.ownerDocument.body as HTMLElement;

    pointer(fixture, 'pointerdown', 228, 146);
    pointer(fixture, 'pointermove', 292, 162);
    pointer(fixture, 'pointerup', 600, 400, outside);
    pointer(fixture, 'pointermove', 260, 180);
    await settle(fixture);
    const released = boxStyle(fixture, 'mouth-drawn');
    pointer(fixture, 'pointerdown', 228, 146);
    pointer(fixture, 'pointermove', 292, 162);
    await settle(fixture);
    const lastMove = boxStyle(fixture, 'mouth-drawn');
    pointer(fixture, 'pointercancel', 0, 0, outside);
    await settle(fixture);
    const cancelled = boxStyle(fixture, 'mouth-drawn');
    pointer(fixture, 'pointermove', 400, 200);
    await settle(fixture);
    const afterCancel = boxStyle(fixture, 'mouth-drawn');

    expect(released).toEqual(['40%', '60%', '60%', '40%']);
    expect(lastMove).toEqual(['40%', '60%', '20%', '10%']);
    expect([cancelled, afterCancel]).toEqual([lastMove, lastMove]);
  });

  it('an animated character scene has the narration row, Edit text, its picture prompt, Regenerate scene, Regenerate picture and Attach image, and shows the presenter over the background while it has no picture of its own', async () => {
    const spoken = scene({
      narration: 'Span is a view over memory. It never allocates.',
      visualSource: 'presenter',
      visualFile: null,
    });
    const withPicture = character({
      presenterFile: 'owner-presenter-0a1b2c3d4e5f.png',
      backgroundFile: 'owner-background-0a1b2c3d4e5f.jpg',
      scenes: [spoken],
    });
    const withoutPicture = character({ id: 'p2', scenes: [spoken] });
    const bridge = makeBridge({
      ...listing(withPicture, withoutPicture),
      operatorEditSceneText: vi.fn(async () => withPicture),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const controls = [
      el(fixture, 'scene-audio-row-s1') !== null,
      el(fixture, 'scene-audio-s1')!.getAttribute('src'),
      text(fixture, 'scene-narration-regenerate-s1'),
      text(fixture, 'scene-narration-replace-s1'),
      text(fixture, 'scene-lock-s1'),
      el(fixture, 'scene-up-s1')!.getAttribute('aria-label'),
      text(fixture, 'scene-edit-s1'),
      text(fixture, 'scene-regenerate-s1'),
      text(fixture, 'scene-picture-regenerate-s1'),
      text(fixture, 'scene-attach-visual-s1'),
      text(fixture, 'scene-sources-s1'),
      text(fixture, 'scene-picture-prompt-line-s1'),
    ];
    const preview = [
      el(fixture, 'scene-preview-presenter-s1')!.getAttribute('src'),
      el(fixture, 'scene-preview-background-s1')!.getAttribute('src'),
      el(fixture, 'scene-preview-text-s1'),
    ];
    await click(fixture, 'scene-edit-s1');
    type(fixture, 'scene-text-s1', ' Span is a window over memory. ');
    await settle(fixture);
    await click(fixture, 'scene-text-save-s1');
    await click(fixture, 'project-p2');

    expect(controls).toEqual([
      true,
      'scout-media://p1/scene-s1.mp3?v=2026-11-03T09%3A05%3A00Z',
      'Regenerate narration',
      'Replace narration',
      'Lock',
      'Move scene 1 up',
      'Edit text',
      'Regenerate scene',
      'New picture',
      'Attach image',
      'Presenter picture · narration: generated',
      'Picture prompt: A ruler laid over a row of bytes',
    ]);
    expect(preview).toEqual([
      'scout-media://p1/owner-presenter-0a1b2c3d4e5f.png?v=2026-11-03T09%3A05%3A00Z',
      'scout-media://p1/owner-background-0a1b2c3d4e5f.jpg?v=2026-11-03T09%3A05%3A00Z',
      null,
    ]);
    expect(bridge.operatorEditSceneText.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', text: 'Span is a window over memory.' }],
    ]);
    expect([
      el(fixture, 'scene-preview-presenter-s1'),
      text(fixture, 'scene-preview-text-s1'),
    ]).toEqual([null, 'Span is a view over memory.']);
  });

  it('the Produce warning of an animated character project names a silent character or a missing picture', async () => {
    const warningOf = async (
      speech: boolean,
      images: boolean,
      presenterFile: string | null,
    ): Promise<string | null> => {
      TestBed.resetTestingModule();
      const fixture = await mount(
        makeBridge({
          ...listing(character({ presenterFile })),
          operatorReadiness: vi.fn(async () => ({
            ...readiness,
            provider: 'openai',
            speechAvailable: speech,
            imagesAvailable: images,
          })),
        }),
      );
      await click(fixture, 'project-p1');
      await click(fixture, 'produce');
      const produce = el(fixture, 'produce-warning')?.textContent?.trim() ?? null;
      await click(fixture, 'delegate');
      const delegate = el(fixture, 'delegate-warning')?.textContent?.trim() ?? null;
      return produce === delegate ? produce : `${produce} | ${delegate}`;
    };

    const warnings = [
      await warningOf(false, true, null),
      await warningOf(false, false, 'owner-presenter-0a1b2c3d4e5f.png'),
      await warningOf(true, false, null),
      await warningOf(true, false, 'owner-presenter-0a1b2c3d4e5f.png'),
      await warningOf(true, true, null),
    ];

    const silent =
      'openai has no speech service in Scout: the character will be silent. Replace the narration on each scene afterwards, or connect an AI Source with speech first.';
    expect(warnings).toEqual([
      silent,
      silent,
      'openai has no image service in Scout: attach a presenter picture, or every scene will be a text card.',
      null,
      null,
    ]);
  });

  it("the project's error reads as a failure only on a failed project, and as a plain note on any other status", async () => {
    const note =
      'The project has no presenter picture; attach one in Scout Desktop, Production, or connect an AI Source with pictures.';
    const fixture = await mount(
      makeBridge(
        listing(
          character({ status: 'media_ready', error: note }),
          character({ id: 'p2', status: 'failed', error: 'The AI model did not answer.' }),
        ),
      ),
    );

    await click(fixture, 'project-p1');
    const mediaReady = [text(fixture, 'project-note'), el(fixture, 'project-failure')];
    await click(fixture, 'project-p2');
    const failed = [el(fixture, 'project-note'), text(fixture, 'project-failure')];

    expect(mediaReady).toEqual([note, null]);
    expect(failed).toEqual([null, 'The AI model did not answer.']);
  });
  function versioned(overrides: Partial<OperatorProjectDto> = {}): OperatorProjectDto {
    return character({
      presenterFile: 'presenter-2.png',
      presenterSource: 'generated',
      presenterVersions: [
        {
          id: 'version-1',
          file: 'presenter-1.png',
          prompt: 'A friendly robot in a workshop. flat 2D cartoon illustration',
          feedback: '',
          previewFile: 'presenter-preview-1.mp4',
          mouthBox: null,
          madeAt: '2026-11-05T10:00:00Z',
          source: 'generated',
        },
        {
          id: 'version-2',
          file: 'presenter-2.png',
          prompt: 'A tall robot with round glasses in a workshop, flat cartoon',
          feedback:
            'Give the robot round glasses and make it a lot taller than the workbench beside it',
          previewFile: 'presenter-preview-2.mp4',
          mouthBox: { x: 0.43, y: 0.62, width: 0.14, height: 0.07 },
          madeAt: '2026-11-05T10:10:00Z',
          source: 'generated',
        },
      ],
      presenterPreviewFile: 'presenter-preview-2.mp4',
      ...overrides,
    });
  }

  it('the Presenter block shows the prompt of the current version, lets the owner edit it and sends it with Generate, then shows the prefilled prompt again', async () => {
    const fresh = character({
      id: 'p2',
      direction: 'Warm colours, a calm tone.',
      presenter: '',
      topic: 'Span<T> in C#',
    });
    const bridge = makeBridge({
      ...listing(versioned(), fresh),
      operatorReadiness: vi.fn(async () => fullReadiness),
      operatorRequestPresenter: vi.fn(async () => versioned({ presenterRequest: 'edited' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p2');
    const composed = el<HTMLTextAreaElement>(fixture, 'presenter-prompt')!.value;
    const safety = text(fixture, 'presenter-safety');
    await click(fixture, 'project-p1');
    const current = el<HTMLTextAreaElement>(fixture, 'presenter-prompt')!.value;

    type(fixture, 'presenter-prompt', '  A tall robot with square glasses, flat cartoon  ');
    await settle(fixture);
    const count = text(fixture, 'presenter-prompt-count');
    await click(fixture, 'presenter-generate');
    const afterGenerate = el<HTMLTextAreaElement>(fixture, 'presenter-prompt')!.value;
    type(fixture, 'presenter-prompt', 'p'.repeat(1501));
    await settle(fixture);
    const overLimit = el<HTMLButtonElement>(fixture, 'presenter-generate')!.disabled;

    expect(composed).toBe(
      'Warm colours, a calm tone. Span<T> in C#. flat 2D cartoon illustration, front-facing, looking at the viewer, mouth closed, neutral expression, head and shoulders, on a solid bright green background (#00FF00).',
    );
    expect(safety).toBe(
      "no text, no letters, no logos, no trademarks, no real person's likeness and no public figure.",
    );
    expect(current).toBe('A tall robot with round glasses in a workshop, flat cartoon');
    expect(count).toBe('46 / 1500');
    expect(bridge.operatorRequestPresenter.mock.calls).toEqual([
      [
        {
          projectId: 'p1',
          feedback: '',
          prompt: 'A tall robot with square glasses, flat cartoon',
        },
      ],
    ]);
    expect(afterGenerate).toBe('A tall robot with round glasses in a workshop, flat cartoon');
    expect(overLimit).toBe(true);
  });

  it('a change request sends the feedback and the versions strip makes an older version current', async () => {
    const older = versioned({
      presenterFile: 'presenter-1.png',
      presenterPreviewFile: 'presenter-preview-1.mp4',
    });
    const bridge = makeBridge({
      ...listing(versioned()),
      operatorReadiness: vi.fn(async () => ({ ...fullReadiness, picturesLeftToday: 37 })),
      operatorRequestPresenter: vi.fn(async () => versioned({ presenterRequest: 'Older' })),
      operatorUsePresenterVersion: vi.fn(async () => older),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const changeAtFirst = el<HTMLButtonElement>(fixture, 'presenter-change')!.disabled;
    type(fixture, 'presenter-feedback', 'f'.repeat(301));
    await settle(fixture);
    const overLimit = [
      text(fixture, 'presenter-feedback-count'),
      el<HTMLButtonElement>(fixture, 'presenter-change')!.disabled,
    ];
    type(fixture, 'presenter-feedback', '  Make the robot look older  ');
    await settle(fixture);
    await click(fixture, 'presenter-change');
    const feedbackAfter = el<HTMLTextAreaElement>(fixture, 'presenter-feedback')!.value;
    const strip = [0, 1].map((i) => [
      el<HTMLImageElement>(fixture, `presenter-version-${i}`)!
        .querySelector('img')!
        .getAttribute('src'),
      text(fixture, `presenter-version-label-${i}`),
      text(fixture, `presenter-version-summary-${i}`),
      el(fixture, `presenter-current-${i}`) !== null,
      el(fixture, `presenter-use-${i}`) !== null,
    ]);
    const header = text(fixture, 'presenter-header');
    await click(fixture, 'presenter-use-0');

    expect(changeAtFirst).toBe(true);
    expect(overLimit).toEqual(['301 / 300', true]);
    expect(bridge.operatorRequestPresenter.mock.calls).toEqual([
      [{ projectId: 'p1', feedback: 'Make the robot look older', prompt: '' }],
    ]);
    expect(feedbackAfter).toBe('');
    expect(strip).toEqual([
      [
        'scout-media://p1/presenter-1.png?v=2026-11-05T10%3A00%3A00Z',
        '1 · Generated',
        'A friendly robot in a workshop. flat 2D cartoon illustration',
        false,
        true,
      ],
      [
        'scout-media://p1/presenter-2.png?v=2026-11-05T10%3A10%3A00Z',
        '2 · Generated',
        'Give the robot round glasses and make it a lot taller than t',
        true,
        false,
      ],
    ]);
    expect(header).toBe('Version 2 of 2 · Generated · 37 of 40 pictures left today');
    expect(bridge.operatorUsePresenterVersion.mock.calls).toEqual([
      [{ projectId: 'p1', versionId: 'version-1' }],
    ]);
    expect([
      el(fixture, 'presenter-current-0') !== null,
      el(fixture, 'presenter-use-1') !== null,
    ]).toEqual([true, true]);
  });

  it('the current presenter preview plays through scout-media and the block says when Scout is making it', async () => {
    const bridge = makeBridge(
      listing(versioned(), versioned({ id: 'p2', status: 'presenting' }), character({ id: 'p3' })),
    );
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const preview = el<HTMLVideoElement>(fixture, 'presenter-preview')!;
    const ready = [
      preview.tagName,
      preview.hasAttribute('controls'),
      preview.getAttribute('src'),
      el(fixture, 'presenter-making'),
      el(fixture, 'presenting-progress'),
    ];
    await click(fixture, 'project-p2');
    const making = [
      text(fixture, 'presenter-making'),
      text(fixture, 'presenting-progress'),
      el(fixture, 'presenter-preview'),
      el(fixture, 'presenter-generate'),
    ];
    await click(fixture, 'project-p3');
    const pending = [text(fixture, 'presenter-pending'), el(fixture, 'presenter-preview')];

    expect(ready).toEqual([
      'VIDEO',
      true,
      'scout-media://p1/presenter-preview-2.mp4?v=2026-11-05T10%3A10%3A00Z',
      null,
      null,
    ]);
    expect(making).toEqual(['Scout is making the presenter.', 'Making the presenter.', null, null]);
    expect(pending).toEqual(['The presenter is made as soon as the script is ready.', null]);
  });

  it('a built-in presenter is chosen from the readiness list', async () => {
    const bridge = makeBridge({
      ...listing(character()),
      operatorReadiness: vi.fn(async () => ({
        ...fullReadiness,
        builtinPresenters: [
          { id: 'ada', label: 'Ada' },
          { id: 'kofi', label: 'Kofi' },
        ],
      })),
      operatorRequestPresenter: vi.fn(async () => character({ presenterRequest: 'builtin:kofi' })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const select = el<HTMLSelectElement>(fixture, 'presenter-builtin')!;
    const options = Array.from(select.options).map((o) => [o.value, o.textContent?.trim()]);

    select.value = 'kofi';
    select.dispatchEvent(new Event('change'));
    await click(fixture, 'presenter-builtin-use');

    TestBed.resetTestingModule();
    const without = await mount(makeBridge(listing(character())));
    await click(without, 'project-p1');

    expect(options).toEqual([
      ['ada', 'Ada'],
      ['kofi', 'Kofi'],
    ]);
    expect(bridge.operatorRequestPresenter.mock.calls).toEqual([
      [{ projectId: 'p1', feedback: 'builtin:kofi', prompt: '' }],
    ]);
    expect([el(without, 'presenter-builtin'), el(without, 'presenter-builtin-use')]).toEqual([
      null,
      null,
    ]);
  });

  it('a scene picture prompt is edited and saved and Regenerate picture sends the scene', async () => {
    const pictured = scene({ visualSource: 'generated', visualFile: 'scene-s1.png' });
    const generated = project({ scenes: [pictured] });
    const animated = character({
      id: 'p2',
      presenterFile: 'presenter-1.png',
      scenes: [pictured],
    });
    const own = project({ id: 'p3', format: 'own_pictures', scenes: [scene()] });
    const bridge = makeBridge({
      ...listing(generated, animated, own),
      operatorEditScenePicturePrompt: vi.fn(async () =>
        project({ scenes: [{ ...pictured, visualPrompt: 'A river seen from above' }] }),
      ),
      operatorRegenerateScenePicture: vi.fn(async () =>
        project({ status: 'production_queued', scenes: [pictured] }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const folded = [
      text(fixture, 'scene-picture-prompt-line-s1'),
      el(fixture, 'scene-picture-prompt-s1'),
    ];
    await click(fixture, 'scene-picture-prompt-edit-s1');
    const unchanged = el<HTMLButtonElement>(fixture, 'scene-picture-prompt-save-s1')!.disabled;
    type(fixture, 'scene-picture-prompt-s1', '   ');
    await settle(fixture);
    const empty = [
      text(fixture, 'scene-picture-prompt-invalid-s1'),
      el<HTMLButtonElement>(fixture, 'scene-picture-prompt-save-s1')!.disabled,
    ];
    type(fixture, 'scene-picture-prompt-s1', 'q'.repeat(701));
    await settle(fixture);
    const tooLong = el<HTMLButtonElement>(fixture, 'scene-picture-prompt-save-s1')!.disabled;
    await click(fixture, 'scene-picture-prompt-cancel-s1');
    const cancelled = [
      el(fixture, 'scene-picture-prompt-s1'),
      text(fixture, 'scene-picture-prompt-line-s1'),
    ];
    await click(fixture, 'scene-picture-prompt-edit-s1');
    type(fixture, 'scene-picture-prompt-s1', '  A river seen from above  ');
    await settle(fixture);
    await click(fixture, 'scene-picture-prompt-save-s1');
    const saved = [
      el(fixture, 'scene-picture-prompt-s1'),
      text(fixture, 'scene-picture-prompt-line-s1'),
    ];
    await click(fixture, 'scene-picture-regenerate-s1');
    await click(fixture, 'project-p2');
    const animatedPreview = [
      el(fixture, 'scene-image-s1')!.getAttribute('src'),
      el(fixture, 'scene-presenter-badge-s1')!.getAttribute('src'),
      el(fixture, 'scene-picture-prompt-edit-s1') !== null,
    ];
    await click(fixture, 'project-p3');
    const ownPictures = [
      el(fixture, 'scene-picture-prompt-edit-s1'),
      el(fixture, 'scene-picture-regenerate-s1'),
      text(fixture, 'scene-s1').includes('Picture prompt: A ruler laid over a row of bytes'),
    ];

    expect(folded).toEqual(['Picture prompt: A ruler laid over a row of bytes', null]);
    expect(unchanged).toBe(true);
    expect(empty).toEqual(['A picture prompt is 1 to 700 characters.', true]);
    expect(tooLong).toBe(true);
    expect(cancelled).toEqual([null, 'Picture prompt: A ruler laid over a row of bytes']);
    expect(bridge.operatorEditScenePicturePrompt.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1', prompt: 'A river seen from above' }],
    ]);
    expect(saved).toEqual([null, 'Picture prompt: A river seen from above']);
    expect(bridge.operatorRegenerateScenePicture.mock.calls).toEqual([
      [{ projectId: 'p1', sceneId: 's1' }],
    ]);
    expect(animatedPreview).toEqual([
      'scout-media://p2/scene-s1.png?v=2026-11-03T09%3A05%3A00Z',
      'scout-media://p2/presenter-1.png?v=2026-11-03T09%3A05%3A00Z',
      true,
    ]);
    expect(ownPictures).toEqual([null, null, true]);
  });

  it('the presenter prompt, the change request, the chosen built-in presenter and a scene picture prompt the owner types survive the 10 s refresh and are dropped when the owner chooses another project', async () => {
    vi.useFakeTimers();
    const typed = versioned({ scenes: [scene()] });
    const bridge = makeBridge({
      ...listing(typed, character({ id: 'p2' })),
      operatorReadiness: vi.fn(async () => ({
        ...fullReadiness,
        builtinPresenters: [
          { id: 'ada', label: 'Ada' },
          { id: 'kofi', label: 'Kofi' },
        ],
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const drafts = () => [
      el<HTMLTextAreaElement>(fixture, 'presenter-prompt')!.value,
      el<HTMLTextAreaElement>(fixture, 'presenter-feedback')!.value,
      el<HTMLSelectElement>(fixture, 'presenter-builtin')!.value,
      el<HTMLTextAreaElement>(fixture, 'scene-picture-prompt-s1')?.value ?? null,
    ];
    await click(fixture, 'scene-picture-prompt-edit-s1');
    type(fixture, 'presenter-prompt', 'A robot with a red scarf');
    type(fixture, 'presenter-feedback', 'Make the robot smile');
    choose(fixture, 'presenter-builtin', 'kofi');
    type(fixture, 'scene-picture-prompt-s1', 'A river seen from above');
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const afterRefresh = drafts();
    await click(fixture, 'project-p2');
    await click(fixture, 'project-p1');
    const afterChoosing = drafts();

    expect(afterRefresh).toEqual([
      'A robot with a red scarf',
      'Make the robot smile',
      'kofi',
      'A river seen from above',
    ]);
    expect(afterChoosing).toEqual([
      'A tall robot with round glasses in a workshop, flat cartoon',
      '',
      'ada',
      null,
    ]);
  });

  it('the Mandate default format preselects the New project radio, falls back to Generated pictures while the machine cannot make it, and is saved with the mandate', async () => {
    const preselected = async (
      defaultFormat: MandateDto['defaultFormat'],
      lipSyncAvailable: boolean,
    ): Promise<string | undefined> => {
      TestBed.resetTestingModule();
      const fixture = await mount(
        makeBridge({
          operatorMandate: vi.fn(async () => ({ mandate: { ...mandate, defaultFormat } })),
          operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable })),
        }),
      );
      return (
        fixture.nativeElement.querySelector(
          'input[name="start-format"]:checked',
        ) as HTMLInputElement | null
      )?.dataset['test'];
    };

    const formats = [
      await preselected('animated_character', true),
      await preselected('own_pictures', false),
      await preselected('animated_character', false),
      await preselected('own_video', false),
      await preselected('', false),
    ];
    TestBed.resetTestingModule();
    const save = vi.fn(async (_req: MandateSaveRequest) => ({
      ...mandate,
      defaultFormat: 'animated_character' as const,
    }));
    const bridge = makeBridge({
      operatorMandate: vi.fn(async () => ({
        mandate: { ...mandate, defaultFormat: 'own_pictures' as const },
      })),
      operatorSaveMandate: save,
      operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable: true })),
    });
    const fixture = await mount(bridge);
    const select = el<HTMLSelectElement>(fixture, 'mandate-default-format')!;
    const options = Array.from(select.options).map((o) => [o.value, o.textContent?.trim()]);
    const shown = select.value;
    select.value = 'animated_character';
    select.dispatchEvent(new Event('change'));
    await click(fixture, 'mandate-save');
    type(fixture, 'topic', 'Records in C#');
    el<HTMLInputElement>(fixture, 'format-generated_pictures')!.click();
    await settle(fixture);
    await click(fixture, 'start-project');
    const afterStart = (
      fixture.nativeElement.querySelector('input[name="start-format"]:checked') as HTMLInputElement
    ).dataset['test'];

    expect(formats).toEqual([
      'format-animated_character',
      'format-own_pictures',
      'format-generated_pictures',
      'format-generated_pictures',
      'format-generated_pictures',
    ]);
    expect(options).toEqual([
      ['generated_pictures', 'Generated pictures'],
      ['own_pictures', 'Your own pictures'],
      ['animated_character', 'Animated character'],
      ['own_video', 'Your own video'],
    ]);
    expect(shown).toBe('own_pictures');
    expect(save.mock.calls.map(([sent]) => sent.defaultFormat)).toEqual(['animated_character']);
    expect(
      (bridge.operatorStartProject.mock.calls as unknown as [{ format: string }][]).map(
        ([req]) => req.format,
      ),
    ).toEqual(['generated_pictures']);
    expect(afterStart).toBe('format-animated_character');
  });

  it('Channel holds the Mandate and What Scout can make, open while no mandate is saved and folded once one is, with the saved date, the default format and what Scout can make on one line', async () => {
    const able = { ...fullReadiness, lipSyncAvailable: true };
    const bridge = makeBridge({
      operatorReadiness: vi.fn(async () => able),
      operatorSaveMandate: vi.fn(async () => ({
        ...mandate,
        defaultFormat: 'own_pictures' as const,
      })),
    });
    const fixture = await mount(bridge);
    const channel = el<HTMLDetailsElement>(fixture, 'channel')!;
    const before = [
      channel.open,
      text(fixture, 'channel-summary'),
      channel.contains(el(fixture, 'mandate')),
      channel.contains(el(fixture, 'readiness')),
    ];
    type(fixture, 'mandate-objective', 'Grow a .NET channel');
    type(fixture, 'mandate-language', 'en');
    await click(fixture, 'mandate-save');
    const saved = [channel.open, text(fixture, 'channel-summary')];
    TestBed.resetTestingModule();
    const reopened = await mount(
      makeBridge({ operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })) }),
    );

    const at = formatDate(mandate.updatedAt!, 'd MMM, HH:mm', 'en-US');
    expect(before).toEqual([
      true,
      'No mandate yet · Can make: narration, generated pictures, animated character',
      true,
      true,
    ]);
    expect(saved).toEqual([
      false,
      `Mandate saved ${at} · Default format: Your own pictures · Can make: narration, generated pictures, animated character`,
    ]);
    expect([
      el<HTMLDetailsElement>(reopened, 'channel')!.open,
      text(reopened, 'channel-summary'),
    ]).toEqual([
      false,
      `Mandate saved ${at} · Default format: Generated pictures · Can make: generated pictures`,
    ]);
  });

  it('Make a video is one row with the topic and Start, and More options folds the format, the direction and the presenter description', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable: true })),
      }),
    );
    el<HTMLInputElement>(fixture, 'format-animated_character')!.click();
    await settle(fixture);
    const options = el<HTMLDetailsElement>(fixture, 'start-options')!;

    expect(
      Array.from(el(fixture, 'topic')!.closest('.make-row')!.children).map(
        (c) =>
          c.querySelector('[data-test]')?.getAttribute('data-test') ?? c.getAttribute('data-test'),
      ),
    ).toEqual(['topic', 'start-project']);
    expect(text(fixture, 'start-project')).toBe('Start');
    expect([options.open, options.querySelector('summary')?.textContent?.trim()]).toEqual([
      false,
      'More options: format and direction',
    ]);
    expect(
      ['format-generated_pictures', 'format-own_video', 'start-direction', 'start-presenter'].map(
        (id) => options.contains(el(fixture, id)),
      ),
    ).toEqual([true, true, true, true]);
  });

  it('the project list and the open project share one wrapping row, the list from 300 px and the detail from 520 px, as the artboard draws them, and the list alone fills it while no project is open', async () => {
    const fixture = await mount(makeBridge(listing(project())));
    const work = el(fixture, 'work')!;
    const closed = [
      work.classList.contains('work--open'),
      Array.from(work.children).map((c) => c.getAttribute('data-test')),
    ];
    await click(fixture, 'project-p1');
    const opened = [
      work.classList.contains('work--open'),
      Array.from(work.children).map((c) => c.getAttribute('data-test')),
    ];

    expect(closed).toEqual([false, ['projects']]);
    expect(opened).toEqual([true, ['projects', 'project-detail']]);
    expect({
      wrap: declared(work, 'flex-wrap'),
      gap: declared(work, 'gap'),
      list: declared(el(fixture, 'projects')!, 'flex'),
      detail: declared(el(fixture, 'project-detail')!, 'flex'),
    }).toEqual({ wrap: 'wrap', gap: '16px', list: '1 1 300px', detail: '2 1 520px' });
  });

  it('the title row carries Let Scout finish it, Delete and the close mark at its right, after the title and its status line', async () => {
    const fixture = await mount(makeBridge(listing(project())));
    await click(fixture, 'project-p1');

    expect(
      Array.from(el(fixture, 'project-title-row')!.children).map(
        (c) =>
          c.getAttribute('data-test') ??
          `${c.firstElementChild?.tagName}: ${c.firstElementChild?.textContent?.trim()}`,
      ),
    ).toEqual(['H2: Span<T> in C#', 'delegate', 'project-title-delete', 'project-close']);
    expect(el(fixture, 'project-close')!.getAttribute('aria-label')).toBe('Close the project');
  });

  it('the close mark and a second click on the open project close the detail', async () => {
    const bridge = makeBridge(listing(project(), project({ id: 'p2', topic: 'Records in C#' })));
    const fixture = await mount(bridge);
    const state = () => [
      el(fixture, 'project-detail') !== null,
      el(fixture, 'project-p1')!.classList.contains('project--selected'),
    ];

    await click(fixture, 'project-p1');
    const opened = state();
    await click(fixture, 'project-close');
    const closedByMark = state();
    await click(fixture, 'project-p1');
    await click(fixture, 'project-p1');
    const closedByList = state();
    await click(fixture, 'project-p1');
    await click(fixture, 'project-p2');
    const switched = [text(fixture, 'project-title-row'), el(fixture, 'project-detail') !== null];

    expect(opened).toEqual([true, true]);
    expect(closedByMark).toEqual([false, false]);
    expect(closedByList).toEqual([false, false]);
    expect(switched).toEqual([
      `Records in C#Script ready for reviewGenerated pictures · updated ${formatDate('2026-11-03T09:05:00Z', 'd MMM, HH:mm', 'en-US')} Let Scout finish it Delete`,
      true,
    ]);
    expect(bridge.operatorProject.mock.calls).toEqual([
      [{ id: 'p1' }],
      [{ id: 'p1' }],
      [{ id: 'p1' }],
      [{ id: 'p2' }],
    ]);
  });

  it('a block the owner unfolded on one project starts folded again on the next one, and stays unfolded through the 10 s refresh', async () => {
    vi.useFakeTimers();
    const fixture = await mount(makeBridge(listing(project(), project({ id: 'p2' }))));
    await click(fixture, 'project-p1');
    el<HTMLDetailsElement>(fixture, 'candidates')!.open = true;
    await vi.advanceTimersByTimeAsync(10_000);
    await settle(fixture);
    const afterRefresh = el<HTMLDetailsElement>(fixture, 'candidates')!.open;
    await click(fixture, 'project-p2');

    expect([afterRefresh, el<HTMLDetailsElement>(fixture, 'candidates')!.open]).toEqual([
      true,
      false,
    ]);
  });

  it('the detail follows the work: Topic candidates, Script, Claims, Presenter, Scenes and Background, with Recording last in an own video project', async () => {
    const fixture = await mount(
      makeBridge(listing(character(), project({ id: 'p2', format: 'own_video' }))),
    );
    const blocks = () =>
      Array.from(el(fixture, 'project-detail')!.querySelectorAll('h3')).map((h) =>
        h.textContent?.trim(),
      );

    await click(fixture, 'project-p1');
    const animated = blocks();
    await click(fixture, 'project-p2');
    const ownVideo = blocks();

    expect(animated).toEqual([
      'Topic candidates',
      'Script',
      'Claims to check',
      'Presenter',
      'Scenes',
      'Background',
    ]);
    expect(ownVideo).toEqual([
      'Topic candidates',
      'Script',
      'Claims to check',
      'Scenes',
      'Recording',
    ]);
  });

  it('Topic candidates fold once the script exists, and Claims stay open while any claim is pending, ignoring a click on their title, and fold once every claim is decided', async () => {
    const bridge = makeBridge({
      ...listing(project(), project({ id: 'p2', script: null, claims: [] })),
      operatorDecideClaim: vi.fn(async () =>
        project({ claims: [{ ...project().claims[0]!, status: 'supported' }] }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const claims = el<HTMLDetailsElement>(fixture, 'claims')!;
    const titleClick = new MouseEvent('click', { bubbles: true, cancelable: true });
    claims.querySelector('summary')!.dispatchEvent(titleClick);
    await settle(fixture);
    const pending = [
      el<HTMLDetailsElement>(fixture, 'candidates')!.open,
      claims.open,
      titleClick.defaultPrevented,
    ];
    await click(fixture, 'supported-c1');
    const decided = [claims.open, el(fixture, 'claims-pending')];
    await click(fixture, 'project-p2');

    expect(pending).toEqual([false, true, true]);
    expect(decided).toEqual([false, null]);
    expect(el<HTMLDetailsElement>(fixture, 'candidates')!.open).toBe(true);
  });

  it('the readiness failure shows on the Channel summary while the details is closed behind a saved mandate, and the message inside stays', async () => {
    const fixture = await mount(
      makeBridge({
        operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })),
        operatorReadiness: vi.fn(async () => {
          throw refusedWith('scout:operator.readiness', 'The readiness check failed.');
        }),
      }),
    );
    const channel = el<HTMLDetailsElement>(fixture, 'channel')!;

    expect([
      channel.open,
      el(fixture, 'channel-summary')!
        .querySelector('[data-test="channel-error"]')
        ?.textContent?.trim(),
      el(fixture, 'readiness-error')?.textContent?.trim(),
    ]).toEqual([false, 'The readiness check failed.', 'The readiness check failed.']);
  });

  it('the own-picture fold starts open only when the project has a presenter picture and no mouth box', async () => {
    const folds = async (project: OperatorProjectDto): Promise<boolean | undefined> => {
      TestBed.resetTestingModule();
      const fixture = await mount(makeBridge(listing(project)));
      await click(fixture, 'project-p1');
      return el<HTMLDetailsElement>(fixture, 'presenter-own')?.open;
    };
    const picture = 'owner-presenter-0a1b2c3d4e5f.png';
    const result = [
      await folds(character({ presenterFile: picture, mouthBox: null })),
      await folds(
        character({
          presenterFile: picture,
          mouthBox: { x: 0.1, y: 0.2, width: 0.3, height: 0.05 },
        }),
      ),
      await folds(character({ presenterFile: null })),
    ];
    TestBed.resetTestingModule();

    expect(result).toEqual([true, false, false]);
  });

  it('the Presenter block has two columns: the preview, or the picture without one, with the versions as thumbnails under it, then the change request as the primary action with the full prompt and the built-in presenters, the picture and the mouth folded', async () => {
    const bridge = makeBridge({
      ...listing(
        versioned({ mouthBox: { x: 0.43, y: 0.62, width: 0.14, height: 0.07 } }),
        versioned({ id: 'p2', presenterPreviewFile: null }),
      ),
      operatorReadiness: vi.fn(async () => ({
        ...fullReadiness,
        builtinPresenters: [{ id: 'ada', label: 'Ada' }],
      })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const block = el(fixture, 'presenter')!;
    const [view, controls] = Array.from(block.children);
    const prompt = el<HTMLDetailsElement>(fixture, 'presenter-prompt-options')!;
    const own = el<HTMLDetailsElement>(fixture, 'presenter-own')!;
    const thumbnail = el(fixture, 'presenter-use-0')!;
    const layout = [
      Array.from(block.children).map((c) => c.className),
      Array.from(view!.children).map((c) => c.getAttribute('data-test')),
      controls!.querySelector('textarea')?.getAttribute('data-test'),
      el(fixture, 'presenter-change')!.classList.contains('btn--primary'),
      [prompt.open, prompt.querySelector('summary')?.textContent?.trim()],
      ['presenter-prompt', 'presenter-safety', 'presenter-generate'].map((id) =>
        prompt.contains(el(fixture, id)),
      ),
      [own.open, own.querySelector('summary')?.textContent?.trim()],
      ['presenter-builtin', 'presenter-attach', 'mouth-area'].map((id) =>
        own.contains(el(fixture, id)),
      ),
      [thumbnail.tagName, thumbnail.querySelector('img') !== null],
      text(fixture, 'project-presenter'),
      el(fixture, 'project-presenter')!.previousElementSibling!.contains(
        el(fixture, 'help-presenter'),
      ),
    ];
    await click(fixture, 'project-p2');
    const pictureOnly = [
      el(fixture, 'presenter-preview'),
      el(fixture, 'presenter-picture')!.getAttribute('src'),
    ];

    expect(layout).toEqual([
      ['presenter__view', 'presenter__controls'],
      ['presenter-preview', 'presenter-versions'],
      'presenter-feedback',
      true,
      [false, 'Edit the full prompt'],
      [true, true, true],
      [false, "Use one of Scout's presenters, or your own picture"],
      [true, true, true],
      ['BUTTON', true],
      'Presenter: A friendly robot in a workshop',
      true,
    ]);
    expect(pictureOnly).toEqual([
      null,
      'scout-media://p2/presenter-2.png?v=2026-11-03T09%3A05%3A00Z',
    ]);
    expect(styleText()).toMatch(
      /@container detail \(max-width: 720px\)\s*\{\s*\.presenter\[[^\]]+\]\s*\{\s*grid-template-columns: minmax\(0, 1fr\);/,
    );
  });

  it('a presenter version that is not current carries a remove mark that sends its id and nothing else, absent on the current one and while presenting, and a refusal shows under the block', async () => {
    const three = versioned({
      presenterFile: 'presenter-2.png',
      presenterVersions: [
        { ...versioned().presenterVersions[0]!, id: 'version-0', file: 'presenter-0.png' },
        ...versioned().presenterVersions,
      ],
    });
    let refuse = false;
    const bridge = makeBridge({
      ...listing(three, versioned({ id: 'p2', status: 'presenting' })),
      operatorRemovePresenterVersion: vi.fn(async () => {
        if (refuse) {
          throw refusedWith(
            'scout:operator.presenterVersionRemoval',
            'The current presenter cannot be removed; choose another version first.',
          );
        }
        return versioned();
      }),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const marks = [0, 1, 2].map((i) => el(fixture, `presenter-version-remove-${i}`) !== null);
    const label = el(fixture, 'presenter-version-remove-0')!.getAttribute('aria-label');
    await click(fixture, 'presenter-version-remove-0');
    const afterRemoval = [
      el(fixture, 'presenter-version-2'),
      el<HTMLImageElement>(fixture, 'presenter-version-0')!
        .querySelector('img')!
        .getAttribute('src'),
    ];
    refuse = true;
    await click(fixture, 'presenter-version-remove-0');
    const refused = text(fixture, 'presenter-error');
    await click(fixture, 'project-p2');
    const presenting = [0, 1].map((i) => el(fixture, `presenter-version-remove-${i}`));

    expect(marks).toEqual([true, true, false]);
    expect(label).toBe('Remove presenter version 1');
    expect(bridge.operatorRemovePresenterVersion.mock.calls).toEqual([
      [{ projectId: 'p1', versionId: 'version-0' }],
      [{ projectId: 'p1', versionId: 'version-1' }],
    ]);
    expect(bridge.operatorUsePresenterVersion).not.toHaveBeenCalled();
    expect(afterRemoval).toEqual([
      null,
      'scout-media://p1/presenter-1.png?v=2026-11-05T10%3A00%3A00Z',
    ]);
    expect(refused).toBe('The current presenter cannot be removed; choose another version first.');
    expect(presenting).toEqual([null, null]);
  });

  function threeVersions(): OperatorProjectDto {
    return versioned({
      presenterVersions: [
        { ...versioned().presenterVersions[0]!, id: 'version-0', file: 'presenter-0.png' },
        ...versioned().presenterVersions,
      ],
    });
  }

  function strip(fixture: ComponentFixture<ProductionComponent>): (string | null)[] {
    return [0, 1, 2, 3].map(
      (i) =>
        el(fixture, `presenter-version-${i}`)?.querySelector('img')?.getAttribute('src') ?? null,
    );
  }

  it('Use and Remove send the id of the row the owner clicked, never its position', async () => {
    const three = threeVersions();
    const bridge = makeBridge({
      ...listing(three),
      operatorUsePresenterVersion: vi.fn(async () => three),
      operatorRemovePresenterVersion: vi.fn(async () => three),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');

    await click(fixture, 'presenter-use-1');
    await click(fixture, 'presenter-version-remove-1');
    await click(fixture, 'presenter-use-0');

    expect(bridge.operatorUsePresenterVersion.mock.calls).toEqual([
      [{ projectId: 'p1', versionId: 'version-1' }],
      [{ projectId: 'p1', versionId: 'version-0' }],
    ]);
    expect(bridge.operatorRemovePresenterVersion.mock.calls).toEqual([
      [{ projectId: 'p1', versionId: 'version-1' }],
    ]);
  });

  it.each([
    ['Use', 'presenter-use-0', 'operatorUsePresenterVersion', 'scout:operator.presenterVersion'],
    [
      'Remove',
      'presenter-version-remove-0',
      'operatorRemovePresenterVersion',
      'scout:operator.presenterVersionRemoval',
    ],
  ] as const)(
    '%s on a list that is stale reads the project again, shows "That version is no longer there; the list was refreshed." and sends nothing more',
    async (_action, button, call, channel) => {
      let stored = threeVersions();
      const bridge = makeBridge({
        operatorProjects: vi.fn(async () => ({ projects: [stored] })),
        operatorProject: vi.fn(async () => stored),
        [call]: vi.fn(async () => {
          throw goneWith(channel, 'That presenter version is no longer there.');
        }),
      });
      const fixture = await mount(bridge);
      await click(fixture, 'project-p1');
      const before = strip(fixture);
      stored = versioned();

      await click(fixture, button);

      expect(text(fixture, 'presenter-error')).toBe(
        'That version is no longer there; the list was refreshed.',
      );
      expect(before).toEqual([
        'scout-media://p1/presenter-0.png?v=2026-11-05T10%3A00%3A00Z',
        'scout-media://p1/presenter-1.png?v=2026-11-05T10%3A00%3A00Z',
        'scout-media://p1/presenter-2.png?v=2026-11-05T10%3A10%3A00Z',
        null,
      ]);
      expect(strip(fixture)).toEqual([
        'scout-media://p1/presenter-1.png?v=2026-11-05T10%3A00%3A00Z',
        'scout-media://p1/presenter-2.png?v=2026-11-05T10%3A10%3A00Z',
        null,
        null,
      ]);
      expect(bridge[call].mock.calls).toEqual([[{ projectId: 'p1', versionId: 'version-0' }]]);
      expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }], [{ id: 'p1' }]]);
      expect(
        bridge.operatorUsePresenterVersion.mock.calls.length +
          bridge.operatorRemovePresenterVersion.mock.calls.length,
      ).toBe(1);
    },
  );

  it.each([
    ['Use', 'presenter-use-0', 'operatorUsePresenterVersion', 'scout:operator.presenterVersion'],
    [
      'Remove',
      'presenter-version-remove-0',
      'operatorRemovePresenterVersion',
      'scout:operator.presenterVersionRemoval',
    ],
  ] as const)(
    '%s with the control plane down shows that it did not answer and keeps the rows on screen',
    async (_action, button, call, channel) => {
      const three = threeVersions();
      const bridge = makeBridge({
        ...listing(three),
        [call]: vi.fn(async () => {
          throw ipcFailure(channel, 'socket reset');
        }),
      });
      const fixture = await mount(bridge);
      await click(fixture, 'project-p1');
      const before = strip(fixture);

      await click(fixture, button);

      expect(text(fixture, 'presenter-error')).toBe('The Scout control plane did not answer.');
      expect(strip(fixture)).toEqual(before);
      expect(before.filter((src) => src !== null)).toHaveLength(3);
      expect(bridge.operatorProject.mock.calls).toEqual([[{ id: 'p1' }]]);
    },
  );

  it('while a Use is waiting for the control plane, every Use and Remove button of the strip is disabled and a second click sends nothing', async () => {
    const three = threeVersions();
    let answer: (project: OperatorProjectDto) => void = () => undefined;
    const bridge = makeBridge({
      ...listing(three),
      operatorUsePresenterVersion: vi.fn(
        () =>
          new Promise<OperatorProjectDto>((resolve) => {
            answer = resolve;
          }),
      ),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const buttons = [
      'presenter-use-0',
      'presenter-use-1',
      'presenter-version-remove-0',
      'presenter-version-remove-1',
    ];

    await click(fixture, 'presenter-use-0');
    const pending = buttons.map((id) => el<HTMLButtonElement>(fixture, id)!.disabled);
    await click(fixture, 'presenter-version-remove-1');
    await click(fixture, 'presenter-use-1');
    answer(three);
    await settle(fixture);
    const answered = buttons.map((id) => el<HTMLButtonElement>(fixture, id)!.disabled);

    expect(pending).toEqual([true, true, true, true]);
    expect(answered).toEqual([false, false, false, false]);
    expect(bridge.operatorUsePresenterVersion).toHaveBeenCalledTimes(1);
    expect(bridge.operatorRemovePresenterVersion).not.toHaveBeenCalled();
  });

  it('a scene is a card as Production.dc.html draws it: the picture, then Scene n with its status and sources, the narration, New picture, Edit text, Move up, Move down and More, which holds Attach image, Edit prompt, Replace narration, Lock, Regenerate scene and Regenerate narration', async () => {
    const pictured = scene({ visualSource: 'generated', visualFile: 'scene-s1.png' });
    const fixture = await mount(
      makeBridge(listing(character({ presenterFile: 'presenter-1.png', scenes: [pictured] }))),
    );
    await click(fixture, 'project-p1');
    const card = el(fixture, 'scene-s1')!;
    const [media, body] = Array.from(card.children);
    const menu = el<HTMLDetailsElement>(fixture, 'scene-narration-menu-s1')!;
    const actions = el(fixture, 'scene-edit-s1')!.parentElement!;

    expect([media!.className, body!.className]).toEqual(['scene__media', 'scene__body']);
    expect(media!.contains(el(fixture, 'scene-preview-s1'))).toBe(true);
    expect(
      Array.from(body!.querySelector('.scene__head')!.children).map((c) => c.textContent?.trim()),
    ).toEqual(['Scene 1', 'Ready', 'Generated picture · narration: generated']);
    expect(
      Array.from(actions.children).map(
        (c) => c.getAttribute('data-test') ?? c.tagName.toLowerCase(),
      ),
    ).toEqual([
      'scene-picture-regenerate-s1',
      'scene-edit-s1',
      'scene-up-s1',
      'scene-down-s1',
      'scene-narration-menu-s1',
    ]);
    expect([
      menu.open,
      menu.querySelector('summary')?.getAttribute('aria-label'),
      Array.from(menu.querySelectorAll('button')).map((b) => b.getAttribute('data-test')),
    ]).toEqual([
      false,
      'More actions for scene 1',
      [
        'scene-attach-visual-s1',
        'scene-picture-prompt-edit-s1',
        'scene-narration-replace-s1',
        'scene-lock-s1',
        'scene-regenerate-s1',
        'scene-narration-regenerate-s1',
      ],
    ]);
    expect(body!.contains(el(fixture, 'scene-picture-prompt-line-s1'))).toBe(true);
  });

  it('claims read at a glance: a stripe and a chip by risk, pending first under the count production waits for, decided ones folded under Decided (n) with Change, and Supported, Unsupported and Waive as one segmented control with the waive note', async () => {
    const claim = project().claims[0]!;
    const fixture = await mount(
      makeBridge(
        listing(
          project({
            claims: [
              { ...claim, id: 'c1', risk: 'low' },
              { ...claim, id: 'c2', risk: 'high', status: 'supported' },
              { ...claim, id: 'c3', risk: 'medium' },
            ],
          }),
        ),
      ),
    );
    await click(fixture, 'project-p1');
    const decided = el<HTMLDetailsElement>(fixture, 'claims-decided')!;
    const control = el(fixture, 'supported-c1')!.parentElement!;
    const glance = [
      Array.from(el(fixture, 'claims-open')!.children).map((c) => c.getAttribute('data-test')),
      text(fixture, 'claims-pending'),
      [decided.open, decided.querySelector('summary')?.textContent?.trim()],
      decided.contains(el(fixture, 'claim-c2')),
      ['c1', 'c2', 'c3'].map((id) => [
        Array.from(el(fixture, `claim-${id}`)!.classList).find((c) => c.startsWith('claim--')),
        text(fixture, `claim-risk-${id}`),
        el(fixture, `claim-risk-${id}`)!.getAttribute('data-tone'),
      ]),
      [control.className, control.getAttribute('role')],
      Array.from(control.children).map((b) => [
        b.getAttribute('data-test'),
        b.getAttribute('aria-pressed'),
      ]),
      [el(fixture, 'supported-c2'), text(fixture, 'claim-change-c2')],
    ];
    await click(fixture, 'claim-change-c2');
    const reopened = el(fixture, 'supported-c2')!.getAttribute('aria-pressed');
    await click(fixture, 'waive-c1');
    const waiving = [
      el(fixture, 'waive-c1')!.getAttribute('aria-pressed'),
      el(fixture, 'waive-note-c1')!.getAttribute('placeholder'),
    ];

    expect(glance).toEqual([
      ['claim-c1', 'claim-c3'],
      '2 waiting for you',
      [false, 'Decided (1)'],
      true,
      [
        ['claim--low', 'Low risk', 'ok'],
        ['claim--high', 'High risk', 'error'],
        ['claim--medium', 'Medium risk', 'warn'],
      ],
      ['segmented', 'group'],
      [
        ['supported-c1', 'false'],
        ['unsupported-c1', 'false'],
        ['waive-c1', 'false'],
      ],
      [null, 'Change'],
    ]);
    expect(reopened).toBe('true');
    expect(waiving).toEqual(['true', 'Why this claim needs no source (required)']);
  });

  it("every block carries a ? beside its title whose text, the page's own, shows on hover or keyboard focus and which does nothing else", async () => {
    const bridge = makeBridge(
      listing(
        character({ claims: [{ ...project().claims[0]! }] }),
        project({ id: 'p2', format: 'own_video' }),
      ),
    );
    const fixture = await mount(bridge);
    const marks = () =>
      Array.from(
        fixture.nativeElement.querySelectorAll('[data-test^="help-"]') as NodeListOf<HTMLElement>,
      ).map((b) => [
        b.getAttribute('data-test')!.slice('help-'.length),
        b.getAttribute('data-tip'),
      ]);
    await click(fixture, 'project-p1');
    const animated = marks();
    const claims = el<HTMLDetailsElement>(fixture, 'claims')!;
    const candidates = el<HTMLDetailsElement>(fixture, 'candidates')!;
    const before = [claims.open, candidates.open];
    await click(fixture, 'help-claims');
    await click(fixture, 'help-topic-candidates');
    const after = [claims.open, candidates.open];
    const mark = el(fixture, 'help-claims')!;
    const shape = [
      mark.tagName,
      mark.getAttribute('type'),
      mark.querySelector('svg path')?.getAttribute('d'),
      mark.getAttribute('aria-label'),
      mark.getAttribute('aria-description') === mark.getAttribute('data-tip'),
    ];
    await click(fixture, 'project-p2');
    const ownVideo = marks();

    const byKey = new Map(BLOCK_HELP);
    expect(animated).toEqual(
      ['mandate', 'topic-candidates', 'script', 'claims', 'presenter', 'scenes', 'background'].map(
        (key) => [key, byKey.get(key)],
      ),
    );
    expect(ownVideo.at(-1)).toEqual(['recording', byKey.get('recording')]);
    expect(after).toEqual(before);
    expect(shape).toEqual([
      'BUTTON',
      'button',
      'M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01',
      'What this block is for',
      true,
    ]);
    expect(styleText()).toMatch(
      /\.help\[[^\]]+\]:hover::after,\s*\.help\[[^\]]+\]:focus-visible::after\s*\{\s*content: attr\(data-tip\);/,
    );
    expect(bridge.operatorDecideClaim).not.toHaveBeenCalled();
  });

  it("versions of the same built-in presenter: the one whose picture and preview are the project's is outlined and its duplicate with another preview carries the remove mark, and two with the same picture and preview are both outlined and both carry it while the other remains", async () => {
    const builtin = {
      file: 'presenter-builtin-ada.png',
      prompt: '',
      feedback: 'builtin:ada',
      mouthBox: null,
      source: 'builtin',
    };
    const duplicated = character({
      presenterFile: 'presenter-builtin-ada.png',
      presenterPreviewFile: 'presenter-preview-2.mp4',
      presenterSource: 'builtin',
      presenterVersions: [
        {
          ...builtin,
          id: 'ada-1',
          previewFile: 'presenter-preview-1.mp4',
          madeAt: '2026-11-05T10:00:00Z',
        },
        {
          ...builtin,
          id: 'ada-2',
          previewFile: 'presenter-preview-2.mp4',
          madeAt: '2026-11-05T10:05:00Z',
        },
      ],
    });
    const twins = character({
      id: 'p2',
      presenterFile: 'presenter-builtin-ada.png',
      presenterPreviewFile: null,
      presenterSource: 'builtin',
      presenterVersions: [
        { ...builtin, id: 'ada-twin-1', previewFile: null, madeAt: '2026-11-05T10:00:00Z' },
        { ...builtin, id: 'ada-twin-2', previewFile: null, madeAt: '2026-11-05T10:05:00Z' },
      ],
    });
    const bridge = makeBridge({
      ...listing(duplicated, twins),
      operatorReadiness: vi.fn(async () => ({ ...fullReadiness, picturesLeftToday: 39 })),
    });
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    const strip = [0, 1].map((i) => [
      el(fixture, `presenter-version-${i}`)!.classList.contains('version--current'),
      el(fixture, `presenter-current-${i}`) !== null,
      el(fixture, `presenter-version-remove-${i}`) !== null,
      el(fixture, `presenter-use-${i}`) !== null,
    ]);
    const header = text(fixture, 'presenter-header');
    const preview = el(fixture, 'presenter-preview')!.getAttribute('src');

    expect(strip).toEqual([
      [false, false, true, true],
      [true, true, false, false],
    ]);
    await click(fixture, 'presenter-version-remove-0');
    await click(fixture, 'project-p2');
    const twinStrip = [0, 1].map((i) => [
      el(fixture, `presenter-version-${i}`)!.classList.contains('version--current'),
      el(fixture, `presenter-version-remove-${i}`) !== null,
      el(fixture, `presenter-use-${i}`) !== null,
    ]);
    expect(twinStrip).toEqual([
      [true, true, false],
      [true, true, false],
    ]);
    await click(fixture, 'presenter-version-remove-1');
    expect(header).toBe("Version 2 of 2 · Scout's presenter · 39 of 40 pictures left today");
    expect(preview).toBe('scout-media://p1/presenter-preview-2.mp4?v=2026-11-05T10%3A05%3A00Z');
    expect(bridge.operatorRemovePresenterVersion.mock.calls).toEqual([
      [{ projectId: 'p1', versionId: 'ada-1' }],
      [{ projectId: 'p2', versionId: 'ada-twin-2' }],
    ]);
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

describe('Production follows the theme and the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/production/production.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }

  it('Start and the delete confirmation show their icon before their text, as the prototype draws them', async () => {
    const fixture = await mount(makeBridge({ ...listing(project()) }));

    await click(fixture, 'project-delete-p1');

    expect(
      ['start-project', 'delete-confirm'].map((id) => [
        el(fixture, id)?.firstElementChild?.tagName.toLowerCase(),
        text(fixture, id),
      ]),
    ).toEqual([
      ['svg', 'Start'],
      ['svg', 'Delete project'],
    ]);
  });

  it('Delete project opens as an alert with Cancel focused, so Enter never deletes, and the delete painted as danger', async () => {
    const fixture = await mount(makeBridge({ ...listing(project()) }));

    await click(fixture, 'project-delete-p1');

    expect({
      role: el(fixture, 'delete-dialog')?.getAttribute('role'),
      focused: document.activeElement?.getAttribute('data-test'),
      danger: el(fixture, 'delete-confirm')?.classList.contains('btn--danger'),
    }).toEqual({ role: 'alertdialog', focused: 'delete-cancel', danger: true });
  });
});

describe('Production as in the prototype', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  function describedBy(fixture: ComponentFixture<ProductionComponent>, testId: string): string[] {
    const ids = (el(fixture, testId)?.getAttribute('aria-describedby') ?? '').split(' ');
    return ids
      .filter((id) => id !== '')
      .map((id) =>
        (fixture.nativeElement.querySelector(`#${id}`)?.textContent ?? '')
          .replace(/\s+/g, ' ')
          .trim(),
      );
  }

  it('the Channel summary names Channel and the mandate on one line: Mandate saved, the default format and what Scout can make', async () => {
    const fixture = await mount(
      makeBridge({ operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })) }),
    );
    const summary = el(fixture, 'channel')!.querySelector('summary')!;
    const at = formatDate(mandate.updatedAt!, 'd MMM, HH:mm', 'en-US');

    expect(
      Array.from(summary.children).map((c) => [
        c.tagName,
        c.textContent!.replace(/\s+/g, ' ').trim(),
      ]),
    ).toEqual([
      ['SPAN', 'Channel'],
      [
        'SPAN',
        `Mandate saved ${at} · Default format: Generated pictures · Can make: generated pictures`,
      ],
    ]);
  });

  it('Make a video has its heading, a labelled Topic with the example and the help line under it, and no help mark', async () => {
    const fixture = await mount(makeBridge());
    const section = el(fixture, 'make-a-video')!;
    const topic = el<HTMLInputElement>(fixture, 'topic')!;
    const label = fixture.nativeElement.querySelector(`label[for="${topic.id}"]`) as HTMLElement;

    expect(section.querySelector(`#${section.getAttribute('aria-labelledby')}`)?.textContent).toBe(
      'Make a video',
    );
    expect([label.textContent?.trim(), topic.placeholder]).toEqual([
      'Topic',
      'For example: why your SSD slows down when it is full',
    ]);
    expect(describedBy(fixture, 'topic')).toEqual([
      'Scout researches the topic, writes the script and lists the claims to check. You can also ask for a video in chat.',
    ]);
    expect(section.querySelector('[data-test^="help-"]')).toBeNull();
  });

  it('More options folds the six formats with the reason each one that is off gives, then Direction with its help and 0 / 1,000', async () => {
    const fixture = await mount(makeBridge());
    const options = el<HTMLDetailsElement>(fixture, 'start-options')!;
    const formats = Array.from(
      options.querySelectorAll('input[type="radio"]') as NodeListOf<HTMLInputElement>,
    ).map((r) => [
      r.closest('label')!.querySelector('.format-card__name')!.textContent!.trim(),
      r.disabled,
    ]);
    const direction = el<HTMLTextAreaElement>(fixture, 'start-direction')!;

    expect(options.querySelector('summary')?.textContent?.trim()).toBe(
      'More options: format and direction',
    );
    expect(options.querySelector('legend')?.textContent?.trim()).toBe('Format');
    expect(formats).toEqual([
      ['Generated pictures', false],
      ['Your own pictures', false],
      ['Animated character', true],
      ['Podcast', true],
      ['AI presenter', true],
      ['Your own video', true],
    ]);
    expect(
      ['format-animated_character', 'format-ai_presenter', 'format-own_video'].map((id) =>
        describedBy(fixture, id),
      ),
    ).toEqual([
      [
        'A cartoon presenter that speaks the script',
        'Needs the lip-sync tool, which is not installed on this computer.',
      ],
      ['Coming later', 'The connected AI Source has no video model.'],
      [
        'Your recording, cut into scenes',
        'Scout has no speech-to-text model on this machine; download one in Models, Transcription.',
      ],
    ]);
    expect(fixture.nativeElement.querySelector(`label[for="${direction.id}"]`)?.textContent).toBe(
      'Direction',
    );
    expect(direction.placeholder).toBe('Plain words, one analogy per idea, end with a checklist');
    expect(describedBy(fixture, 'start-direction')).toEqual([
      'How the video should feel: tone, audience, what to stress or avoid.',
      '0 / 1,000',
    ]);
  });

  it('Presenter shows with Animated character only, with its help and 0 / 300', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable: true })),
      }),
    );
    const before = el(fixture, 'start-presenter-field');

    await click(fixture, 'format-animated_character');
    const presenter = el<HTMLTextAreaElement>(fixture, 'start-presenter')!;

    expect(before).toBeNull();
    expect(fixture.nativeElement.querySelector(`label[for="${presenter.id}"]`)?.textContent).toBe(
      'Presenter',
    );
    expect(presenter.placeholder).toBe(
      'A friendly woman in her thirties, glasses, a navy blazer, a bright studio',
    );
    expect(describedBy(fixture, 'start-presenter')).toEqual([
      'Who appears on screen: look, age, clothing, setting. Scout keeps the same character in every scene.',
      '0 / 300',
    ]);
  });

  it('the delete dialog asks Delete "<topic>"?, its body ends It cannot be undone., and its button is Delete project', async () => {
    const fixture = await mount(makeBridge({ ...listing(project()) }));

    await click(fixture, 'project-delete-p1');
    const dialog = el(fixture, 'delete-dialog')!;

    expect({
      role: dialog.getAttribute('role'),
      title: fixture.nativeElement
        .querySelector(`#${dialog.getAttribute('aria-labelledby')}`)
        ?.textContent?.trim(),
      body: describedBy(fixture, 'delete-dialog')[0]?.endsWith('It cannot be undone.'),
      confirm: text(fixture, 'delete-confirm'),
      danger: el(fixture, 'delete-confirm')?.classList.contains('btn--danger'),
      focused: document.activeElement?.getAttribute('data-test'),
      hint: text(fixture, 'delete-hint'),
    }).toEqual({
      role: 'alertdialog',
      title: 'Delete "Span<T> in C#"?',
      body: true,
      confirm: 'Delete project',
      danger: true,
      focused: 'delete-cancel',
      hint: 'Escape or Cancel closes this without deleting.',
    });
  });

  it('Escape closes the delete dialog without deleting', async () => {
    const bridge = makeBridge({ ...listing(project()) });
    const fixture = await mount(bridge);
    await click(fixture, 'project-delete-p1');

    el(fixture, 'delete-cancel')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    await settle(fixture);

    expect(el(fixture, 'delete-dialog')).toBeNull();
    expect(bridge.operatorDeleteProject).not.toHaveBeenCalled();
  });
});

describe('Production as Production.dc.html draws it, state by state', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function idsOf(fixture: ComponentFixture<ProductionComponent>, testId: string): string[] {
    return (el(fixture, testId)?.getAttribute('aria-describedby') ?? '')
      .split(' ')
      .filter((id) => id !== '');
  }

  it('one h1, "Production", over its lede; the page is at most 1180 px, 24 px in, its parts 16 px apart, its text at 14 px', async () => {
    const fixture = await mount(makeBridge());
    const page = fixture.nativeElement.querySelector('main.page') as HTMLElement;

    expect({
      headings: Array.from(
        fixture.nativeElement.querySelectorAll('h1') as NodeListOf<HTMLElement>,
        (h) => h.textContent?.trim(),
      ),
      lede: page.querySelector('.lede')?.textContent?.trim(),
      maxWidth: declared(page, 'max-width'),
      padding: declared(page, 'padding'),
      gap: declared(page, 'gap'),
      size: declared(page, 'font-size'),
    }).toEqual({
      headings: ['Production'],
      lede: 'What the channel is for, the videos Scout is making, and what to check.',
      maxWidth: '1180px',
      padding: '24px',
      gap: '16px',
      size: '14px',
    });
  });

  it('New video sits at the right of the header, a 40 px pill on the strong accent with the plus, and takes the owner to the topic without leaving the page', async () => {
    const fixture = await mount(makeBridge());
    const link = el<HTMLAnchorElement>(fixture, 'new-video')!;
    const hash = location.hash;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    await settle(fixture);

    expect({
      text: link.textContent?.trim(),
      inHeader: link.parentElement?.classList.contains('header'),
      glyph: link.querySelector('svg path')?.getAttribute('d'),
      height: declared(link, 'height'),
      radius: declared(link, 'border-radius'),
      background: declared(link, 'background'),
      focused: document.activeElement?.getAttribute('data-test'),
      prevented: click.defaultPrevented,
      hash: location.hash,
    }).toEqual({
      text: 'New video',
      inHeader: true,
      glyph: 'M12 5v14M5 12h14',
      height: '40px',
      radius: '20px',
      background: 'var(--sc-accent-strong)',
      focused: 'topic',
      prevented: true,
      hash,
    });
  });

  it('Control plane down: one alert, the sentence on the error container under an error border, 14 px corners', async () => {
    const fixture = await mount(
      makeBridge({
        operatorProjects: vi.fn(async () => {
          throw ipcFailure('scout:operator.projects', 'spawn ENOENT');
        }),
      }),
    );
    const alert = el(fixture, 'production-error')!;

    expect({
      role: alert.getAttribute('role'),
      text: alert.textContent?.trim(),
      background: declared(alert, 'background'),
      border: declared(alert, 'border'),
      radius: declared(alert, 'border-radius'),
      projects: text(fixture, 'projects'),
      loading: el(fixture, 'projects-loading'),
      empty: el(fixture, 'projects-empty'),
    }).toEqual({
      role: 'alert',
      text: 'The Scout control plane did not answer.',
      background: 'var(--sc-error-container)',
      border: '1px solid var(--sc-error)',
      radius: '14px',
      projects: 'Projects',
      loading: null,
      empty: null,
    });
  });

  for (const [what, ready, makes] of [
    [
      'speech, generated and stock pictures',
      { ...readiness, speechAvailable: true, imagesAvailable: true, stockPictures: true },
      'narration, generated pictures, stock pictures',
    ],
    [
      'stock pictures only',
      { ...readiness, imagesAvailable: false, stockPictures: true },
      'stock pictures',
    ],
    [
      'lip sync and speech-to-text',
      { ...readiness, lipSyncAvailable: true, transcriptionAvailable: true },
      'generated pictures, animated character, your own video',
    ],
    ['nothing', { ...readiness, imagesAvailable: false }, 'text cards only'],
  ] as const) {
    it(`the Channel summary reads "Mandate saved 3 Nov, 09:00 · Default format: ... · Can make: ..." in lower case: ${what}`, async () => {
      const fixture = await mount(
        makeBridge({
          operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })),
          operatorReadiness: vi.fn(async () => ready),
        }),
      );

      expect(text(fixture, 'channel-summary')).toBe(
        `Mandate saved ${formatDate(mandate.updatedAt!, 'd MMM, HH:mm', 'en-US')} · Default format: Generated pictures · Can make: ${makes}`,
      );
    });
  }

  it('Channel is the artboard card: surface, line border, 14 px corners, 14 px by 18 px in; Channel at 16 px and 600, the line at 13 px in the muted colour', async () => {
    const fixture = await mount(
      makeBridge({ operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })) }),
    );
    const channel = el(fixture, 'channel')!;
    const summaryLine = el(fixture, 'channel-summary')!;

    expect({
      background: declared(channel, 'background'),
      border: declared(channel, 'border'),
      radius: declared(channel, 'border-radius'),
      padding: declared(channel, 'padding'),
      title: [
        declared(channel.querySelector('.channel-title')!, 'font-size'),
        declared(channel.querySelector('.channel-title')!, 'font-weight'),
      ],
      line: [declared(summaryLine, 'font-size'), declared(summaryLine, 'color')],
    }).toEqual({
      background: 'var(--sc-surface)',
      border: '1px solid var(--sc-line)',
      radius: '14px',
      padding: '14px 18px',
      title: ['16px', '600'],
      line: ['13px', 'var(--sc-muted)'],
    });
  });

  it('the mandate fields sit in the artboard grid, at least 220 px a column, 12 px apart, each label at 12 px in the muted colour above its field, and the owner can still edit and save every one', async () => {
    const bridge = makeBridge({
      operatorMandate: vi.fn(async () => ({ mandate: { ...mandate } })),
    });
    const fixture = await mount(bridge);
    const form = el(fixture, 'mandate')!.querySelector('.form')!;
    const label = el(fixture, 'mandate-objective')!.closest('label')!;
    type(fixture, 'mandate-objective', 'Teach testing');
    await click(fixture, 'mandate-save');

    expect({
      columns: declared(form, 'grid-template-columns'),
      gap: declared(form, 'gap'),
      labelSize: declared(label, 'font-size'),
      labelColor: declared(label, 'color'),
      labelFirst: label.firstChild?.textContent?.trim(),
      sent: (bridge.operatorSaveMandate.mock.calls[0] as unknown as [MandateSaveRequest])[0]
        .objective,
    }).toEqual({
      columns: 'repeat(auto-fit, minmax(220px, 1fr))',
      gap: '12px',
      labelSize: '12px',
      labelColor: 'var(--sc-muted)',
      labelFirst: 'Objective',
      sent: 'Teach testing',
    });
  });

  it('Make a video is the artboard card, 18 px in, 12 px between its parts; Start is the 40 px pill on the strong accent with the play triangle', async () => {
    const fixture = await mount(makeBridge());
    const section = el(fixture, 'make-a-video')!;
    const start = el(fixture, 'start-project')!;
    type(fixture, 'topic', 'Records in C#');
    await settle(fixture);

    expect({
      card: [
        declared(section, 'padding'),
        declared(section, 'gap'),
        declared(section, 'border-radius'),
      ],
      heading: declared(section.querySelector('h2')!, 'font-size'),
      start: [
        declared(start, 'height'),
        declared(start, 'border-radius'),
        declared(start, 'background'),
        start.querySelector('svg path')?.getAttribute('d'),
      ],
      topic: [
        declared(el(fixture, 'topic')!, 'height'),
        declared(el(fixture, 'topic')!, 'border-radius'),
      ],
    }).toEqual({
      card: ['18px', '12px', '14px'],
      heading: '18px',
      start: ['40px', '20px', 'var(--sc-accent-strong)', 'M8 5v14l11-7z'],
      topic: ['40px', '10px'],
    });
  });

  for (const [id, name, blurb, glyph] of [
    [
      'generated_pictures',
      'Generated pictures',
      'Narration over pictures Scout makes or finds',
      'M3 5h18v14H3z M3 16l5-5 4 4 3-3 6 6',
    ],
    [
      'own_pictures',
      'Your own pictures',
      'Narration over pictures you attach',
      'M12 16V4 M7 9l5-5 5 5 M4 20h16',
    ],
    [
      'animated_character',
      'Animated character',
      'A cartoon presenter that speaks the script',
      'M12 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M4 21c0-4 3-7 8-7s8 3 8 7',
    ],
    [
      'podcast',
      'Podcast',
      'One or two animated presenters talking about the subject',
      'M9 4a3 3 0 1 0 0 6 3 3 0 0 0 0-6z M2 20c0-3.5 3-6 7-6 M17 5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z M13 14.5c1-.5 2-.5 3-.5 3.5 0 6 2.3 6 5.5',
    ],
    ['ai_presenter', 'AI presenter', 'Coming later', 'M12 3l2 5 5 2-5 2-2 5-2-5-5-2 5-2z'],
    [
      'own_video',
      'Your own video',
      'Your recording, cut into scenes',
      'M3 6h12v12H3z M15 10l6-3v10l-6-3',
    ],
  ] as const) {
    it(`the ${name} card: a radio, then its 16 px stroke icon and name at 600, then its one-line blurb at 12 px`, async () => {
      const fixture = await mount(makeBridge());
      const card = el(fixture, `format-card-${id}`)!;
      const nameNode = card.querySelector('.format-card__name')!;

      expect({
        first: card.firstElementChild?.getAttribute('data-test'),
        name: nameNode.textContent?.trim(),
        glyph: nameNode.querySelector('svg path')?.getAttribute('d'),
        size: nameNode.querySelector('svg')?.getAttribute('width'),
        weight: declared(nameNode, 'font-weight'),
        blurb: card.querySelector('.format-card__blurb')?.textContent?.trim(),
        blurbSize: declared(card.querySelector('.format-card__blurb')!, 'font-size'),
      }).toEqual({
        first: `format-${id}`,
        name,
        glyph,
        size: '16',
        weight: '600',
        blurb,
        blurbSize: '12px',
      });
    });
  }

  for (const [what, defaultFormat, chosen] of [
    ['with no mandate', null, 'generated_pictures'],
    ['with Your own pictures as the mandate default', 'own_pictures', 'own_pictures'],
  ] as const) {
    it(`the chosen card has the accent border and ring and its radio checked, never by colour alone, the others neither: ${what}`, async () => {
      const fixture = await mount(
        makeBridge({
          operatorMandate: vi.fn(async () => ({
            mandate: defaultFormat === null ? null : { ...mandate, defaultFormat },
          })),
        }),
      );
      const cards = ['generated_pictures', 'own_pictures', 'animated_character'].map((id) => {
        const card = el(fixture, `format-card-${id}`)!;
        return [
          id,
          el<HTMLInputElement>(fixture, `format-${id}`)!.checked,
          declared(card, 'border-color') ?? 'var(--sc-line)',
          declared(card, 'box-shadow'),
        ];
      });

      expect(cards).toEqual(
        ['generated_pictures', 'own_pictures', 'animated_character'].map((id) =>
          id === chosen
            ? [id, true, 'var(--sc-accent)', '0 0 0 1px var(--sc-accent)']
            : [id, false, 'var(--sc-line)', '0 0 0 1px transparent'],
        ),
      );
    });
  }

  for (const [what, ready, id, reason] of [
    [
      'Animated character without the lip-sync tool',
      readiness,
      'animated_character',
      'Needs the lip-sync tool, which is not installed on this computer.',
    ],
    ['Podcast, not in this version', fullReadiness, 'podcast', 'Coming later.'],
    ['AI presenter', fullReadiness, 'ai_presenter', 'The connected AI Source has no video model.'],
    [
      'Your own video without a speech-to-text model',
      readiness,
      'own_video',
      'Scout has no speech-to-text model on this machine; download one in Models, Transcription.',
    ],
  ] as const) {
    it(`an off card is dashed on surface-2, its name and blurb muted, its radio off, and its reason under the blurb describes it: ${what}`, async () => {
      const fixture = await mount(makeBridge({ operatorReadiness: vi.fn(async () => ready) }));
      const card = el(fixture, `format-card-${id}`)!;
      const radio = el<HTMLInputElement>(fixture, `format-${id}`)!;
      const reasonNode = el(fixture, `format-${id}-reason`);

      expect({
        disabled: radio.disabled,
        style: declared(card, 'border-style'),
        background: declared(card, 'background'),
        cursor: declared(card, 'cursor'),
        name: declared(card.querySelector('.format-card__name')!, 'color'),
        blurb: declared(card.querySelector('.format-card__blurb')!, 'color'),
        reason: reasonNode?.textContent?.trim(),
        reasonAfterBlurb:
          reasonNode?.previousElementSibling?.classList.contains('format-card__blurb'),
        describedBy: idsOf(fixture, `format-${id}`),
      }).toEqual({
        disabled: true,
        style: 'dashed',
        background: 'var(--sc-surface-2)',
        cursor: 'not-allowed',
        name: 'var(--sc-muted)',
        blurb: 'var(--sc-muted)',
        reason,
        reasonAfterBlurb: true,
        describedBy: [`format-${id}-blurb`, `format-${id}-reason`],
      });
    });
  }

  it('a card that is on carries no reason and is described by its blurb alone', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...fullReadiness, lipSyncAvailable: true })),
      }),
    );

    expect([
      el(fixture, 'format-animated_character-reason'),
      idsOf(fixture, 'format-animated_character'),
      el<HTMLInputElement>(fixture, 'format-animated_character')!.disabled,
    ]).toEqual([null, ['format-animated_character-blurb'], false]);
  });

  async function arrows(keys: readonly string[], ready: ReadinessDto) {
    const bridge = makeBridge({ operatorReadiness: vi.fn(async () => ready) });
    const fixture = await mount(bridge);
    el<HTMLDetailsElement>(fixture, 'start-options')!.open = true;
    let at = 'generated_pictures';
    const seen: string[] = [];
    for (const key of keys) {
      el(fixture, `format-${at}`)!.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
      );
      await settle(fixture);
      at = (
        fixture.nativeElement.querySelector('input[name="start-format"]:checked') as HTMLElement
      ).dataset['test']!.replace('format-', '');
      seen.push(
        `${at}${document.activeElement?.getAttribute('data-test') === `format-${at}` ? '' : ' (not focused)'}`,
      );
    }
    return { bridge, fixture, seen };
  }

  for (const [what, keys, ready, seen] of [
    [
      'Right and Down move to the next format that can be chosen and take the focus with them',
      ['ArrowRight', 'ArrowDown'],
      { ...readiness, lipSyncAvailable: true },
      ['own_pictures', 'animated_character'],
    ],
    [
      'past the last one that can be chosen the choice goes round to the first, skipping Podcast and AI presenter',
      ['ArrowRight', 'ArrowRight', 'ArrowRight', 'ArrowRight'],
      { ...readiness, lipSyncAvailable: true, transcriptionAvailable: true },
      ['own_pictures', 'animated_character', 'own_video', 'generated_pictures'],
    ],
    [
      'Left and Up go back, round the start, skipping the formats that are off',
      ['ArrowLeft', 'ArrowUp'],
      readiness,
      ['own_pictures', 'generated_pictures'],
    ],
  ] as const) {
    it(`the formats are one radio group the arrow keys move through: ${what}`, async () => {
      const result = await arrows(keys, ready);

      expect(result.seen).toEqual([...seen]);
    });
  }

  it('the format the arrows reach is the one Start sends', async () => {
    const { bridge, fixture } = await arrows(['ArrowRight'], readiness);
    type(fixture, 'topic', 'Records in C#');
    await settle(fixture);
    await click(fixture, 'start-project');

    expect(bridge.operatorStartProject.mock.calls).toEqual([
      [{ topic: 'Records in C#', format: 'own_pictures', direction: '', presenter: '' }],
    ]);
  });

  it('Direction and Presenter are plain fields under the cards, 12 px apart from them, their help and count at 12 px in the muted colour, with no dashed box', async () => {
    const fixture = await mount(
      makeBridge({
        operatorReadiness: vi.fn(async () => ({ ...readiness, lipSyncAvailable: true })),
      }),
    );
    await click(fixture, 'format-animated_character');
    const presenter = el(fixture, 'start-presenter-field')!;

    expect({
      presenterBorder: declared(presenter, 'border'),
      presenterMargin: declared(presenter, 'margin-top'),
      count: [
        declared(el(fixture, 'start-direction-count')!, 'font-size'),
        declared(el(fixture, 'start-direction-count')!, 'color'),
        declared(el(fixture, 'start-direction-count')!, 'align-self'),
      ],
      textarea: [
        declared(el(fixture, 'start-direction')!, 'border-radius'),
        declared(el(fixture, 'start-direction')!, 'padding'),
      ],
    }).toEqual({
      presenterBorder: null,
      presenterMargin: '12px',
      count: ['12px', 'var(--sc-muted)', 'flex-end'],
      textarea: ['10px', '10px 12px'],
    });
  });

  it('Loading: while the list is not read yet, Projects shows the bar and four skeleton rows under one status named Loading projects', async () => {
    const fixture = await mount(
      makeBridge({ operatorProjects: vi.fn(() => new Promise(() => undefined)) }),
    );
    const loading = el(fixture, 'projects-loading')!;

    expect({
      role: loading.getAttribute('role'),
      name: loading.getAttribute('aria-label'),
      bar: loading.querySelectorAll('.bar__fill').length,
      rows: loading.querySelectorAll('.skeleton').length,
      empty: el(fixture, 'projects-empty'),
      count: el(fixture, 'projects-count'),
    }).toEqual({
      role: 'status',
      name: 'Loading projects',
      bar: 1,
      rows: 4,
      empty: null,
      count: null,
    });
  });

  it('No projects: the round mark, "No videos yet. Start one above, or ask Scout in chat." and Start a video, which takes the owner to the topic', async () => {
    const fixture = await mount(makeBridge());
    const empty = el(fixture, 'projects-empty')!;
    const link = el<HTMLAnchorElement>(fixture, 'start-a-video')!;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    await settle(fixture);

    expect({
      mark: declared(empty.querySelector('.empty-state__mark')!, 'width'),
      text: text(fixture, 'projects-empty-text'),
      link: link.textContent?.trim(),
      focused: document.activeElement?.getAttribute('data-test'),
      prevented: click.defaultPrevented,
    }).toEqual({
      mark: '44px',
      text: 'No videos yet. Start one above, or ask Scout in chat.',
      link: 'Start a video',
      focused: 'topic',
      prevented: true,
    });
  });

  it('Projects carries its count after the heading, in the muted colour at 500', async () => {
    const fixture = await mount(
      makeBridge(listing(project(), project({ id: 'p2', topic: 'Records' }))),
    );
    const count = el(fixture, 'projects-count')!;

    expect([
      text(fixture, 'projects-count'),
      count.parentElement?.tagName,
      declared(count, 'color'),
      declared(count, 'font-weight'),
    ]).toEqual(['(2)', 'H2', 'var(--sc-muted)', '500']);
  });

  for (const [status, extra, label, tone, line] of [
    ['queued', {}, 'Queued', 'neutral', 'today'],
    ['script_ready', {}, 'Script ready for review', 'warn', 'today'],
    [
      'producing',
      { scenes: [scene(), scene({ id: 's2', order: 2, status: 'planned' })] },
      'Making the media',
      'info',
      'today · 1 of 2 scenes ready',
    ],
    ['published', {}, 'On YouTube', 'ok', 'today'],
    [
      'failed',
      { error: 'the AI Source reached its usage limit' },
      'Failed',
      'error',
      'today · the AI Source reached its usage limit',
    ],
    ['production_queued', {}, 'production queued', 'neutral', 'today'],
  ] as const) {
    it(`a project row reads its status in words on its tone's chip with the tone's glyph, then its time: ${status}`, async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2026-11-03T18:00:00'));
      const updatedAt = new Date('2026-11-03T14:02:00').toISOString();
      const fixture = await mount(makeBridge(listing(project({ status, updatedAt, ...extra }))));
      const chip = el(fixture, 'project-status-p1')!;

      expect([
        chip.textContent?.trim(),
        chip.getAttribute('data-tone'),
        chip.querySelector('svg path')?.getAttribute('d'),
        text(fixture, 'project-updated-p1'),
      ]).toEqual([
        label,
        tone,
        {
          ok: 'M20 6L9 17l-5-5',
          warn: 'M12 8v5M12 16.5h.01',
          error: 'M18 6L6 18M6 6l12 12',
          info: 'M12 7v5l3 2',
          neutral: 'M5 12h14',
        }[tone],
        line.replace('today', 'Today, 14:02'),
      ]);
    });
  }

  it('a row of another day reads its day and month, "2 Nov, 09:15"', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-11-03T18:00:00'));
    const fixture = await mount(
      makeBridge(listing(project({ updatedAt: new Date('2026-11-02T09:15:00').toISOString() }))),
    );

    expect(text(fixture, 'project-updated-p1')).toBe('2 Nov, 09:15');
  });

  it("the open project's row is on surface-2 under the accent border, the others on the page colour under the line; the trash is a 34 px round button in the error colour, titled like its name", async () => {
    const fixture = await mount(
      makeBridge(listing(project(), project({ id: 'p2', topic: 'Records' }))),
    );
    await click(fixture, 'project-p1');
    const open = el(fixture, 'project-p1')!.parentElement!;
    const other = el(fixture, 'project-p2')!.parentElement!;
    const trash = el(fixture, 'project-delete-p2')!;

    expect({
      open: [declared(open, 'background'), declared(open, 'border-color')],
      other: [declared(other, 'background'), declared(other, 'border')],
      trash: [
        declared(trash, 'width'),
        declared(trash, 'border-radius'),
        declared(trash, 'color'),
        trash.getAttribute('title'),
        trash.getAttribute('aria-label'),
      ],
    }).toEqual({
      open: ['var(--sc-surface-2)', 'var(--sc-accent)'],
      other: ['var(--sc-bg)', '1px solid var(--sc-line)'],
      trash: ['34px', '17px', 'var(--sc-error)', 'Delete Records', 'Delete Records'],
    });
  });

  it('Project open: the title at 20 px over its status chip, format and time; Let Scout finish it, Delete and the round close; Direction:, the script, the claims to check and the scenes as drawn', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-11-03T18:00:00'));
    const claim = project().claims[0]!;
    const open = project({
      updatedAt: new Date('2026-11-03T14:02:00').toISOString(),
      direction: 'Plain words',
      claims: [
        { ...claim, id: 'c1', risk: 'high' },
        { ...claim, id: 'c2', risk: 'low' },
      ],
      scenes: [scene({ visualSource: 'generated' })],
    });
    const fixture = await mount(makeBridge(listing(open)));
    await click(fixture, 'project-p1');
    const detail = el(fixture, 'project-detail')!;
    const title = detail.querySelector('h2')!;
    const delegate = el(fixture, 'delegate')!;
    const remove = el(fixture, 'project-title-delete')!;
    const close = el(fixture, 'project-close')!;
    const group = el(fixture, 'supported-c1')!.parentElement!;
    const produce = el<HTMLButtonElement>(fixture, 'produce')!;
    const sceneCard = el(fixture, 'scene-s1')!;

    expect({
      card: [
        declared(detail, 'padding'),
        declared(detail, 'gap'),
        declared(detail, 'border-radius'),
      ],
      title: [title.textContent?.trim(), declared(title, 'font-size')],
      meta: text(fixture, 'project-meta'),
      delegate: [
        delegate.textContent?.trim(),
        delegate.getAttribute('title'),
        declared(delegate, 'height'),
      ],
      remove: [
        remove.textContent?.trim(),
        declared(remove, 'color'),
        declared(remove, 'border-color'),
      ],
      close: [
        close.getAttribute('aria-label'),
        declared(close, 'width'),
        declared(close, 'border-radius'),
      ],
      direction: text(fixture, 'project-direction'),
      blockLine: declared(detail.querySelector(':scope > .block-head')!, 'border-top'),
      section: declared(detail.querySelector('.section')!, 'background'),
      claims: [
        detail.querySelector('[data-test="claims"] h3')?.textContent?.trim(),
        text(fixture, 'claims-pending'),
        text(fixture, 'claim-risk-c1'),
        declared(el(fixture, 'claim-c1')!, 'border-left-width'),
      ],
      choices: [
        group.getAttribute('aria-label'),
        Array.from(group.querySelectorAll('button')).map((b) => [
          b.textContent?.trim(),
          b.querySelector('svg path')?.getAttribute('d'),
        ]),
        declared(group, 'border-radius'),
      ],
      produce: [
        produce.textContent?.trim(),
        produce.disabled,
        declared(produce, 'background'),
        produce.getAttribute('aria-describedby'),
        text(fixture, 'produce-blocked'),
      ],
      scene: [
        declared(sceneCard, 'background'),
        declared(sceneCard, 'border-radius'),
        declared(sceneCard.querySelector('.scene__media')!, 'flex'),
      ],
    }).toEqual({
      card: ['18px', '16px', '14px'],
      title: ['Span<T> in C#', '20px'],
      meta: 'Script ready for reviewGenerated pictures · updated Today, 14:02',
      delegate: [
        'Let Scout finish it',
        'Scout finishes the video on its own and asks you only at approval',
        '36px',
      ],
      remove: ['Delete', 'var(--sc-error)', 'var(--sc-error)'],
      close: ['Close the project', '36px', '18px'],
      direction: 'Direction: Plain words',
      blockLine: '1px solid var(--sc-line)',
      section: 'var(--sc-bg)',
      claims: ['Claims to check', '2 waiting for you', 'High risk', '3px'],
      choices: [
        'Decision on the claim: Span<T> is a ref struct.',
        [
          ['Supported', 'M20 6L9 17l-5-5'],
          ['Not supported', 'M18 6L6 18M6 6l12 12'],
          ['Needs no source', 'M5 12h14'],
        ],
        '18px',
      ],
      produce: [
        'Produce media',
        true,
        'var(--sc-surface-2)',
        'produce-why',
        '2 claims are still waiting; decide every claim before producing the media.',
      ],
      scene: ['var(--sc-bg)', '10px', '0 1 200px'],
    });
  });

  it("Delete in the title row opens the delete dialog for the open project, as the row's trash does; it sends nothing until Delete project", async () => {
    const bridge = makeBridge(listing(project()));
    const fixture = await mount(bridge);
    await click(fixture, 'project-p1');
    await click(fixture, 'project-title-delete');
    const asked = [
      el(fixture, 'delete-dialog') !== null,
      text(fixture, 'delete-dialog').startsWith('Delete "Span<T> in C#"?'),
      bridge.operatorDeleteProject.mock.calls.length,
    ];
    el(fixture, 'delete-confirm')?.click();
    await settle(fixture);

    expect(asked).toEqual([true, true, 0]);
    expect(bridge.operatorDeleteProject.mock.calls).toEqual([[{ id: 'p1' }]]);
  });

  for (const status of ['researching', 'producing', 'assembling', 'presenting']) {
    it(`Delete is not in the title row while the project is running: ${status}`, async () => {
      const fixture = await mount(makeBridge(listing(project({ status }))));
      await click(fixture, 'project-p1');

      expect([el(fixture, 'project-title-delete'), el(fixture, 'project-delete-p1')]).toEqual([
        null,
        null,
      ]);
    });
  }

  it('Delete dialog: a 460 px card 160 px from the top, 14 px corners, a 17 px title, Cancel and Delete project as 40 px pills, Delete project white on the danger red with the trash', async () => {
    const fixture = await mount(makeBridge(listing(project())));
    await click(fixture, 'project-delete-p1');
    const backdrop = el(fixture, 'delete-dialog')!;
    const card = backdrop.querySelector('.dialog')!;
    const confirm = el(fixture, 'delete-confirm')!;

    expect({
      top: declared(backdrop, 'padding'),
      width: declared(card, 'max-width'),
      radius: declared(card, 'border-radius'),
      title: declared(card.querySelector('h2')!, 'font-size'),
      buttons: [declared(confirm, 'height'), declared(el(fixture, 'delete-cancel')!, 'height')],
      confirm: [declared(confirm, 'background'), declared(confirm, 'color')],
      glyph: confirm.querySelector('svg path')?.getAttribute('d'),
    }).toEqual({
      top: '160px 16px 16px',
      width: '460px',
      radius: '14px',
      title: '17px',
      buttons: ['40px', '40px'],
      confirm: ['var(--sc-danger)', 'var(--sc-on-danger)'],
      glyph: 'M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14M10 11v6M14 11v6',
    });
  });
});
