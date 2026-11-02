import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { SlackSaveRequest, SlackStatus, WorkerStatus } from '@scout/shared';

import { LogStore } from '../../core/log.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

import { SlackSetupComponent } from './slack-setup.component';

const BOT_TOKEN = 'xoxb-1111-2222-bot-secret';
const APP_TOKEN = 'xapp-1-A111-3333-app-secret';
const MANIFEST_URL = 'https://api.slack.com/apps?new_app=1&manifest_json=%7B%7D';
const NOT_CONNECTED: SlackStatus = { connected: false, team: null, botUser: null };
const CONNECTED: SlackStatus = { connected: true, team: 'Acme', botUser: 'scout' };

function makeBridge(first: SlackStatus = NOT_CONNECTED) {
  return {
    slackStatus: vi.fn(async (): Promise<SlackStatus> => first),
    slackManifestUrl: vi.fn(async () => ({ url: MANIFEST_URL })),
    slackSave: vi.fn(async (_req: SlackSaveRequest): Promise<SlackStatus> => CONNECTED),
    slackDisconnect: vi.fn(async (): Promise<SlackStatus> => NOT_CONNECTED),
    appOpenExternal: vi.fn(async () => undefined),
    workerStart: vi.fn(async () => ({ pid: 7 })),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
  workerState: WorkerStatus['state'] = 'idle',
) {
  const setSlackConnected = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: WizardStore, useValue: { setSlackConnected } },
      {
        provide: LogStore,
        useValue: { status: signal<WorkerStatus>({ state: workerState, restarts: 0 }) },
      },
    ],
  });
  const fixture = TestBed.createComponent(SlackSetupComponent);
  await settle(fixture);
  return { fixture, setSlackConnected };
}

async function settle(fixture: ComponentFixture<SlackSetupComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<SlackSetupComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<SlackSetupComponent>, test: string): string {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

async function type(
  fixture: ComponentFixture<SlackSetupComponent>,
  test: string,
  value: string,
): Promise<void> {
  const input = el(fixture, test) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

async function pasteBothAndSave(fixture: ComponentFixture<SlackSetupComponent>): Promise<void> {
  await type(fixture, 'slack-bot-token', BOT_TOKEN);
  await type(fixture, 'slack-app-token', APP_TOKEN);
  el(fixture, 'slack-save')?.click();
  await settle(fixture);
}

describe('Slack setup', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('Create the Slack app opens the prefilled manifest link through the allow-listed external opener', async () => {
    const bridge = makeBridge();
    const { fixture } = await render(bridge);

    el(fixture, 'slack-create')?.click();
    await settle(fixture);

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: MANIFEST_URL });
  });

  it('tells the owner how to get both tokens, including the connections:write scope', async () => {
    const { fixture } = await render(makeBridge());

    const steps = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-test="slack-steps"] li',
      ) as NodeListOf<HTMLElement>,
    ).map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(steps).toEqual([
      'Pick the workspace and click Create.',
      'Click Install to Workspace, then copy the Bot User OAuth Token.',
      'Open Basic Information, App-Level Tokens, Generate Token and Scopes, add the scope connections:write, Generate, then copy the token.',
      'In Slack, invite @scout to each channel where it should answer.',
    ]);
  });

  it('takes both tokens in password fields and keeps Save disabled until both are filled', async () => {
    const { fixture } = await render(makeBridge());

    expect((el(fixture, 'slack-bot-token') as HTMLInputElement).type).toBe('password');
    expect((el(fixture, 'slack-app-token') as HTMLInputElement).type).toBe('password');
    await type(fixture, 'slack-bot-token', BOT_TOKEN);
    expect((el(fixture, 'slack-save') as HTMLButtonElement).disabled).toBe(true);
    await type(fixture, 'slack-app-token', APP_TOKEN);
    expect((el(fixture, 'slack-save') as HTMLButtonElement).disabled).toBe(false);
  });

  it('Save sends both tokens to main, then shows "Connected to <team> as <botUser>" and no token', async () => {
    const bridge = makeBridge();
    const { fixture, setSlackConnected } = await render(bridge);

    await pasteBothAndSave(fixture);

    expect(bridge.slackSave).toHaveBeenCalledWith({ botToken: BOT_TOKEN, appToken: APP_TOKEN });
    expect(text(fixture, 'slack-connected')).toBe('Connected to Acme as scout');
    expect(setSlackConnected).toHaveBeenCalledWith(true);
    expect(fixture.nativeElement.innerHTML).not.toContain(BOT_TOKEN);
    expect(fixture.nativeElement.innerHTML).not.toContain(APP_TOKEN);
  });

  it('a first platform saved while the worker never started starts the worker', async () => {
    const bridge = makeBridge();
    const { fixture } = await render(bridge, 'idle');

    await pasteBothAndSave(fixture);

    expect(bridge.workerStart).toHaveBeenCalledTimes(1);
  });

  it('a running worker is left to main, which restarts it', async () => {
    const bridge = makeBridge();
    const { fixture } = await render(bridge, 'running');

    await pasteBothAndSave(fixture);

    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it("a token Slack rejects shows main's reason and stays on the form", async () => {
    const bridge = makeBridge();
    bridge.slackSave.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:slack.save': Error: [scout:slack/token-rejected] Slack rejected the app-level token.",
      ),
    );
    const { fixture, setSlackConnected } = await render(bridge);

    await pasteBothAndSave(fixture);

    expect(text(fixture, 'slack-error')).toBe('Slack rejected the app-level token.');
    expect(el(fixture, 'slack-connected')).toBeNull();
    expect(el(fixture, 'slack-save')).not.toBeNull();
    expect(setSlackConnected).not.toHaveBeenCalled();
    expect(bridge.workerStart).not.toHaveBeenCalled();
  });

  it('Disconnect clears the connection and returns to the setup steps', async () => {
    const bridge = makeBridge(CONNECTED);
    const { fixture, setSlackConnected } = await render(bridge);
    expect(text(fixture, 'slack-connected')).toBe('Connected to Acme as scout');

    el(fixture, 'slack-disconnect')?.click();
    await settle(fixture);

    expect(bridge.slackDisconnect).toHaveBeenCalledTimes(1);
    expect(el(fixture, 'slack-connected')).toBeNull();
    expect(el(fixture, 'slack-create')).not.toBeNull();
    expect(setSlackConnected).toHaveBeenCalledWith(false);
  });
});
