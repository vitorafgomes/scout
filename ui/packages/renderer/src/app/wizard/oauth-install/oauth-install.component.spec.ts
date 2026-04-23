import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import {
  DiscordTokenExchangeError,
  OAuthLoopbackPortInUseError,
  OAuthStateMismatchError,
  OAuthUserCancelledError,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { OAUTH_INSTALL_PERMISSIONS, OAuthInstallComponent } from './oauth-install.component';

type ResolvablePromise<T> = {
  readonly promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
};

function createResolvable<T>(): ResolvablePromise<T> {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason: unknown) => void = () => undefined;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function makeBridge(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    discordStartOAuthInstall: vi.fn(),
    discordCancelOAuthInstall: vi.fn().mockResolvedValue(undefined),
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
    ...overrides,
  } as unknown as ScoutBridgeService;
}

async function setup(bridge: ScoutBridgeService, clientId: string | null) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: '', children: [] },
        { path: 'token', children: [] },
        { path: 'launch', children: [] },
      ]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(WizardStore);
  if (clientId !== null) {
    store.setClientId(clientId);
  }
  const fixture = TestBed.createComponent(OAuthInstallComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('OAuthInstallComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('auto-starts the install with the computed permissions integer', async () => {
    const gate = createResolvable<{ guildId: string; guildName: string }>();
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockReturnValue(gate.promise),
    });
    await setup(bridge, '1234567890');

    expect(bridge.discordStartOAuthInstall).toHaveBeenCalledTimes(1);
    expect(bridge.discordStartOAuthInstall).toHaveBeenCalledWith({
      clientId: '1234567890',
      permissions: OAUTH_INSTALL_PERMISSIONS,
    });
    // Permission integer must be greater than 2^31, exercising BigInt math.
    expect(BigInt(OAUTH_INSTALL_PERMISSIONS)).toBeGreaterThan(2n ** 31n);
  });

  it('navigates to /launch with guild in the store on success', async () => {
    const gate = createResolvable<{ guildId: string; guildName: string }>();
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockReturnValue(gate.promise),
    });
    const fixture = await setup(bridge, '1234567890');
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    gate.resolve({ guildId: 'g42', guildName: 'Scout Dev' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.state()).toBe('success');
    expect(TestBed.inject(WizardStore).guild()).toEqual({ id: 'g42', name: 'Scout Dev' });

    const cont = fixture.nativeElement.querySelector('[data-test="continue"]') as HTMLButtonElement;
    cont.click();
    expect(nav).toHaveBeenCalledWith('/launch');
  });

  it('redirects to / when no clientId is set in the store', async () => {
    const bridge = makeBridge();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '', children: [] }]),
        { provide: ScoutBridgeService, useValue: bridge },
      ],
    });
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');
    const fixture = TestBed.createComponent(OAuthInstallComponent);
    fixture.detectChanges();

    expect(bridge.discordStartOAuthInstall).not.toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith('/');
  });

  it('maps state-mismatch error to the security copy', async () => {
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockRejectedValue(new OAuthStateMismatchError()),
    });
    const fixture = await setup(bridge, '1234567890');

    expect(fixture.componentInstance.state()).toBe('error');
    expect(fixture.componentInstance.errorMessage()).toBe(
      'Authorization was rejected for security reasons. Try again.',
    );
  });

  it('maps user-cancelled error to the cancelled copy', async () => {
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockRejectedValue(new OAuthUserCancelledError()),
    });
    const fixture = await setup(bridge, '1234567890');

    expect(fixture.componentInstance.errorMessage()).toBe(
      'Authorization cancelled. Try again or go back.',
    );
  });

  it('maps loopback-port-in-use error to the port copy', async () => {
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockRejectedValue(new OAuthLoopbackPortInUseError()),
    });
    const fixture = await setup(bridge, '1234567890');

    expect(fixture.componentInstance.errorMessage()).toBe(
      'Cannot listen for the browser callback - close other Scout Desktop instances and retry.',
    );
  });

  it('surfaces token-exchange reason on discord/token-exchange-failed', async () => {
    const bridge = makeBridge({
      discordStartOAuthInstall: vi
        .fn()
        .mockRejectedValue(new DiscordTokenExchangeError('invalid_grant')),
    });
    const fixture = await setup(bridge, '1234567890');

    const msg = fixture.componentInstance.errorMessage();
    expect(msg).toContain('Token exchange failed');
    expect(msg).toContain('invalid_grant');
  });

  it('cancel during awaiting calls discordCancelOAuthInstall and routes to /token', async () => {
    const gate = createResolvable<{ guildId: string; guildName: string }>();
    const bridge = makeBridge({
      discordStartOAuthInstall: vi.fn().mockReturnValue(gate.promise),
    });
    const fixture = await setup(bridge, '1234567890');
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    expect(fixture.componentInstance.state()).toBe('awaiting');
    const cancelBtn = fixture.nativeElement.querySelector(
      '[data-test="cancel"]',
    ) as HTMLButtonElement;
    cancelBtn.click();
    await fixture.whenStable();

    expect(bridge.discordCancelOAuthInstall).toHaveBeenCalledTimes(1);
    expect(nav).toHaveBeenCalledWith('/token');
  });
});
