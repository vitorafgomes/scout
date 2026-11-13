import { describe, it, expect } from 'bun:test';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { inflateSync } from 'node:zlib';
import { load } from 'js-yaml';
import {
  resolveControlPlaneBinary,
  resolveMediaTools,
  resolveProtoDir,
  resolveTrayIcon,
} from '../src/worker/resolve-binary.js';

const UI_ROOT = join(import.meta.dirname, '../../..');
const CONFIG_PATH = join(UI_ROOT, 'build/electron-builder.yml');

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
  electronFuses?: Record<string, boolean>;
  files: string[];
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
    syncDesktopName?: boolean;
    desktop?: { entry?: Record<string, string> };
    target: LinuxTarget[];
  };
  mac: {
    identity: null | string;
    extendInfo?: Record<string, string>;
    target: MacTarget[];
  };
  win: {
    certificateFile?: string;
    target: WinTarget[];
  };
  publish?: Record<string, unknown>;
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

  it('the installed app gets its icon: the launcher entry names the window class the app reports, which Electron takes from desktopName, and the desktop file and its icon carry the same name', () => {
    const windowClass = String(config.extraMetadata['desktopName']).replace(/\.desktop$/, '');

    expect(config.extraMetadata['desktopName']).toBe('scout-desktop.desktop');
    expect(config.linux.desktop?.entry?.['StartupWMClass']).toBe(windowClass);
    expect(config.linux.syncDesktopName).toBe(true);
    expect(config.linux.executableName).toBe(windowClass);
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

  it('the packaged app cannot be turned into a Node runtime: the fuses turn off run-as-Node, NODE_OPTIONS and the inspector flags, turn on cookie encryption, asar integrity and loading only from the asar, keep the file protocol privileges the renderer loads with, and reset the unsigned macOS signature after flipping', () => {
    expect(config.electronFuses).toEqual({
      runAsNode: false,
      enableNodeOptionsEnvironmentVariable: false,
      enableNodeCliInspectArguments: false,
      enableCookieEncryption: true,
      enableEmbeddedAsarIntegrityValidation: true,
      onlyLoadAppFromAsar: true,
      grantFileProtocolExtraPrivileges: true,
      resetAdHocDarwinSignature: true,
    });
  });

  it('mac.identity is null (unsigned)', () => {
    expect(config.mac.identity).toBeNull();
  });

  it('mac asks for the microphone with a usage description and the audio-input entitlement', () => {
    expect(config.mac.extendInfo?.['NSMicrophoneUsageDescription']).toContain('quick-ask');
    const entitlements = readFileSync(join(UI_ROOT, 'build/entitlements.mac.plist'), 'utf8');
    expect(entitlements).toMatch(
      /<key>com\.apple\.security\.device\.audio-input<\/key>\s*<true\/>/,
    );
  });

  it('win targets include nsis', () => {
    const nsis = config.win.target.find((t) => t.target === 'nsis');
    expect(nsis).toBeDefined();
  });

  it('win config has no certificateFile (unsigned)', () => {
    expect(config.win.certificateFile).toBeUndefined();
  });

  it('publishes the updater metadata for the public GitHub releases of vitorafgomes/scout, without a token', () => {
    expect(config.publish).toEqual({
      provider: 'github',
      owner: 'vitorafgomes',
      repo: 'scout',
      releaseType: 'release',
    });
  });
});

interface Png {
  width: number;
  height: number;
  colourType: number;
  rgba(x: number, y: number): number[];
}

/** Decodes an 8-bit, non-interlaced PNG into its header and a pixel reader; RGBA only for the pixels. */
function readPng(file: string): Png {
  const bytes = readFileSync(file);
  const idat: Buffer[] = [];
  let width = 0;
  let height = 0;
  let colourType = -1;
  for (let at = 8; at < bytes.length;) {
    const length = bytes.readUInt32BE(at);
    const type = bytes.toString('latin1', at + 4, at + 8);
    const data = bytes.subarray(at + 8, at + 8 + length);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      colourType = data[9] ?? -1;
    }
    if (type === 'IDAT') idat.push(data);
    at += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * 4;
  const pixels = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    for (let x = 0; x < stride; x++) {
      const value = raw[y * (stride + 1) + 1 + x] ?? 0;
      const left = x >= 4 ? (pixels[y * stride + x - 4] ?? 0) : 0;
      const up = y > 0 ? (pixels[(y - 1) * stride + x] ?? 0) : 0;
      const upLeft = x >= 4 && y > 0 ? (pixels[(y - 1) * stride + x - 4] ?? 0) : 0;
      const guess = left + up - upLeft;
      const paeth =
        Math.abs(guess - left) <= Math.abs(guess - up) &&
        Math.abs(guess - left) <= Math.abs(guess - upLeft)
          ? left
          : Math.abs(guess - up) <= Math.abs(guess - upLeft)
            ? up
            : upLeft;
      const predictor = [0, left, up, (left + up) >> 1, paeth][filter ?? 0] ?? 0;
      pixels[y * stride + x] = (value + predictor) & 0xff;
    }
  }
  return {
    width,
    height,
    colourType,
    rgba: (x, y) => [...pixels.subarray(y * stride + x * 4, y * stride + x * 4 + 4)],
  };
}

describe('the app icon is round: a circle on a transparent square, same colours and letter', () => {
  const ICONS = join(UI_ROOT, 'build/icons');
  const TRANSPARENT = [0, 0, 0, 0];
  const WHITE = [255, 255, 255, 255];
  const NAVY = [0x1a, 0x1a, 0x2e, 255];

  it.each([
    ['the app and tray icon', join(ICONS, 'icon.png')],
    ['the project site logo and favicon', join(UI_ROOT, '../docs/icon.png')],
  ])(
    '%s is a 512 px RGBA circle: transparent corners, navy inside the circle, the letter opaque white',
    (_label, file) => {
      const png = readPng(file);

      expect([png.width, png.height, png.colourType]).toEqual([512, 512, 6]);
      expect([png.rgba(0, 0), png.rgba(511, 0), png.rgba(0, 511), png.rgba(511, 511)]).toEqual([
        TRANSPARENT,
        TRANSPARENT,
        TRANSPARENT,
        TRANSPARENT,
      ]);
      expect(png.rgba(256, 256)).toEqual(WHITE);
      expect([png.rgba(256, 40), png.rgba(40, 256)]).toEqual([NAVY, NAVY]);
    },
  );

  it('the Windows icon carries the round picture from 16 to 256 px', () => {
    const ico = readFileSync(join(ICONS, 'icon.ico'));
    const count = ico.readUInt16LE(4);
    const sizes = Array.from({ length: count }, (_, i) => ico[6 + i * 16] || 256);

    expect([...ico.subarray(0, 4)]).toEqual([0, 0, 1, 0]);
    expect(sizes).toEqual([16, 24, 32, 48, 64, 128, 256]);
  });

  it('the macOS icon carries the round picture up to 1024 px', () => {
    const icns = readFileSync(join(ICONS, 'icon.icns'));

    expect(icns.toString('latin1', 0, 4)).toBe('icns');
    expect(icns.readUInt32BE(4)).toBe(icns.length);
    expect(icns.includes(Buffer.from('ic10', 'latin1'))).toBe(true);
  });
});

describe('the packaged app finds the control-plane host and its protos', () => {
  const RESOURCES = '/opt/scout/resources';
  const packaged = {
    app: { isPackaged: true },
    resourcesPath: RESOURCES,
    platform: 'linux' as const,
    arch: 'x64' as const,
    cwd: '/any',
  };

  it('ships the control-plane protos where resolveProtoDir looks for them', () => {
    const protos = config.extraResources.find((r) => r.to === 'protos');

    expect(protos).toBeDefined();
    const source = join(UI_ROOT, protos!.from);
    expect(existsSync(join(source, 'features.proto'))).toBe(true);
    expect(existsSync(join(source, 'providers.proto'))).toBe(true);
    expect(existsSync(join(source, 'operator.proto'))).toBe(true);
    expect(resolveProtoDir(packaged)).toBe(join(RESOURCES, protos!.to));
  });

  it('ships the tray icon where resolveTrayIcon looks for it', () => {
    const icons = config.extraResources.find((r) => r.to === 'icons');

    expect(icons).toBeDefined();
    expect(icons!.filter).toEqual(['icon.png']);
    expect(existsSync(join(UI_ROOT, icons!.from, 'icon.png'))).toBe(true);
    expect(resolveTrayIcon(packaged)).toBe(join(RESOURCES, icons!.to, 'icon.png'));
  });

  it('resolves the tray icon from ui/build/icons when not packaged', () => {
    expect(resolveTrayIcon({ cwd: join(UI_ROOT, 'packages/main/src') })).toBe(
      join(UI_ROOT, 'build/icons/icon.png'),
    );
  });

  it('resolves the packaged control-plane host inside the worker resources folder', () => {
    const worker = config.extraResources.find((r) => r.from === 'build/worker');

    expect(resolveControlPlaneBinary(packaged, {})).toBe(
      join(RESOURCES, worker!.to, 'linux-x64', 'Scout.Host.ControlPlane'),
    );
  });

  it('ships the bundled ffmpeg and fonts once, as resources where resolveMediaTools looks for them', () => {
    const tools = config.extraResources.find((r) => r.to === 'media-tools');
    const fonts = config.extraResources.find((r) => r.to === 'fonts');
    const everyFile = new Set([
      join(RESOURCES, 'media-tools', 'linux-x64', 'ffmpeg'),
      join(RESOURCES, 'fonts', 'NotoSans-Regular.ttf'),
    ]);

    expect([tools?.from, fonts?.from]).toEqual([
      'packages/main/resources/media-tools',
      'packages/main/resources/fonts',
    ]);
    expect(fonts!.filter).toEqual(['*.ttf', 'OFL.txt']);
    expect(config.files).toContain('!packages/main/resources/media-tools/**');
    expect(config.files).toContain('!packages/main/resources/fonts/**');
    expect(resolveMediaTools(packaged, (file) => everyFile.has(file))).toEqual({
      ffmpeg: join(RESOURCES, tools!.to, 'linux-x64', 'ffmpeg'),
      font: join(RESOURCES, fonts!.to, 'NotoSans-Regular.ttf'),
      piper: '',
      voices: '',
      rhubarb: '',
      mouths: '',
      tesseract: '',
      tessdata: '',
      presenters: '',
      faceModels: '',
    });
  });

  it('ships the voices folder as a resource outside the asar, where resolveMediaTools looks for it, with piper inside media-tools', () => {
    const voices = config.extraResources.find((r) => r.to === 'voices');
    const tools = config.extraResources.find((r) => r.to === 'media-tools');
    const piper = join(RESOURCES, 'media-tools', 'linux-x64', 'piper', 'piper');

    expect(voices?.from).toBe('packages/main/resources/voices');
    expect(voices!.filter).toEqual(['**/*']);
    expect(tools!.filter).toEqual(['**/*']);
    expect(config.files).toContain('!packages/main/resources/voices/**');
    expect(
      resolveMediaTools(
        packaged,
        (file) => file === piper,
        (dir) => (dir === join(RESOURCES, 'voices') ? ['en_GB-cori-medium.onnx'] : []),
      ),
    ).toMatchObject({ piper, voices: join(RESOURCES, 'voices') });
  });

  it('ships the committed mouth shapes as a resource outside the asar, where resolveMediaTools looks for them, with rhubarb inside media-tools', () => {
    const mouths = config.extraResources.find((r) => r.to === 'mouths');
    const rhubarb = join(RESOURCES, 'media-tools', 'linux-x64', 'rhubarb', 'rhubarb');
    const committed = readdirSync(join(UI_ROOT, 'packages/main/resources/mouths')).sort();

    expect(mouths?.from).toBe('packages/main/resources/mouths');
    expect(mouths!.filter).toEqual(['**/*']);
    expect(config.files).toContain('!packages/main/resources/mouths/**');
    expect(committed).toEqual([
      'A.png',
      'B.png',
      'C.png',
      'D.png',
      'E.png',
      'F.png',
      'G.png',
      'H.png',
      'LICENSE.txt',
      'VERSION',
      'X.png',
    ]);
    expect(
      resolveMediaTools(
        packaged,
        (file) => file === rhubarb,
        (dir) => (dir === join(RESOURCES, 'mouths') ? committed : []),
      ),
    ).toMatchObject({ rhubarb, mouths: join(RESOURCES, 'mouths') });
  });

  it("ships Scout's committed presenters as a resource outside the asar, where resolveMediaTools finds them through presenters.json, and the face models inside media-tools", () => {
    const presenters = config.extraResources.find((r) => r.to === 'presenters');
    const tools = config.extraResources.find((r) => r.to === 'media-tools');
    const folder = join(UI_ROOT, 'packages/main/resources/presenters');
    const committed = readdirSync(folder).sort();
    const listed = JSON.parse(readFileSync(join(folder, 'presenters.json'), 'utf8')) as {
      id: string;
      label: string;
      file: string;
      mouthBox: { x: number; y: number; width: number; height: number };
    }[];
    const face = join(RESOURCES, 'media-tools', 'linux-x64', 'face');
    const shipped = new Set([
      join(RESOURCES, 'presenters', 'presenters.json'),
      ...committed.map((name) => join(RESOURCES, 'presenters', name)),
      join(face, 'blazeface-short-range.onnx'),
      join(face, 'face-landmarker.onnx'),
    ]);

    expect(presenters?.from).toBe('packages/main/resources/presenters');
    expect(presenters!.filter).toEqual(['**/*']);
    expect(tools!.filter).toEqual(['**/*']);
    expect(config.files).toContain('!packages/main/resources/presenters/**');
    expect(committed).toEqual([
      'LICENSE.txt',
      'ada.png',
      'kofi.png',
      'mei.png',
      'presenters.json',
      'rafa.png',
    ]);
    expect(listed.map(({ id, label, file }) => [id, label, file])).toEqual([
      ['ada', 'Ada', 'ada.png'],
      ['kofi', 'Kofi', 'kofi.png'],
      ['mei', 'Mei', 'mei.png'],
      ['rafa', 'Rafa', 'rafa.png'],
    ]);
    expect(
      listed.filter(
        ({ mouthBox: b }) =>
          b.width < 0.02 ||
          b.height < 0.02 ||
          b.x < 0 ||
          b.y < 0 ||
          b.x + b.width > 1 ||
          b.y + b.height > 1,
      ),
    ).toEqual([]);
    expect(
      resolveMediaTools(
        packaged,
        (file) => shipped.has(file),
        () => [],
        (file) =>
          readFileSync(join(folder, file.slice(join(RESOURCES, 'presenters').length + 1)), 'utf8'),
      ),
    ).toMatchObject({ presenters: join(RESOURCES, 'presenters'), faceModels: face });
  });

  it.each(['publish-worker.sh', 'publish-worker.ps1'])(
    '%s publishes both the chat host and the control-plane host',
    (script) => {
      const text = readFileSync(join(UI_ROOT, 'build/scripts', script), 'utf8');
      const hosts = /\$?HOSTS\s*=\s*@?\(([^)]*)\)/i.exec(text)?.[1] ?? '';

      expect(hosts.match(/Scout\.Host\.[A-Za-z]+/g)).toEqual([
        'Scout.Host.Console',
        'Scout.Host.ControlPlane',
      ]);
    },
  );
});
