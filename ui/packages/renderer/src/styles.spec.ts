import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

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

function compiledStylesheet(): string {
  const renderer = createRequire(local('../package.json'));
  const sass = createRequire(renderer.resolve('@angular/build/package.json'))(
    'sass',
  ) as SassCompiler;
  return sass.compile(local('./styles.scss'), { loadPaths: [local('../node_modules')] }).css;
}

function systemColours(css: string, roles: readonly string[]): Record<string, string> {
  const found: Record<string, string> = {};
  for (const match of css.matchAll(/--mat-sys-([\w-]+):\s*([^;]+);/g)) {
    const [, role, value] = match;
    if (role !== undefined && value !== undefined && roles.includes(role))
      found[role] = value.trim();
  }
  return found;
}

describe('the global stylesheet', () => {
  it("is the build's global stylesheet and applies the Material theme to the whole page", () => {
    const html = block(read('./styles.scss'), 'html');

    expect(build().options.styles).toEqual(['src/styles.scss']);
    expect(html).toContain('color-scheme: dark;');
    expect(html).toContain('@include mat.theme(');
    expect(html).toContain('theme-type: dark,');
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

  it('the stylesheet defines the sc aliases on the root', () => {
    const root = block(read('./styles.scss'), ':root');

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
      '--sc-ok': '#2f7a57',
      '--sc-warn': '#c9962a',
      '--sc-error': 'var(--mat-sys-error)',
      '--sc-radius': '12px',
      '--sc-radius-sm': '8px',
    });
  });

  it('the theme pins the Material colour roles to the approved palette', () => {
    const approved: Record<string, string> = {
      surface: '#0f1623',
      'surface-container': '#131c2d',
      'surface-container-high': '#1a2436',
      'outline-variant': '#253249',
      'on-surface': '#e6e9f0',
      'on-surface-variant': '#c3cadb',
      outline: '#8a93a6',
      primary: '#7aa2f7',
      'on-primary': '#0f1623',
      'primary-container': '#3b6ff2',
      'on-primary-container': '#ffffff',
      error: '#c0504f',
      'on-error': '#ffffff',
    };

    expect(systemColours(compiledStylesheet(), Object.keys(approved))).toEqual(approved);
  });
});
