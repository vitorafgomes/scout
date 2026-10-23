import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { AccessAllowedUsers } from '@scout/shared';

import { ScoutBridgeService } from '../../core/scout-bridge';
import { WizardStore } from '../../core/wizard.store';

import { SettingsComponent } from './settings.component';

function makeBridge(initial: readonly string[] = []) {
  let stored: readonly string[] = initial;
  return {
    appGetVersion: vi.fn(async () => ({ version: '1.0.0' })),
    accessGet: vi.fn(async (): Promise<AccessAllowedUsers> => ({ userIds: stored })),
    accessSet: vi.fn(async (req: AccessAllowedUsers): Promise<AccessAllowedUsers> => {
      stored = [...new Set(req.userIds.map((id) => id.trim()))];
      return { userIds: stored };
    }),
  };
}

async function render(
  bridge: ReturnType<typeof makeBridge>,
): Promise<ComponentFixture<SettingsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: WizardStore, useValue: {} },
    ],
  });
  const fixture = TestBed.createComponent(SettingsComponent);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<SettingsComponent>): Promise<void> {
  for (let i = 0; i < 4; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<SettingsComponent>, test: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement | null;
}

function text(fixture: ComponentFixture<SettingsComponent>, test: string): string {
  return el(fixture, test)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function listed(fixture: ComponentFixture<SettingsComponent>): string[] {
  return Array.from(
    fixture.nativeElement.querySelectorAll(
      '[data-test^="access-user-"]',
    ) as NodeListOf<HTMLElement>,
  ).map((node) => node.textContent?.trim() ?? '');
}

async function type(fixture: ComponentFixture<SettingsComponent>, value: string): Promise<void> {
  const input = el(fixture, 'access-input') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
}

describe('Settings: Who can use Scout', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('tells the owner where each platform shows a user id', async () => {
    const fixture = await render(makeBridge());

    expect(text(fixture, 'access-help')).toBe(
      "Discord: turn on Developer Mode, right-click a user, Copy User ID. Slack: profile, More, Copy member ID. Teams: the user's Bot Framework user ID, which starts with 29:.",
    );
  });

  it('lists the allowed users the app already stores', async () => {
    const fixture = await render(makeBridge(['111', 'U02ABC']));

    expect(listed(fixture)).toEqual(['111', 'U02ABC']);
  });

  it('adding then removing a user saves the whole list through access.set each time', async () => {
    const bridge = makeBridge(['111']);
    const fixture = await render(bridge);

    await type(fixture, ' 222 ');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.accessSet).toHaveBeenNthCalledWith(1, { userIds: ['111', ' 222 '] });
    expect(listed(fixture)).toEqual(['111', '222']);
    expect((el(fixture, 'access-input') as HTMLInputElement).value).toBe('');

    (el(fixture, 'access-remove-111') as HTMLButtonElement).click();
    await settle(fixture);

    expect(bridge.accessSet).toHaveBeenNthCalledWith(2, { userIds: ['222'] });
    expect(listed(fixture)).toEqual(['222']);
  });

  it('shows why main refused an id and keeps the list and the typed id', async () => {
    const bridge = makeBridge(['111']);
    bridge.accessSet.mockRejectedValueOnce(
      new Error(
        "Error invoking remote method 'scout:access.set': Error: [scout:access/invalid-id] A user id must be 1 to 64 letters, digits or . _ : @ - characters.",
      ),
    );
    const fixture = await render(bridge);

    await type(fixture, 'bad id');
    (el(fixture, 'access-add') as HTMLButtonElement).click();
    await settle(fixture);

    expect(text(fixture, 'access-error')).toBe(
      'A user id must be 1 to 64 letters, digits or . _ : @ - characters.',
    );
    expect(listed(fixture)).toEqual(['111']);
    expect((el(fixture, 'access-input') as HTMLInputElement).value).toBe('bad id');
  });

  it('keeps Add disabled until an id is typed', async () => {
    const fixture = await render(makeBridge());

    expect((el(fixture, 'access-add') as HTMLButtonElement).disabled).toBe(true);
    await type(fixture, '   ');
    expect((el(fixture, 'access-add') as HTMLButtonElement).disabled).toBe(true);
  });
});
