import { describe, expect, test } from 'bun:test';
import { kubernetesLabelError } from '../src/kubernetes-labels.js';

describe('kubernetesLabelError', () => {
  test('accepts trimmed labels of letters, digits, spaces and hyphens', () => {
    expect(
      kubernetesLabelError([
        { name: 'AZDK8FP01-admin', label: ' dev ' },
        { name: 'docker-desktop', label: 'Docker Desktop 2' },
      ]),
    ).toBeNull();
  });

  test.each(['', '   ', 'a'.repeat(33), 'prod/eu', 'prod.eu', 'prod_eu'])(
    'refuses the label %p',
    (label) => {
      expect(kubernetesLabelError([{ name: 'AZPK8FP01-admin', label }])).toBe(
        'The label for AZPK8FP01-admin must be 1 to 32 letters, digits, spaces or hyphens.',
      );
    },
  );

  test('refuses the same label twice, ignoring case and surrounding spaces', () => {
    expect(
      kubernetesLabelError([
        { name: 'AZDK8FP01-admin', label: 'prod' },
        { name: 'AZPK8FP01-admin', label: ' PROD ' },
      ]),
    ).toBe('The label PROD is used more than once.');
  });
});
