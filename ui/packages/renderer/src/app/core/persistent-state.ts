import { Injectable } from '@angular/core';

export interface PersistentState {
  readonly clientId: string;
  readonly guildId: string;
  readonly guildName: string;
}

const STORAGE_KEY = 'scout:discord-provisioning';

function getStorage(): Storage | null {
  try {
    const globalWindow = (globalThis as { window?: Window }).window;
    return globalWindow?.localStorage ?? null;
  } catch {
    return null;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function parse(raw: string | null): PersistentState | null {
  if (raw === null) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return null;
    const { clientId, guildId, guildName } = parsed;
    if (
      typeof clientId !== 'string' ||
      typeof guildId !== 'string' ||
      typeof guildName !== 'string'
    ) {
      return null;
    }
    if (clientId.length === 0 || guildId.length === 0 || guildName.length === 0) {
      return null;
    }
    return { clientId, guildId, guildName };
  } catch {
    return null;
  }
}

@Injectable({ providedIn: 'root' })
export class PersistentStateService {
  get(): PersistentState | null {
    const storage = getStorage();
    if (storage === null) return null;
    try {
      return parse(storage.getItem(STORAGE_KEY));
    } catch {
      return null;
    }
  }

  set(partial: Partial<PersistentState>): void {
    const storage = getStorage();
    if (storage === null) return;
    const current = this.get();
    const merged: PersistentState = {
      clientId: partial.clientId ?? current?.clientId ?? '',
      guildId: partial.guildId ?? current?.guildId ?? '',
      guildName: partial.guildName ?? current?.guildName ?? '',
    };
    try {
      storage.setItem(STORAGE_KEY, JSON.stringify(merged));
    } catch {
      // Ignore quota or access errors; renderer will fall back to re-provisioning.
    }
  }

  clear(): void {
    const storage = getStorage();
    if (storage === null) return;
    try {
      storage.removeItem(STORAGE_KEY);
    } catch {
      // ignore
    }
  }
}

export const PERSISTENT_STATE_STORAGE_KEY = STORAGE_KEY;
