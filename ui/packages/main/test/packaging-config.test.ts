import { describe, it, expect } from 'bun:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { load } from 'js-yaml';

const CONFIG_PATH = join(import.meta.dirname, '../../../build/electron-builder.yml');

interface LinuxTarget {
  target: string;
  arch: string[];
}

interface WinTarget {
  target: string;
  arch?: string[];
}

interface MacTarget {
  target: string;
  arch?: string[];
}

interface ExtraResource {
  from: string;
  to: string;
  filter?: string[];
}

interface BuilderConfig {
  appId: string;
  electronVersion: string;
  productName: string;
  asar: boolean;
  asarUnpack: string[];
  extraResources: ExtraResource[];
  extraMetadata: {
    main: string;
    [key: string]: unknown;
  };
  linux: {
    executableName?: string;
    target: LinuxTarget[];
  };
  mac: {
    identity: null | string;
    target: MacTarget[];
  };
  win: {
    certificateFile?: string;
    target: WinTarget[];
  };
}

const raw = readFileSync(CONFIG_PATH, 'utf8');
const config = load(raw) as BuilderConfig;
const installedElectron = JSON.parse(
  readFileSync(createRequire(import.meta.url).resolve('electron/package.json'), 'utf8'),
) as { version: string };

describe('electron-builder.yml', () => {
  it('appId is com.scout.desktop', () => {
    expect(config.appId).toBe('com.scout.desktop');
  });

  it('productName is Scout Desktop', () => {
    expect(config.productName).toBe('Scout Desktop');
  });

  it('electronVersion packages the same Electron the app is built and tested against', () => {
    expect(config.electronVersion).toBe(installedElectron.version);
  });

  it('asar is enabled', () => {
    expect(config.asar).toBe(true);
  });

  it('asarUnpack includes packages/main/resources/**', () => {
    expect(config.asarUnpack).toContain('packages/main/resources/**');
  });

  it('extraResources maps build/worker to worker', () => {
    const entry = config.extraResources.find((r) => r.from === 'build/worker' && r.to === 'worker');
    expect(entry).toBeDefined();
  });

  it('extraMetadata.main points to packages/main/dist/main.js', () => {
    expect(config.extraMetadata.main).toBe('packages/main/dist/main.js');
  });

  it('linux executableName is safe for file paths', () => {
    expect(config.linux.executableName).toMatch(/^[A-Za-z0-9._ ()-]+$/);
  });

  it('linux targets include AppImage for x64', () => {
    const appimage = config.linux.target.find(
      (t) => t.target === 'AppImage' && Array.isArray(t.arch) && t.arch.includes('x64'),
    );
    expect(appimage).toBeDefined();
  });

  it('linux targets include deb', () => {
    const deb = config.linux.target.find((t) => t.target === 'deb');
    expect(deb).toBeDefined();
  });

  it('mac targets include dmg', () => {
    const dmg = config.mac.target.find((t) => t.target === 'dmg');
    expect(dmg).toBeDefined();
  });

  it('mac.identity is null (unsigned)', () => {
    expect(config.mac.identity).toBeNull();
  });

  it('win targets include nsis', () => {
    const nsis = config.win.target.find((t) => t.target === 'nsis');
    expect(nsis).toBeDefined();
  });

  it('win config has no certificateFile (unsigned)', () => {
    expect(config.win.certificateFile).toBeUndefined();
  });
});
