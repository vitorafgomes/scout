import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import type { FeatureDto } from '@scout/shared';

import { WriteConfirmationDialogComponent } from './write-confirmation-dialog.component';

const youtube: FeatureDto = {
  id: 'youtube',
  displayName: 'YouTube',
  description: '',
  requiresCredentials: true,
  hasCredentials: true,
  enabled: false,
  status: 'disabled',
  lastError: null,
  credentialFields: [],
  settingsFields: [],
  settings: {},
  scopeMatrix: [
    {
      scope: 'videos.list',
      summary: 'Video statistics',
      actions: ['Read video statistics'],
      access: 'read',
    },
    {
      scope: 'videos.insert',
      summary: 'Public or private',
      actions: ['Publish the rendered video', 'Schedule the rendered video'],
      access: 'irreversible',
    },
    {
      scope: 'thumbnails.set',
      summary: 'A thumbnail',
      actions: ["Set a video's thumbnail, once you approve it"],
      access: 'irreversible',
    },
  ],
  setupSteps: null,
  writeClass: 'executive',
  granted: false,
  grantedAt: null,
};

async function render(): Promise<ComponentFixture<WriteConfirmationDialogComponent>> {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(WriteConfirmationDialogComponent);
  fixture.componentInstance.feature = youtube;
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('the write confirmation dialog', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('names a write the way the feature card does: its label and "Cannot be undone", not the scope code', async () => {
    const fixture = await render();
    const row = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-test="write-row-videos.insert"]',
    );
    const shown = row?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

    expect({
      label: shown.includes('Upload a video'),
      access: shown.includes('Cannot be undone'),
      code: shown.includes('videos.insert'),
    }).toEqual({ label: true, access: true, code: false });
  });

  function lines(fixture: ComponentFixture<WriteConfirmationDialogComponent>, scope: string) {
    const what = (fixture.nativeElement as HTMLElement).querySelector(
      `[data-test="write-row-${scope}"] .what`,
    );
    return {
      list: what?.tagName ?? null,
      lines: Array.from(what?.querySelectorAll('li') ?? [], (li) => li.textContent?.trim()),
    };
  }

  it("a write with two actions shows each action on its own line, in order, in full and never the details panel's short sentence", async () => {
    const fixture = await render();

    expect(lines(fixture, 'videos.insert')).toEqual({
      list: 'UL',
      lines: ['Publish the rendered video', 'Schedule the rendered video'],
    });
  });

  it('a write with one action shows one line, its whole sentence with its commas', async () => {
    const fixture = await render();

    expect(lines(fixture, 'thumbnails.set')).toEqual({
      list: 'UL',
      lines: ["Set a video's thumbnail, once you approve it"],
    });
  });

  it('opens with Cancel focused', async () => {
    await render();

    expect((document.activeElement as HTMLElement | null)?.getAttribute('data-test') ?? null).toBe(
      'write-confirmation-cancel',
    );
  });
});
