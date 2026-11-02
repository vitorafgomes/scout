/** A GitHub release asset, with the field names the Releases API uses. */
export interface ReleaseAsset {
  readonly name: string;
  readonly browser_download_url: string;
}

function parse(version: string): { core: number[]; prerelease: string } {
  const [main = '', prerelease = ''] = version
    .trim()
    .replace(/^v/i, '')
    .split('+')[0]!
    .split(/-(.*)/s);
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(main);
  return match === null
    ? { core: [0, 0, 0], prerelease: '' }
    : { core: match.slice(1).map(Number), prerelease };
}

/** Orders two versions numerically by major.minor.patch; a prerelease ranks below its release. Returns -1, 0 or 1. */
export function compareVersions(a: string, b: string): -1 | 0 | 1 {
  const left = parse(a);
  const right = parse(b);
  for (let i = 0; i < 3; i++) {
    if (left.core[i]! !== right.core[i]!) return left.core[i]! < right.core[i]! ? -1 : 1;
  }
  if (left.prerelease === right.prerelease) return 0;
  if (left.prerelease === '') return 1;
  if (right.prerelease === '') return -1;
  return Math.sign(left.prerelease.localeCompare(right.prerelease, 'en', { numeric: true })) as
    -1 | 0 | 1;
}

/** The download for this machine: AppImage or deb on Linux, the dmg for the arch on macOS, the exe on Windows; otherwise the fallback. */
export function pickDownload(
  machine: {
    assets: readonly ReleaseAsset[];
    platform: string;
    arch: string;
    appImage: boolean;
  },
  fallbackUrl: string,
): string {
  const { assets, platform, arch, appImage } = machine;
  const wanted = (name: string): boolean => {
    if (platform === 'linux') return name.endsWith(appImage ? '.AppImage' : '.deb');
    if (platform === 'darwin') return name.endsWith(`-${arch}.dmg`);
    if (platform === 'win32') return name.endsWith('.exe');
    return false;
  };
  return assets.find((asset) => wanted(asset.name))?.browser_download_url ?? fallbackUrl;
}
