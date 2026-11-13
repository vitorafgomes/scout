import { OLLAMA_EMBEDDING_MODEL, type WhisperModelRow, type WhisperVariant } from '@scout/shared';

export type LocalModelKind = 'transcription' | 'analysis';

export type LocalModelBadge = 'recommended' | 'text-only' | 'text-image';

export interface LocalModelCatalogEntry {
  readonly id: string;
  readonly kind: LocalModelKind;
  readonly title: string;
  readonly description: string;
  readonly badges: readonly LocalModelBadge[];
  readonly sizeBytes: number;
  readonly ramBytes: number;
}

const GiB = 1024 * 1024 * 1024;
const MiB = 1024 * 1024;

/** What the screens say about each Whisper model main pins; the list, the files and the sizes come from main. */
export const WHISPER_COPY: Readonly<
  Record<
    WhisperVariant,
    Pick<LocalModelCatalogEntry, 'title' | 'description' | 'badges' | 'ramBytes'>
  >
> = Object.freeze({
  tiny: {
    title: 'Whisper Tiny',
    description: 'Fastest and smallest. Less accurate with accents and background noise.',
    badges: [],
    ramBytes: Math.round(273 * MiB),
  },
  base: {
    title: 'Whisper Base',
    description:
      'Recommended for live transcription: words appear within about a second on most computers.',
    badges: ['recommended'],
    ramBytes: Math.round(388 * MiB),
  },
  small: {
    title: 'Whisper Small',
    description: 'More accurate than Base, and slower for live transcription.',
    badges: [],
    ramBytes: Math.round(852 * MiB),
  },
  'large-v3-turbo': {
    title: 'Whisper Large v3 Turbo',
    description: 'Full precision turbo. Faster high-quality fallback.',
    badges: [],
    ramBytes: Math.round(3.0 * GiB),
  },
  'large-v3': {
    title: 'Whisper Large v3',
    description: 'Highest local accuracy. Slower and requires more memory.',
    badges: [],
    ramBytes: Math.round(4.7 * GiB),
  },
});

/** The catalog ids of the Whisper models, `whisper-<variant>`. */
export const WHISPER_IDS: ReadonlySet<string> = new Set(
  Object.keys(WHISPER_COPY).map((variant) => `whisper-${variant}`),
);

/** The Whisper rows of a screen: main's table, in main's order and with main's sizes, with this catalog's copy. */
export function whisperCatalog(rows: readonly WhisperModelRow[]): LocalModelCatalogEntry[] {
  return rows.map((row) => ({
    id: `whisper-${row.variant}`,
    kind: 'transcription',
    ...WHISPER_COPY[row.variant],
    sizeBytes: row.size,
  }));
}

export const LOCAL_MODEL_CATALOG: readonly LocalModelCatalogEntry[] = Object.freeze([
  {
    id: 'qwen-2.5-0.5b-instruct',
    kind: 'analysis',
    title: 'Qwen 2.5 0.5B (Instruct)',
    description:
      'Ultra-lightweight text analysis. Ideal for machines with 4 GB RAM or less. No image support.',
    badges: ['text-only'],
    sizeBytes: Math.round(491 * MiB),
    ramBytes: Math.round(1.5 * GiB),
  },
  {
    id: 'qwen-2.5-1.5b-instruct',
    kind: 'analysis',
    title: 'Qwen 2.5 1.5B (Instruct)',
    description:
      'Small and capable text analysis. Good quality for low-RAM systems. No image support.',
    badges: ['text-only'],
    sizeBytes: Math.round(1.12 * GiB),
    ramBytes: Math.round(2.5 * GiB),
  },
  {
    id: 'qwen-3-vl-2b',
    kind: 'analysis',
    title: 'Qwen 2.5 3B',
    description:
      'Lightweight text analysis from the Qwen 2.5 family. Best for low-resource machines.',
    badges: ['recommended', 'text-only'],
    sizeBytes: Math.round(1.9 * GiB),
    ramBytes: Math.round(6.0 * GiB),
  },
  {
    id: 'gemma-3-4b',
    kind: 'analysis',
    title: 'Gemma 3 4B',
    description: 'Recommended default. Handles both text and image analysis.',
    badges: ['text-image'],
    sizeBytes: Math.round(3.3 * GiB),
    ramBytes: Math.round(8.0 * GiB),
  },
  {
    id: 'gemma-3-12b',
    kind: 'analysis',
    title: 'Gemma 3 12B',
    description: 'Higher quality. Handles both text and image analysis.',
    badges: ['text-image'],
    sizeBytes: Math.round(8.0 * GiB),
    ramBytes: Math.round(16.0 * GiB),
  },
] as const);

/** The Ollama model Scout matches a message to a skill with, at the Ollama library's size; never a chat choice. */
export const SKILL_MODEL = Object.freeze({
  id: OLLAMA_EMBEDDING_MODEL,
  tag: OLLAMA_EMBEDDING_MODEL,
  sizeBytes: Math.round(274 * MiB),
});

export function catalogFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
  return LOCAL_MODEL_CATALOG.filter((m) => m.kind === kind);
}

export function lookupCatalog(id: string): LocalModelCatalogEntry | null {
  return LOCAL_MODEL_CATALOG.find((m) => m.id === id) ?? null;
}

const OLLAMA_TAG_BY_CATALOG_ID: Readonly<Record<string, string>> = Object.freeze({
  'qwen-2.5-0.5b-instruct': 'qwen2.5:0.5b',
  'qwen-2.5-1.5b-instruct': 'qwen2.5:1.5b',
  'qwen-3-vl-2b': 'qwen2.5:3b',
  'gemma-3-4b': 'gemma3:4b',
  'gemma-3-12b': 'gemma3:12b',
  [SKILL_MODEL.id]: SKILL_MODEL.tag,
});

const CATALOG_ID_BY_OLLAMA_TAG: Readonly<Record<string, string>> = Object.freeze(
  Object.fromEntries(Object.entries(OLLAMA_TAG_BY_CATALOG_ID).map(([id, tag]) => [tag, id])),
);

/** True for the key of an Ollama model outside the catalog: its tag as Ollama lists it, which always has a colon. */
export function isOllamaTagKey(id: string): boolean {
  return id.includes(':');
}

/** The Ollama tag of a catalog id or of a model outside the catalog; null for a Whisper id or an unknown one. */
export function ollamaTagForCatalogId(catalogId: string): string | null {
  return OLLAMA_TAG_BY_CATALOG_ID[catalogId] ?? (isOllamaTagKey(catalogId) ? catalogId : null);
}

/** The key Scout keeps an Ollama model under: its catalog id when the catalog has its tag, else the tag itself. */
export function catalogIdForOllamaTag(tag: string): string {
  return CATALOG_ID_BY_OLLAMA_TAG[tag] ?? tag;
}

/** A size as the model screens say it: "3.3 GB", "274 MB", or bytes below a megabyte. */
export function formatBytes(n: number): string {
  if (n >= GiB) return `${(n / GiB).toFixed(1).replace(/\.0$/, '')} GB`;
  if (n >= MiB) return `${Math.round(n / MiB)} MB`;
  return `${n} B`;
}
