import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getLlmProviderDescriptor, type LlmProviderDescriptor } from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

import {
  LlmProviderDialogComponent,
  type LlmProviderDialogSubmit,
} from './llm-provider-dialog.component';

function makeBridge() {
  return {
    appOpenExternal: vi.fn(async () => undefined),
  };
}

function create(
  descriptor: LlmProviderDescriptor,
  opts: { existingValues?: Readonly<Record<string, string>>; secretPresent?: boolean } = {},
): {
  fixture: ComponentFixture<LlmProviderDialogComponent>;
  bridge: ReturnType<typeof makeBridge>;
} {
  const bridge = makeBridge();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge },
    ],
  });
  const fixture = TestBed.createComponent(LlmProviderDialogComponent);
  fixture.componentInstance.descriptor = descriptor;
  if (opts.existingValues !== undefined) {
    fixture.componentInstance.existingValues = opts.existingValues;
  }
  if (opts.secretPresent !== undefined) {
    fixture.componentInstance.secretPresent = opts.secretPresent;
  }
  fixture.detectChanges();
  return { fixture, bridge };
}

function setInput(
  fixture: ComponentFixture<LlmProviderDialogComponent>,
  key: string,
  value: string,
): void {
  const input = fixture.nativeElement.querySelector(
    `[data-test="llm-input-${key}"]`,
  ) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('LlmProviderDialogComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the connect title with the descriptor displayName', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    const title = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-title"]',
    ) as HTMLElement;
    expect(title.textContent?.trim()).toBe('Connect OpenAI API');
  });

  it('renders a password input for secret fields and a text input otherwise', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    const apiKey = fixture.nativeElement.querySelector(
      '[data-test="llm-input-apiKey"]',
    ) as HTMLInputElement;
    const model = fixture.nativeElement.querySelector(
      '[data-test="llm-input-model"]',
    ) as HTMLInputElement;
    expect(apiKey.type).toBe('password');
    expect(model.type).toBe('text');
  });

  it('renders one field per descriptor.fields entry', () => {
    const { fixture } = create(getLlmProviderDescriptor('azure-openai'));
    for (const key of ['apiKey', 'endpoint', 'chatDeployment', 'embeddingsDeployment']) {
      expect(
        fixture.nativeElement.querySelector(`[data-test="llm-input-${key}"]`),
        `field ${key} should render`,
      ).not.toBeNull();
    }
  });

  it('renders the docs link pointing at descriptor.docsUrl', () => {
    const { fixture } = create(getLlmProviderDescriptor('gemini'));
    const link = fixture.nativeElement.querySelector(
      '[data-test="llm-docs-link"]',
    ) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://aistudio.google.com/app/apikey');
  });

  it('clicking the docs link calls bridge.appOpenExternal with docsUrl', () => {
    const { fixture, bridge } = create(getLlmProviderDescriptor('gemini'));
    const link = fixture.nativeElement.querySelector(
      '[data-test="llm-docs-link"]',
    ) as HTMLAnchorElement;
    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    expect(bridge.appOpenExternal).toHaveBeenCalledWith({
      url: 'https://aistudio.google.com/app/apikey',
    });
  });

  it('disables Save while a required field is empty (openai: apiKey + model required, endpoint optional)', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    const submit = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-submit"]',
    ) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    setInput(fixture, 'apiKey', 'sk-abc');
    expect(submit.disabled).toBe(true);

    setInput(fixture, 'model', 'gpt-4o');
    expect(submit.disabled).toBe(false);
  });

  it('allows Save with the optional endpoint left blank', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    setInput(fixture, 'apiKey', 'sk-abc');
    setInput(fixture, 'model', 'gpt-4o');
    const submit = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-submit"]',
    ) as HTMLButtonElement;
    expect(submit.disabled).toBe(false);
  });

  it('requires every field for azure-openai (apiKey, endpoint, chatDeployment, embeddingsDeployment)', () => {
    const { fixture } = create(getLlmProviderDescriptor('azure-openai'));
    const submit = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-submit"]',
    ) as HTMLButtonElement;

    setInput(fixture, 'apiKey', 'az-key');
    setInput(fixture, 'endpoint', 'https://res.openai.azure.com');
    setInput(fixture, 'chatDeployment', 'gpt-4o');
    expect(submit.disabled).toBe(true);

    setInput(fixture, 'embeddingsDeployment', 'text-embedding-3-large');
    expect(submit.disabled).toBe(false);
  });

  it('requires the openrouter embeddings trio in addition to apiKey and model', () => {
    const { fixture } = create(getLlmProviderDescriptor('openrouter'));
    const submit = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-submit"]',
    ) as HTMLButtonElement;

    setInput(fixture, 'apiKey', 'or-key');
    setInput(fixture, 'model', 'openai/gpt-4o');
    setInput(fixture, 'embeddingsModel', 'text-embedding-3-small');
    expect(submit.disabled).toBe(true);

    setInput(fixture, 'embeddingsApiKey', 'emb-key');
    expect(submit.disabled).toBe(false); // embeddingsEndpoint has a defaultValue seeded on init
  });

  it('prefills non-secret fields from existingValues and leaves secret fields blank', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'), {
      existingValues: { model: 'gpt-4o', endpoint: 'https://custom.example/v1' },
    });
    const model = fixture.nativeElement.querySelector(
      '[data-test="llm-input-model"]',
    ) as HTMLInputElement;
    const endpoint = fixture.nativeElement.querySelector(
      '[data-test="llm-input-endpoint"]',
    ) as HTMLInputElement;
    const apiKey = fixture.nativeElement.querySelector(
      '[data-test="llm-input-apiKey"]',
    ) as HTMLInputElement;
    expect(model.value).toBe('gpt-4o');
    expect(endpoint.value).toBe('https://custom.example/v1');
    expect(apiKey.value).toBe('');
  });

  it('seeds a field default value when there is no existing value (lm-studio endpoint)', () => {
    const { fixture } = create(getLlmProviderDescriptor('lm-studio'));
    const endpoint = fixture.nativeElement.querySelector(
      '[data-test="llm-input-endpoint"]',
    ) as HTMLInputElement;
    expect(endpoint.value).toBe('http://localhost:1234/v1');
  });

  it('shows a "key already set" hint for a blank secret field when secretPresent is true', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'), { secretPresent: true });
    const hint = fixture.nativeElement.querySelector(
      '[data-test="llm-secret-hint-apiKey"]',
    ) as HTMLElement;
    expect(hint).not.toBeNull();
  });

  it('hides the "key already set" hint once the user types into the secret field', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'), { secretPresent: true });
    setInput(fixture, 'apiKey', 'sk-new');
    expect(fixture.nativeElement.querySelector('[data-test="llm-secret-hint-apiKey"]')).toBeNull();
  });

  it('allows Save with a required secret left blank when secretPresent is true', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'), {
      secretPresent: true,
      existingValues: { model: 'gpt-4o' },
    });
    const submit = fixture.nativeElement.querySelector(
      '[data-test="llm-dialog-submit"]',
    ) as HTMLButtonElement;
    expect(submit.disabled).toBe(false);
  });

  it('emits submit with { id, values } containing only non-empty fields', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    const emissions: LlmProviderDialogSubmit[] = [];
    fixture.componentInstance.submit.subscribe((v: LlmProviderDialogSubmit) => emissions.push(v));

    setInput(fixture, 'apiKey', 'sk-abc');
    setInput(fixture, 'model', 'gpt-4o');

    (
      fixture.nativeElement.querySelector('[data-test="llm-dialog-submit"]') as HTMLButtonElement
    ).click();

    expect(emissions).toEqual([{ id: 'openai', values: { apiKey: 'sk-abc', model: 'gpt-4o' } }]);
  });

  it('emits cancel when the cancel button is clicked', () => {
    const { fixture } = create(getLlmProviderDescriptor('openai'));
    let cancelled = 0;
    fixture.componentInstance.cancel.subscribe(() => cancelled++);

    (
      fixture.nativeElement.querySelector('[data-test="llm-dialog-cancel"]') as HTMLButtonElement
    ).click();
    expect(cancelled).toBe(1);
  });
});
