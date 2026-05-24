import { Injectable, computed, inject, signal } from '@angular/core';

import type { FeatureDto, FeatureId } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

export interface FeatureViewModel extends FeatureDto {
  readonly busy: boolean;
}

interface StoreState {
  readonly features: readonly FeatureDto[];
  readonly busy: ReadonlySet<FeatureId>;
}

@Injectable({ providedIn: 'root' })
export class FeaturesStore {
  private readonly _bridge = inject(ScoutBridgeService);

  private readonly _features = signal<readonly FeatureDto[]>([]);
  private readonly _busy = signal<ReadonlySet<FeatureId>>(new Set());
  private readonly _lastError = signal<string | null>(null);

  readonly lastError = this._lastError.asReadonly();

  readonly features = computed<readonly FeatureViewModel[]>(() => {
    const busy = this._busy();
    return this._features().map((f) => ({ ...f, busy: busy.has(f.id) }));
  });

  async load(): Promise<void> {
    try {
      const res = await this._bridge.featuresList();
      this._features.set(res.features);
      this._lastError.set(null);
    } catch (err: unknown) {
      this._lastError.set(err instanceof Error ? err.message : String(err));
    }
  }

  async toggle(id: FeatureId): Promise<void> {
    const current = this._features().find((f) => f.id === id);
    if (current === undefined) return;
    const nextEnabled = !current.enabled;
    // Optimistic flip
    this.patchOne(id, { enabled: nextEnabled });
    this.markBusy(id, true);
    try {
      const updated = nextEnabled
        ? await this._bridge.featureEnable({ id })
        : await this._bridge.featureDisable({ id });
      this.replaceOne(updated);
    } catch (err: unknown) {
      // Roll back optimistic change and surface error on the feature itself.
      this.patchOne(id, {
        enabled: current.enabled,
        status: 'error',
        lastError: err instanceof Error ? err.message : String(err),
      });
      this._lastError.set(err instanceof Error ? err.message : String(err));
    } finally {
      this.markBusy(id, false);
    }
  }

  async storeCredentials(id: FeatureId, body: Record<string, string>): Promise<void> {
    this.markBusy(id, true);
    try {
      const updated = await this._bridge.featureSetCredentials({ id, body });
      this.replaceOne(updated);
      this._lastError.set(null);
    } catch (err: unknown) {
      this.patchOne(id, {
        status: 'error',
        lastError: err instanceof Error ? err.message : String(err),
      });
      this._lastError.set(err instanceof Error ? err.message : String(err));
    } finally {
      this.markBusy(id, false);
    }
  }

  async clearCredentials(id: FeatureId): Promise<void> {
    this.markBusy(id, true);
    try {
      const updated = await this._bridge.featureDeleteCredentials({ id });
      this.replaceOne(updated);
      this._lastError.set(null);
    } catch (err: unknown) {
      this.patchOne(id, {
        status: 'error',
        lastError: err instanceof Error ? err.message : String(err),
      });
      this._lastError.set(err instanceof Error ? err.message : String(err));
    } finally {
      this.markBusy(id, false);
    }
  }

  private markBusy(id: FeatureId, busy: boolean): void {
    const next = new Set(this._busy());
    if (busy) next.add(id);
    else next.delete(id);
    this._busy.set(next);
  }

  private patchOne(id: FeatureId, patch: Partial<FeatureDto>): void {
    this._features.set(this._features().map((f) => (f.id === id ? { ...f, ...patch } : f)));
  }

  private replaceOne(dto: FeatureDto): void {
    const exists = this._features().some((f) => f.id === dto.id);
    if (exists) {
      this._features.set(this._features().map((f) => (f.id === dto.id ? dto : f)));
    } else {
      this._features.set([...this._features(), dto]);
    }
  }

  // Exposed for tests / debugging — read-only snapshot.
  snapshot(): StoreState {
    return { features: this._features(), busy: this._busy() };
  }
}
