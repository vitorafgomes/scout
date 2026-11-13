import { Injectable, computed, inject, signal } from '@angular/core';

import {
  OLLAMA_PULL_CANCELLED,
  WHISPER_DOWNLOAD_CANCELLED,
  readIpcError,
  type OllamaModelInfo,
  type WhisperListInstalledResponse,
  type WhisperModelRow,
  type WhisperVariant,
} from '@scout/shared';

import {
  LOCAL_MODEL_CATALOG,
  SKILL_MODEL,
  WHISPER_IDS,
  catalogFor,
  catalogIdForOllamaTag,
  formatBytes,
  isOllamaTagKey,
  lookupCatalog,
  ollamaTagForCatalogId,
  whisperCatalog,
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
const OLLAMA_ENDPOINT = 'http://localhost:11434';
const VALID_IDS: ReadonlySet<string> = new Set([
  ...LOCAL_MODEL_CATALOG.map((m) => m.id),
  ...WHISPER_IDS,
  SKILL_MODEL.id,
]);

function isKnownId(id: string): boolean {
  return VALID_IDS.has(id) || isOllamaTagKey(id);
}

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
          (v): v is string => typeof v === 'string' && isKnownId(v),
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
        typeof selectedRaw['analysis'] === 'string' && isKnownId(selectedRaw['analysis'])
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

  private readonly _whisperRows = signal<readonly WhisperModelRow[]>([]);
  private readonly _whisperInUse = signal<string | null>(null);
  private readonly _whisperPreferred = signal<string | null>(null);
  private readonly _whisperTableError = signal<string | null>(null);
  private whisperTableLoad: Promise<readonly LocalModelCatalogEntry[]> | null = null;

  private readonly _ollamaAnswers = signal<boolean | null>(null);
  private readonly _ollamaRows = signal<ReadonlyMap<string, OllamaModelInfo>>(new Map());
  private ollamaRead: Promise<boolean> | null = null;

  private readonly _progress = signal<Readonly<Record<string, number>>>({});
  private readonly requests = new Map<string, string>();
  private readonly cancelling = new Set<string>();
  private listening = false;

  readonly downloaded = this._downloaded.asReadonly();
  readonly selected = this._selected.asReadonly();
  /** The Whisper model Scout uses, as main reports it (what the worker loads), or null with none installed. */
  readonly whisperInUse = this._whisperInUse.asReadonly();
  /** The saved Whisper choice, as main reports it, installed or not, or null. */
  readonly whisperPreferred = this._whisperPreferred.asReadonly();
  /** Why main's Whisper table could not be read, or null. */
  readonly whisperTableError = this._whisperTableError.asReadonly();
  /** Whether Ollama answered the last list read: true or false, null before the first read. */
  readonly ollamaAnswers = this._ollamaAnswers.asReadonly();

  readonly totalDownloadedBytes = computed<number>(() =>
    this._downloaded()
      .map((id) => this.lookup(id)?.sizeBytes ?? 0)
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

  /** The downloaded models of a kind; for Ollama the catalog ones first, then every other model it lists, by tag. */
  downloadedFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
    const set = new Set(this._downloaded());
    const listed = this.catalogFor(kind).filter((m) => set.has(m.id));
    if (kind !== 'analysis') return listed;
    const outside = this._downloaded()
      .filter(isOllamaTagKey)
      .map((id) => this.lookup(id))
      .filter((m): m is LocalModelCatalogEntry => m !== null);
    return [...listed, ...outside];
  }

  /** The models of a kind: the Whisper ones exactly as main's table lists them, empty until it is read. */
  catalogFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
    return kind === 'transcription' ? whisperCatalog(this._whisperRows()) : catalogFor(kind);
  }

  /**
   * The entry of a catalog id; a Whisper entry exists once main's table is read. A model outside the catalog is an
   * Ollama chat entry titled by its tag, with the size Ollama last listed and no description.
   */
  lookup(id: string): LocalModelCatalogEntry | null {
    if (WHISPER_IDS.has(id))
      return this.catalogFor('transcription').find((m) => m.id === id) ?? null;
    const entry = lookupCatalog(id);
    if (entry !== null || !isOllamaTagKey(id)) return entry;
    return {
      id,
      kind: 'analysis',
      title: id,
      description: '',
      badges: [],
      sizeBytes: this._ollamaRows().get(id)?.sizeBytes ?? 0,
      ramBytes: 0,
    };
  }

  /**
   * True for an Ollama model that only turns text into embeddings: its capabilities list embedding without
   * completion, or, when Ollama did not report them, its name says embed or its family is a BERT.
   */
  isEmbeddingsOnly(id: string): boolean {
    const row = this._ollamaRows().get(id);
    const capabilities = row?.capabilities;
    if (Array.isArray(capabilities)) {
      return capabilities.includes('embedding') && !capabilities.includes('completion');
    }
    return /embed/i.test(id) || /bert/i.test(row?.family ?? '');
  }

  /**
   * True for an Ollama chat model whose capabilities, as Ollama reported them, lack tools; false while Ollama has not
   * reported them, for an embeddings-only model, and for a model Ollama does not list.
   */
  cannotCallTools(id: string): boolean {
    const capabilities = this._ollamaRows().get(id)?.capabilities;
    return (
      Array.isArray(capabilities) && !capabilities.includes('tools') && !this.isEmbeddingsOnly(id)
    );
  }

  /** What an Ollama model outside the catalog says under its name: family, parameters, quantization, size on disk, each when known. */
  detailsOf(id: string): string {
    const row = this._ollamaRows().get(id);
    if (row === undefined) return '';
    return [
      row.family,
      row.parameterSize,
      row.quantization,
      row.sizeBytes > 0 ? `${formatBytes(row.sizeBytes)} on disk` : undefined,
    ]
      .filter((part): part is string => part !== undefined)
      .join(' · ');
  }

  /**
   * Reads main's pinned Whisper table once and resolves with its rows; a failed read keeps its reason in
   * `whisperTableError`, resolves with no rows, and the next call reads again.
   */
  loadWhisperTable(): Promise<readonly LocalModelCatalogEntry[]> {
    this.whisperTableLoad ??= this._bridge.whisperModelTable().then(
      ({ models }) => {
        this._whisperRows.set(models);
        this._whisperTableError.set(null);
        return this.catalogFor('transcription');
      },
      (err: unknown) => {
        this.whisperTableLoad = null;
        this._whisperTableError.set(readIpcError(err).message);
        return [];
      },
    );
    return this.whisperTableLoad;
  }

  /**
   * Reads from main which Whisper models are on this computer and which one Scout uses; the verified ones replace
   * the Whisper part of the downloaded list. False when main could not answer, which changes nothing.
   */
  async refreshWhisper(): Promise<boolean> {
    let res: WhisperListInstalledResponse;
    try {
      res = await this._bridge.whisperListInstalled();
    } catch {
      return false;
    }
    const verified = res.installed.filter((m) => m.verified).map((m) => `whisper-${m.variant}`);
    this._downloaded.set([...this._downloaded().filter((id) => !WHISPER_IDS.has(id)), ...verified]);
    this._whisperInUse.set(res.inUse == null ? null : `whisper-${res.inUse}`);
    this._whisperPreferred.set(res.preferred == null ? null : `whisper-${res.preferred}`);
    this.persist();
    return true;
  }

  private kindOf(id: string): LocalModelKind | null {
    if (WHISPER_IDS.has(id)) return 'transcription';
    return lookupCatalog(id)?.kind ?? (isOllamaTagKey(id) ? 'analysis' : null);
  }

  markDownloaded(id: string): void {
    if (!isKnownId(id)) return;
    if (this._downloaded().includes(id)) return;
    const next = [...this._downloaded(), id];
    this._downloaded.set(next);

    const kind = this.kindOf(id);
    if (kind !== null && this._selected()[kind] === null) {
      this._selected.update((prev) => ({ ...prev, [kind]: id }));
      if (kind === 'analysis') void this.persistAnalysisToVault(id);
    }
    this.persist();
  }

  markRemoved(id: string): void {
    if (!this._downloaded().includes(id)) return;
    this._downloaded.set(this._downloaded().filter((m) => m !== id));

    const kind = this.kindOf(id);
    if (kind !== null && this._selected()[kind] === id) {
      const fallback =
        this.downloadedFor(kind).find((m) => !this.isEmbeddingsOnly(m.id))?.id ??
        this._downloaded().find((m) => this.kindOf(m) === kind && !this.isEmbeddingsOnly(m)) ??
        null;
      this._selected.update((prev) => ({ ...prev, [kind]: fallback }));
      if (kind === 'analysis' && fallback !== null) void this.persistAnalysisToVault(fallback);
    }
    this.persist();
  }

  /**
   * Makes a downloaded model this window's selection. An Ollama model is also saved as `llm.model`: the promise
   * settles once it is, and rejects with the vault's reason, keeping the previous selection, when it cannot be saved.
   * An Ollama model that is no longer downloaded, or that only turns text into embeddings, is not saved: the
   * promise rejects with a sentence that says so. A voice model is saved only by main (`scout:whisper.useModel`).
   */
  async select(kind: LocalModelKind, id: string): Promise<void> {
    if (!isKnownId(id)) return;
    if (this.kindOf(id) !== kind) return;
    if (!this._downloaded().includes(id)) {
      if (kind === 'analysis') {
        throw new Error(`${this.lookup(id)?.title ?? id} is no longer on this computer.`);
      }
      return;
    }
    if (kind === 'analysis' && this.isEmbeddingsOnly(id)) {
      throw new Error(`${id} only turns text into embeddings; it cannot answer in chat.`);
    }
    const previous = this._selected()[kind];
    this._selected.update((prev) => ({ ...prev, [kind]: id }));
    this.persist();
    const tag = kind === 'analysis' ? ollamaTagForCatalogId(id) : null;
    if (tag === null) return;
    try {
      await this._bridge.vaultStore({ key: 'llm.model', value: tag });
    } catch (err: unknown) {
      this._selected.update((prev) => ({ ...prev, [kind]: previous }));
      this.persist();
      throw err;
    }
  }

  /** The percent of a download or pull this window started, or null while none runs for `id`. */
  progressOf(id: string): number | null {
    const value = this._progress()[id];
    return typeof value === 'number' ? value : null;
  }

  /**
   * Downloads a voice model through main, or pulls an Ollama model, keeping its progress here so it outlives the
   * screen that started it. Resolves once the model is downloaded and, for Ollama, once Ollama's list was read again
   * after the pull ended; rejects with main's reason, or with `DOWNLOAD_CANCELLED` after Cancel. A second call for a
   * model already downloading joins nothing and resolves. A Download skips a model already downloaded and makes the
   * first model of its kind the selection; a pull `byName`, the owner's Pull by name, pulls an installed model again,
   * which updates it, and never chooses a model.
   */
  async download(id: string, options: { readonly byName?: boolean } = {}): Promise<void> {
    const byName = options.byName === true;
    if (this.requests.has(id) || (!byName && this._downloaded().includes(id))) return;
    const variant = WHISPER_IDS.has(id) ? (id.replace(/^whisper-/, '') as WhisperVariant) : null;
    const tag = variant === null ? ollamaTagForCatalogId(id) : null;
    if (variant === null && tag === null) return;
    this.listenForProgress();
    const requestId = `${id}:${Date.now()}`;
    this.requests.set(id, requestId);
    this.setProgress(id, 0);
    try {
      if (variant !== null) {
        await this._bridge.whisperDownloadModel({ variant, requestId });
      } else {
        await this._bridge.llmPullOllamaModel({ endpoint: OLLAMA_ENDPOINT, tag: tag!, requestId });
      }
    } catch (err: unknown) {
      if (this.cancelling.has(id) || isCancelled(err)) throw new Error(DOWNLOAD_CANCELLED);
      throw err;
    } finally {
      this.requests.delete(id);
      this.cancelling.delete(id);
      this.setProgress(id, null);
    }
    if (!byName) this.markDownloaded(id);
    if (variant === null) await this.readOllamaAfterPull();
  }

  /** A read of Ollama's list that starts after the pull ended: a read already in flight may predate it. */
  private async readOllamaAfterPull(): Promise<void> {
    await this.ollamaRead;
    await this.refreshOllama();
  }

  /** Cancel on a row that is downloading: main stops the download, and `download` rejects with `DOWNLOAD_CANCELLED`. */
  async cancelDownload(id: string): Promise<void> {
    const requestId = this.requests.get(id);
    if (requestId === undefined) return;
    this.cancelling.add(id);
    try {
      if (WHISPER_IDS.has(id)) {
        await this._bridge.whisperCancelDownload({
          variant: id.replace(/^whisper-/, '') as WhisperVariant,
        });
      } else {
        await this._bridge.llmCancelOllamaPull({ requestId });
      }
    } catch (err: unknown) {
      this.cancelling.delete(id);
      throw err;
    }
  }

  private listenForProgress(): void {
    if (this.listening) return;
    this.listening = true;
    const report = (event: { requestId: string; percent: number }): void => {
      for (const [id, requestId] of this.requests) {
        if (requestId === event.requestId) this.setProgress(id, event.percent);
      }
    };
    this._bridge.onWhisperDownloadProgress(report);
    this._bridge.onLlmPullOllamaProgress(report);
  }

  private setProgress(id: string, percent: number | null): void {
    this._progress.update((prev) => {
      const next = { ...prev };
      if (percent === null) delete next[id];
      else next[id] = percent;
      return next;
    });
  }

  /**
   * Reads Ollama's model list once and resolves true when Ollama answered. On an answer `ollamaAnswers` is true and the
   * Ollama part of the downloaded list becomes exactly what Ollama lists, in its order: a catalog model under its catalog
   * id, the skill-matching model under its bare name or its `:latest` tag, as the worker asks for it, and every other
   * model under its tag, with its details kept for this window. A list Ollama cannot give sets `ollamaAnswers` to false,
   * resolves false and keeps the downloaded list. It never chooses a model and writes nothing to the vault. A call made
   * while a read is in flight gets that read's answer and sends no second request.
   */
  refreshOllama(): Promise<boolean> {
    this.ollamaRead ??= this.readOllama().finally(() => {
      this.ollamaRead = null;
    });
    return this.ollamaRead;
  }

  private async readOllama(): Promise<boolean> {
    let models: readonly OllamaModelInfo[];
    try {
      ({ models } = await this._bridge.llmListOllamaModels({ endpoint: OLLAMA_ENDPOINT }));
    } catch {
      this._ollamaAnswers.set(false);
      return false;
    }
    this._ollamaAnswers.set(true);
    const rows = new Map<string, OllamaModelInfo>();
    for (const model of models) {
      const key = ollamaKeyForName(model.name);
      if (!rows.has(key)) rows.set(key, model);
    }
    this._ollamaRows.set(rows);
    this._downloaded.set([
      ...this._downloaded().filter((id) => WHISPER_IDS.has(id)),
      ...rows.keys(),
    ]);
    this.persist();
    return true;
  }

  /** What the Ollama view does when it opens: forgets the last answer, so the chip reads Checking, and reads again. */
  openOllama(): Promise<boolean> {
    this._ollamaAnswers.set(null);
    return this.refreshOllama();
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

/**
 * The key Scout keeps an Ollama model under, from a name Ollama lists or the owner types: a name without a tag is
 * its `:latest`, the skill-matching model's `:latest` is its own id, a catalog tag is its catalog id, and any other
 * name is its tag.
 */
export function ollamaKeyForName(name: string): string {
  const tagged = name.includes(':') ? name : `${name}:latest`;
  return tagged === `${SKILL_MODEL.tag}:latest` ? SKILL_MODEL.id : catalogIdForOllamaTag(tagged);
}

/** What the desktop says wherever a model that cannot call tools is shown or in use. */
export const NO_TOOLS =
  'This model cannot call tools, so Scout cannot read your repositories or cluster with it. Choose a model that supports tools.';

/** The reason `LocalModelsStore.download` rejects with after Cancel, from this window or any other. */
export const DOWNLOAD_CANCELLED = 'The download was cancelled.';

/** True when main rejected a download or pull because some window cancelled it. */
export function isCancelled(err: unknown): boolean {
  const code = readIpcError(err).code;
  return code === WHISPER_DOWNLOAD_CANCELLED || code === OLLAMA_PULL_CANCELLED;
}
