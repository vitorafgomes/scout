import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';
import { WorkerLaunchComponent } from './worker-launch.component';

function makeBridge() {
  return {
    vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
  } as unknown as ScoutBridgeService;
}

async function setup(clientId: string | null, guildName: string | null) {
  const bridge = makeBridge();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: '', children: [] }]),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const store = TestBed.inject(WizardStore);
  if (clientId !== null) store.setClientId(clientId);
  if (guildName !== null) store.setGuild({ id: 'g1', name: guildName });
  const fixture = TestBed.createComponent(WorkerLaunchComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, bridge };
}

describe('WorkerLaunchComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the placeholder copy and guild name when a guild is present', async () => {
    const { fixture } = await setup('cid', 'Scout Dev');
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Scout is ready to start in');
    expect(text).toContain('Scout Dev');
    expect(text).toContain('worker supervisor ships in the next release');
  });

  it('redirects to / when no guild is in the store', async () => {
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
    const fixture = TestBed.createComponent(WorkerLaunchComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(nav).toHaveBeenCalledWith('/');
  });

  it('back-to-home resets the wizard and navigates home', async () => {
    const { fixture, bridge } = await setup('cid', 'Scout Dev');
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigateByUrl');

    const btn = fixture.nativeElement.querySelector('[data-test="back-home"]') as HTMLButtonElement;
    btn.click();
    await fixture.whenStable();

    expect(bridge.vaultDelete).toHaveBeenCalledTimes(2);
    expect(TestBed.inject(WizardStore).guild()).toBeNull();
    expect(TestBed.inject(WizardStore).clientId()).toBeNull();
    expect(nav).toHaveBeenCalledWith('/');
  });
});
