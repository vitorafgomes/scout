import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import type { FeatureDto } from '@scout/shared';

import {
  CredentialDialogComponent,
  type CredentialDialogSubmit,
} from './credential-dialog.component';

function baseFeature(overrides: Partial<FeatureDto> = {}): FeatureDto {
  return {
    id: 'github',
    displayName: 'GitHub',
    description: 'GitHub integration',
    requiresCredentials: true,
    hasCredentials: false,
    enabled: false,
    status: 'needs-credentials',
    lastError: null,
    credentialFields: [],
    settingsFields: [],
    settings: {},
    scopeMatrix: null,
    setupSteps: null,
    writeClass: 'none',
    granted: false,
    grantedAt: null,
    ...overrides,
  };
}

function create(feature: FeatureDto): ComponentFixture<CredentialDialogComponent> {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(CredentialDialogComponent);
  fixture.componentInstance.feature = feature;
  fixture.detectChanges();
  return fixture;
}

function setInput(
  fixture: ComponentFixture<CredentialDialogComponent>,
  key: string,
  value: string,
): void {
  const input = fixture.nativeElement.querySelector(`[data-test="input-${key}"]`) as
    HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

function setSettingInput(
  fixture: ComponentFixture<CredentialDialogComponent>,
  key: string,
  value: string,
): void {
  const input = fixture.nativeElement.querySelector(`[data-test="setting-input-${key}"]`) as
    HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

function pressSave(fixture: ComponentFixture<CredentialDialogComponent>): {
  emitted: number;
  line: string | null;
  invalid: string[];
} {
  let emitted = 0;
  const sub = fixture.componentInstance.submit.subscribe(() => (emitted += 1));
  (
    fixture.nativeElement.querySelector('[data-test="credential-submit"]') as HTMLButtonElement
  ).click();
  fixture.detectChanges();
  sub.unsubscribe();
  const root = fixture.nativeElement as HTMLElement;
  return {
    emitted,
    line: root.querySelector('[data-test="credential-missing"]')?.textContent?.trim() ?? null,
    invalid: Array.from(
      root.querySelectorAll('[aria-invalid="true"]'),
      (el) => el.getAttribute('data-test') ?? '',
    ),
  };
}

describe('CredentialDialogComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the connect title with the feature displayName', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );
    const title = fixture.nativeElement.querySelector('[data-test="dialog-title"]') as HTMLElement;
    expect(title.textContent?.trim()).toBe('Connect GitHub');
  });

  it('renders one input per credentialFields entry with the right element kind', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: 'Create a fine-grained PAT',
            createLinkUrl: 'https://github.com/settings/tokens',
            createLinkLabel: 'Open GitHub',
          },
          {
            key: 'org',
            kind: 'text',
            label: 'Organisation',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );

    const pat = fixture.nativeElement.querySelector('[data-test="input-pat"]') as HTMLInputElement;
    const org = fixture.nativeElement.querySelector('[data-test="input-org"]') as HTMLInputElement;
    expect(pat).not.toBeNull();
    expect(org).not.toBeNull();
    expect(pat.tagName).toBe('INPUT');
    expect(pat.type).toBe('password');
    expect(org.tagName).toBe('INPUT');
    expect(org.type).toBe('text');

    const helper = fixture.nativeElement.querySelector('[data-test="helper-pat"]') as HTMLElement;
    expect(helper.textContent?.trim()).toBe('Create a fine-grained PAT');

    const link = fixture.nativeElement.querySelector('[data-test="link-pat"]') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://github.com/settings/tokens');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener');
    expect(link.textContent?.trim()).toBe('Open GitHub');
  });

  it('renders a textarea for kind="textarea"', () => {
    const fixture = create(
      baseFeature({
        id: 'kubernetes',
        displayName: 'Kubernetes',
        credentialFields: [
          {
            key: 'kubeconfig',
            kind: 'textarea',
            label: 'kubeconfig',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );
    const ta = fixture.nativeElement.querySelector(
      '[data-test="input-kubeconfig"]',
    ) as HTMLTextAreaElement;
    expect(ta.tagName).toBe('TEXTAREA');
    expect(ta.rows).toBe(6);
  });

  it('renders the scope matrix when present (GitHub case)', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'PAT',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        scopeMatrix: [
          {
            scope: 'repo',
            summary: 'Read repository metadata',
            actions: ['Read repository metadata', 'Read issues'],
            access: 'read',
          },
          { scope: 'read:org', summary: 'List teams', actions: ['List teams'], access: 'read' },
        ],
      }),
    );

    const matrix = fixture.nativeElement.querySelector('[data-test="scope-matrix"]');
    expect(matrix).not.toBeNull();

    const repoRow = fixture.nativeElement.querySelector(
      '[data-test="scope-row-repo"]',
    ) as HTMLElement;
    expect(repoRow).not.toBeNull();
    expect(repoRow.textContent).toContain('repo');
    expect(repoRow.textContent).toContain('Read repository metadata');
    expect(repoRow.textContent).toContain('Read issues');

    const orgRow = fixture.nativeElement.querySelector(
      '[data-test="scope-row-read:org"]',
    ) as HTMLElement;
    expect(orgRow).not.toBeNull();
  });

  it('hides the scope matrix when scopeMatrix is null (Kubernetes case)', () => {
    const fixture = create(
      baseFeature({
        id: 'kubernetes',
        displayName: 'Kubernetes',
        credentialFields: [
          {
            key: 'kubeconfig',
            kind: 'textarea',
            label: 'kubeconfig',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        scopeMatrix: null,
      }),
    );
    expect(fixture.nativeElement.querySelector('[data-test="scope-matrix"]')).toBeNull();
  });

  it('Save with required fields empty saves nothing, marks them and says how many, as the artboard draws it; once filled it saves', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
          {
            key: 'org',
            kind: 'text',
            label: 'Organisation',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );

    const empty = pressSave(fixture);
    setInput(fixture, 'pat', 'ghp_abc');
    const one = pressSave(fixture);
    setInput(fixture, 'org', 'acme');
    const full = pressSave(fixture);

    expect([empty, one, full]).toEqual([
      {
        emitted: 0,
        line: '2 required fields are empty. Nothing was saved.',
        invalid: ['input-pat', 'input-org'],
      },
      {
        emitted: 0,
        line: 'One required field is empty. Nothing was saved.',
        invalid: ['input-org'],
      },
      { emitted: 1, line: null, invalid: [] },
    ]);
  });

  it('does not require fields whose label ends with "(optional)", and marks only the others with *', () => {
    const fixture = create(
      baseFeature({
        id: 'kubernetes',
        displayName: 'Kubernetes',
        credentialFields: [
          {
            key: 'kubeconfig',
            kind: 'textarea',
            label: 'kubeconfig',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
          {
            key: 'context',
            kind: 'text',
            label: 'Context (optional)',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );

    const labels = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.label'),
      (el) => el.textContent?.trim(),
    );
    const empty = pressSave(fixture);
    setInput(fixture, 'kubeconfig', 'apiVersion: v1');
    const full = pressSave(fixture);

    expect(labels).toEqual(['kubeconfig *', 'Context (optional)']);
    expect([empty.emitted, empty.invalid, full.emitted]).toEqual([0, ['input-kubeconfig'], 1]);
  });

  it('emits submit with a keyed map matching field.key', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'PAT',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
          {
            key: 'org',
            kind: 'text',
            label: 'Org',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );

    const emissions: CredentialDialogSubmit[] = [];
    fixture.componentInstance.submit.subscribe((v: CredentialDialogSubmit) => emissions.push(v));

    setInput(fixture, 'pat', 'ghp_abc');
    setInput(fixture, 'org', 'acme');

    (
      fixture.nativeElement.querySelector('[data-test="credential-submit"]') as HTMLButtonElement
    ).click();

    expect(emissions).toEqual([{ credentials: { pat: 'ghp_abc', org: 'acme' }, settings: {} }]);
  });

  it('renders settings fields above the credential fields (PAT) in the DOM', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        settingsFields: [
          {
            key: 'default_repo_url',
            kind: 'text',
            label: 'Default repository URL',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
            required: false,
          },
        ],
      }),
    );

    const settingInput = fixture.nativeElement.querySelector(
      '[data-test="setting-input-default_repo_url"]',
    ) as HTMLElement;
    const patInput = fixture.nativeElement.querySelector('[data-test="input-pat"]') as HTMLElement;

    expect(settingInput).not.toBeNull();
    expect(patInput).not.toBeNull();

    const position = settingInput.compareDocumentPosition(patInput);
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('a blank required setting keeps Save from saving and is marked like a field', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        settingsFields: [
          {
            key: 'default_repo_url',
            kind: 'text',
            label: 'Default repository URL',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
            required: true,
          },
        ],
      }),
    );

    setInput(fixture, 'pat', 'ghp_abc');
    const blank = pressSave(fixture);
    setSettingInput(fixture, 'default_repo_url', 'https://github.com/acme/repo');
    const full = pressSave(fixture);

    expect([blank, full.emitted]).toEqual([
      {
        emitted: 0,
        line: 'One required field is empty. Nothing was saved.',
        invalid: ['setting-input-default_repo_url'],
      },
      1,
    ]);
  });

  it('emits submit with separate credentials and settings sections', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'Personal Access Token',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        settingsFields: [
          {
            key: 'default_repo_url',
            kind: 'text',
            label: 'Default repository URL',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
            required: false,
          },
        ],
      }),
    );

    const emissions: CredentialDialogSubmit[] = [];
    fixture.componentInstance.submit.subscribe((v: CredentialDialogSubmit) => emissions.push(v));

    setSettingInput(fixture, 'default_repo_url', 'https://github.com/acme/repo');
    setInput(fixture, 'pat', 'ghp_abc');

    (
      fixture.nativeElement.querySelector('[data-test="credential-submit"]') as HTMLButtonElement
    ).click();

    expect(emissions).toEqual([
      {
        credentials: { pat: 'ghp_abc' },
        settings: { default_repo_url: 'https://github.com/acme/repo' },
      },
    ]);
  });

  it('emits cancel when the cancel button is clicked', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'PAT',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
      }),
    );
    let cancelled = 0;
    fixture.componentInstance.cancel.subscribe(() => cancelled++);

    (
      fixture.nativeElement.querySelector('[data-test="credential-cancel"]') as HTMLButtonElement
    ).click();
    expect(cancelled).toBe(1);
  });

  it('renders the setup steps as a numbered list when setupSteps is non-empty', () => {
    const steps = [
      "Click 'Create token on GitHub' below.",
      'Generate token and copy the value.',
      'Paste the token into the field above.',
      'Click Save to validate and store.',
    ];
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'PAT',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        setupSteps: steps,
      }),
    );

    const list = fixture.nativeElement.querySelector(
      '[data-test="setup-steps"]',
    ) as HTMLOListElement | null;
    expect(list).not.toBeNull();
    expect(list!.tagName).toBe('OL');

    const items = list!.querySelectorAll('li');
    expect(items.length).toBe(steps.length);
    items.forEach((li, i) => {
      expect(li.textContent ?? '').toContain(steps[i]);
    });
  });

  it('hides the setup steps section when setupSteps is null', () => {
    const fixture = create(
      baseFeature({
        credentialFields: [
          {
            key: 'pat',
            kind: 'password',
            label: 'PAT',
            helperText: null,
            createLinkUrl: null,
            createLinkLabel: null,
          },
        ],
        setupSteps: null,
      }),
    );

    expect(fixture.nativeElement.querySelector('[data-test="setup-steps"]')).toBeNull();
  });

  describe('backwards-compat fallback (worker without schema)', () => {
    it('renders the single textarea when credentialFields is empty', () => {
      const fixture = create(baseFeature({ credentialFields: [] }));
      const ta = fixture.nativeElement.querySelector(
        '[data-test="credential-textarea"]',
      ) as HTMLTextAreaElement;
      expect(ta).not.toBeNull();
    });

    it('emits { raw } from the legacy textarea', () => {
      const fixture = create(baseFeature({ credentialFields: [] }));
      const emissions: CredentialDialogSubmit[] = [];
      fixture.componentInstance.submit.subscribe((v: CredentialDialogSubmit) => emissions.push(v));

      const ta = fixture.nativeElement.querySelector(
        '[data-test="credential-textarea"]',
      ) as HTMLTextAreaElement;
      ta.value = '{"token":"abc"}';
      ta.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      const submit = fixture.nativeElement.querySelector(
        '[data-test="credential-submit"]',
      ) as HTMLButtonElement;
      expect(submit.disabled).toBe(false);
      submit.click();

      expect(emissions).toEqual([{ credentials: { raw: '{"token":"abc"}' }, settings: {} }]);
    });
  });
});
