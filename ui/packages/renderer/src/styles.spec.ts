import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeAll, describe, expect, it, vi } from 'vitest';

import { LlmConfigStore } from './app/core/llm-config.store';
import { ScoutBridgeService } from './app/core/scout-bridge';
import { TopBarComponent } from './app/shell/top-bar.component';

function local(relative: string): string {
  return fileURLToPath(new URL(relative, import.meta.url));
}

function read(relative: string): string {
  return readFileSync(local(relative), 'utf8');
}

function block(css: string, selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start, `${selector} has a rule in styles.scss`).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('\n}', start));
}

function declarations(rule: string, prefix: string): Record<string, string> {
  const found: Record<string, string> = {};
  for (const match of rule.matchAll(/(--[\w-]+):\s*([^;]+);/g)) {
    const [, name, value] = match;
    if (name !== undefined && value !== undefined && name.startsWith(prefix))
      found[name] = value.trim();
  }
  return found;
}

interface BuildTarget {
  readonly options: { readonly styles?: readonly string[] };
  readonly configurations: { readonly production: { readonly optimization: unknown } };
}

function build(): BuildTarget {
  const angular = JSON.parse(read('../angular.json')) as {
    projects: Record<string, { architect: { build: BuildTarget } }>;
  };
  const target = angular.projects['desktop-renderer']?.architect.build;
  expect(target, 'angular.json has the desktop-renderer build target').toBeDefined();
  return target as BuildTarget;
}

interface SassCompiler {
  compile(path: string, options: { loadPaths: string[] }): { css: string };
}

let compiled: string | undefined;

function compiledStylesheet(): string {
  if (compiled === undefined) {
    const renderer = createRequire(local('../package.json'));
    const sass = createRequire(renderer.resolve('@angular/build/package.json'))(
      'sass',
    ) as SassCompiler;
    compiled = sass.compile(local('./styles.scss'), {
      loadPaths: [local('../node_modules')],
    }).css;
  }
  return compiled;
}

function systemColours(rule: string, roles: readonly string[]): Record<string, string> {
  const found: Record<string, string> = {};
  for (const match of rule.matchAll(/--mat-sys-([\w-]+):\s*([^;]+);/g)) {
    const [, role, value] = match;
    if (role !== undefined && value !== undefined && roles.includes(role))
      found[role] = value.trim();
  }
  return found;
}

function compiledRule(css: string, selector: string, media?: string): string {
  const from = media === undefined ? 0 : css.indexOf(`@media ${media} {`);
  expect(from, `${media ?? 'the stylesheet'} is in the compiled stylesheet`).toBeGreaterThanOrEqual(
    0,
  );
  const start = css.indexOf(`${selector} {`, from);
  expect(start, `${selector} has a rule in the compiled stylesheet`).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('}', start));
}

const DARK_ROLES: Record<string, string> = {
  surface: '#0f1623',
  'surface-container': '#131c2d',
  'surface-container-high': '#1a2436',
  'outline-variant': '#253249',
  'on-surface': '#e6e9f0',
  'on-surface-variant': '#c3cadb',
  outline: '#9aa3b6',
  primary: '#8fb2f9',
  'on-primary': '#0f1623',
  'primary-container': '#3466e8',
  'on-primary-container': '#ffffff',
  error: '#f28b82',
  'on-error': '#0f1623',
};

const LIGHT_ROLES: Record<string, string> = {
  surface: '#f5f7fb',
  'surface-container': '#ffffff',
  'surface-container-high': '#e8ecf4',
  'outline-variant': '#d7dce7',
  'on-surface': '#18202e',
  'on-surface-variant': '#3f4a61',
  outline: '#5d677d',
  primary: '#2a56c6',
  'on-primary': '#ffffff',
  'primary-container': '#2a56c6',
  'on-primary-container': '#ffffff',
  error: '#b3261e',
  'on-error': '#ffffff',
};

const SCHEME_TOKENS = [
  '--sc-ok',
  '--sc-warn',
  '--sc-ok-container',
  '--sc-on-ok-container',
  '--sc-warn-container',
  '--sc-on-warn-container',
  '--sc-error-container',
  '--sc-on-error-container',
  '--sc-info-container',
  '--sc-on-info-container',
  '--sc-unread',
  '--sc-code-bg',
  '--sc-media',
  '--sc-scrim',
  '--sc-recording',
  '--sc-danger',
  '--sc-pressed',
  '--sc-shadow',
  '--sc-inverse-bg',
  '--sc-inverse-fg',
  '--sc-inverse-action',
];

function luminance(hex: string): number {
  const channels = [1, 3, 5].map((at) => parseInt(hex.slice(at, at + 2), 16) / 255);
  const [r, g, b] = channels.map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

function contrast(text: string, background: string): number {
  const [high, low] = [luminance(text), luminance(background)].sort((x, y) => y - x);
  return (high! + 0.05) / (low! + 0.05);
}

const TEXT_ON_BACKGROUND: readonly (readonly [string, string])[] = [
  ['mat:on-surface', 'mat:surface'],
  ['mat:on-surface', 'mat:surface-container'],
  ['mat:on-surface', 'mat:surface-container-high'],
  ['mat:on-surface-variant', 'mat:surface'],
  ['mat:on-surface-variant', 'mat:surface-container'],
  ['mat:on-surface-variant', 'mat:surface-container-high'],
  ['mat:outline', 'mat:surface'],
  ['mat:outline', 'mat:surface-container'],
  ['mat:primary', 'mat:surface'],
  ['mat:primary', 'mat:surface-container'],
  ['mat:error', 'mat:surface'],
  ['mat:error', 'mat:surface-container'],
  ['mat:on-primary', 'mat:primary'],
  ['mat:on-primary-container', 'mat:primary-container'],
  ['mat:on-error', 'mat:error'],
  ['#ffffff', 'sc:ok'],
  ['#ffffff', 'sc:danger'],
  ['sc:on-ok-container', 'sc:ok-container'],
  ['sc:on-warn-container', 'sc:warn-container'],
  ['sc:on-error-container', 'sc:error-container'],
  ['sc:on-info-container', 'sc:info-container'],
  ['mat:on-surface', 'sc:code-bg'],
  ['mat:on-surface', 'sc:pressed'],
  ['sc:inverse-fg', 'sc:inverse-bg'],
  ['sc:inverse-action', 'sc:inverse-bg'],
  ['mat:on-surface', 'sc:info-container'],
  ['mat:on-surface-variant', 'sc:info-container'],
  ['mat:outline', 'mat:surface-container-high'],
  ['sc:on-ok-container', 'mat:surface'],
  ['sc:on-ok-container', 'mat:surface-container'],
  ['sc:on-ok-container', 'mat:surface-container-high'],
  ['sc:on-warn-container', 'mat:surface'],
  ['sc:on-error-container', 'mat:surface'],
  ['sc:on-error-container', 'mat:surface-container-high'],
  ['mat:on-surface-variant', 'sc:ok-container'],
  ['mat:on-surface', 'mat:outline-variant'],
  ['mat:on-surface-variant', 'sc:media'],
  ['mat:error', 'sc:error-container'],
  ['mat:on-surface', 'sc:error-container'],
  ['mat:on-surface-variant', 'sc:code-bg'],
  ['mat:outline', 'sc:code-bg'],
  ['mat:primary', 'sc:code-bg'],
  ['sc:on-warn-container', 'sc:code-bg'],
  ['mat:error', 'sc:code-bg'],
  ['mat:outline', 'sc:error-container'],
  ['mat:on-surface-variant', 'sc:error-container'],
  ['mat:on-surface', 'sc:warn-container'],
  ['mat:primary', 'mat:surface-container-high'],
  ['sc:on-warn-container', 'mat:surface-container-high'],
  ['mat:error', 'mat:surface-container-high'],
  ['mat:on-surface', 'sc:unread'],
  ['mat:on-surface-variant', 'sc:unread'],
  ['mat:outline', 'sc:unread'],
  ['mat:primary', 'sc:unread'],
];

const DESIGN_FONT = 'Inter, -apple-system, Segoe UI, sans-serif';

const SCHEMES = [
  ['light', LIGHT_ROLES],
  ['dark', DARK_ROLES],
] as const;

describe('the global stylesheet', () => {
  beforeAll(() => {
    compiledStylesheet();
  }, 30_000);

  it("is the build's global stylesheet and themes the page for both schemes, never dark unconditionally", () => {
    const html = block(read('./styles.scss'), 'html');
    const compiled = compiledRule(compiledStylesheet(), 'html');

    expect(build().options.styles).toEqual(['src/styles.scss']);
    expect(html).toContain('@include mat.theme(');
    expect(html).toContain('theme-type: color-scheme,');
    expect(html).not.toContain('theme-type: dark');
    expect(compiled).toContain('color-scheme: light dark;');
    expect(compiled).not.toContain('color-scheme: dark;');
  });

  it('the production build links the stylesheet without the inline loader the CSP blocks', () => {
    const index = read('./index.html');

    expect(index).toContain("script-src 'self';");
    expect(build().configurations.production.optimization).toEqual({
      scripts: true,
      styles: { minify: true, inlineCritical: false },
      fonts: true,
    });
  });

  it('the stylesheet defines the scheme-independent sc aliases on the root', () => {
    const root = block(read('./styles.scss'), ':root,\n.sc-dark-canvas');

    expect(declarations(root, '--sc-')).toEqual({
      '--sc-bg': 'var(--mat-sys-surface)',
      '--sc-surface': 'var(--mat-sys-surface-container)',
      '--sc-surface-2': 'var(--mat-sys-surface-container-high)',
      '--sc-line': 'var(--mat-sys-outline-variant)',
      '--sc-fg': 'var(--mat-sys-on-surface)',
      '--sc-fg-2': 'var(--mat-sys-on-surface-variant)',
      '--sc-muted': 'var(--mat-sys-outline)',
      '--sc-accent': 'var(--mat-sys-primary)',
      '--sc-accent-strong': 'var(--mat-sys-primary-container)',
      '--sc-on-accent': 'var(--mat-sys-on-primary-container)',
      '--sc-error': 'var(--mat-sys-error)',
      '--sc-on-danger': '#ffffff',
      '--sc-focus': 'var(--sc-accent)',
      '--sc-nav-active': 'color-mix(in srgb, var(--sc-accent) 16%, transparent)',
      '--sc-mono': "'JetBrains Mono', ui-monospace, Menlo, Consolas, monospace",
      '--sc-radius': '14px',
      '--sc-radius-sm': '8px',
    });
  });

  it('the dark theme pins the Material colour roles to the approved palette', () => {
    const dark = compiledRule(compiledStylesheet(), 'html[data-theme=dark]');

    expect(dark).toContain('color-scheme: dark;');
    expect(systemColours(dark, Object.keys(DARK_ROLES))).toEqual(DARK_ROLES);
  });

  it("the light theme pins the Material colour roles to the prototype's light palette", () => {
    const light = compiledRule(compiledStylesheet(), 'html[data-theme=light]');

    expect(light).toContain('color-scheme: light;');
    expect(systemColours(light, Object.keys(LIGHT_ROLES))).toEqual(LIGHT_ROLES);
  });

  it('the ok, warn, error and info container pairs, the code background, the media tile and the scrim have a light and a dark value', () => {
    const css = compiledStylesheet();
    const light = declarations(compiledRule(css, 'html[data-theme=light]'), '--sc-');
    const dark = declarations(compiledRule(css, 'html[data-theme=dark]'), '--sc-');

    expect(Object.keys(light).sort()).toEqual([...SCHEME_TOKENS].sort());
    expect(Object.keys(dark).sort()).toEqual([...SCHEME_TOKENS].sort());
    expect(dark['--sc-ok']).toBe('#2f7a57');
    expect([light['--sc-pressed'], dark['--sc-pressed']]).toEqual(['#ffffff', '#253249']);
    expect(
      ['--sc-inverse-bg', '--sc-inverse-fg', '--sc-inverse-action'].map((token) => [
        light[token],
        dark[token],
      ]),
    ).toEqual([
      ['#2a3243', '#e6e9f0'],
      ['#f5f7fb', '#18202e'],
      ['#a9c1fa', '#2a56c6'],
    ]);
    expect([light['--sc-shadow'], dark['--sc-shadow']]).toEqual([
      'rgba(24, 32, 46, 0.14)',
      'rgba(0, 0, 0, 0.45)',
    ]);
    expect(dark['--sc-warn']).toBe('#c9962a');
    for (const token of SCHEME_TOKENS) expect(light[token]).not.toBe(dark[token]);
  });

  it("the bell's icon pairs, its unread row and its shadow are Main.dc.html's palette in both themes", () => {
    const css = compiledStylesheet();
    const tokens = [
      '--sc-ok-container',
      '--sc-on-ok-container',
      '--sc-warn-container',
      '--sc-on-warn-container',
      '--sc-error-container',
      '--sc-on-error-container',
      '--sc-unread',
      '--sc-shadow',
    ];
    const pick = (scheme: string): string[] => {
      const found = declarations(compiledRule(css, `html[data-theme=${scheme}]`), '--sc-');
      return tokens.map((token) => found[token] ?? '');
    };

    expect({ light: pick('light'), dark: pick('dark') }).toEqual({
      light: [
        '#e2f3ea',
        '#1d6b46',
        '#fbf0d6',
        '#8a5d00',
        '#fbe4e2',
        '#b3261e',
        '#eef3ff',
        'rgba(24, 32, 46, 0.14)',
      ],
      dark: [
        '#16332a',
        '#7ed6a6',
        '#33290f',
        '#f0c25e',
        '#3a1c1b',
        '#f28b82',
        '#17223a',
        'rgba(0, 0, 0, 0.45)',
      ],
    });
  });

  for (const [scheme, roles] of SCHEMES) {
    it(`before the theme store answers, a ${scheme} system preference paints the ${scheme} theme`, () => {
      const css = compiledStylesheet();
      const chosen = compiledRule(css, `html[data-theme=${scheme}]`);
      const fallback = compiledRule(
        css,
        'html:not([data-theme])',
        `(prefers-color-scheme: ${scheme})`,
      );

      expect(systemColours(fallback, Object.keys(roles))).toEqual(roles);
      expect(declarations(fallback, '--sc-')).toEqual(declarations(chosen, '--sc-'));
    });
  }

  for (const [scheme] of SCHEMES) {
    it(`in the ${scheme} theme every text colour reaches 4.5:1 on the background it sits on`, () => {
      const rule = compiledRule(compiledStylesheet(), `html[data-theme=${scheme}]`);
      const value = (name: string): string => {
        if (name.startsWith('#')) return name;
        const [kind, role] = name.split(':') as [string, string];
        const found =
          kind === 'mat'
            ? systemColours(rule, [role])[role]
            : declarations(rule, `--sc-${role}`)[`--sc-${role}`];
        expect(found, `${name} is defined in the ${scheme} theme`).toMatch(/^#[0-9a-f]{6}$/);
        return found!;
      };

      const below = TEXT_ON_BACKGROUND.map(([text, background]) => ({
        pair: `${text} on ${background}`,
        ratio: Math.round(contrast(value(text), value(background)) * 100) / 100,
      })).filter(({ ratio }) => ratio < 4.5);

      expect(below).toEqual([]);
    });
  }

  it('every notice, an error or a success included, is the inverse card the Components artboard draws: the inverse colours, the action in the inverse accent, 10 px corners', () => {
    const css = compiledStylesheet();
    const rule = compiledRule(css, '.sc-notice');

    expect({
      tokens: declarations(rule, '--mat-snack-bar-'),
      kindsOwnColours: [
        '.sc-notice--success {',
        '.sc-notice--error {',
        '.sc-notice--info {',
      ].filter((selector) => css.includes(selector)),
    }).toEqual({
      tokens: {
        '--mat-snack-bar-container-color': 'var(--sc-inverse-bg)',
        '--mat-snack-bar-supporting-text-color': 'var(--sc-inverse-fg)',
        '--mat-snack-bar-button-color': 'var(--sc-inverse-action)',
        '--mat-snack-bar-container-shape': '10px',
      },
      kindsOwnColours: [],
    });
  });

  it("a dialog opened with the sc-dialog panel class is the artboards' dialog card: 14 px corners, the surface colour, a 1 px line all round and the dialog shadow", () => {
    const rule = compiledRule(compiledStylesheet(), '.sc-dialog');

    expect(declarations(rule, '--mat-dialog-container-')).toEqual({
      '--mat-dialog-container-shape': '14px',
      '--mat-dialog-container-color': 'var(--sc-surface)',
      '--mat-dialog-container-elevation-shadow':
        '0 0 0 1px var(--sc-line), 0 20px 48px var(--sc-shadow)',
    });
  });

  it("the filled primary button is the artboards' primary under white text, #2a56c6 in light and #3466e8 in dark; links keep the accent", () => {
    const css = compiledStylesheet();
    const light = systemColours(compiledRule(css, 'html[data-theme=light]'), [
      'primary',
      'primary-container',
    ]);
    const dark = systemColours(compiledRule(css, 'html[data-theme=dark]'), [
      'primary',
      'primary-container',
    ]);

    expect(light).toEqual({ primary: '#2a56c6', 'primary-container': '#2a56c6' });
    expect(dark).toEqual({ primary: '#8fb2f9', 'primary-container': '#3466e8' });
  });

  it("muted text is the artboards' grey in both themes: #5d677d in light and #9aa3b6 in dark", () => {
    const css = compiledStylesheet();

    expect([
      systemColours(compiledRule(css, 'html[data-theme=light]'), ['outline']),
      systemColours(compiledRule(css, 'html[data-theme=dark]'), ['outline']),
    ]).toEqual([{ outline: '#5d677d' }, { outline: '#9aa3b6' }]);
  });

  it('a screen not moved to the tokens yet keeps its dark look in either theme: the dark canvas pins the dark roles, tokens and native controls', () => {
    const css = compiledStylesheet();
    const canvas = compiledRule(css, '.sc-dark-canvas');
    const dark = compiledRule(css, 'html[data-theme=dark]');

    expect(canvas).toContain('color-scheme: dark;');
    expect(systemColours(canvas, Object.keys(DARK_ROLES))).toEqual(DARK_ROLES);
    expect(declarations(canvas, '--sc-')).toEqual(declarations(dark, '--sc-'));
    expect(compiledRule(css, ':root,\n.sc-dark-canvas')).toContain(
      '--sc-bg: var(--mat-sys-surface);',
    );
  });

  it('the quick-ask window follows the theme: nothing pins it dark any more', () => {
    expect(read('./styles.scss')).not.toContain('sc-quick-ask');
    expect(compiledStylesheet()).not.toContain('sc-quick-ask');
  });

  it('the page sets in the design font stack, and the top bar and the bell panel inherit it instead of the serif default', async () => {
    const sheet = document.createElement('style');
    sheet.textContent = compiledStylesheet();
    document.head.append(sheet);
    try {
      TestBed.configureTestingModule({
        providers: [
          provideZonelessChangeDetection(),
          {
            provide: ScoutBridgeService,
            useValue: {
              themeGet: vi.fn(async () => ({ choice: 'dark', resolved: 'dark' })),
              themeSet: vi.fn(async () => ({ choice: 'dark', resolved: 'dark' })),
              onTheme: vi.fn(() => () => undefined),
              notificationsList: vi.fn(async () => []),
              notificationsMarkRead: vi.fn(async () => undefined),
              onNotifications: vi.fn(() => () => undefined),
            } as unknown as ScoutBridgeService,
          },
          {
            provide: LlmConfigStore,
            useValue: { provider: signal(null), model: signal(null), loadFromVault: vi.fn() },
          },
        ],
      });
      const fixture = TestBed.createComponent(TopBarComponent);
      fixture.componentRef.setInput('title', 'Chat');
      fixture.detectChanges();
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      root.querySelector<HTMLButtonElement>('[data-test="bell"]')!.click();
      fixture.detectChanges();
      const fontOf = (selector: string): string =>
        getComputedStyle(root.querySelector(selector)!).fontFamily;

      expect(getComputedStyle(document.body).fontFamily).toBe(DESIGN_FONT);
      expect({
        title: fontOf('[data-test="top-bar-title"]'),
        source: fontOf('[data-test="top-bar-source"]'),
        panelHeading: fontOf('[data-test="panel"] .heading'),
        panelEmpty: fontOf('[data-test="empty"]'),
      }).toEqual({
        title: DESIGN_FONT,
        source: DESIGN_FONT,
        panelHeading: DESIGN_FONT,
        panelEmpty: DESIGN_FONT,
      });
    } finally {
      sheet.remove();
    }
  });

  it('the page has no margin of its own, so the window never scrolls as a document beside the shell', () => {
    const body = /^body \{([^}]*)\}/m.exec(compiledStylesheet())?.[1] ?? '';

    expect(body).toContain('margin: 0;');
  });

  it("outlined Material buttons are the prototype's secondary button, the foreground on the line at 16 px, text buttons pad 10 px, filled buttons are white on the strong accent, in either scheme", () => {
    const overrides = compiledRule(compiledStylesheet(), 'html,\n.sc-dark-canvas');

    expect(declarations(overrides, '--mat-button-')).toEqual({
      '--mat-button-outlined-label-text-color': 'var(--sc-fg)',
      '--mat-button-outlined-outline-color': 'var(--sc-line)',
      '--mat-button-outlined-horizontal-padding': '16px',
      '--mat-button-text-horizontal-padding': '10px',
      '--mat-button-filled-container-color': 'var(--sc-accent-strong)',
      '--mat-button-filled-label-text-color': 'var(--sc-on-accent)',
    });
  });

  it('the code background is the one every artboard that draws it uses: #eef1f7 in light, #0b111c in dark', () => {
    const css = compiledStylesheet();

    expect([
      declarations(compiledRule(css, 'html[data-theme=light]'), '--sc-code-bg'),
      declarations(compiledRule(css, 'html[data-theme=dark]'), '--sc-code-bg'),
    ]).toEqual([{ '--sc-code-bg': '#eef1f7' }, { '--sc-code-bg': '#0b111c' }]);
  });

  it("a dialog opened with the sc-dialog-backdrop class sits on the theme's scrim, not the CDK's flat black", () => {
    expect(compiledRule(compiledStylesheet(), '.sc-dialog-backdrop')).toContain(
      'background: var(--sc-scrim);',
    );
  });

  it('an icon inside a Material button is centred on its label, not raised to the baseline', () => {
    const icon = compiledRule(compiledStylesheet(), '.mdc-button__label > sc-icon');

    expect(icon).toContain('vertical-align: middle;');
  });

  it("the Material badge is the prototype's count: white 11 px bold on the danger red, 18 px high, overlapping its corner by 14 px, in either scheme", () => {
    const css = compiledStylesheet();
    const overrides = compiledRule(css, 'html,\n.sc-dark-canvas');

    expect(declarations(overrides, '--mat-badge-')).toEqual({
      '--mat-badge-background-color': 'var(--sc-danger)',
      '--mat-badge-text-color': '#ffffff',
      '--mat-badge-container-size': '18px',
      '--mat-badge-line-height': '18px',
      '--mat-badge-container-padding': '0 5px',
      '--mat-badge-container-overlap-offset': '-14px',
      '--mat-badge-text-size': '11px',
      '--mat-badge-text-weight': '700',
    });
    expect([
      declarations(compiledRule(css, 'html[data-theme=light]'), '--sc-danger'),
      declarations(compiledRule(css, 'html[data-theme=dark]'), '--sc-danger'),
    ]).toEqual([{ '--sc-danger': '#b3261e' }, { '--sc-danger': '#c5372c' }]);
  });

  it('Inter, the first family of the page, is loaded from the pinned package inside the app: one variable latin file for every weight, no font from the network', () => {
    const css = compiledStylesheet();
    const faces = Array.from(css.matchAll(/@font-face \{([^}]*)\}/g), (match) => match[1]!);
    const face = faces[0] ?? '';
    const manifest = JSON.parse(read('../package.json')) as {
      dependencies: Record<string, string>;
    };

    expect(faces.length).toBe(1);
    expect(face).toContain('font-family: Inter;');
    expect(face).toContain('font-weight: 100 900;');
    expect(face).toContain(
      'src: url("@fontsource-variable/inter/files/inter-latin-wght-normal.woff2") format("woff2");',
    );
    expect(compiledRule(css, 'html')).toContain('font-family: Inter, ');
    expect(css).not.toMatch(/url\(["']?(https?:)?\/\//);
    expect(read('./index.html')).toContain("font-src 'self';");
    expect(manifest.dependencies['@fontsource-variable/inter']).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it("the build ships Inter's licence text beside the font file", () => {
    const assets = (build().options as { assets?: readonly unknown[] }).assets ?? [];

    expect(assets).toContainEqual({
      glob: 'LICENSE',
      input: 'node_modules/@fontsource-variable/inter',
      output: 'media/inter',
    });
    expect(read('../node_modules/@fontsource-variable/inter/LICENSE')).toContain(
      'SIL OPEN FONT LICENSE Version 1.1',
    );
  });

  it('a keyboard focus shows the themed focus ring everywhere', () => {
    const focus = compiledRule(compiledStylesheet(), ':focus-visible');

    expect(focus).toContain('outline: 2px solid var(--sc-focus);');
    expect(focus).toContain('outline-offset: 2px;');
  });
});
