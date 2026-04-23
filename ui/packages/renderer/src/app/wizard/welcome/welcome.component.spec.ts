import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ScoutError, type DetectClientsResponse, type DetectedClient } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WelcomeComponent } from './welcome.component';

type ResolvablePromise<T> = {
  readonly promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
};

function createResolvablePromise<T>(): ResolvablePromise<T> {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason: unknown) => void = () => undefined;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

class FakeBridge {
  pending: ResolvablePromise<DetectClientsResponse> | null = null;
  calls = 0;

  detectClients(): Promise<DetectClientsResponse> {
    this.calls += 1;
    const next = createResolvablePromise<DetectClientsResponse>();
    this.pending = next;
    return next.promise;
  }
}

async function createComponent(bridge: FakeBridge): Promise<ComponentFixture<WelcomeComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: 'provision', children: [] }]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(WelcomeComponent);
  fixture.detectChanges();
  return fixture;
}

describe('WelcomeComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders a platform card per client when detection resolves', async () => {
    const bridge = new FakeBridge();
    const fixture = await createComponent(bridge);

    expect(bridge.calls).toBe(1);
    expect(fixture.componentInstance.loading()).toBe(true);

    const clients: readonly DetectedClient[] = [
      { kind: 'discord', installed: true, method: 'uri-scheme' },
      { kind: 'slack', installed: false, method: 'none' },
      { kind: 'teams', installed: false, method: 'none' },
    ];
    bridge.pending?.resolve({ clients });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.componentInstance.clients().length).toBe(3);

    const host = fixture.nativeElement as HTMLElement;
    const cards = host.querySelectorAll('.platform-card');
    expect(cards.length).toBe(3);
    const discordCard = host.querySelector('.platform-card[data-kind="discord"]');
    expect(discordCard).not.toBeNull();
    expect(discordCard?.querySelector('h2')?.textContent?.trim()).toBe('DISCORD');
    expect(discordCard?.querySelector('p')?.textContent?.trim()).toBe('Installed');
    const slackCard = host.querySelector('.platform-card[data-kind="slack"]');
    expect(slackCard?.querySelector('p')?.textContent?.trim()).toBe('Not installed');
  });

  it('renders the error state when detection rejects with a ScoutError', async () => {
    const bridge = new FakeBridge();
    const fixture = await createComponent(bridge);

    bridge.pending?.reject(new ScoutError('bridge/not-available', 'preload missing'));
    // allow the rejected promise to flow through
    await Promise.resolve();
    await Promise.resolve();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.componentInstance.clients().length).toBe(0);
    const message = fixture.componentInstance.error();
    expect(message).not.toBeNull();
    expect(message).toContain('bridge/not-available');

    const host = fixture.nativeElement as HTMLElement;
    const errorEl = host.querySelector('.error');
    expect(errorEl).not.toBeNull();
    expect(errorEl?.textContent ?? '').toContain('bridge/not-available');

    // On error, placeholder cards for all three CLIENT_KINDS remain visible.
    const cards = host.querySelectorAll('.platform-card');
    expect(cards.length).toBe(3);
    expect(host.querySelector('.platform-card[data-kind="discord"]')).not.toBeNull();
    expect(host.querySelector('.platform-card[data-kind="slack"]')).not.toBeNull();
    expect(host.querySelector('.platform-card[data-kind="teams"]')).not.toBeNull();
  });

  it('renders the loading state before the detection promise settles', async () => {
    const bridge = new FakeBridge();
    const fixture = await createComponent(bridge);

    expect(fixture.componentInstance.loading()).toBe(true);
    const host = fixture.nativeElement as HTMLElement;
    const loadingEl = host.querySelector('.loading');
    expect(loadingEl).not.toBeNull();
    expect(loadingEl?.textContent?.trim()).toBe('Detecting clients...');
    expect(host.querySelector('.platform-card')).toBeNull();
    expect(host.querySelector('.error')).toBeNull();
  });

  it('enables Provision Discord button after detection resolves and navigates to /provision on click', async () => {
    const bridge = new FakeBridge();
    const fixture = await createComponent(bridge);
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const host = fixture.nativeElement as HTMLElement;
    const cta = host.querySelector('[data-test="provision-discord"]') as HTMLButtonElement;
    expect(cta).not.toBeNull();
    expect(cta.disabled).toBe(true);

    bridge.pending?.resolve({ clients: [] });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(cta.disabled).toBe(false);

    cta.click();
    expect(nav).toHaveBeenCalledWith('/provision');
    expect(TestBed.inject(WizardStore).currentStep()).toBe('provision');
  });
});
