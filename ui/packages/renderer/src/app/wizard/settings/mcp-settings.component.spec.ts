import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import {
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type McpActivityRow,
  type McpStatus,
} from '@scout/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { McpStore } from '../../core/mcp.store';
import { ScoutBridgeService } from '../../core/scout-bridge';
import { McpSettingsComponent, mcpAssistantSettings } from './mcp-settings.component';

const TOKEN = 'scout_mcp_7fQ2mVx9Kc41TnRz8LdW3aYb6HsE0pUj';
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
const FAILED: McpStatus = {
  ...OFF,
  enabled: true,
  listener: { state: 'failed', port: 47231, reason: 'Port 47231 is in use.' },
};
const at = (hours: number, minutes: number) => new Date(2026, 10, 20, hours, minutes).getTime();

function refusal(message: string): Error {
  return new Error(encodeIpcErrorMessage(serializeError(new ScoutError('mcp/refused', message))));
}

function fakeBridge(start: McpStatus) {
  let current = start;
  const listeners: ((status: McpStatus) => void)[] = [];
  const answer = (next: McpStatus) => async () => (current = next);
  return {
    push: (status: McpStatus) => listeners.forEach((listener) => listener((current = status))),
    mcpStatus: vi.fn(async () => current),
    mcpSetEnabled: vi.fn(async (enabled: boolean) => (current = enabled ? ON : OFF)),
    mcpCreateToken: vi.fn(async () => ({
      token: TOKEN,
      status: (current = { ...current, hasToken: true, tokenCreatedAt: Date.now() }),
    })),
    mcpRevokeToken: vi.fn(
      async () => (current = { ...current, hasToken: false, tokenCreatedAt: null }),
    ),
    mcpSetStartMode: vi.fn(
      async (mode: 'ask' | 'free') => (current = { ...current, startMode: mode }),
    ),
    mcpPickPort: vi.fn(answer({ ...ON, listener: { state: 'listening', port: 50123 } })),
    mcpAnswerStart: vi.fn(async () => true),
    onMcpChanged: vi.fn((listener: (status: McpStatus) => void) => {
      listeners.push(listener);
      return () => undefined;
    }),
  };
}

const copied: string[] = [];

async function render(start: McpStatus) {
  const bridge = fakeBridge(start);
  TestBed.configureTestingModule({
    imports: [McpSettingsComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
    ],
  });
  const fixture = TestBed.createComponent(McpSettingsComponent);
  await settle(fixture);
  return { fixture, bridge, el: fixture.nativeElement as HTMLElement };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

const q = (el: HTMLElement, test: string) => el.querySelector<HTMLElement>(`[data-test="${test}"]`);
/** The words of `el` as a reader hears them: each text node trimmed, joined by one space. */
function text(el: Element | null): string | null {
  if (el === null) return null;
  const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
  const words: string[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const part = node.textContent?.replace(/\s+/g, ' ').trim();
    if (part) words.push(part);
  }
  return words.join(' ');
}
const switchOf = (el: HTMLElement) =>
  el.querySelector<HTMLButtonElement>('[data-test="mcp-switch"] button[role="switch"]')!;

describe('McpSettingsComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    copied.length = 0;
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn(async (value: string) => void copied.push(value)) },
    });
  });
  afterEach(() => vi.restoreAllMocks());

  it('off: the chip says Off, the switch is off, and only the intro and the switch show', async () => {
    const { el } = await render(OFF);

    expect(
      [text(q(el, 'mcp-chip')), switchOf(el).getAttribute('aria-checked')],
      'the server is off by default',
    ).toEqual(['Off', 'false']);
    expect(text(el.querySelector('.intro')), 'the intro the owner reads').toBe(
      'A coding assistant on this computer asks Scout for a review and follows it, through the Model Context Protocol. It never writes on GitHub: a verdict, a comment and an issue are given only from this app, with your approval.',
    );
    expect(q(el, 'mcp-address'), 'nothing of a running server shows while it is off').toBeNull();
  });

  it('on, nothing yet: the address, this computer only, no token, what an assistant can do, ask each time, and nothing listed', async () => {
    const { el } = await render(ON);

    expect(
      [text(q(el, 'mcp-chip')), switchOf(el).getAttribute('aria-checked')],
      'a listening server is Active and its switch on',
    ).toEqual(['Active', 'true']);
    expect(text(el.querySelector('.facts')), 'the address, the reach and the token line').toBe(
      'Address http://127.0.0.1:47231/mcp Reachable from This computer only Token None yet. An assistant cannot connect until you create one.',
    );
    expect([text(q(el, 'mcp-create-token')), q(el, 'mcp-revoke')]).toEqual([
      'Create a token',
      null,
    ]);
    expect(
      [...el.querySelectorAll('[data-test="mcp-ability"]')].map(text),
      'the three abilities, the write never offered',
    ).toEqual([
      'Read Offered The reviews and audits Scout ran and how far each one is, the finished review card with its findings and what Scout offers, and the note Scout would post on the pull request. Never asks you.',
      'Start a review Offered Start a review of a pull request that the GitHub Feature already reads. Scout reads GitHub and uses your AI Source. Asks you here before each start.',
      'Write on GitHub Not offered Give a verdict, post a comment, open an issue, merge. An assistant that asks is refused, and the refusal is listed below.',
    ]);
    expect(
      [q(el, 'mcp-mode-ask'), q(el, 'mcp-mode-free')].map((b) => [
        text(b),
        b!.getAttribute('aria-pressed'),
      ]),
      'ask each time is the default start mode',
    ).toEqual([
      ['Ask me each time', 'true'],
      ['Start without asking', 'false'],
    ]);
    expect(text(q(el, 'mcp-nothing-yet'))).toBe(
      'Nothing yet. Each request of an assistant is listed here, the refused ones too.',
    );
  });

  it('token created: shown once with its two copies; a copy forgets it, and the store never holds it', async () => {
    const { fixture, el } = await render(ON);

    q(el, 'mcp-create-token')!.click();
    await settle(fixture);
    const shownText = text(q(el, 'mcp-token'));
    q(el, 'mcp-copy-settings')!.click();
    await settle(fixture);

    expect(shownText, 'the token shows once, with the warning and both copies').toBe(
      `Copy the token now. Scout does not show it again. ${TOKEN} Copy the token Copy the settings for an assistant`,
    );
    expect(
      copied,
      'the settings for an assistant carry the address and the token as a bearer header',
    ).toEqual([mcpAssistantSettings(47231, TOKEN)]);
    expect(el.textContent, 'after a copy the token is gone from the group').not.toContain(TOKEN);
    expect(
      JSON.stringify(TestBed.inject(McpStore).status()),
      'the store never holds the token',
    ).not.toContain(TOKEN);
    expect([text(q(el, 'mcp-create-token')), text(q(el, 'mcp-revoke'))]).toEqual([
      'Create a new token',
      'Revoke the token',
    ]);
    expect(text(q(el, 'mcp-token-line'))).toMatch(
      /^Created today at \d\d:\d\d\. Scout keeps only a fingerprint of it, never the token\.$/,
    );
  });

  it('a token shown is gone with the component that showed it', async () => {
    const first = await render(ON);
    q(first.el, 'mcp-create-token')!.click();
    await settle(first.fixture);
    first.fixture.destroy();

    const second = TestBed.createComponent(McpSettingsComponent);
    await settle(second);

    expect(
      (second.nativeElement as HTMLElement).textContent,
      'a new Settings group never shows a token created before it',
    ).not.toContain(TOKEN);
  });

  it('did not start: the chip says so, the switch is off, and the alert offers another port', async () => {
    const { fixture, el, bridge } = await render(FAILED);

    const shown = [
      text(q(el, 'mcp-chip')),
      switchOf(el).getAttribute('aria-checked'),
      text(q(el, 'mcp-failed')),
      q(el, 'mcp-failed')!.getAttribute('role'),
    ];
    q(el, 'mcp-pick-port')!.click();
    await settle(fixture);

    expect(shown, 'a server that did not start says why and offers another port').toEqual([
      'Did not start',
      'false',
      'The MCP server did not start: port 47231 on this computer is in use. Close what uses it, or let Scout pick another port. Pick another port and start',
      'alert',
    ]);
    expect([bridge.mcpPickPort.mock.calls.length, text(q(el, 'mcp-address'))]).toEqual([
      1,
      'http://127.0.0.1:50123/mcp',
    ]);
  });

  it('lists what assistants did, newest first, the refusals in their own words', async () => {
    const activity: McpActivityRow[] = [
      {
        at: at(11, 42),
        tool: 'start_review',
        target: 'https://github.com/acme/api/pull/44',
        result: 'started',
      },
      { at: at(11, 44), tool: 'list_reviews', target: '', result: 'listed' },
      { at: at(12, 1), tool: 'request', target: '', result: 'refused 401' },
    ];
    const { el } = await render({ ...ON, activity });

    expect([...el.querySelectorAll('[data-test="mcp-activity-row"]')].map(text)).toEqual([
      '12:01 A request Refused: no token, or a wrong one.',
      '11:44 List the reviews Listed the reviews and audits.',
      '11:42 Start a review https://github.com/acme/api/pull/44 Started.',
    ]);
  });

  it('a call main refuses says why in one alert line, and the switch goes back to what main reports', async () => {
    const { fixture, el, bridge } = await render(OFF);
    bridge.mcpSetEnabled.mockRejectedValueOnce(
      refusal('The MCP server can be changed only in the Scout window.'),
    );

    switchOf(el).click();
    await settle(fixture);

    expect(
      [text(q(el, 'mcp-error')), q(el, 'mcp-error')!.getAttribute('role')],
      "the refusal shows in the group, in main's words",
    ).toEqual(['The MCP server can be changed only in the Scout window.', 'alert']);
    expect(switchOf(el).getAttribute('aria-checked'), 'the switch shows what main reports').toBe(
      'false',
    );
  });

  it('follows the start mode main reports and what main pushes', async () => {
    const { fixture, el, bridge } = await render(ON);

    q(el, 'mcp-mode-free')!.click();
    await settle(fixture);
    const free = q(el, 'mcp-mode-free')!.getAttribute('aria-pressed');
    bridge.push(OFF);
    await settle(fixture);

    expect([bridge.mcpSetStartMode.mock.calls[0], free]).toEqual([['free'], 'true']);
    expect(text(q(el, 'mcp-chip')), 'a pushed status is shown').toBe('Off');
  });
});
