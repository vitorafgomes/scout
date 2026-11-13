import type { ScoutBridge } from '@scout/shared';

declare global {
  interface Window {
    readonly scout: ScoutBridge;
  }
}

export {};
