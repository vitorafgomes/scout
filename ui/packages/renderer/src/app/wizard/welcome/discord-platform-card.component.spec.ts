import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  DISCORD_CARD_BADGES,
  DiscordPlatformCardComponent,
  type DiscordCardState,
} from './discord-platform-card.component';

function create(
  kind: 'discord' | 'slack' | 'teams',
  state: DiscordCardState = 'idle',
  guildName: string | null = null,
) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(DiscordPlatformCardComponent);
  fixture.componentInstance.kind = kind;
  fixture.componentInstance.state = state;
  fixture.componentInstance.guildName = guildName;
  fixture.detectChanges();
  return fixture;
}

describe('DiscordPlatformCardComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const STATES: readonly DiscordCardState[] = [
    'not-installed',
    'idle',
    'provisioning',
    'connecting',
    'connected',
    'disconnected',
    'error',
  ];

  for (const state of STATES) {
    it(`renders the expected badge text for state "${state}"`, () => {
      const fixture = create('discord', state, 'My Guild');
      const badge = fixture.nativeElement.querySelector('[data-test="discord-badge"]');
      expect(badge).not.toBeNull();
      expect((badge?.textContent ?? '').trim()).toContain(DISCORD_CARD_BADGES[state].label);
    });
  }

  it('connecting state renders a spinner inside the badge', () => {
    const fixture = create('discord', 'connecting');
    const spinner = fixture.nativeElement.querySelector('[data-test="discord-badge"] .spinner');
    expect(spinner).not.toBeNull();
  });

  it('connected state includes the guild name as caption', () => {
    const fixture = create('discord', 'connected', 'Scout Dev');
    const caption = fixture.nativeElement.querySelector('[data-test="discord-caption"]');
    expect(caption?.textContent?.trim()).toBe('Scout Dev');
  });

  it('not-installed state exposes a Download Discord link', () => {
    const fixture = create('discord', 'not-installed');
    const link = fixture.nativeElement.querySelector('[data-test="discord-download"]');
    expect(link).not.toBeNull();
  });

  it('choosing the Discord radio emits choose', () => {
    const fixture = create('discord', 'idle');
    const choose = vi.fn();
    fixture.componentInstance.choose.subscribe(choose);

    (
      fixture.nativeElement.querySelector('[data-test="choose-discord"]') as HTMLInputElement
    ).click();

    expect(choose).toHaveBeenCalledTimes(1);
  });

  it('Download Discord emits download and does not choose', () => {
    const fixture = create('discord', 'not-installed');
    const choose = vi.fn();
    const download = vi.fn();
    fixture.componentInstance.choose.subscribe(choose);
    fixture.componentInstance.download.subscribe(download);

    (fixture.nativeElement.querySelector('[data-test="discord-download"]') as HTMLElement).click();

    expect(download).toHaveBeenCalledTimes(1);
    expect(choose).not.toHaveBeenCalled();
  });

  it('Slack says Not connected while Slack is not connected, and Connected once it is', () => {
    const before = create('slack');
    const notConnected = before.nativeElement
      .querySelector('[data-test="slack-badge"]')
      .textContent.trim();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const after = TestBed.createComponent(DiscordPlatformCardComponent);
    after.componentInstance.kind = 'slack';
    after.componentInstance.slackConnected = true;
    after.detectChanges();

    expect(notConnected).toBe('Not connected');
    expect(after.nativeElement.querySelector('[data-test="slack-badge"]').textContent.trim()).toBe(
      'Connected',
    );
  });

  it('Teams is coming soon, says it is not available yet, and has no radio', () => {
    const fixture = create('teams', 'connected', 'Irrelevant');
    const card = fixture.nativeElement.querySelector('.card--coming-soon') as HTMLElement;

    expect(card.getAttribute('aria-disabled')).toBe('true');
    expect(card.querySelector('[data-test="helper-teams"]')?.textContent?.trim()).toBe(
      'Not available yet.',
    );
    expect(card.querySelector('input[type="radio"]')).toBeNull();
  });

  it("the radio's name includes the guild name when connected", () => {
    const fixture = create('discord', 'connected', 'Scout Dev');
    const label =
      (
        fixture.nativeElement.querySelector('[data-test="choose-discord"]') as HTMLElement
      ).getAttribute('aria-label') ?? '';

    expect(label).toBe('Discord, Connected, connected to Scout Dev');
  });
});

describe('The Chat app step as in the prototype: each card', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  const rows: readonly {
    readonly kind: 'discord' | 'slack';
    readonly checked: boolean;
    readonly button: string;
    readonly helper: string;
  }[] = [
    {
      kind: 'discord',
      checked: true,
      button: 'Chosen',
      helper: 'Scout joins a server through a bot you create. About five minutes.',
    },
    {
      kind: 'discord',
      checked: false,
      button: 'Choose Discord',
      helper: 'Scout joins a server through a bot you create. About five minutes.',
    },
    {
      kind: 'slack',
      checked: true,
      button: 'Chosen',
      helper: 'Scout joins a workspace as a Slack app.',
    },
    {
      kind: 'slack',
      checked: false,
      button: 'Choose Slack',
      helper: 'Scout joins a workspace as a Slack app.',
    },
  ];

  for (const row of rows) {
    it(`${row.kind} ${row.checked ? 'chosen' : 'not chosen'}: a radio reading "${row.button}", the helper line, ${row.checked ? 'and the chosen outline' : 'and no outline'}`, () => {
      const fixture = create(row.kind);
      fixture.componentRef.setInput('checked', row.checked);
      fixture.detectChanges();
      const card = fixture.nativeElement.querySelector('article') as HTMLElement;
      const radio = card.querySelector('input[type="radio"]') as HTMLInputElement;

      expect([
        radio.name,
        radio.checked,
        radio.closest('label')?.textContent?.trim(),
        card.querySelector('.helper')?.textContent?.trim(),
        card.classList.contains('card--chosen'),
      ]).toEqual(['chat-app', row.checked, row.button, row.helper, row.checked]);
    });
  }

  it('Discord that is installed shows the Installed chip in the ok tone', () => {
    const fixture = create('discord', 'idle');
    const chip = fixture.nativeElement.querySelector('[data-test="discord-badge"]') as HTMLElement;

    expect([chip.textContent?.trim(), chip.getAttribute('data-tone')]).toEqual([
      'Installed',
      'success',
    ]);
  });
});

/** The colour literals left in a component's styles, read from the renderer root: hex, rgb(), hsl() and the named white and black. */
function colourLiterals(relative: string): string[] {
  const source = readFileSync(relative, 'utf8');
  const start = source.indexOf('  styles:');
  const css = source.slice(start, source.indexOf('\n})\n', start));
  expect(start, `${relative} declares its styles`).toBeGreaterThanOrEqual(0);
  expect(css, `${relative} paints with the theme tokens`).toContain('var(--sc-');
  return Array.from(
    css.matchAll(
      /#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(|(?<![\w-])(?:white|black)(?![\w-])/g,
    ),
    (match) => match[0],
  );
}

describe("The wizard's Discord card follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  for (const file of ['src/app/wizard/welcome/discord-platform-card.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }
});
