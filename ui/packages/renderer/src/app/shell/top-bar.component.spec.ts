import { readFileSync } from 'node:fs';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ThemeChoice,
  type ThemeState,
} from '@scout/shared';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LlmConfigStore, type LlmProvider } from '../core/llm-config.store';
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
  } = {},
) {
  const bridge = themeBridge(options.start ?? 'not loaded', options.refuse ?? false);
  const llm = llmStore(options.provider ?? null, options.model ?? null);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: ScoutBridgeService, useValue: bridge as unknown as ScoutBridgeService },
      { provide: LlmConfigStore, useValue: llm },
    ],
  });
  TestBed.inject(ThemeStore).start();
  const fixture = TestBed.createComponent(TopBarComponent);
  fixture.componentRef.setInput('title', options.title ?? 'Chat');
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
