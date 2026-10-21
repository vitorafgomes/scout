import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';

import { LogStore } from '../core/log.store';
import { LOCAL_MODELS_STORAGE_KEY, LocalModelsStore } from '../core/local-models.store';
import { ModelsComponent } from './models.component';

class FakeLogStore {
  private state: { state: string; restarts: number } = { state: 'idle', restarts: 0 };
  status(): { state: string; restarts: number } {
    return this.state;
  }
  setRunning(running: boolean): void {
    this.state = { state: running ? 'running' : 'idle', restarts: 0 };
  }
}

async function setup(opts: { running?: boolean } = {}): Promise<{
  fixture: ComponentFixture<ModelsComponent>;
  store: LocalModelsStore;
  logStore: FakeLogStore;
}> {
  const logStore = new FakeLogStore();
  if (opts.running) logStore.setRunning(true);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: LogStore, useValue: logStore },
    ],
  });
  const store = TestBed.inject(LocalModelsStore);
  const fixture = TestBed.createComponent(ModelsComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, store, logStore };
}

describe('ModelsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    window.localStorage.removeItem(LOCAL_MODELS_STORAGE_KEY);
  });

  it('renders three tabs with Translation disabled', async () => {
    const { fixture } = await setup();

    const tr = fixture.nativeElement.querySelector(
      '[data-test="tab-transcription"]',
    ) as HTMLButtonElement;
    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    const tl = fixture.nativeElement.querySelector(
      '[data-test="tab-translation"]',
    ) as HTMLButtonElement;

    expect(tr).not.toBeNull();
    expect(an).not.toBeNull();
    expect(tl).not.toBeNull();
    expect(tl.disabled).toBe(true);
  });

  it('shows the empty state on Transcription when nothing downloaded', async () => {
    const { fixture } = await setup();

    expect(fixture.nativeElement.querySelector('[data-test="empty-state"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="empty-cta"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).toBeNull();
  });

  it('shows only the downloaded models in Transcription', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="empty-state"]')).toBeNull();
  });

  it('shows the Selected pill once a model is auto-selected by download', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-test="selected-pill"]') as HTMLElement;
    expect(pill).not.toBeNull();
    expect(pill.textContent ?? '').toContain('Whisper Large v3 Turbo');
  });

  it('clicking an option changes the selected model via the store', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const v3 = fixture.nativeElement.querySelector(
      '[data-test="option-whisper-large-v3"]',
    ) as HTMLButtonElement;
    v3.click();
    fixture.detectChanges();

    expect(store.selectedFor('transcription')).toBe('whisper-large-v3');
  });

  it('Analysis tab shows the Local LLM downloaded models', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-3-vl-2b');

    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-test="option-qwen-3-vl-2b"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="panel-analysis"]')).not.toBeNull();
  });

  it('locks Transcription options when worker is running', async () => {
    const { fixture, store } = await setup({ running: true });
    store.markDownloaded('whisper-large-v3-turbo');
    fixture.detectChanges();

    const opt = fixture.nativeElement.querySelector(
      '[data-test="option-whisper-large-v3-turbo"]',
    ) as HTMLButtonElement;
    expect(opt.disabled).toBe(true);
  });

  it('Translation tab cannot be activated', async () => {
    const { fixture } = await setup();
    const tl = fixture.nativeElement.querySelector(
      '[data-test="tab-translation"]',
    ) as HTMLButtonElement;
    tl.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-test="panel-transcription"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-test="panel-translation"]')).toBeNull();
  });

  it('Analysis tab with a selected model renders only the selected option', async () => {
    const { fixture, store } = await setup();
    // Download three analysis models. The first one is auto-selected by the store
    // (markDownloaded). Override selection to the middle one explicitly to make
    // the narrowing rule observable.
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.markDownloaded('qwen-3-vl-2b');
    store.select('analysis', 'qwen-2.5-1.5b-instruct');

    const an = fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fixture.detectChanges();

    const options = fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(1);
    expect(options[0]?.getAttribute('data-test')).toBe('option-qwen-2.5-1.5b-instruct');
    expect(fixture.nativeElement.querySelector('[data-test="no-active-model-nudge"]')).toBeNull();
  });

  it('Analysis tab with no selection renders all downloaded options plus a nudge', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('qwen-2.5-0.5b-instruct');
    store.markDownloaded('qwen-2.5-1.5b-instruct');
    store.markDownloaded('qwen-3-vl-2b');
    // Wipe the auto-selection so the nudge path is exercised. The store API
    // does not expose a clear-selection method, so simulate the "no selection"
    // state by purging localStorage and re-hydrating via a fresh component.
    // Easiest path: reach into the private signal through localStorage reset.
    window.localStorage.setItem(
      LOCAL_MODELS_STORAGE_KEY,
      JSON.stringify({
        downloaded: ['qwen-2.5-0.5b-instruct', 'qwen-2.5-1.5b-instruct', 'qwen-3-vl-2b'],
        selected: { transcription: null, analysis: null },
      }),
    );
    TestBed.resetTestingModule();
    const fresh = await setup();

    const an = fresh.fixture.nativeElement.querySelector(
      '[data-test="tab-analysis"]',
    ) as HTMLButtonElement;
    an.click();
    fresh.fixture.detectChanges();

    const options = fresh.fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(3);
    expect(
      fresh.fixture.nativeElement.querySelector('[data-test="no-active-model-nudge"]'),
    ).not.toBeNull();
    // store and fixture from outer setup are unused once we rehydrate
    void store;
    void fixture;
  });

  it('Transcription tab still renders the full downloaded list (no narrowing)', async () => {
    const { fixture, store } = await setup();
    store.markDownloaded('whisper-large-v3-turbo');
    store.markDownloaded('whisper-large-v3');
    fixture.detectChanges();

    const options = fixture.nativeElement.querySelectorAll(
      '[data-test^="option-"]',
    ) as NodeListOf<HTMLElement>;
    expect(options.length).toBe(2);
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3-turbo"]'),
    ).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-test="option-whisper-large-v3"]'),
    ).not.toBeNull();
  });
});
