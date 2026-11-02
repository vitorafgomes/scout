import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScoutError, encodeIpcErrorMessage, serializeError } from '@scout/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PersistentStateService } from '../../core/persistent-state';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { OAuthInstallComponent } from './oauth-install.component';

const INVITE =
  'https://discord.com/oauth2/authorize?client_id=111222333&scope=bot&permissions=274877991936';
const OLD = { id: '1', name: 'Old Server' };
const NEW = { id: '2', name: 'Team' };

function ipcError(code: string, message: string): Error {
  return new Error(encodeIpcErrorMessage(serializeError(new ScoutError(code, message))));
}

function makeBridge(guilds: ReturnType<typeof vi.fn>) {
  return {
    discordGuilds: guilds,
    discordInviteUrl: vi.fn().mockResolvedValue({ url: INVITE }),
    appOpenExternal: vi.fn().mockResolvedValue(undefined),
    vaultStore: vi.fn().mockResolvedValue(undefined),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
  } as unknown as ScoutBridgeService & Record<string, ReturnType<typeof vi.fn>>;
}

async function setup(bridge: ScoutBridgeService, clientId: string | null = '111222333') {
  const persistent = { get: vi.fn().mockReturnValue(null), set: vi.fn(), clear: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'token', children: [] },
        { path: 'launch', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
      { provide: PersistentStateService, useValue: persistent },
    ],
  });
  if (clientId !== null) TestBed.inject(WizardStore).setClientId(clientId);
  const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
  const fixture = TestBed.createComponent(OAuthInstallComponent);
  fixture.detectChanges();
  await vi.advanceTimersByTimeAsync(0);
  fixture.detectChanges();
  return { fixture, nav, persistent };
}

describe('OAuthInstallComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('opens the invite and continues when the bot appears in a new server, storing it', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [OLD] })
      .mockResolvedValueOnce({ guilds: [OLD] })
      .mockResolvedValueOnce({ guilds: [OLD, NEW] });
    const bridge = makeBridge(guilds);
    const { fixture, nav, persistent } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INVITE });
    await vi.advanceTimersByTimeAsync(3_000);
    expect(nav).not.toHaveBeenCalledWith('/launch');
    await vi.advanceTimersByTimeAsync(3_000);
    fixture.detectChanges();

    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-id', value: '2' });
    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-name', value: 'Team' });
    expect(persistent.set).toHaveBeenCalledWith({
      clientId: '111222333',
      guildId: '2',
      guildName: 'Team',
    });
    expect(TestBed.inject(WizardStore).guild()).toEqual(NEW);
    expect(fixture.nativeElement.querySelector('[data-test="joined"]').textContent).toContain(
      'Scout joined Team',
    );
    expect(nav).toHaveBeenCalledWith('/launch');
  });

  it('offers the servers the bot is already in and stores the one the owner picks', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [OLD] }));
    const { fixture, nav } = await setup(bridge);

    const option = fixture.nativeElement.querySelector(
      '[data-test="guild-option"]',
    ) as HTMLButtonElement;
    expect(option.textContent?.trim()).toBe('Old Server');
    option.click();
    await vi.advanceTimersByTimeAsync(0);

    expect(bridge.vaultStore).toHaveBeenCalledWith({ key: 'discord.guild-id', value: '1' });
    expect(nav).toHaveBeenCalledWith('/launch');
  });

  it('never picks a server the bot was already in when the first server list was slow', async () => {
    let releaseFirst: (value: { guilds: (typeof OLD)[] }) => void = () => undefined;
    const guilds = vi
      .fn()
      .mockReturnValueOnce(new Promise((resolve) => (releaseFirst = resolve)))
      .mockResolvedValue({ guilds: [OLD] });
    const bridge = makeBridge(guilds);
    const { fixture, nav } = await setup(bridge);

    const invite = fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 2);
    releaseFirst({ guilds: [OLD] });
    await invite;
    await vi.advanceTimersByTimeAsync(3_000 * 3);

    expect(bridge.appOpenExternal).toHaveBeenCalledWith({ url: INVITE });
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(nav).not.toHaveBeenCalledWith('/launch');
  });

  it('does not open the invite while the current server list cannot be read', async () => {
    const guilds = vi
      .fn()
      .mockRejectedValue(ipcError('discord/network-error', 'Could not reach Discord.'));
    const bridge = makeBridge(guilds);
    const { fixture, nav } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    guilds.mockResolvedValue({ guilds: [OLD] });
    await vi.advanceTimersByTimeAsync(3_000 * 3);
    fixture.detectChanges();

    expect(bridge.appOpenExternal).not.toHaveBeenCalled();
    expect(bridge.vaultStore).not.toHaveBeenCalled();
    expect(nav).not.toHaveBeenCalledWith('/launch');
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Could not reach Discord.',
    );
  });

  it('keeps polling through a network error and stops at the bound', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [] })
      .mockRejectedValue(ipcError('discord/network-error', 'Could not reach Discord.'));
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 120);
    fixture.detectChanges();

    expect(guilds).toHaveBeenCalledTimes(1 + 100);
    expect(fixture.componentInstance.waiting()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Scout has not joined a server yet',
    );
  });

  it('stops polling and says so when Discord rejects the bot token', async () => {
    const guilds = vi
      .fn()
      .mockResolvedValueOnce({ guilds: [] })
      .mockRejectedValue(ipcError('discord/token-rejected', 'Discord rejected this bot token.'));
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    await vi.advanceTimersByTimeAsync(3_000 * 5);
    fixture.detectChanges();

    expect(guilds).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('[data-test="error-copy"]').textContent).toContain(
      'Discord rejected this bot token.',
    );
  });

  it('stops polling when the step is left', async () => {
    const guilds = vi.fn().mockResolvedValue({ guilds: [] });
    const bridge = makeBridge(guilds);
    const { fixture } = await setup(bridge);

    await fixture.componentInstance.openInvite();
    fixture.destroy();
    await vi.advanceTimersByTimeAsync(3_000 * 10);

    expect(guilds).toHaveBeenCalledTimes(1);
  });

  it('redirects to / when no application id is known', async () => {
    const bridge = makeBridge(vi.fn().mockResolvedValue({ guilds: [] }));
    const { nav } = await setup(bridge, null);

    expect(nav).toHaveBeenCalledWith('/');
    expect(bridge.discordGuilds).not.toHaveBeenCalled();
  });
});
