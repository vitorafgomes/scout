import { readFileSync } from 'node:fs';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type McpStatus,
  type ThemeChoice,
  type ThemeState,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LlmConfigStore, type LlmProvider } from '../core/llm-config.store';
import { McpStore } from '../core/mcp.store';
import { ScoutBridgeService } from '../core/scout-bridge';
import { ThemeStore } from '../core/theme.store';
import { TopBarComponent, screenTitle } from './top-bar.component';

type Start = ThemeChoice | 'not loaded';

const SYSTEM_RESOLVES_TO = 'dark';

function resolved(choice: ThemeChoice): 'light' | 'dark' {
  return choice === 'system' ? SYSTEM_RESOLVES_TO : choice;
}

function themeBridge(start: Start, refuse: boolean) {
  return {
    themeGet: vi.fn(async (): Promise<ThemeState> => {
      if (start === 'not loaded') throw new Error('no answer yet');
      return { choice: start, resolved: resolved(start) };
    }),
    themeSet: vi.fn(async (choice: ThemeChoice): Promise<ThemeState> => {
      if (refuse) {
        const serialized = serializeError(
          new ScoutError('theme/invalid-choice', 'Scout could not use that theme.'),
        );
        throw new Error(encodeIpcErrorMessage(serialized));
      }
      return { choice, resolved: resolved(choice) };
    }),
    onTheme: vi.fn(() => () => undefined),
    notificationsList: vi.fn(async () => []),
    notificationsMarkRead: vi.fn(async () => undefined),
    onNotifications: vi.fn(() => () => undefined),
  };
}

function llmStore(provider: LlmProvider | null, model: string | null) {
  return {
    provider: signal(provider),
    model: signal(model),
    loadFromVault: vi.fn(async () => undefined),
  };
}

async function render(
  options: {
    start?: Start;
    refuse?: boolean;
    provider?: LlmProvider | null;
    model?: string | null;
    title?: string;
    settings?: boolean;
    mcp?: McpStatus | null;
  } = {},
) {
  const bridge = themeBridge(options.start ?? 'not loaded', options.refuse ?? false);
  const llm = llmStore(options.provider ?? null, options.model ?? null);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: LlmConfigStore, useValue: llm },
      { provide: McpStore, useValue: { status: signal(options.mcp ?? null) } },
      provideRouter([]),
    ],
  });
  TestBed.inject(ThemeStore).start();
  const fixture = TestBed.createComponent(TopBarComponent);
  fixture.componentRef.setInput('title', options.title ?? 'Chat');
  fixture.componentRef.setInput('settings', options.settings ?? false);
  await settle(fixture);
  return { fixture, bridge, llm };
}

async function settle(fixture: ComponentFixture<TopBarComponent>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

function el(fixture: ComponentFixture<TopBarComponent>, test: string): HTMLElement {
  return fixture.nativeElement.querySelector(`[data-test="${test}"]`) as HTMLElement;
}

function pressed(fixture: ComponentFixture<TopBarComponent>): ThemeChoice[] {
  return (['light', 'dark', 'system'] as const).filter(
    (choice) => el(fixture, `theme-${choice}`).getAttribute('aria-pressed') === 'true',
  );
}

describe('TopBarComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    document.documentElement.removeAttribute('data-theme');
  });

  it('shows the screen title it is given', async () => {
    const { fixture } = await render({ title: 'AI Sources' });

    expect(el(fixture, 'top-bar-title').textContent?.trim()).toBe('AI Sources');
  });

  const sources: [LlmProvider | null, string | null, string][] = [
    [null, null, 'No AI Source'],
    ['None', null, 'No AI Source'],
    ['Ollama', 'llama3.2:3b', 'Ollama · llama3.2:3b'],
    ['ChatGptPlan', 'gpt-5.6-luna', 'ChatGPT plan · gpt-5.6-luna'],
    ['OpenAI', null, 'OpenAI API'],
    ['Gemini', 'gemini-2.5-flash', 'Google Gemini · gemini-2.5-flash'],
    ['Anthropic', 'claude-opus-5-5', 'Claude API · claude-opus-5-5'],
  ];

  for (const [provider, model, expected] of sources) {
    it(`the AI Source line reads "${expected}" for ${String(provider)} with model ${String(model)}`, async () => {
      const { fixture, llm } = await render({ provider, model });

      expect(el(fixture, 'top-bar-source').textContent?.trim()).toBe(expected);
      expect(llm.loadFromVault).toHaveBeenCalledTimes(1);
    });
  }

  it('the three theme buttons carry a label for screen readers and a tooltip', async () => {
    const { fixture } = await render();

    const buttons = (['light', 'dark', 'system'] as const).map((choice) => {
      const button = el(fixture, `theme-${choice}`);
      return [button.getAttribute('aria-label'), button.getAttribute('title')];
    });

    expect(buttons).toEqual([
      ['Light theme', 'Light theme'],
      ['Dark theme', 'Dark theme'],
      ['Follow the system theme', 'Follow the system theme'],
    ]);
    expect(
      fixture.nativeElement.querySelector('[role="group"][aria-label="Theme"]'),
    ).not.toBeNull();
  });

  it('the bell is a button named Notifications', async () => {
    const { fixture } = await render();

    const bell = fixture.nativeElement.querySelector(
      'sc-notification-bell button',
    ) as HTMLButtonElement;
    expect(bell.getAttribute('aria-label')).toBe('Notifications');
  });

  describe('the theme switch, every starting state by every click', () => {
    const starts: Start[] = ['not loaded', 'light', 'dark', 'system'];
    const clicks: ThemeChoice[] = ['light', 'dark', 'system'];

    for (const start of starts) {
      it(`starting ${start}, nothing clicked: ${start === 'not loaded' ? 'no button' : start} is pressed`, async () => {
        const { fixture } = await render({ start });

        expect(pressed(fixture)).toEqual(start === 'not loaded' ? [] : [start]);
        expect(document.documentElement.getAttribute('data-theme')).toBe(
          start === 'not loaded' ? null : resolved(start),
        );
      });

      for (const click of clicks) {
        it(`starting ${start}, clicking ${click}: main is asked for ${click}, ${click} alone is pressed and the window paints ${resolved(click)}`, async () => {
          const { fixture, bridge } = await render({ start });

          el(fixture, `theme-${click}`).click();
          await settle(fixture);

          expect(bridge.themeSet.mock.calls).toEqual([[click]]);
          expect(pressed(fixture)).toEqual([click]);
          expect(document.documentElement.getAttribute('data-theme')).toBe(resolved(click));
        });

        it(`starting ${start}, clicking ${click} while main refuses: the switch keeps ${start === 'not loaded' ? 'nothing' : start} pressed`, async () => {
          const { fixture } = await render({ start, refuse: true });

          el(fixture, `theme-${click}`).click();
          await settle(fixture);

          expect(pressed(fixture)).toEqual(start === 'not loaded' ? [] : [start]);
        });
      }
    }
  });
});

describe('the Settings button of the top bar', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('is not drawn in a window that does not ask for it, such as the operator window', async () => {
    const { fixture } = await render();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-test="nav-settings"]'),
      'only the main window, which has the Settings screen, shows the button',
    ).toBeNull();
  });
});

const MCP_OFF: McpStatus = {
  enabled: false,
  startMode: 'ask',
  port: 47231,
  hasToken: true,
  tokenCreatedAt: 1,
  listener: { state: 'off' },
  pending: null,
  activity: [],
};
const MCP_ON: McpStatus = {
  ...MCP_OFF,
  enabled: true,
  listener: { state: 'listening', port: 47231 },
};

describe('the MCP mark of the top bar', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('shows in the main window while the server listens: a link to Settings left of the bell, named for what it opens, with its dot', async () => {
    const { fixture } = await render({ settings: true, mcp: MCP_ON });
    const bar = fixture.nativeElement as HTMLElement;
    const mark = bar.querySelector<HTMLAnchorElement>('[data-test="mcp-mark"]');

    expect(
      [mark?.getAttribute('aria-label'), mark?.getAttribute('title'), mark?.getAttribute('href')],
      'the mark links to Settings and says the server is active',
    ).toEqual([
      'MCP server is active. Open its settings',
      'MCP server is active. Open its settings',
      '/settings',
    ]);
    expect(mark?.querySelector('.mcp-dot'), 'the mark carries its dot').not.toBeNull();
    expect(
      mark?.nextElementSibling?.tagName.toLowerCase(),
      'the mark sits at the left of the bell',
    ).toBe('sc-notification-bell');
  });

  for (const [why, settings, mcp] of [
    ['the server is off', true, MCP_OFF],
    [
      'the server did not start',
      true,
      { ...MCP_ON, listener: { state: 'failed', port: 47231, reason: 'Port 47231 is in use.' } },
    ],
    ['main has not answered yet', true, null],
    ['the window is the operator window, which has no Settings', false, MCP_ON],
  ] as const) {
    it(`is not drawn when ${why}`, async () => {
      const { fixture } = await render({ settings, mcp: mcp as McpStatus | null });

      expect(
        (fixture.nativeElement as HTMLElement).querySelector('[data-test="mcp-mark"]'),
        'the mark shows only in the main window while the server listens',
      ).toBeNull();
    });
  }
});

describe('screenTitle', () => {
  const main = [
    { path: '/chat', label: 'Chat' },
    { path: '/sources', label: 'AI Sources' },
    { path: '/llm', label: 'Models' },
    { path: '/settings', label: 'Settings' },
  ];
  const operator = [
    { path: '/operator/production', label: 'Production' },
    { path: '/operator/approvals', label: 'Approvals' },
  ];
  const rows: [typeof main, string, string, string][] = [
    [main, '/chat', 'Scout', 'Chat'],
    [main, '/sources/local-models', 'Scout', 'AI Sources'],
    [main, '/llm?tab=analysis', 'Scout', 'Models'],
    [main, '/settings#about', 'Scout', 'Settings'],
    [main, '/settingsx', 'Scout', 'Scout'],
    [main, '/slack', 'Scout', 'Scout'],
    [main, '/', 'Scout', 'Scout'],
    [operator, '/operator/approvals', 'YouTube operator', 'Approvals'],
    [operator, '/operator', 'YouTube operator', 'YouTube operator'],
  ];

  for (const [items, url, fallback, expected] of rows) {
    it(`${url} is titled ${expected}`, () => {
      expect(screenTitle(items, url, fallback)).toBe(expected);
    });
  }
});

describe("the pressed segment of the top bar's theme switch (Main.dc.html)", () => {
  it('is on the pressed colour every artboard draws, white in light and #253249 in dark, under the text colour', () => {
    const source = readFileSync('src/app/shell/top-bar.component.ts', 'utf8');
    const block = /\.theme-option\[aria-pressed='true'\] \{([^}]*)\}/.exec(source)?.[1] ?? '';

    expect({
      background: /background:\s*([^;]+);/.exec(block)?.[1] ?? null,
      color: /color:\s*([^;]+);/.exec(block)?.[1] ?? null,
    }).toEqual({ background: 'var(--sc-pressed)', color: 'var(--sc-fg)' });
  });
});
