import type { LocalModelCatalogEntry } from '../core/local-models.catalog';

/** A label shown next to a model on the Models screen. */
export type ModelTag = 'Recommended' | 'Fast' | 'Reasoning' | 'Vision';

const FAMILY_PREFIXES: readonly (readonly [RegExp, string])[] = [
  [/^gpt-5/, 'GPT-5'],
  [/^gpt-4\.1/, 'GPT-4.1'],
  [/^gpt-4o/, 'GPT-4o'],
  [/^o[134]/, 'Reasoning'],
  [/^gemini-2\.5/, 'Gemini 2.5'],
  [/^gemini-2\.0/, 'Gemini 2.0'],
];

/** The families in the order a provider group lists them: generations newest first, then Reasoning, then Other. */
export const FAMILY_ORDER: readonly string[] = Object.freeze([
  'GPT-5',
  'GPT-4.1',
  'GPT-4o',
  'Gemini 2.5',
  'Gemini 2.0',
  'Reasoning',
  'Other',
]);

const GENERATIONS: readonly string[] = ['GPT-5', 'GPT-4.1', 'GPT-4o', 'Gemini 2.5', 'Gemini 2.0'];
const FAST_WORDS: ReadonlySet<string> = new Set(['mini', 'nano', 'flash', 'lite', 'haiku']);
const NOT_RECOMMENDED_WORDS: ReadonlySet<string> = new Set(['mini', 'nano', 'preview']);

function hasWord(id: string, words: ReadonlySet<string>): boolean {
  return id
    .toLowerCase()
    .split(/[-./:_]/)
    .some((word) => words.has(word));
}

/** The family label of a cloud model id, from its prefix; "Other" when no family matches. */
export function familyOf(id: string): string {
  const lower = id.toLowerCase();
  return FAMILY_PREFIXES.find(([prefix]) => prefix.test(lower))?.[1] ?? 'Other';
}

/** The tags a model shows: from its catalog badges for a local entry, from its id for a cloud model. Recommended for a cloud model comes from isRecommended. */
export function tagsOf(id: string, entry?: LocalModelCatalogEntry): readonly ModelTag[] {
  if (entry !== undefined) {
    const tags: ModelTag[] = [];
    if (entry.badges.includes('recommended')) tags.push('Recommended');
    if (entry.badges.includes('text-image')) tags.push('Vision');
    return tags;
  }
  const tags: ModelTag[] = [];
  if (hasWord(id, FAST_WORDS)) tags.push('Fast');
  if (familyOf(id) === 'Reasoning') tags.push('Reasoning');
  return tags;
}

/** True for the first id of the newest family in the list that is not a mini, nano or preview model. */
export function isRecommended(id: string, ids: readonly string[]): boolean {
  const newest = GENERATIONS.find((family) => ids.some((other) => familyOf(other) === family));
  if (newest === undefined) return false;
  const pick = ids.find(
    (other) => familyOf(other) === newest && !hasWord(other, NOT_RECOMMENDED_WORDS),
  );
  return pick === id;
}
