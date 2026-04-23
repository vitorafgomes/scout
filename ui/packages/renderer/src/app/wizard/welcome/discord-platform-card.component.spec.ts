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

  it('clicking the Discord card emits activate', () => {
    const fixture = create('discord', 'idle');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.click();
    expect(activate).toHaveBeenCalledTimes(1);
  });

  it('pressing Enter on the card triggers the same activate as click', () => {
    const fixture = create('discord', 'idle');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(activate).toHaveBeenCalledTimes(1);
  });

  it('pressing Space on the card triggers activate', () => {
    const fixture = create('discord', 'idle');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.dispatchEvent(new KeyboardEvent('keydown', { key: ' ' }));
    expect(activate).toHaveBeenCalledTimes(1);
  });

  it('pressing an irrelevant key does not activate', () => {
    const fixture = create('discord', 'idle');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    card.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab' }));
    expect(activate).not.toHaveBeenCalled();
  });

  it('clicking the download link emits download and does not activate', () => {
    const fixture = create('discord', 'not-installed');
    const activate = vi.fn();
    const download = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);
    fixture.componentInstance.download.subscribe(download);
    const link = fixture.nativeElement.querySelector(
      '[data-test="discord-download"]',
    ) as HTMLElement;
    link.click();
    expect(download).toHaveBeenCalledTimes(1);
    expect(activate).not.toHaveBeenCalled();
  });

  it('exposes the correct role and tabindex on the Discord card', () => {
    const fixture = create('discord', 'idle');
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    expect(card.getAttribute('role')).toBe('button');
    expect(card.getAttribute('tabindex')).toBe('0');
  });

  it('Slack card renders as coming-soon and is non-clickable', () => {
    const fixture = create('slack');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);

    const card = fixture.nativeElement.querySelector('.card--coming-soon') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.getAttribute('aria-disabled')).toBe('true');
    expect(card.getAttribute('data-kind')).toBe('slack');
    card.click();
    expect(activate).not.toHaveBeenCalled();
    const discordOnly = fixture.nativeElement.querySelector('.card--discord');
    expect(discordOnly).toBeNull();
  });

  it('Teams card renders as coming-soon and is non-clickable regardless of state', () => {
    const fixture = create('teams', 'connected', 'Irrelevant');
    const activate = vi.fn();
    fixture.componentInstance.activate.subscribe(activate);

    const card = fixture.nativeElement.querySelector('.card--coming-soon') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.getAttribute('aria-disabled')).toBe('true');
    card.click();
    expect(activate).not.toHaveBeenCalled();
  });

  it('aria-label includes the guild name when connected', () => {
    const fixture = create('discord', 'connected', 'Scout Dev');
    const card = fixture.nativeElement.querySelector('.card--discord') as HTMLElement;
    const label = card.getAttribute('aria-label') ?? '';
    expect(label).toContain('Connected');
    expect(label).toContain('Scout Dev');
  });
});
