import { readFileSync } from 'node:fs';
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { WorkerEvent, WorkerStatus } from '@scout/shared';

import { LlmConfigStore } from '../core/llm-config.store';
import { LogStore } from '../core/log.store';
import { PersistentStateService } from '../core/persistent-state';
import { ScoutBridgeService } from '../core/scout-bridge';
import { WorkerLaunchComponent } from '../wizard/worker-launch/worker-launch.component';
import { PostConnectShellComponent } from './post-connect-shell.component';
import { WizardStore } from '../core/wizard.store';
import { ShellComponent } from './shell.component';

@Component({ selector: 'sc-stub-page', standalone: true, template: 'page' })
class StubPage {}

async function render(
  provisioned: boolean,
  url = '/',
  step = 'welcome',
  worker: WorkerStatus['state'] = 'idle',
  bridge?: Partial<ScoutBridgeService>,
): Promise<HTMLElement> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      ...(bridge === undefined ? [] : [{ provide: ScoutBridgeService, useValue: bridge }]),
      provideRouter(
        ['', 'provision', 'token', 'oauth', 'launch', 'ready', 'slack', 'settings', 'llm'].map(
          (path) => ({ path, component: StubPage }),
        ),
      ),
      {
        provide: WizardStore,
        useValue: { hasProvisioning: signal(provisioned), currentStep: signal(step) },
      },
      { provide: LogStore, useValue: { status: signal({ state: worker, restarts: 0 }) } },
      {
        provide: LlmConfigStore,
        useValue: {
          provider: signal(null),
          model: signal(null),
          loadFromVault: async () => undefined,
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(ShellComponent);
  await TestBed.inject(Router).navigateByUrl(url);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ShellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const [provisioned, url, host, main] of [
    [false, '/', false, null],
    [false, '/provision', false, null],
    [false, '/token', false, null],
    [false, '/oauth?retry=1', false, null],
    [false, '/launch', false, null],
    [false, '/ready', false, null],
    [false, '/slack', true, null],
    [false, '/settings', false, null],
    [true, '/launch', false, false],
    [true, '/ready', false, false],
    [true, '/', false, false],
    [true, '/llm', false, false],
  ] as const) {
    it(`${provisioned ? 'after setup' : 'before setup'} on ${url} the frame ${host ? 'stays on the dark canvas' : 'follows the theme'}${main === null ? '' : `, the page area ${main ? 'stays dark' : 'follows it too'}`}`, async () => {
      const node = await render(provisioned, url);
      const area = node.querySelector('main.main');

      expect({
        host: node.classList.contains('sc-dark-canvas'),
        main: area === null ? null : area.classList.contains('sc-dark-canvas'),
        stepper: node.querySelector('sc-step-indicator') !== null,
      }).toEqual({ host, main, stepper: !provisioned });
    });
  }

  for (const [worker, wizardFrame] of [
    ['starting', true],
    ['crashed', true],
    ['running', false],
  ] as const) {
    it(`a server just chosen, on Launch with the worker ${worker}: ${wizardFrame ? 'the wizard keeps its frame and stepper' : 'the main shell takes over'}`, async () => {
      const node = await render(true, '/launch', 'launch', worker);

      expect({
        stepper: node.querySelector('sc-step-indicator') !== null,
        mainShell: node.querySelector('sc-post-connect-shell') !== null,
      }).toEqual({ stepper: wizardFrame, mainShell: !wizardFrame });
    });
  }
});

describe('The setup header as in the prototype: Scout setup, the step count and the theme switch', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
  });

  const STEPS = [
    ['welcome', '/', 'Chat app'],
    ['provision', '/provision', 'Create bot'],
    ['token', '/token', 'Bot token'],
    ['oauth', '/oauth', 'Add to server'],
    ['launch', '/launch', 'Launch'],
    ['ready', '/ready', 'Ready'],
  ] as const;

  for (const [index, [step, url, name]] of STEPS.entries()) {
    it(`on ${name} the header reads "Scout setup" and "Step ${index + 1} of 6 · ${name}", with the theme switch, above the stepper's six steps`, async () => {
      const node = await render(false, url, step);
      const header = node.querySelector('[data-test="wizard-header"]') as HTMLElement;
      const stepper = node.querySelector('sc-step-indicator') as HTMLElement;

      expect({
        title: header.querySelector('[data-test="wizard-title"]')?.textContent?.trim(),
        counter: header.querySelector('[data-test="step-counter"]')?.textContent?.trim(),
        theme: Array.from(
          header.querySelectorAll('[role="group"][aria-label="Theme"] button'),
          (button) => button.getAttribute('aria-label'),
        ),
        aboveStepper:
          (header.compareDocumentPosition(stepper) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
        steps: stepper.querySelectorAll('li.step').length,
      }).toEqual({
        title: 'Scout setup',
        counter: `Step ${index + 1} of 6 · ${name}`,
        theme: ['Light theme', 'Dark theme', 'Follow the system theme'],
        aboveStepper: true,
        steps: 6,
      });
    });
  }

  it('after setup the main shell shows no setup header', async () => {
    const node = await render(true, '/');

    expect(node.querySelector('[data-test="wizard-header"]')).toBeNull();
  });

  it("choosing Light asks main for the light theme and the switch shows main's answer; when main refuses Dark, Light stays pressed", async () => {
    const themeSet = vi
      .fn()
      .mockResolvedValueOnce({ choice: 'light', resolved: 'light' })
      .mockRejectedValueOnce(new Error('main is busy'));
    const node = await render(false, '/', 'welcome', 'idle', {
      themeSet,
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
    });
    const pressed = (): (string | null)[] =>
      ['theme-light', 'theme-dark', 'theme-system'].map(
        (test) => node.querySelector(`[data-test="${test}"]`)?.getAttribute('aria-pressed') ?? null,
      );
    const click = async (test: string): Promise<void> => {
      (node.querySelector(`[data-test="${test}"]`) as HTMLButtonElement).click();
      await Promise.allSettled(themeSet.mock.results.map((result) => result.value as unknown));
      TestBed.tick();
    };
    const before = pressed();

    await click('theme-light');
    const afterLight = { pressed: pressed(), page: document.documentElement.dataset['theme'] };
    await click('theme-dark');

    expect({
      before,
      asked: themeSet.mock.calls.map(([choice]) => choice),
      afterLight,
      afterRefusedDark: { pressed: pressed(), page: document.documentElement.dataset['theme'] },
    }).toEqual({
      before: ['false', 'false', 'false'],
      asked: ['light', 'dark'],
      afterLight: { pressed: ['true', 'false', 'false'], page: 'light' },
      afterRefusedDark: { pressed: ['true', 'false', 'false'], page: 'light' },
    });
  });
});

@Component({
  selector: 'sc-post-connect-shell',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
class MainShellOutlet {}

describe('The handover from the wizard frame to the main shell on Launch', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  it('starts the worker once and opens Ready once, though Launch mounts again in the main shell', async () => {
    const listeners = new Set<(event: WorkerEvent) => void>();
    let state: WorkerStatus['state'] = 'idle';
    const report = (status: WorkerStatus): void => {
      state = status.state;
      for (const listener of [...listeners]) listener({ kind: 'state', status });
    };
    let finishStart: () => void = () => undefined;
    const bridge = {
      workerStart: vi.fn(() =>
        state === 'running'
          ? Promise.resolve({ pid: 7 })
          : new Promise<{ pid: number }>((resolve) => {
              report({ state: 'starting', restarts: 0 });
              finishStart = () => {
                report({ state: 'running', restarts: 0, pid: 7 });
                resolve({ pid: 7 });
              };
            }),
      ),
      vaultHas: vi.fn().mockResolvedValue({ exists: false }),
      vaultGetPublic: vi.fn().mockResolvedValue({ value: null }),
      vaultIsAvailable: vi.fn().mockResolvedValue({ available: true }),
      onWorkerEvent: vi.fn((listener: (event: WorkerEvent) => void) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
      }),
    };
    TestBed.overrideComponent(ShellComponent, {
      remove: { imports: [PostConnectShellComponent] },
      add: { imports: [MainShellOutlet] },
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: '', component: StubPage },
          { path: 'launch', component: WorkerLaunchComponent },
          { path: 'ready', component: StubPage },
        ]),
        { provide: ScoutBridgeService, useValue: bridge },
        {
          provide: PersistentStateService,
          useValue: { get: () => null, set: vi.fn(), clear: vi.fn() },
        },
      ],
    });
    const store = TestBed.inject(WizardStore);
    store.setClientId('111222333');
    store.setGuild({ id: 'g1', name: 'Team' });
    store.goTo('launch');
    const fixture = TestBed.createComponent(ShellComponent);
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/launch');
    const settle = async (): Promise<void> => {
      for (let i = 0; i < 3; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
    };
    await settle();
    const framedWhileStarting = fixture.nativeElement.querySelector('sc-step-indicator') !== null;
    const nav = vi.spyOn(router, 'navigateByUrl');

    finishStart();
    await settle();
    await new Promise((resolve) => setTimeout(resolve, 300));
    await settle();

    expect({
      framedWhileStarting,
      starts: bridge.workerStart.mock.calls.length,
      readyOpened: nav.mock.calls.filter(([url]) => url === '/ready').length,
      url: router.url,
    }).toEqual({ framedWhileStarting: true, starts: 1, readyOpened: 1, url: '/ready' });
  });
});

describe("the pressed segment of the setup wizard header's theme switch (Wizard.dc.html)", () => {
  it('is on the pressed colour every artboard draws, white in light and #253249 in dark, under the text colour', () => {
    const source = readFileSync('src/app/shell/shell.component.ts', 'utf8');
    const block = /\.theme-option\[aria-pressed='true'\] \{([^}]*)\}/.exec(source)?.[1] ?? '';

    expect({
      background: /background:\s*([^;]+);/.exec(block)?.[1] ?? null,
      color: /color:\s*([^;]+);/.exec(block)?.[1] ?? null,
    }).toEqual({ background: 'var(--sc-pressed)', color: 'var(--sc-fg)' });
  });
});
