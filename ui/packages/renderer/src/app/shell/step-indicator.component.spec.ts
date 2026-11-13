import { readFileSync } from 'node:fs';

import { provideZonelessChangeDetection, type Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PersistentStateService } from '../core/persistent-state';
import { ScoutBridgeService } from '../core/scout-bridge';
import { WIZARD_STEPS, WizardStore, wizardStepCounter } from '../core/wizard.store';
import { OAuthInstallComponent } from '../wizard/oauth-install/oauth-install.component';
import { ProvisionComponent } from '../wizard/provision/provision.component';
import { TokenPasteComponent } from '../wizard/token-paste/token-paste.component';
import { WelcomeComponent } from '../wizard/welcome/welcome.component';
import { StepIndicatorComponent } from './step-indicator.component';

const PAGES: readonly { readonly name: string; readonly page: Type<unknown> }[] = [
  { name: 'Chat app', page: WelcomeComponent },
  { name: 'Create bot', page: ProvisionComponent },
  { name: 'Bot token', page: TokenPasteComponent },
  { name: 'Add to server', page: OAuthInstallComponent },
];

const NO_FILL = 'rgba(0, 0, 0, 0)';
const OK_CONTAINER = 'rgb(22, 51, 42)';
const ACCENT_STRONG = 'rgb(52, 102, 232)';
const SURFACE_2 = 'rgb(26, 36, 54)';

function configure(): void {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([{ path: '**', children: [] }]),
      {
        provide: ScoutBridgeService,
        useValue: {
          appOpenExternal: vi.fn().mockResolvedValue(undefined),
          discordGuilds: vi.fn().mockResolvedValue({ guilds: [] }),
          vaultDelete: vi.fn().mockResolvedValue({ deleted: true }),
          vaultHas: vi.fn().mockResolvedValue({ exists: false }),
          slackStatus: vi.fn().mockResolvedValue({ connected: false }),
          detectClients: vi.fn().mockResolvedValue({ clients: [] }),
          onWorkerEvent: vi.fn().mockReturnValue(() => undefined),
        },
      },
      {
        provide: PersistentStateService,
        useValue: { get: vi.fn().mockReturnValue(null), set: vi.fn(), clear: vi.fn() },
      },
    ],
  });
  TestBed.inject(WizardStore).setClientId('111222333');
}

describe('StepIndicatorComponent and the wizard pages', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const { name, page } of PAGES) {
    it(`on the ${name} page the setup header's line, "Step N of M · ${name}", matches the stepper's current step, its name and the step count`, async () => {
      configure();
      const pageFixture = TestBed.createComponent(page);
      pageFixture.detectChanges();
      await pageFixture.whenStable();
      const stepper = TestBed.createComponent(StepIndicatorComponent);
      stepper.detectChanges();

      const root = stepper.nativeElement as HTMLElement;
      const line = wizardStepCounter(TestBed.inject(WizardStore).currentStep());
      const counter = /^Step (\d+) of (\d+) · (.+)$/.exec(line);
      const items = Array.from(root.querySelectorAll('li.step'));
      const current = items.findIndex((li) => li.getAttribute('aria-current') === 'step');

      expect(counter, `counter "${line}"`).not.toBeNull();
      expect(current).toBeGreaterThanOrEqual(0);
      expect(Number(counter![1])).toBe(current + 1);
      expect(Number(counter![2])).toBe(items.length);
      expect(items[current]!.querySelector('.num')?.textContent?.trim()).toBe(counter![1]);
      expect(counter![3]).toBe(name);
      expect(items[current]!.querySelector('.label')?.textContent?.trim()).toBe(name);
      expect(
        (pageFixture.nativeElement as HTMLElement).textContent,
        'the page repeats no step count under its title',
      ).not.toMatch(/Step \d+ of \d+/);

      pageFixture.destroy();
      stepper.destroy();
    });
  }
});

describe('The step indicator as in the prototype: done, current and to-do steps', () => {
  const tokens = document.documentElement.style;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    tokens.setProperty('--sc-ok-container', OK_CONTAINER);
    tokens.setProperty('--sc-accent-strong', ACCENT_STRONG);
    tokens.setProperty('--sc-surface-2', SURFACE_2);
  });

  afterEach(() => {
    for (const name of ['--sc-ok-container', '--sc-accent-strong', '--sc-surface-2']) {
      tokens.removeProperty(name);
    }
  });

  const LABELS = ['Chat app', 'Create bot', 'Bot token', 'Add to server', 'Launch', 'Ready'];

  for (const [index, step] of WIZARD_STEPS.entries()) {
    it(`on ${LABELS[index]}: ${index} done with a check on the ok container, ${LABELS[index]} a filled accent circle in a pill, ${LABELS.length - index - 1} numbered, a dash after every step but the last`, () => {
      configure();
      TestBed.inject(WizardStore).goTo(step);
      const fixture = TestBed.createComponent(StepIndicatorComponent);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const items = Array.from(root.querySelectorAll<HTMLElement>('li.step'));

      expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Setup progress');
      expect(
        items.map((li) => {
          const dot = li.querySelector('.dot') as HTMLElement;
          const marker = li.querySelector('.marker') as HTMLElement;
          return {
            label: li.querySelector('.label')?.textContent?.trim(),
            current: li.getAttribute('aria-current'),
            mark: dot.querySelector('sc-icon.check') !== null ? 'check' : dot.textContent?.trim(),
            circle: getComputedStyle(dot).backgroundColor,
            pill: getComputedStyle(marker).backgroundColor,
            dash: li.querySelector('.dash') !== null,
          };
        }),
      ).toEqual(
        LABELS.map((label, i) => ({
          label,
          current: i === index ? 'step' : null,
          mark: i < index ? 'check' : String(i + 1),
          circle: i < index ? OK_CONTAINER : i === index ? ACCENT_STRONG : NO_FILL,
          pill: i === index ? SURFACE_2 : NO_FILL,
          dash: i < LABELS.length - 1,
        })),
      );
    });
  }
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

describe("The wizard's step indicator follows the theme and the prototype", () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
  });

  for (const file of ['src/app/shell/step-indicator.component.ts']) {
    it(`${file} paints only with the theme tokens, no hex, rgb(), hsl() or named colour left`, () => {
      expect(colourLiterals(file)).toEqual([]);
    });
  }
});
