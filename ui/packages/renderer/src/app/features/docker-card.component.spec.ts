import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  RESTART_DEFERRED_LINE,
  type DockerImage,
  type DockerPullProgress,
  type DockerRepository,
  type DockerRun,
  type DockerState,
  type DockerWorkFolder,
} from '@scout/shared';

import { restartAsMain } from '../core/main-restart.testing';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import {
  DOCKER_INSTALL_URL,
  DOCKER_SECTIONS,
  DOCKER_SECTIONS_STORAGE_KEY,
  DOCKER_USES,
  DockerCardComponent,
  formatBytes,
  type DockerCardStatus,
  type DockerSection,
} from './docker-card.component';

const RUNNING: DockerState = { state: 'running', version: '28.5.1' };
const FOLDER: DockerWorkFolder = {
  path: '/home/owner/.local/share/Scout/sandbox',
  bytes: 184 * 1024 * 1024,
  runs: 12,
};

const image = (stack: string, tag: string, present: boolean): DockerImage => ({
  stack,
  reference: `${tag}@sha256:0123456789abcdef0123456789abcdef`,
  present,
});
const SOME_MISSING = [
  image('.NET', 'dotnet/sdk:10.0', true),
  image('Node', 'node:22', true),
  image('Java (Maven)', 'maven:3-eclipse-temurin-17', true),
  image('Java (Maven)', 'maven:3-eclipse-temurin-21', false),
  image('Java (Gradle)', 'gradle:8-jdk21', false),
];
const ALL_PRESENT = SOME_MISSING.map((i) => ({ ...i, present: true }));

interface Options {
  state?: DockerState | 'fail';
  enabled?: string | null;
  images?: readonly DockerImage[];
  folder?: DockerWorkFolder;
  github?: boolean;
  worker?: 'running' | 'idle';
  listening?: boolean;
  storeFails?: boolean;
  pullError?: string | null;
  pullImageErrors?: Readonly<Record<string, string>>;
  repositories?: readonly DockerRepository[] | 'fail';
  lastRun?: DockerRun | null | 'fail';
}

function makeBridge(o: Options = {}) {
  let images = o.images ?? SOME_MISSING;
  let progress: ((p: DockerPullProgress) => void) | null = null;
  const bridge = {
    dockerState: vi.fn(async () => {
      if (o.state === 'fail') throw new Error('bridge down');
      return o.state ?? RUNNING;
    }),
    dockerImages: vi.fn(async () => ({ images })),
    dockerWorkFolder: vi.fn(async () => o.folder ?? FOLDER),
    dockerRepositories: vi.fn(async () => {
      if (o.repositories === 'fail') throw new Error('bridge down');
      return { repositories: o.repositories ?? [] };
    }),
    dockerLastRun: vi.fn(async () => {
      if (o.lastRun === 'fail') throw new Error('bridge down');
      return { run: o.lastRun ?? null };
    }),
    dockerPullImage: vi.fn(async (req: { reference: string }) => {
      progress?.({ reference: req.reference, index: 1, total: 1, state: 'pulling' });
      const error = o.pullImageErrors?.[req.reference] ?? null;
      if (error === null) {
        images = images.map((i) => (i.reference === req.reference ? { ...i, present: true } : i));
      }
      return { error };
    }),
    dockerPull: vi.fn(async (req: { stack: string }) => {
      const missing = images.filter((i) => i.stack === req.stack && !i.present);
      progress?.({ reference: missing[0]!.reference, index: 1, total: 1, state: 'pulling' });
      if ((o.pullError ?? null) === null) {
        images = images.map((i) => (i.stack === req.stack ? { ...i, present: true } : i));
      }
      return { error: o.pullError ?? null };
    }),
    progressShown: false,
    onDockerPullProgress: vi.fn((listener: (p: DockerPullProgress) => void) => {
      progress = listener;
      return () => {
        progress = null;
      };
    }),
    dockerClear: vi.fn(async () => ({
      refused: null as string | null,
      folder: { path: FOLDER.path, bytes: 0, runs: 0 },
    })),
    vaultGetPublic: vi.fn(async () => ({ value: o.enabled ?? null })),
    vaultStore: vi.fn(async () => {
      if (o.storeFails === true) throw new Error('The vault is locked.');
    }),
    workerStatus: vi.fn(async () => ({
      state: o.worker === 'running' ? 'running' : 'idle',
      restarts: 0,
    })),
    workerStop: vi.fn(async () => ({ exitCode: 0 })),
    workerStart: vi.fn(async () => ({ pid: 1 })),
    localListening: vi.fn(async () => ({ listening: o.listening === true })),
    appOpenExternal: vi.fn(async () => undefined),
    noticeRecord: vi.fn(async () => undefined),
  };
  return Object.assign(bridge, { workerRestart: vi.fn(() => restartAsMain(bridge)) });
}

async function mount(o: Options = {}) {
  const bridge = makeBridge(o);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
    ],
  });
  const shown = vi.spyOn(TestBed.inject(NoticeService), 'show');
  const fixture = TestBed.createComponent(DockerCardComponent);
  fixture.componentRef.setInput('githubConnected', o.github ?? true);
  const statuses: DockerCardStatus[] = [];
  fixture.componentInstance.statusChange.subscribe((s) => statuses.push(s));
  await settle(fixture);
  const notices = () => shown.mock.calls.map(([message, kind]) => [message, kind]);
  return { fixture, bridge, statuses, notices };
}

async function settle(fixture: ComponentFixture<DockerCardComponent>): Promise<void> {
  for (let i = 0; i < 8; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<DockerCardComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<DockerCardComponent>, test: string): string {
  return (el(fixture, test)?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function switchInput(fixture: ComponentFixture<DockerCardComponent>): HTMLButtonElement {
  return el(fixture, 'docker-switch')!.querySelector('button[role="switch"]') as HTMLButtonElement;
}

async function click(fixture: ComponentFixture<DockerCardComponent>, test: string): Promise<void> {
  const target = el(fixture, test);
  expect(target, `${test} is on the card`).not.toBeNull();
  target!.click();
  await settle(fixture);
}

async function answer(fixture: ComponentFixture<DockerCardComponent>, test: string) {
  const button = document.querySelector<HTMLButtonElement>(`[data-test="${test}"]`);
  if (button === null) throw new Error('the confirmation dialog did not open');
  button.click();
  await settle(fixture);
}

async function open(
  fixture: ComponentFixture<DockerCardComponent>,
  id: DockerSection,
): Promise<void> {
  if (el(fixture, `docker-toggle-${id}`)?.getAttribute('aria-expanded') !== 'true') {
    await click(fixture, `docker-toggle-${id}`);
  }
}

function expanded(fixture: ComponentFixture<DockerCardComponent>): DockerSection[] {
  return DOCKER_SECTIONS.filter(
    (id) => el(fixture, `docker-toggle-${id}`)?.getAttribute('aria-expanded') === 'true',
  );
}

describe('Docker card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(DOCKER_SECTIONS_STORAGE_KEY);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  describe('a card for Docker: its states', () => {
    const rows: readonly [string, DockerState | 'fail', string, string, boolean][] = [
      [
        'not installed',
        { state: 'notInstalled' },
        'Not installed',
        'Docker is not installed on this computer.',
        true,
      ],
      [
        'not running',
        { state: 'notRunning' },
        'Installed, not running',
        'Docker is installed but not running. Start Docker Desktop or the Docker service.',
        false,
      ],
      [
        'running',
        RUNNING,
        'Running · Docker 28.5.1',
        'Docker Engine 28.5.1 answers on this computer.',
        false,
      ],
      [
        'unreadable',
        'fail',
        'Unknown',
        'Scout could not ask Docker for its state. Try Refresh.',
        false,
      ],
    ];
    for (const [name, state, chip, line, installLink] of rows) {
      it(`${name}: the chip says "${chip}", the line explains it, and the install guide ${installLink ? 'shows' : 'does not show'}`, async () => {
        const { fixture } = await mount({ state });

        expect(fixture.componentInstance.chipLabel()).toBe(chip);
        expect(text(fixture, 'docker-status-line')).toBe(line);
        expect(el(fixture, 'docker-install-guide') !== null).toBe(installLink);
        expect(el(fixture, 'docker-refresh')).not.toBeNull();
      });
    }

    it("How to install Docker opens Docker's install guide", async () => {
      const { fixture, bridge } = await mount({ state: { state: 'notInstalled' } });

      await click(fixture, 'docker-install-guide');

      expect(bridge.appOpenExternal.mock.calls).toEqual([[{ url: DOCKER_INSTALL_URL }]]);
      expect(DOCKER_INSTALL_URL).toBe('https://docs.docker.com/get-started/get-docker/');
    });

    it('Refresh asks Docker again, and nothing asks it on a timer', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'setInterval'] });
      const { fixture, bridge } = await mount();
      vi.advanceTimersByTime(10 * 60_000);
      const beforeRefresh = bridge.dockerState.mock.calls.length;
      vi.useRealTimers();

      await click(fixture, 'docker-refresh');

      expect(beforeRefresh).toBe(1);
      expect(bridge.dockerState.mock.calls.length).toBe(2);
    });

    it('tells the GitHub card whether Docker runs and whether its runs are on', async () => {
      const { statuses } = await mount({ state: { state: 'notRunning' }, enabled: 'true' });

      expect(statuses.at(-1)).toEqual({ running: false, enabled: true });
    });
  });

  describe("the switch: Docker runs, Scout's own setting", () => {
    const helpRows: readonly [boolean, DockerState | 'fail', string, boolean][] = [
      [
        false,
        RUNNING,
        "Connect GitHub first. Scout runs a project's tests only for a pull request you ask it to review. Runs also need GitHub's write permission in its card.",
        true,
      ],
      [
        true,
        RUNNING,
        "Scout runs a project's tests only for a pull request you ask it to review. Runs also need GitHub's write permission in its card.",
        false,
      ],
      [
        true,
        { state: 'notRunning' },
        "Scout runs a project's tests only for a pull request you ask it to review. Runs also need GitHub's write permission in its card. Runs wait for Docker to be running.",
        false,
      ],
    ];
    for (const [github, state, help, disabled] of helpRows) {
      it(`GitHub ${github ? 'connected' : 'not connected'}, Docker ${(state as DockerState).state}: the switch is ${disabled ? 'disabled' : 'enabled'} and says why`, async () => {
        const { fixture } = await mount({ github, state });

        expect(switchInput(fixture).disabled).toBe(disabled);
        expect(text(fixture, 'docker-switch-help')).toBe(help);
      });
    }

    for (const stored of ['true', 'false', null]) {
      it(`shows the stored choice ${String(stored)} as ${stored === 'true' ? 'on' : 'off'}`, async () => {
        const { fixture, bridge } = await mount({ enabled: stored });

        expect(bridge.vaultGetPublic.mock.calls).toEqual([[{ key: 'sandbox.enabled' }]]);
        expect(switchInput(fixture).getAttribute('aria-checked')).toBe(String(stored === 'true'));
      });
    }

    const toggleRows: readonly [string | null, 'running' | 'idle', string, boolean][] = [
      [null, 'running', 'true', true],
      [null, 'idle', 'true', false],
      ['true', 'running', 'false', true],
      ['true', 'idle', 'false', false],
    ];
    for (const [stored, worker, written, restarted] of toggleRows) {
      it(`turning Docker runs ${written === 'true' ? 'on' : 'off'} with the worker ${worker} writes sandbox.enabled ${written} and ${restarted ? 'restarts' : 'does not restart'} the worker`, async () => {
        const { fixture, bridge, statuses } = await mount({ enabled: stored, worker });

        switchInput(fixture).click();
        await settle(fixture);

        expect(bridge.vaultStore.mock.calls).toEqual([
          [{ key: 'sandbox.enabled', value: written }],
        ]);
        expect(bridge.workerStop.mock.calls.length).toBe(restarted ? 1 : 0);
        expect(bridge.workerStart.mock.calls.length).toBe(restarted ? 1 : 0);
        expect(statuses.at(-1)).toEqual({ running: true, enabled: written === 'true' });
        expect(switchInput(fixture).getAttribute('aria-checked')).toBe(written);
      });
    }

    it('turning Docker runs on while a live listen runs leaves Scout running and says it restarts once the listen ends', async () => {
      const { fixture, bridge } = await mount({ worker: 'running', listening: true });

      switchInput(fixture).click();
      await settle(fixture);

      expect({
        restartAsked: bridge.workerRestart.mock.calls.length,
        stopped: bridge.workerStop.mock.calls.length,
        note: text(fixture, 'docker-switch-note'),
      }).toEqual({ restartAsked: 1, stopped: 0, note: RESTART_DEFERRED_LINE });
    });

    it('a vault that refuses the write leaves the switch off, says why, and restarts nothing', async () => {
      const { fixture, bridge } = await mount({ storeFails: true, worker: 'running' });

      switchInput(fixture).click();
      await settle(fixture);

      expect(switchInput(fixture).getAttribute('aria-checked')).toBe('false');
      expect(text(fixture, 'docker-switch-error')).toBe('The vault is locked.');
      expect(bridge.workerStop).not.toHaveBeenCalled();
    });
  });

  describe('what Scout uses Docker for', () => {
    it("lists the five uses in ADR 0043's words", async () => {
      const { fixture } = await mount();

      await click(fixture, 'docker-toggle-uses');
      const items = Array.from(el(fixture, 'docker-body-uses')!.querySelectorAll('li'), (li) =>
        li.textContent?.trim(),
      );

      expect(items).toEqual([...DOCKER_USES]);
      expect(items).toEqual([
        "Running a pull request's own tests before it gives a verdict, when no check ran them or the review asks for one.",
        "The project's own tests only, never Scout's own code.",
        'Offline during the tests; the network is on only to restore the packages.',
        'One disposable container per run, deleted after it.',
        '20 minutes at most per run.',
      ]);
    });
  });

  describe('download the images, one stack at a time', () => {
    function summaries(fixture: ComponentFixture<DockerCardComponent>): string[] {
      return Array.from(
        fixture.nativeElement.querySelectorAll(
          '[data-test^="docker-group-summary-"]',
        ) as NodeListOf<HTMLElement>,
        (e) => (e.textContent ?? '').replace(/\s+/g, ' ').trim(),
      );
    }

    function stackButton(fixture: ComponentFixture<DockerCardComponent>, stack: string) {
      return el(fixture, `docker-download-${stack}`) as HTMLButtonElement | null;
    }

    it('a stack with every image downloaded counts each of them', async () => {
      const { fixture } = await mount({ images: ALL_PRESENT });
      if (!expanded(fixture).includes('images')) await click(fixture, 'docker-toggle-images');

      expect(summaries(fixture)).toEqual([
        '.NET: 1 of 1 downloaded',
        'Node: 1 of 1 downloaded',
        'Java (Maven): 2 of 2 downloaded',
        'Java (Gradle): 1 of 1 downloaded',
      ]);
    });

    it('groups the pinned images by stack, each with how many are downloaded', async () => {
      const { fixture } = await mount();

      expect(summaries(fixture)).toEqual([
        '.NET: 1 of 1 downloaded',
        'Node: 1 of 1 downloaded',
        'Java (Maven): 1 of 2 downloaded',
        'Java (Gradle): 0 of 1 downloaded',
      ]);
      expect(text(fixture, 'docker-images-help')).toBe('3 of 5 downloaded.');
      expect(text(fixture, 'docker-group-Java (Maven)')).toContain('Not downloaded');
    });

    const rows: readonly [string, Options, string, boolean | null][] = [
      ['a stack with an image missing', {}, 'Java (Maven)', false],
      ['a stack fully downloaded', {}, '.NET', true],
      ['Docker not running', { state: { state: 'notRunning' } }, 'Java (Maven)', null],
    ];
    for (const [name, o, stack, disabled] of rows) {
      it(`${name}: the ${stack} Download is ${disabled === null ? 'not shown' : disabled ? 'disabled' : 'enabled'}`, async () => {
        const { fixture } = await mount(o);
        if (!expanded(fixture).includes('images')) await click(fixture, 'docker-toggle-images');

        expect(stackButton(fixture, stack)?.disabled ?? null).toBe(disabled);
      });
    }

    it('Download images never pulls every image: it waits for the list of what the repositories need, and says so', async () => {
      const { fixture, bridge } = await mount();

      const button = el(fixture, 'docker-download') as HTMLButtonElement;
      button.click();
      await settle(fixture);

      expect(button.disabled).toBe(true);
      expect(bridge.dockerPull).not.toHaveBeenCalled();
      expect(text(fixture, 'docker-download-help')).toBe(
        'Download images fetches only what the repositories Scout has read need, and Scout has not listed them yet. Docker Hub limits anonymous downloads and the Java images are several hundred MB each, so download a stack below when you need it.',
      );
    });

    it("a stack's Download asks main for that stack only, and lists the images again when it ends", async () => {
      const { fixture, bridge } = await mount();

      await click(fixture, 'docker-download-Java (Maven)');

      expect(bridge.dockerPull.mock.calls).toEqual([[{ stack: 'Java (Maven)' }]]);
      expect(bridge.dockerImages).toHaveBeenCalledTimes(2);
      expect(summaries(fixture)).toEqual([
        '.NET: 1 of 1 downloaded',
        'Node: 1 of 1 downloaded',
        'Java (Maven): 2 of 2 downloaded',
        'Java (Gradle): 0 of 1 downloaded',
      ]);
      expect(stackButton(fixture, 'Java (Maven)')?.disabled).toBe(true);
      expect(el(fixture, 'docker-progress')).toBeNull();
      expect(el(fixture, 'docker-pull-error')).toBeNull();
    });

    const failures: readonly string[] = [
      'Docker could not download maven:3-eclipse-temurin-21: manifest unknown.',
      'Docker Hub is limiting downloads; try again later.',
    ];
    for (const sentence of failures) {
      it(`a pull main answers with "${sentence}" shows that sentence`, async () => {
        const { fixture } = await mount({ pullError: sentence });

        await click(fixture, 'docker-download-Java (Gradle)');

        expect(text(fixture, 'docker-pull-error')).toBe(sentence);
        expect(el(fixture, 'docker-pull-error')?.getAttribute('role')).toBe('alert');
      });
    }

    it('the progress main pushes shows under the stack that is downloading', async () => {
      const { fixture, bridge } = await mount();
      bridge.dockerPull.mockImplementationOnce(async () => {
        const listener = bridge.onDockerPullProgress.mock.calls[0]![0];
        listener({ reference: SOME_MISSING[4]!.reference, index: 1, total: 1, state: 'pulling' });
        await settle(fixture);
        const group = el(fixture, 'docker-group-Java (Gradle)');
        bridge.progressShown =
          (group?.querySelector('[data-test="docker-progress"]')?.textContent ?? '')
            .replace(/\s+/g, ' ')
            .trim()
            .startsWith('Downloading 1 of 1: gradle:8-jdk21@sha256:01234567...') &&
          el(fixture, 'docker-group-Java (Maven)')?.querySelector(
            '[data-test="docker-progress"]',
          ) === null;
        return { error: null };
      });

      await click(fixture, 'docker-download-Java (Gradle)');

      expect(bridge.progressShown).toBe(true);
    });
  });

  describe('clear the work folder, asking first', () => {
    async function openFolder(o: Options = {}) {
      const mounted = await mount(o);
      await click(mounted.fixture, 'docker-toggle-folder');
      return mounted;
    }

    const lines: readonly [number, string][] = [
      [12, "184 MB used · the logs of 12 runs; each run's copy of the code is deleted after it"],
      [1, "184 MB used · the logs of 1 run; each run's copy of the code is deleted after it"],
      [0, "184 MB used · each run's copy of the code is deleted after it"],
    ];
    for (const [runs, line] of lines) {
      it(`shows the path and, for a folder holding ${runs} run records, "${line}"`, async () => {
        const { fixture } = await openFolder({ folder: { ...FOLDER, runs } });

        expect(text(fixture, 'docker-folder-path')).toBe(FOLDER.path);
        expect(
          text(fixture, 'docker-folder-size'),
          'the work folder line gives its size and the logs of the runs it holds, and none when it holds no run',
        ).toBe(line);
      });
    }

    it('Clear asks first with Cancel focused, and Cancel deletes nothing', async () => {
      const { fixture, bridge } = await openFolder();

      await click(fixture, 'docker-clear');
      const title = document.querySelector('sc-confirm-dialog h2')?.textContent?.trim();
      const body = document.querySelector('sc-confirm-dialog .body')?.textContent?.trim();
      const initialFocus = document
        .querySelector('[data-test="confirm-cancel"]')
        ?.hasAttribute('cdkfocusinitial');
      expect(title, 'the confirmation opened before anything was deleted').toBe(
        'Clear the work folder?',
      );
      await answer(fixture, 'confirm-cancel');

      expect(body).toBe(
        `Scout deletes the logs of past test runs in ${FOLDER.path} (184 MB). The pinned images stay in Docker.`,
      );
      expect(initialFocus).toBe(true);
      expect(bridge.dockerClear).not.toHaveBeenCalled();
    });

    it('Clear confirmed asks main to clear and shows the folder empty', async () => {
      const { fixture, bridge } = await openFolder();

      await click(fixture, 'docker-clear');
      await answer(fixture, 'confirm-ok');

      expect(bridge.dockerClear).toHaveBeenCalledTimes(1);
      expect(text(fixture, 'docker-folder-size')).toBe('Empty · run logs are deleted');
      expect((el(fixture, 'docker-clear') as HTMLButtonElement).disabled).toBe(true);
    });

    it('Clear confirmed takes the last run off the card, since its record was in the work folder', async () => {
      const { fixture, bridge } = await openFolder({
        lastRun: {
          repository: 'octocat/hello',
          pullRequest: 42,
          stack: 'Node',
          image: 'node:22@sha256:0123456789abcdef0123456789abcdef',
          command: 'npm test',
          result: 'passed',
          reason: null,
          exitCode: 0,
          durationSeconds: 12.5,
          startedAt: null,
          finishedAt: '2026-11-07T10:05:00+00:00',
        },
      });
      bridge.dockerLastRun.mockResolvedValue({ run: null });

      await click(fixture, 'docker-clear');
      await answer(fixture, 'confirm-ok');

      expect(text(fixture, 'docker-toggle-last')).toBe('Last run: none yet');
    });

    it('a clear main refuses shows its sentence and keeps the size', async () => {
      const refused = 'A test run is in progress in the work folder; clear it after the run ends.';
      const { fixture, bridge } = await openFolder();
      bridge.dockerClear.mockResolvedValueOnce({ refused, folder: FOLDER });

      await click(fixture, 'docker-clear');
      await answer(fixture, 'confirm-ok');

      expect(text(fixture, 'docker-clear-refused')).toBe(refused);
      expect(text(fixture, 'docker-folder-size')).toBe(
        "184 MB used · the logs of 12 runs; each run's copy of the code is deleted after it",
      );
    });
  });

  describe('Repositories and Last run, empty until the sandbox writes their records', () => {
    it('Repositories says when Scout lists a stack, and Last run says no run yet', async () => {
      const { fixture } = await mount();

      await click(fixture, 'docker-toggle-repositories');
      await click(fixture, 'docker-toggle-last');

      expect(text(fixture, 'docker-repositories-empty')).toBe(
        "Scout lists a repository's stack the first time it reads it.",
      );
      expect(text(fixture, 'docker-last-empty')).toBe('No run yet.');
    });
  });

  describe("each repository's stack, known on access, shown on the card", () => {
    const NODE20 = image('Node', 'node:20', false);
    const BUN = image('Bun', 'oven/bun:1', true);
    const GRADLE8 = image('Java (Gradle)', 'gradle:8-jdk21', false);
    const repo = (over: Partial<DockerRepository> = {}): DockerRepository => ({
      repository: 'octocat/hello',
      commit: 'abc1234def5678',
      stack: 'Node',
      image: NODE20.reference,
      runs: 'Node 20.20.2',
      declared: [{ source: '.nvmrc', version: 'Node 20' }],
      refusal: null,
      detectedAt: '2026-11-07T10:00:00+00:00',
      ...over,
    });
    const REFUSED = repo({
      repository: 'octocat/legacy',
      image: null,
      runs: null,
      declared: [],
      refusal: 'This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.',
    });

    const rows: readonly {
      name: string;
      o: Options;
      stack: string;
      versions: string | null;
      refusal: string | null;
      imageChip: string | null;
      download: boolean;
      disabled: boolean | null;
      describedBy: string | null;
    }[] = [
      {
        name: 'Docker running, the image missing',
        o: { images: [NODE20, BUN], repositories: [repo()] },
        stack: 'Node',
        versions: 'Runs on Node 20.20.2. .nvmrc asks for Node 20.',
        refusal: null,
        imageChip: 'Image not downloaded',
        download: true,
        disabled: false,
        describedBy: null,
      },
      {
        name: 'Docker running, the image downloaded',
        o: { images: [{ ...NODE20, present: true }, BUN], repositories: [repo()] },
        stack: 'Node',
        versions: 'Runs on Node 20.20.2. .nvmrc asks for Node 20.',
        refusal: null,
        imageChip: 'Image downloaded',
        download: false,
        disabled: null,
        describedBy: null,
      },
      {
        name: 'Docker not running',
        o: { state: { state: 'notRunning' }, repositories: [repo()] },
        stack: 'Node',
        versions: 'Runs on Node 20.20.2. .nvmrc asks for Node 20.',
        refusal: null,
        imageChip: 'Docker is not running',
        download: true,
        disabled: true,
        describedBy: 'Docker is not running',
      },
      {
        name: 'a Bun repository that declares nothing',
        o: {
          images: [NODE20, BUN],
          repositories: [repo({ image: BUN.reference, runs: 'Bun 1.4.4', declared: [] })],
        },
        stack: 'Bun',
        versions: 'Runs on Bun 1.4.4. Nothing declared; the default runs.',
        refusal: null,
        imageChip: 'Image downloaded',
        download: false,
        disabled: null,
        describedBy: null,
      },
      {
        name: 'two declared versions',
        o: {
          images: [NODE20, GRADLE8],
          repositories: [
            repo({
              stack: 'Java',
              image: GRADLE8.reference,
              runs: 'Java 21 with Gradle 8.14.6',
              declared: [
                { source: 'build.gradle.kts', version: 'Java 21' },
                { source: 'gradle/wrapper/gradle-wrapper.properties', version: 'Gradle 8.10.2' },
              ],
            }),
          ],
        },
        stack: 'Java (Gradle)',
        versions:
          'Runs on Java 21 with Gradle 8.14.6. build.gradle.kts asks for Java 21; gradle/wrapper/gradle-wrapper.properties asks for Gradle 8.10.2.',
        refusal: null,
        imageChip: 'Image not downloaded',
        download: true,
        disabled: false,
        describedBy: null,
      },
      {
        name: 'a version Scout does not run',
        o: { images: [NODE20], repositories: [REFUSED] },
        stack: 'Node',
        versions: null,
        refusal: 'This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.',
        imageChip: null,
        download: false,
        disabled: null,
        describedBy: null,
      },
      {
        name: 'no manifest at all',
        o: {
          images: [NODE20],
          repositories: [
            repo({
              stack: null,
              image: null,
              runs: null,
              declared: [],
              refusal:
                "No .NET, Node, Python, Go or Java project was found at the repository's root.",
            }),
          ],
        },
        stack: 'No stack',
        versions: null,
        refusal: "No .NET, Node, Python, Go or Java project was found at the repository's root.",
        imageChip: null,
        download: false,
        disabled: null,
        describedBy: null,
      },
    ];
    for (const r of rows) {
      it(`${r.name}: the row shows the stack ${r.stack}, ${r.refusal === null ? 'the versions and the image' : 'the refusal'}, and ${r.download ? (r.disabled ? 'a disabled' : 'an enabled') : 'no'} Download`, async () => {
        const { fixture } = await mount(r.o);
        await click(fixture, 'docker-toggle-repositories');
        const row = (r.o.repositories as DockerRepository[])[0]!.repository;
        const inRow = (test: string) =>
          el(fixture, `docker-repository-${row}`)?.querySelector(`[data-test="${test}"]`) ?? null;
        const shown = (test: string) =>
          (inRow(test)?.textContent ?? '').replace(/\s+/g, ' ').trim() || null;

        expect(el(fixture, 'docker-repositories-empty')).toBeNull();
        expect(shown('docker-repository-stack')).toBe(r.stack);
        expect(shown('docker-repository-versions')).toBe(r.versions);
        expect(shown('docker-repository-refusal')).toBe(
          r.refusal === null ? null : `${r.stack}: ${r.refusal}`,
        );
        expect(shown('docker-repository-image')).toBe(r.imageChip);
        const download = inRow('docker-repository-download') as HTMLButtonElement | null;
        expect(download !== null).toBe(r.download);
        expect(download?.disabled ?? null).toBe(r.disabled);
        const reason = download?.getAttribute('aria-describedby') ?? null;
        expect(
          reason === null ? null : (document.getElementById(reason)?.textContent ?? '').trim(),
        ).toBe(r.describedBy);
      });
    }

    it("a repository's Download pulls only that repository's image, never the rest of its stack", async () => {
      const node24 = image('Node', 'node:24', false);
      const { fixture, bridge } = await mount({
        images: [NODE20, node24, BUN],
        repositories: [repo()],
      });
      await click(fixture, 'docker-toggle-repositories');

      await click(fixture, 'docker-repository-download');

      expect(bridge.dockerPullImage.mock.calls).toEqual([[{ reference: NODE20.reference }]]);
      expect(bridge.dockerPull).not.toHaveBeenCalled();
      expect(text(fixture, 'docker-repository-image')).toBe('Image downloaded');
      await open(fixture, 'images');
      expect(text(fixture, `docker-image-${node24.reference}`)).toContain('Not downloaded');
    });

    it('lists every repository main answers, in its order', async () => {
      const { fixture } = await mount({ images: [NODE20], repositories: [REFUSED, repo()] });
      await click(fixture, 'docker-toggle-repositories');

      const listed = [
        ...(fixture.nativeElement as HTMLElement).querySelectorAll(
          '[data-test^="docker-repository-octocat"]',
        ),
      ].map((e) => e.getAttribute('data-test'));
      expect(listed).toEqual([
        'docker-repository-octocat/legacy',
        'docker-repository-octocat/hello',
      ]);
    });

    const empties: readonly [string, Options][] = [
      ['no record', { repositories: [] }],
      ['the read failing', { repositories: 'fail' }],
    ];
    for (const [name, o] of empties) {
      it(`${name}: the section says when Scout lists a stack`, async () => {
        const { fixture } = await mount(o);
        await click(fixture, 'docker-toggle-repositories');

        expect(text(fixture, 'docker-repositories-empty')).toBe(
          "Scout lists a repository's stack the first time it reads it.",
        );
      });
    }

    const summaries: readonly [string, Options, string][] = [
      ['none', {}, 'Repositories: none read yet'],
      [
        'one with its image',
        { images: [{ ...NODE20, present: true }], repositories: [repo()] },
        'Repositories: 1 read',
      ],
      [
        'one missing its image and one refused',
        { images: [NODE20], repositories: [repo(), REFUSED] },
        'Repositories: 2 read, 1 missing an image, 1 Scout cannot run',
      ],
      [
        'Docker not running',
        { state: { state: 'notRunning' }, repositories: [repo()] },
        'Repositories: 1 read',
      ],
    ];
    for (const [name, o, summary] of summaries) {
      it(`${name}: closed, the section reads "${summary}"`, async () => {
        const { fixture } = await mount(o);

        expect(text(fixture, 'docker-toggle-repositories')).toBe(summary);
      });
    }
  });

  describe("Download images: the repositories' missing images, one at a time", () => {
    const NODE20 = image('Node', 'node:20', false);
    const MAVEN17 = image('Java (Maven)', 'maven:3-eclipse-temurin-17', false);
    const GO124 = image('Go', 'golang:1.24', false);
    const UNNEEDED = image('Python', 'python:3.12', false);
    const repo = (name: string, img: DockerImage | null): DockerRepository => ({
      repository: `octocat/${name}`,
      commit: 'abc1234def5678',
      stack: 'Node',
      image: img?.reference ?? null,
      runs: img === null ? null : 'Node 20.20.2',
      declared: [],
      refusal:
        img === null
          ? 'This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26.'
          : null,
      detectedAt: '2026-11-07T10:00:00+00:00',
    });
    const NO_RECORDS_HELP =
      'Download images fetches only what the repositories Scout has read need, and Scout has not listed them yet. Docker Hub limits anonymous downloads and the Java images are several hundred MB each, so download a stack below when you need it.';
    const RECORDS_HELP =
      'Download images fetches the one image each repository Scout has read needs and Docker does not hold yet, one at a time; a repository on a version Scout does not run is skipped. Docker Hub limits anonymous downloads, so nothing else is fetched.';
    const DONE_HELP = 'Every image your repositories need is downloaded.';

    const matrix: readonly {
      records: 'none' | 'some';
      missing: boolean;
      running: boolean;
      enabled: boolean;
      help: string;
    }[] = [
      { records: 'none', missing: true, running: true, enabled: false, help: NO_RECORDS_HELP },
      { records: 'none', missing: false, running: true, enabled: false, help: NO_RECORDS_HELP },
      { records: 'none', missing: true, running: false, enabled: false, help: NO_RECORDS_HELP },
      { records: 'none', missing: false, running: false, enabled: false, help: NO_RECORDS_HELP },
      { records: 'some', missing: true, running: true, enabled: true, help: RECORDS_HELP },
      { records: 'some', missing: false, running: true, enabled: false, help: DONE_HELP },
      { records: 'some', missing: true, running: false, enabled: false, help: RECORDS_HELP },
      { records: 'some', missing: false, running: false, enabled: false, help: RECORDS_HELP },
    ];
    for (const m of matrix) {
      it(`records ${m.records}, an image they need ${m.missing ? 'missing' : 'downloaded'}, Docker ${m.running ? 'running' : 'not running'}: Download images is ${m.enabled ? 'enabled' : 'disabled'}`, async () => {
        const needed = { ...NODE20, present: !m.missing };
        const { fixture } = await mount({
          state: m.running ? RUNNING : { state: 'notRunning' },
          images: [needed, UNNEEDED],
          repositories: m.records === 'some' ? [repo('hello', needed)] : [],
        });
        await open(fixture, 'images');

        expect((el(fixture, 'docker-download') as HTMLButtonElement).disabled).toBe(!m.enabled);
        expect(text(fixture, 'docker-download-help')).toBe(m.help);
      });
    }

    it('pulls exactly the missing images the repositories need, each once and in order, skipping refused and downloaded ones', async () => {
      const { fixture, bridge } = await mount({
        images: [NODE20, MAVEN17, { ...GO124, present: true }, UNNEEDED],
        repositories: [
          repo('hello', NODE20),
          repo('legacy', null),
          repo('api', MAVEN17),
          repo('web', NODE20),
          repo('tool', GO124),
        ],
      });
      await open(fixture, 'images');

      await click(fixture, 'docker-download');

      expect(bridge.dockerPullImage.mock.calls).toEqual([
        [{ reference: NODE20.reference }],
        [{ reference: MAVEN17.reference }],
      ]);
      expect(bridge.dockerPull).not.toHaveBeenCalled();
      expect(text(fixture, 'docker-download-help')).toBe(DONE_HELP);
      expect((el(fixture, 'docker-download') as HTMLButtonElement).disabled).toBe(true);
    });

    it('shows its progress over the images it pulls', async () => {
      const { fixture, bridge } = await mount({
        images: [NODE20, MAVEN17],
        repositories: [repo('hello', NODE20), repo('api', MAVEN17)],
      });
      await open(fixture, 'images');
      const shown: string[] = [];
      bridge.dockerPullImage.mockImplementation(async (_req: { reference: string }) => {
        fixture.detectChanges();
        shown.push(text(fixture, 'docker-needed-progress'));
        return { error: null };
      });

      await click(fixture, 'docker-download');

      expect(shown).toEqual([
        'Downloading 1 of 2: node:20@sha256:01234567...',
        'Downloading 2 of 2: maven:3-eclipse-temurin-17@sha256:01234567...',
      ]);
      expect(el(fixture, 'docker-needed-progress')).toBeNull();
    });

    it("a failure stops the rest and says so, with main's sentence", async () => {
      const { fixture, bridge } = await mount({
        images: [NODE20, MAVEN17, GO124],
        repositories: [repo('hello', NODE20), repo('api', MAVEN17), repo('tool', GO124)],
        pullImageErrors: {
          [MAVEN17.reference]: 'Docker Hub is limiting downloads; try again later.',
        },
      });
      await open(fixture, 'images');

      await click(fixture, 'docker-download');

      expect(bridge.dockerPullImage.mock.calls).toEqual([
        [{ reference: NODE20.reference }],
        [{ reference: MAVEN17.reference }],
      ]);
      expect(text(fixture, 'docker-pull-error')).toBe(
        'Docker Hub is limiting downloads; try again later. The other 1 image was not downloaded.',
      );
    });
  });

  describe('the last run on the card', () => {
    const RUN: DockerRun = {
      repository: 'octocat/hello',
      pullRequest: 42,
      stack: 'Node',
      image: 'node:22@sha256:0123456789abcdef0123456789abcdef',
      command: null,
      result: 'passed',
      reason: null,
      exitCode: 0,
      durationSeconds: 12.5,
      startedAt: null,
      finishedAt: '2026-11-07T10:05:00+00:00',
    };
    const runs: readonly {
      name: string;
      run: DockerRun;
      what: string;
      pr: string | null;
      chip: string;
      tone: string;
      stack: string;
      duration: string;
      reason: string | null;
      summary: string;
    }[] = [
      {
        name: 'passed',
        run: RUN,
        what: 'octocat/hello',
        pr: '#42',
        chip: 'Passed',
        tone: 'chip--ok',
        stack: 'Node',
        duration: '12.5 s',
        reason: null,
        summary: 'Last run: octocat/hello #42, passed',
      },
      {
        name: 'failed',
        run: { ...RUN, result: 'failed', exitCode: 1, durationSeconds: 185 },
        what: 'octocat/hello',
        pr: '#42',
        chip: 'Failed (exit 1)',
        tone: 'chip--warn',
        stack: 'Node',
        duration: '3 min 5 s',
        reason: null,
        summary: 'Last run: octocat/hello #42, failed (exit 1)',
      },
      {
        name: 'a restore that failed',
        run: {
          ...RUN,
          result: 'failed',
          exitCode: 1,
          reason: 'The restore exited with code 1; the tests did not run.',
        },
        what: 'octocat/hello',
        pr: '#42',
        chip: 'Failed (exit 1)',
        tone: 'chip--warn',
        stack: 'Node',
        duration: '12.5 s',
        reason: 'The restore exited with code 1; the tests did not run.',
        summary: 'Last run: octocat/hello #42, failed (exit 1)',
      },
      {
        name: 'did not run',
        run: {
          ...RUN,
          result: 'notRun',
          exitCode: null,
          durationSeconds: 1200,
          reason: 'The tests ran past the 20-minute limit.',
        },
        what: 'octocat/hello',
        pr: '#42',
        chip: 'Did not run',
        tone: 'chip--neutral',
        stack: 'Node',
        duration: '20 min 0 s',
        reason: 'The tests ran past the 20-minute limit.',
        summary: 'Last run: octocat/hello #42, did not run',
      },
      {
        name: 'a run with no repository named, in the Bun image',
        run: {
          ...RUN,
          repository: null,
          pullRequest: null,
          image: 'oven/bun:1@sha256:0123456789abcdef0123456789abcdef',
        },
        what: 'A repository',
        pr: null,
        chip: 'Passed',
        tone: 'chip--ok',
        stack: 'Bun',
        duration: '12.5 s',
        reason: null,
        summary: 'Last run: a repository, passed',
      },
      {
        name: 'a .NET run that names the command it ran',
        run: {
          ...RUN,
          stack: '.NET',
          image: 'dotnet/sdk:10.0@sha256:0123456789abcdef0123456789abcdef',
          command: 'dotnet test',
        },
        what: 'octocat/hello',
        pr: '#42',
        chip: 'Passed',
        tone: 'chip--ok',
        stack: '.NET (dotnet test)',
        duration: '12.5 s',
        reason: null,
        summary: 'Last run: octocat/hello #42, passed',
      },
    ];
    for (const r of runs) {
      it(`${r.name}: the tiles show ${r.what}${r.pr === null ? '' : ' ' + r.pr}, the result "${r.chip}", the stack, duration and time${r.reason === null ? '' : ', and the reason under them'}`, async () => {
        const { fixture } = await mount({
          images: [...SOME_MISSING, image('Bun', 'oven/bun:1', true)],
          lastRun: r.run,
        });
        const summary = text(fixture, 'docker-toggle-last');
        await click(fixture, 'docker-toggle-last');

        expect(summary).toBe(r.summary);
        expect(el(fixture, 'docker-last-empty')).toBeNull();
        expect(text(fixture, 'docker-last-what')).toBe(r.what);
        expect(el(fixture, 'docker-last-pr')?.textContent?.trim() ?? null).toBe(r.pr);
        expect(text(fixture, 'docker-last-result')).toBe(r.chip);
        expect(el(fixture, 'docker-last-result')!.classList.contains(r.tone)).toBe(true);
        expect(
          text(fixture, 'docker-last-stack'),
          'the Stack tile names the command it ran, or the stack alone when the record has none',
        ).toBe(r.stack);
        expect(text(fixture, 'docker-last-duration')).toBe(r.duration);
        expect(text(fixture, 'docker-last-finished')).toContain(
          new Intl.DateTimeFormat('en-US', {
            month: 'short',
            day: 'numeric',
            year: 'numeric',
          }).format(new Date(r.run.finishedAt)),
        );
        expect(el(fixture, 'docker-last-reason')?.textContent?.trim() ?? null).toBe(r.reason);
      });
    }

    const empties: readonly [string, Options][] = [
      ['no run', { lastRun: null }],
      ['the read failing', { lastRun: 'fail' }],
    ];
    for (const [name, o] of empties) {
      it(`${name}: the section says no run yet`, async () => {
        const { fixture } = await mount(o);
        const summary = text(fixture, 'docker-toggle-last');
        await click(fixture, 'docker-toggle-last');

        expect(summary).toBe('Last run: none yet');
        expect(text(fixture, 'docker-last-empty')).toBe('No run yet.');
      });
    }

    it('Refresh reads the records again and shows a run that ended since the card opened', async () => {
      const { fixture, bridge } = await mount();
      bridge.dockerLastRun.mockResolvedValueOnce({ run: RUN });

      await click(fixture, 'docker-refresh');

      expect(text(fixture, 'docker-toggle-last')).toBe('Last run: octocat/hello #42, passed');
    });
  });

  describe('sections the owner can hide', () => {
    const defaults: readonly [string, Options, DockerSection[]][] = [
      ['Docker running with an image missing', {}, ['images']],
      ['Docker running with every image downloaded', { images: ALL_PRESENT }, []],
      ['Docker not running', { state: { state: 'notRunning' } }, []],
      ['Docker not installed', { state: { state: 'notInstalled' } }, []],
    ];
    for (const [name, o, open] of defaults) {
      it(`${name}: by default ${open.length === 0 ? 'every section is closed' : `only ${open.join(', ')} is open`}, and the switch always shows`, async () => {
        const { fixture } = await mount(o);

        expect(expanded(fixture)).toEqual(open);
        expect(el(fixture, 'docker-switch')).not.toBeNull();
        for (const id of DOCKER_SECTIONS) {
          expect(el(fixture, `docker-body-${id}`) !== null).toBe(open.includes(id));
        }
      });
    }

    for (const id of DOCKER_SECTIONS) {
      it(`${id}: a click opens it with aria-controls naming its body, and a second click closes it`, async () => {
        const { fixture } = await mount({ images: ALL_PRESENT });
        const toggle = el(fixture, `docker-toggle-${id}`)!;

        await click(fixture, `docker-toggle-${id}`);
        const body = el(fixture, `docker-body-${id}`);
        const openState = toggle.getAttribute('aria-expanded');
        await click(fixture, `docker-toggle-${id}`);

        expect(openState).toBe('true');
        expect(body?.id).toBe(toggle.getAttribute('aria-controls'));
        expect(toggle.getAttribute('aria-expanded')).toBe('false');
        expect(el(fixture, `docker-body-${id}`)).toBeNull();
      });
    }

    it("remembers the owner's choices across restarts, over the defaults", async () => {
      const first = await mount();
      await click(first.fixture, 'docker-toggle-images');
      await click(first.fixture, 'docker-toggle-folder');
      const stored = JSON.parse(window.localStorage.getItem(DOCKER_SECTIONS_STORAGE_KEY) ?? '{}');
      TestBed.resetTestingModule();

      const second = await mount();

      expect(stored).toEqual({ images: false, folder: true });
      expect(expanded(second.fixture)).toEqual(['folder']);
    });

    it('ignores a stored value that is not a choice, and keeps the defaults for it', async () => {
      window.localStorage.setItem(
        DOCKER_SECTIONS_STORAGE_KEY,
        JSON.stringify({ uses: true, images: 'yes', other: true }),
      );

      const { fixture } = await mount();

      expect(expanded(fixture)).toEqual(['uses', 'images']);
    });

    it('storage that cannot be read falls back to the defaults', async () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new Error('storage disabled');
      });

      const { fixture } = await mount();

      expect(expanded(fixture)).toEqual(['images']);
    });

    it('storage that cannot be written still opens and closes the section', async () => {
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new Error('quota exceeded');
      });
      const { fixture } = await mount();

      await click(fixture, 'docker-toggle-uses');

      expect(expanded(fixture)).toEqual(['uses', 'images']);
    });

    it('stored text that is not JSON falls back to the defaults', async () => {
      window.localStorage.setItem(DOCKER_SECTIONS_STORAGE_KEY, '{not json');

      const { fixture } = await mount();

      expect(expanded(fixture)).toEqual(['images']);
    });

    const summaries: readonly [DockerSection, Options, string][] = [
      [
        'uses',
        {},
        "What Scout uses Docker for: a pull request's tests, offline, 20 minutes at most",
      ],
      ['images', { images: ALL_PRESENT }, 'Images: 5 of 5 downloaded'],
      ['images', { state: { state: 'notRunning' } }, 'Images: Docker is not running'],
      ['repositories', {}, 'Repositories: none read yet'],
      ['folder', {}, 'Work folder: 184 MB'],
      ['folder', { folder: { path: FOLDER.path, bytes: 0, runs: 0 } }, 'Work folder: empty'],
      ['last', {}, 'Last run: none yet'],
    ];
    for (const [id, o, summary] of summaries) {
      it(`${id} closed reads "${summary}", and open reads its title`, async () => {
        const { fixture } = await mount(o);
        const closedText = text(fixture, `docker-toggle-${id}`);

        await click(fixture, `docker-toggle-${id}`);

        expect(closedText).toBe(summary);
        expect(text(fixture, `docker-toggle-${id}`)).not.toBe(summary);
      });
    }

    it('formats the folder size the way the card shows it', () => {
      expect([0, 900, 2048, 15 * 1024 * 1024, 3.5 * 1024 ** 3].map(formatBytes)).toEqual([
        '0 B',
        '900 B',
        '2.0 KB',
        '15 MB',
        '3.5 GB',
      ]);
    });
  });

  describe("the panel's actions over Docker's state: the toast, the controls and the calls", () => {
    type Docker = 'not installed' | 'not running' | 'running';
    const STATES: Readonly<Record<Docker, DockerState>> = {
      'not installed': { state: 'notInstalled' },
      'not running': { state: 'notRunning' },
      running: RUNNING,
    };
    const DOCKERS = Object.keys(STATES) as Docker[];
    const LINES: Readonly<Record<Docker, string>> = {
      'not installed': 'Docker is not installed on this computer.',
      'not running':
        'Docker is installed but not running. Start Docker Desktop or the Docker service.',
      running: 'Docker Engine 28.5.1 answers on this computer.',
    };
    const REFRESHED: Readonly<Record<Docker, string>> = {
      'not installed': 'Docker is still not installed',
      'not running': 'Docker is still not running',
      running: 'Docker is running',
    };
    const NODE20 = image('Node', 'node:20', false);
    const HELLO: DockerRepository = {
      repository: 'octocat/hello',
      commit: 'abc1234def5678',
      stack: 'Node',
      image: NODE20.reference,
      runs: 'Node 20.20.2',
      declared: [{ source: '.nvmrc', version: 'Node 20' }],
      refusal: null,
      detectedAt: '2026-11-07T10:00:00+00:00',
    };
    const LIMITED = 'Docker Hub is limiting downloads; try again later.';
    const UNREADABLE = 'Scout could not ask Docker for its state. Try Refresh.';
    const KEPT =
      "184 MB used · the logs of 12 runs; each run's copy of the code is deleted after it";
    const shownText = (fixture: ComponentFixture<DockerCardComponent>, test: string) =>
      el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;

    const refreshes = DOCKERS.flatMap((docker) => [
      {
        docker,
        outcome: 'done',
        fails: false,
        notices: [[REFRESHED[docker], 'info']],
        line: LINES[docker],
      },
      { docker, outcome: 'the call fails', fails: true, notices: [], line: UNREADABLE },
    ]);
    it.each(refreshes.map((r) => [r.docker, r.outcome, r] as const))(
      'Docker %s, Refresh %s: one toast saying what Docker answered, none when the call fails and the line says so, and nothing at load',
      async (_docker, _outcome, r) => {
        const { fixture, bridge, notices } = await mount({ state: STATES[r.docker] });
        const atLoad = notices();
        if (r.fails) bridge.dockerState.mockRejectedValueOnce(new Error('bridge down'));

        await click(fixture, 'docker-refresh');

        expect({
          atLoad,
          notices: notices(),
          line: text(fixture, 'docker-status-line'),
          asked: bridge.dockerState.mock.calls.length,
          refreshEnabled: !(el(fixture, 'docker-refresh') as HTMLButtonElement).disabled,
        }).toEqual({
          atLoad: [],
          notices: r.notices,
          line: r.line,
          asked: 2,
          refreshEnabled: true,
        });
      },
    );

    const switches = DOCKERS.flatMap((docker) =>
      (['on', 'off'] as const).flatMap((turn) => [
        {
          docker,
          turn,
          outcome: 'done',
          storeFails: false,
          restartFails: false,
          checked: turn === 'on',
          error: null,
        },
        {
          docker,
          turn,
          outcome: 'refused',
          storeFails: true,
          restartFails: false,
          checked: turn === 'off',
          error: 'The vault is locked.',
        },
        {
          docker,
          turn,
          outcome: 'the call fails',
          storeFails: false,
          restartFails: true,
          checked: turn === 'on',
          error: 'Saved, but Scout could not restart to apply it: main is gone',
        },
      ]),
    );
    it.each(switches.map((r) => [r.docker, r.turn, r.outcome, r] as const))(
      'Docker %s, the panel switch turned %s, %s: no toast from the panel switch, the setting written once, the switch and its error as the outcome left them',
      async (_docker, _turn, _outcome, r) => {
        const { fixture, bridge, notices } = await mount({
          state: STATES[r.docker],
          enabled: r.turn === 'on' ? 'false' : 'true',
          worker: 'running',
          storeFails: r.storeFails,
        });
        if (r.restartFails) bridge.workerRestart.mockRejectedValueOnce(new Error('main is gone'));

        switchInput(fixture).click();
        await settle(fixture);

        expect({
          notices: notices(),
          written: bridge.vaultStore.mock.calls,
          checked: switchInput(fixture).getAttribute('aria-checked'),
          error: shownText(fixture, 'docker-switch-error'),
          switchEnabled: !switchInput(fixture).disabled,
        }).toEqual({
          notices: [],
          written: [[{ key: 'sandbox.enabled', value: r.turn === 'on' ? 'true' : 'false' }]],
          checked: String(r.checked),
          error: r.error,
          switchEnabled: true,
        });
      },
    );

    interface PullRow {
      readonly docker: Docker;
      readonly outcome: string;
      readonly answer: 'progress' | 'refused' | 'throws' | 'none';
      readonly enabled: boolean;
      readonly notices: readonly (readonly string[])[];
      readonly error: string | null;
    }

    const pulls: readonly PullRow[] = DOCKERS.flatMap((docker): PullRow[] =>
      docker === 'running'
        ? [
            {
              docker,
              outcome: 'done',
              answer: 'progress',
              enabled: true,
              notices: [['Downloading 1 image in Docker', 'info']],
              error: null,
            },
            {
              docker,
              outcome: 'refused',
              answer: 'refused',
              enabled: true,
              notices: [],
              error: `${LIMITED}`,
            },
            {
              docker,
              outcome: 'the call fails',
              answer: 'throws',
              enabled: true,
              notices: [],
              error: 'bridge down',
            },
          ]
        : [
            {
              docker,
              outcome: 'disabled',
              answer: 'none',
              enabled: false,
              notices: [],
              error: null,
            },
          ],
    );
    it.each(pulls.map((r) => [r.docker, r.outcome, r] as const))(
      'Docker %s, Download images %s: the toast only once Docker reports progress, the error otherwise, and no call while it is disabled',
      async (_docker, _outcome, r) => {
        const { fixture, bridge, notices } = await mount({
          state: STATES[r.docker],
          images: [NODE20],
          repositories: [HELLO],
        });
        if (r.answer === 'refused') {
          bridge.dockerPullImage.mockImplementationOnce(async () => ({ error: LIMITED }));
        }
        if (r.answer === 'throws') {
          bridge.dockerPullImage.mockImplementationOnce(async () => {
            throw new Error('bridge down');
          });
        }
        await open(fixture, 'images');
        const button = el(fixture, 'docker-download') as HTMLButtonElement;
        const enabled = !button.disabled;

        button.click();
        await settle(fixture);

        expect({
          enabled,
          calls: bridge.dockerPullImage.mock.calls,
          notices: notices(),
          error: shownText(fixture, 'docker-pull-error'),
        }).toEqual({
          enabled: r.enabled,
          calls: r.enabled ? [[{ reference: NODE20.reference }]] : [],
          notices: r.notices,
          error: r.error,
        });
      },
    );

    const rowPulls: readonly PullRow[] = DOCKERS.flatMap((docker): PullRow[] =>
      docker === 'running'
        ? [
            {
              docker,
              outcome: 'done',
              answer: 'progress',
              enabled: true,
              notices: [],
              error: null,
            },
            {
              docker,
              outcome: 'refused',
              answer: 'refused',
              enabled: true,
              notices: [],
              error: LIMITED,
            },
            {
              docker,
              outcome: 'the call fails',
              answer: 'throws',
              enabled: true,
              notices: [],
              error: 'bridge down',
            },
          ]
        : [
            {
              docker,
              outcome: 'disabled',
              answer: 'none',
              enabled: false,
              notices: [],
              error: null,
            },
          ],
    );
    it.each(rowPulls.map((r) => [r.docker, r.outcome, r] as const))(
      "Docker %s, a repository's Download %s: drawn in every state, disabled with its reason while Docker is not running, never a toast",
      async (_docker, _outcome, r) => {
        const { fixture, bridge, notices } = await mount({
          state: STATES[r.docker],
          images: [NODE20],
          repositories: [HELLO],
        });
        if (r.answer === 'refused') {
          bridge.dockerPullImage.mockImplementationOnce(async () => ({ error: LIMITED }));
        }
        if (r.answer === 'throws') {
          bridge.dockerPullImage.mockImplementationOnce(async () => {
            throw new Error('bridge down');
          });
        }
        await open(fixture, 'repositories');
        const drawn = el(fixture, 'docker-repository-download');
        expect(drawn, "the repository's Download is drawn").not.toBeNull();
        const button = drawn as HTMLButtonElement;
        const reason = button.getAttribute('aria-describedby');
        const described =
          reason === null ? null : (document.getElementById(reason)?.textContent ?? '').trim();
        const name = button.getAttribute('aria-label');
        const enabled = !button.disabled;

        button.click();
        await settle(fixture);

        expect({
          name,
          enabled,
          described,
          calls: bridge.dockerPullImage.mock.calls,
          notices: notices(),
          error: shownText(fixture, 'docker-pull-error'),
        }).toEqual({
          name: 'Download the image for octocat/hello',
          enabled: r.enabled,
          described: r.enabled ? null : 'Docker is not running',
          calls: r.enabled ? [[{ reference: NODE20.reference }]] : [],
          notices: r.notices,
          error: r.error,
        });
      },
    );

    const clears = DOCKERS.flatMap((docker) => [
      {
        docker,
        outcome: 'done',
        answer: 'cleared',
        notices: [['Work folder cleared', 'success']],
        size: 'Empty · run logs are deleted',
        refusal: null,
        clearEnabled: false,
      },
      {
        docker,
        outcome: 'refused',
        answer: 'refused',
        notices: [],
        size: KEPT,
        refusal: 'A test run is in progress in the work folder; clear it after the run ends.',
        clearEnabled: true,
      },
      {
        docker,
        outcome: 'the call fails',
        answer: 'throws',
        notices: [],
        size: KEPT,
        refusal: 'bridge down',
        clearEnabled: true,
      },
    ]);
    it.each(clears.map((r) => [r.docker, r.outcome, r] as const))(
      'Docker %s, Clear %s: "Work folder cleared" and the emptied line only when main cleared it, its sentence otherwise',
      async (_docker, _outcome, r) => {
        const { fixture, bridge, notices } = await mount({ state: STATES[r.docker] });
        if (r.answer === 'refused') {
          bridge.dockerClear.mockResolvedValueOnce({ refused: r.refusal, folder: FOLDER });
        }
        if (r.answer === 'throws')
          bridge.dockerClear.mockRejectedValueOnce(new Error('bridge down'));
        await open(fixture, 'folder');

        await click(fixture, 'docker-clear');
        await answer(fixture, 'confirm-ok');

        expect({
          calls: bridge.dockerClear.mock.calls.length,
          notices: notices(),
          size: text(fixture, 'docker-folder-size'),
          refusal: shownText(fixture, 'docker-clear-refused'),
          clearEnabled: !(el(fixture, 'docker-clear') as HTMLButtonElement).disabled,
        }).toEqual({
          calls: 1,
          notices: r.notices,
          size: r.size,
          refusal: r.refusal,
          clearEnabled: r.clearEnabled,
        });
      },
    );
  });
});
