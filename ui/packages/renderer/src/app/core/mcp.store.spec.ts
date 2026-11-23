import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { McpStatus } from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { McpStore } from './mcp.store';
import { ScoutBridgeService } from './scout-bridge';

const OFF: McpStatus = {
  enabled: false,
  startMode: 'ask',
  port: 47231,
  hasToken: false,
  tokenCreatedAt: null,
  listener: { state: 'off' },
  pending: null,
  activity: [],
};
const ON: McpStatus = { ...OFF, enabled: true, listener: { state: 'listening', port: 47231 } };

function fakeBridge() {
  const listeners: ((status: McpStatus) => void)[] = [];
  return {
    listeners,
    mcpStatus: vi.fn(async () => OFF),
    mcpSetEnabled: vi.fn(async (enabled: boolean) => (enabled ? ON : OFF)),
    mcpCreateToken: vi.fn(async () => ({
      token: 'scout_mcp_secret',
      status: { ...ON, hasToken: true, tokenCreatedAt: 5 },
    })),
    mcpRevokeToken: vi.fn(async () => ON),
    mcpSetStartMode: vi.fn(async () => ({ ...ON, startMode: 'free' as const })),
    mcpPickPort: vi.fn(async () => ON),
    mcpAnswerStart: vi.fn(async () => true),
    onMcpChanged: vi.fn((listener: (status: McpStatus) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
  };
}

function store(bridge: ReturnType<typeof fakeBridge>): McpStore {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  return TestBed.inject(McpStore);
}

describe('McpStore', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('reads the status from main and follows what main pushes', async () => {
    const bridge = fakeBridge();
    const mcp = store(bridge);

    mcp.start();
    mcp.start();
    await vi.waitFor(() => expect(mcp.status()).toEqual(OFF));
    bridge.listeners.forEach((listener) => listener(ON));

    expect(mcp.status(), 'a pushed status replaces the one read').toEqual(ON);
    expect(bridge.onMcpChanged, 'a second start subscribes nothing more').toHaveBeenCalledTimes(1);
  });

  it('keeps the status each call answers, and gives the new token to the caller only', async () => {
    const bridge = fakeBridge();
    const mcp = store(bridge);

    await mcp.setEnabled(true);
    const token = await mcp.createToken();
    const afterCreate = JSON.stringify(mcp.status());
    await mcp.setStartMode('free');
    await mcp.revokeToken();
    await mcp.pickPort();
    const answered = await mcp.answerStart('start-1', false);

    expect(token, 'createToken answers the token to the component that asked').toBe(
      'scout_mcp_secret',
    );
    expect(afterCreate, 'the store never holds the token').not.toContain('scout_mcp_secret');
    expect(answered).toBe(true);
    expect(bridge.mcpSetStartMode).toHaveBeenCalledWith('free');
    expect(bridge.mcpAnswerStart).toHaveBeenCalledWith({ id: 'start-1', start: false });
    expect(mcp.status(), 'the store keeps the last status main answered').toEqual(ON);
  });

  it('passes a refusal to the caller and keeps the status main reported', async () => {
    const bridge = fakeBridge();
    bridge.mcpSetEnabled.mockRejectedValueOnce(new Error('nope'));
    const mcp = store(bridge);
    mcp.start();
    await vi.waitFor(() => expect(mcp.status()).toEqual(OFF));

    await expect(mcp.setEnabled(true), 'the caller sees the refusal').rejects.toThrow('nope');
    expect(mcp.status(), 'a refused call leaves the status as main reported it').toEqual(OFF);
  });
});
