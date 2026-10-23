import type { KubernetesLabelledContext } from './ipc-contract.js';

const LABEL = /^[A-Za-z0-9 -]{1,32}$/;

/**
 * Why a set of Kubernetes context labels cannot be used, or null when every label is 1 to 32
 * letters, digits, spaces or hyphens after trimming and no two are the same ignoring case.
 */
export function kubernetesLabelError(
  contexts: readonly KubernetesLabelledContext[],
): string | null {
  const seen = new Set<string>();
  for (const { name, label } of contexts) {
    const trimmed = label.trim();
    if (!LABEL.test(trimmed)) {
      return `The label for ${name} must be 1 to 32 letters, digits, spaces or hyphens.`;
    }
    const key = trimmed.toLowerCase();
    if (seen.has(key)) return `The label ${trimmed} is used more than once.`;
    seen.add(key);
  }
  return null;
}
