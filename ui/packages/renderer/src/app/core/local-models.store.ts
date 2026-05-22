import { Injectable, computed, inject, signal } from '@angular/core';

import {
  LOCAL_MODEL_CATALOG,
  catalogFor,
  lookupCatalog,
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
        if (!VALID_IDS.has(model.name)) continue;
        if (current.has(model.name)) continue;
        current.add(model.name);
        changed = true;
      }
      if (!changed) return;
      this._downloaded.set([...current]);
      this.persist();
    } catch (err) {
      console.debug('[LocalModelsStore] hydrateFromWorker skipped:', err);
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
