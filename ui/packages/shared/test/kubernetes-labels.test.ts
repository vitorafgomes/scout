import { describe, expect, test } from 'bun:test';
import { kubernetesLabelError } from '../src/kubernetes-labels.js';

describe('kubernetesLabelError', () => {
  test('accepts trimmed labels of letters, digits, spaces and hyphens', () => {
    expect(
      kubernetesLabelError([
        { name: 'AKSDEV01-admin', label: ' dev ' },
        { name: 'kind-local', label: 'Kind local 2' },
      ]),
    ).toBeNull();
  });

  test.each(['', '   ', 'a'.repeat(33), 'prod/eu', 'prod.eu', 'prod_eu'])(
    'refuses the label %p',
    (label) => {
      expect(kubernetesLabelError([{ name: 'AKSPRD01-admin', label }])).toBe(
        'The label for AKSPRD01-admin must be 1 to 32 letters, digits, spaces or hyphens.',
      );
    },
  );

  test('refuses the same label twice, ignoring case and surrounding spaces', () => {
    expect(
      kubernetesLabelError([
        { name: 'AKSDEV01-admin', label: 'prod' },
        { name: 'AKSPRD01-admin', label: ' PROD ' },
      ]),
    ).toBe('The label PROD is used more than once.');
  });
});
