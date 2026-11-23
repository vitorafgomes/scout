import { Injectable, inject, signal } from '@angular/core';
import type { McpStartMode, McpStatus } from '@scout/shared';

import { ScoutBridgeService } from './scout-bridge';

/**
 * The MCP server as main reports it, kept current by main's pushes, and the owner's calls on it. A call that main refuses
 * rejects to its caller and leaves the status as main last reported it. The token a new one answers goes to the caller
 * only: the store never holds it.
 */
@Injectable({ providedIn: 'root' })
export class McpStore {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly _status = signal<McpStatus | null>(null);
  private started = false;

  /** Main's last report; null until main answered. */
  readonly status = this._status.asReadonly();

  /** Reads the status from main and follows its pushes for the life of the window; a second call does nothing. */
  start(): void {
    if (this.started) return;
    this.started = true;
    this.bridge.onMcpChanged((status) => this._status.set(status));
    void this.bridge
      .mcpStatus()
      .then((status) => this._status.set(status))
      .catch(() => undefined);
  }

  async setEnabled(enabled: boolean): Promise<void> {
    this._status.set(await this.bridge.mcpSetEnabled(enabled));
  }

  /** Creates a token, replacing the one before it, and answers it: the caller shows it once and keeps it nowhere. */
  async createToken(): Promise<string> {
    const created = await this.bridge.mcpCreateToken();
    this._status.set(created.status);
    return created.token;
  }

  async revokeToken(): Promise<void> {
    this._status.set(await this.bridge.mcpRevokeToken());
  }

  async setStartMode(mode: McpStartMode): Promise<void> {
    this._status.set(await this.bridge.mcpSetStartMode(mode));
  }

  async pickPort(): Promise<void> {
    this._status.set(await this.bridge.mcpPickPort());
  }

  /** The owner's Start or Refuse of the start waiting; false when that start no longer waits. */
  answerStart(id: string, start: boolean): Promise<boolean> {
    return this.bridge.mcpAnswerStart({ id, start });
  }
}
