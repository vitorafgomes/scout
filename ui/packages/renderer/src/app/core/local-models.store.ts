import { Injectable, computed, inject, signal } from '@angular/core';

import {
  LOCAL_MODEL_CATALOG,
  catalogFor,
  catalogIdForOllamaTag,
  lookupCatalog,
  ollamaTagForCatalogId,
  type LocalModelCatalogEntry,
  type LocalModelKind,
} from './local-models.catalog';
import { ScoutBridgeService } from './scout-bridge';

interface SelectedSnapshot {
  readonly transcription: string | null;
  readonly analysis: string | null;
}

interface PersistedShape {
  readonly downloaded: readonly string[];
  readonly selected: SelectedSnapshot;
}

const STORAGE_KEY = 'scout:local-models';
const VALID_IDS: ReadonlySet<string> = new Set(LOCAL_MODEL_CATALOG.map((m) => m.id));

function getStorage(): Storage | null {
  try {
    const globalWindow = (globalThis as { window?: Window }).window;
    return globalWindow?.localStorage ?? null;
  } catch {
    return null;
  }
}

function readPersisted(): PersistedShape | null {
  const storage = getStorage();
  if (storage === null) return null;
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const obj = parsed as Record<string, unknown>;
    const downloaded = Array.isArray(obj['downloaded'])
      ? (obj['downloaded'] as unknown[]).filter(
          (v): v is string => typeof v === 'string' && VALID_IDS.has(v),
        )
      : [];
    const selectedRaw = (obj['selected'] ?? {}) as Record<string, unknown>;
    const selected: SelectedSnapshot = {
      transcription:
        typeof selectedRaw['transcription'] === 'string' &&
        VALID_IDS.has(selectedRaw['transcription'])
          ? selectedRaw['transcription']
          : null,
      analysis:
        typeof selectedRaw['analysis'] === 'string' && VALID_IDS.has(selectedRaw['analysis'])
          ? selectedRaw['analysis']
          : null,
    };
    return { downloaded, selected };
  } catch {
    return null;
  }
}

function writePersisted(snapshot: PersistedShape): void {
  const storage = getStorage();
  if (storage === null) return;
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
  } catch {
    // ignore quota / access
  }
}

@Injectable({ providedIn: 'root' })
export class LocalModelsStore {
  private readonly _bridge = inject(ScoutBridgeService);

  private readonly _downloaded = signal<readonly string[]>([]);
  private readonly _selected = signal<SelectedSnapshot>({
    transcription: null,
    analysis: null,
  });

  readonly downloaded = this._downloaded.asReadonly();
  readonly selected = this._selected.asReadonly();

  readonly totalDownloadedBytes = computed<number>(() =>
    this._downloaded()
      .map((id) => lookupCatalog(id)?.sizeBytes ?? 0)
      .reduce((sum, n) => sum + n, 0),
  );

  constructor() {
    const persisted = readPersisted();
    if (persisted !== null) {
      this._downloaded.set(persisted.downloaded);
      this._selected.set(persisted.selected);
    }
  }

  isDownloaded(id: string): boolean {
    return this._downloaded().includes(id);
  }

  selectedFor(kind: LocalModelKind): string | null {
    return this._selected()[kind];
  }

  downloadedFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
    const set = new Set(this._downloaded());
    return catalogFor(kind).filter((m) => set.has(m.id));
  }

  catalogFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
    return catalogFor(kind);
  }

  markDownloaded(id: string): void {
    if (!VALID_IDS.has(id)) return;
    if (this._downloaded().includes(id)) return;
    const next = [...this._downloaded(), id];
    this._downloaded.set(next);

    // First downloaded model of its kind becomes selected automatically.
    const entry = lookupCatalog(id);
    if (entry !== null && this._selected()[entry.kind] === null) {
      this._selected.update((prev) => ({ ...prev, [entry.kind]: id }));
      if (entry.kind === 'analysis') {
        void this.persistAnalysisToVault(id);
      }
    }
    this.persist();
  }

  markRemoved(id: string): void {
    if (!this._downloaded().includes(id)) return;
    this._downloaded.set(this._downloaded().filter((m) => m !== id));

    const entry = lookupCatalog(id);
    if (entry !== null && this._selected()[entry.kind] === id) {
      // Selected model was removed — fall back to first remaining downloaded of same kind, else null.
      const remaining = this.downloadedFor(entry.kind);
      this._selected.update((prev) => ({
        ...prev,
        [entry.kind]: remaining[0]?.id ?? null,
      }));
    }
    this.persist();
  }

  select(kind: LocalModelKind, id: string): void {
    if (!VALID_IDS.has(id)) return;
    if (!this._downloaded().includes(id)) return;
    const entry = lookupCatalog(id);
    if (entry === null || entry.kind !== kind) return;
    this._selected.update((prev) => ({ ...prev, [kind]: id }));
    this.persist();
    if (kind === 'analysis') {
      void this.persistAnalysisToVault(id);
    }
  }

  deleteUnused(): void {
    const sel = this._selected();
    const keep = new Set([sel.transcription, sel.analysis].filter((v): v is string => v !== null));
    this._downloaded.set(this._downloaded().filter((id) => keep.has(id)));
    this.persist();
  }

  async hydrateFromWorker(): Promise<void> {
    try {
      const status = await this._bridge.providerStatus({ id: 'ollama' });
      const current = new Set(this._downloaded());
      let changed = false;
      for (const model of status.models) {
        // Ollama reports tags like "qwen2.5:3b"; map them back to Scout's
        // catalog IDs ("qwen-3-vl-2b") so the rest of the UI can keep using
        // catalog IDs as the single key.
        const catalogId = catalogIdForOllamaTag(model.name);
        if (catalogId === null) continue;
        if (current.has(catalogId)) continue;
        current.add(catalogId);
        changed = true;
      }
      if (changed) {
        this._downloaded.set([...current]);
      }

      // Auto-pick the first analysis model the daemon already has so the
      // chat completion path has a model name without forcing the user to
      // visit the LLM wizard. Must run even when the worker reports the
      // same model list as before, since selection may still be null on
      // first boot (otherwise chat completion ends up with no model name).
      const analysisDownloaded = this.downloadedFor('analysis').map((m) => m.id);
      let selectionChanged = false;
      if (this._selected().analysis === null && analysisDownloaded.length > 0) {
        const firstAnalysis = analysisDownloaded[0]!;
        this._selected.update((prev) => ({ ...prev, analysis: firstAnalysis }));
        void this.persistAnalysisToVault(firstAnalysis);
        selectionChanged = true;
      }

      if (!changed && !selectionChanged) return;

      this.persist();
    } catch (err) {
      console.warn('[LocalModelsStore] hydrateFromWorker skipped:', err);
    }
  }

  private async persistAnalysisToVault(catalogId: string): Promise<void> {
    // The .NET worker reads the chat-completion model from vault.llm.model
    // (forwarded by the supervisor as SCOUT__LLM__OLLAMA__MODEL). Writing
    // here keeps the Local Analysis selection in sync with the LLM wizard
    // so both screens point at the same model.
    const tag = ollamaTagForCatalogId(catalogId);
    if (tag === null) return;
    try {
      await this._bridge.vaultStore({ key: 'llm.model', value: tag });
    } catch (err) {
      console.warn('[LocalModelsStore] vault write skipped:', err);
    }
  }

  private persist(): void {
    writePersisted({
      downloaded: this._downloaded(),
      selected: this._selected(),
    });
  }
}

export const LOCAL_MODELS_STORAGE_KEY = STORAGE_KEY;
