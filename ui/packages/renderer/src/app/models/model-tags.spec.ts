import { describe, expect, it } from 'vitest';

import { lookupCatalog } from '../core/local-models.catalog';
import { FAMILY_ORDER, familyOf, isRecommended, tagsOf } from './model-tags';

const OPENAI_IDS = [
  'gpt-4o-mini',
  'gpt-5-mini',
  'gpt-5-preview',
  'gpt-5.1',
  'gpt-5.1-nano',
  'gpt-4.1',
  'o3',
  'o4-mini',
  'chatgpt-4o-latest',
];

describe('model tags', () => {
  it('familyOf, tagsOf and isRecommended derive the family, the tags and the recommended model from the ids', () => {
    expect(familyOf('gpt-5.1-mini')).toBe('GPT-5');
    expect(familyOf('gpt-4.1-nano')).toBe('GPT-4.1');
    expect(familyOf('gpt-4o')).toBe('GPT-4o');
    expect(familyOf('o1-mini')).toBe('Reasoning');
    expect(familyOf('o3')).toBe('Reasoning');
    expect(familyOf('o4-mini')).toBe('Reasoning');
    expect(familyOf('gemini-2.5-pro')).toBe('Gemini 2.5');
    expect(familyOf('gemini-2.0-flash-lite')).toBe('Gemini 2.0');
    expect(familyOf('gemini-1.5-pro')).toBe('Other');
    expect(familyOf('chatgpt-4o-latest')).toBe('Other');
    expect(familyOf('gpt-6.1-sol')).toBe('Other');
    expect(FAMILY_ORDER.indexOf('GPT-5')).toBeLessThan(FAMILY_ORDER.indexOf('GPT-4.1'));
    expect(FAMILY_ORDER.indexOf('GPT-4.1')).toBeLessThan(FAMILY_ORDER.indexOf('GPT-4o'));
    expect(FAMILY_ORDER.indexOf('Gemini 2.5')).toBeLessThan(FAMILY_ORDER.indexOf('Gemini 2.0'));
    expect(FAMILY_ORDER.at(-1)).toBe('Other');

    expect(tagsOf('gpt-5.1-mini')).toEqual(['Fast']);
    expect(tagsOf('gpt-5.1-nano')).toEqual(['Fast']);
    expect(tagsOf('gemini-2.5-flash')).toEqual(['Fast']);
    expect(tagsOf('gemini-2.0-flash-lite')).toEqual(['Fast']);
    expect(tagsOf('claude-haiku-4.5')).toEqual(['Fast']);
    expect(tagsOf('o3')).toEqual(['Reasoning']);
    expect(tagsOf('o4-mini')).toEqual(['Fast', 'Reasoning']);
    expect(tagsOf('gpt-5.1')).toEqual([]);
    expect(tagsOf('gemini-2.5-pro')).toEqual([]);
    expect(tagsOf('gemma-3-4b', lookupCatalog('gemma-3-4b') ?? undefined)).toEqual(['Vision']);
    expect(tagsOf('qwen-3-vl-2b', lookupCatalog('qwen-3-vl-2b') ?? undefined)).toEqual([
      'Recommended',
    ]);
    expect(
      tagsOf('qwen-2.5-0.5b-instruct', lookupCatalog('qwen-2.5-0.5b-instruct') ?? undefined),
    ).toEqual([]);

    expect(OPENAI_IDS.filter((id) => isRecommended(id, OPENAI_IDS))).toEqual(['gpt-5.1']);
    const gemini = ['gemini-2.0-flash', 'gemini-2.5-pro-preview', 'gemini-2.5-flash'];
    expect(gemini.filter((id) => isRecommended(id, gemini))).toEqual(['gemini-2.5-flash']);
    const onlySmall = ['gpt-5-mini', 'gpt-5-nano', 'gpt-4.1'];
    expect(onlySmall.filter((id) => isRecommended(id, onlySmall))).toEqual([]);
    const plan = ['gpt-6.1-sol', 'gpt-5.6-luna'];
    expect(plan.filter((id) => isRecommended(id, plan))).toEqual(['gpt-5.6-luna']);
    const noGeneration = ['gpt-6.1-sol', 'o3', 'chatgpt-4o-latest'];
    expect(noGeneration.filter((id) => isRecommended(id, noGeneration))).toEqual([]);
  });
});
