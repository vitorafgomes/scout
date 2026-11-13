import { vaultPendingRevokes, type PendingRevokes } from '../src/ipc/write-grant-revoke.js';

/** Pending revokes over an in-memory vault, with the map the store writes to. */
export function memoryPendingRevokes(): PendingRevokes & { readonly vault: Map<string, string> } {
  const vault = new Map<string, string>();
  const store = vaultPendingRevokes({
    get: async (key) => vault.get(key) ?? null,
    store: async (key, value) => {
      vault.set(key, value);
    },
    delete: async (key) => vault.delete(key),
  });
  return Object.assign(store, { vault });
}
