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

export const LOCAL_MODEL_CATALOG: readonly LocalModelCatalogEntry[] = Object.freeze([
  {
    id: 'whisper-large-v3',
    kind: 'transcription',
    title: 'Whisper Large v3',
    description: 'Highest local accuracy. Slower and requires more memory.',
    badges: ['recommended'],
    sizeBytes: Math.round(2.9 * GiB),
    ramBytes: Math.round(4.7 * GiB),
  },
  {
    id: 'whisper-large-v3-turbo',
    kind: 'transcription',
    title: 'Whisper Large v3 Turbo',
    description: 'Full precision turbo. Faster high-quality fallback.',
    badges: [],
    sizeBytes: Math.round(1.5 * GiB),
    ramBytes: Math.round(3.0 * GiB),
  },
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
    description: 'Small and capable text analysis. Good quality for low-RAM systems. No image support.',
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

export function catalogFor(kind: LocalModelKind): readonly LocalModelCatalogEntry[] {
  return LOCAL_MODEL_CATALOG.filter((m) => m.kind === kind);
}

export function lookupCatalog(id: string): LocalModelCatalogEntry | null {
  return LOCAL_MODEL_CATALOG.find((m) => m.id === id) ?? null;
}
