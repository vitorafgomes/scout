import { describe, expect, test } from 'bun:test';
import { compareVersions, pickDownload, type ReleaseAsset } from '../src/version.js';

const RELEASE_PAGE = 'https://github.com/vitorafgomes/scout/releases/tag/v0.2.0';

const asset = (name: string): ReleaseAsset => ({
  name,
  browser_download_url: `https://github.com/vitorafgomes/scout/releases/download/v0.2.0/${name}`,
});

const ASSETS: readonly ReleaseAsset[] = [
  asset('SHA256SUMS-linux.txt'),
  asset('Scout-Desktop-0.2.0-linux-x86_64.AppImage'),
  asset('Scout-Desktop-0.2.0-linux-amd64.deb'),
  asset('Scout-Desktop-0.2.0-mac-x64.dmg'),
  asset('Scout-Desktop-0.2.0-mac-arm64.dmg'),
  asset('Scout-Desktop-0.2.0-win-x64.exe'),
];

describe('compareVersions', () => {
  test('compareVersions orders releases numerically, ignores a leading v and ranks prereleases below the release', () => {
    expect(compareVersions('0.1.0', '0.2.0')).toBe(-1);
    expect(compareVersions('0.2.0', '0.1.0')).toBe(1);
    expect(compareVersions('0.9.0', '0.10.0')).toBe(-1);
    expect(compareVersions('1.0.0', '0.99.99')).toBe(1);
    expect(compareVersions('v0.1.0', '0.1.0')).toBe(0);
    expect(compareVersions('0.2.0-beta.1', '0.2.0')).toBe(-1);
    expect(compareVersions('0.2.0', '0.2.0-beta.1')).toBe(1);
    expect(compareVersions('0.2.0-beta.1', '0.1.0')).toBe(1);
    expect(compareVersions('1.2.3', '1.2.3')).toBe(0);
  });

  test('a version that is not major.minor.patch counts as 0.0.0', () => {
    expect(compareVersions('not-a-version', '0.0.0')).toBe(0);
    expect(compareVersions('', '0.0.1')).toBe(-1);
    expect(compareVersions('0.0.1', 'garbage')).toBe(1);
  });
});

describe('pickDownload', () => {
  test('pickDownload chooses the AppImage, the deb, the dmg for the arch or the exe, and falls back to the release page', () => {
    const pick = (platform: string, arch: string, appImage: boolean, assets = ASSETS) =>
      pickDownload({ assets, platform, arch, appImage }, RELEASE_PAGE).split('/').pop();

    expect(pick('linux', 'x64', true)).toBe('Scout-Desktop-0.2.0-linux-x86_64.AppImage');
    expect(pick('linux', 'x64', false)).toBe('Scout-Desktop-0.2.0-linux-amd64.deb');
    expect(pick('darwin', 'arm64', false)).toBe('Scout-Desktop-0.2.0-mac-arm64.dmg');
    expect(pick('darwin', 'x64', false)).toBe('Scout-Desktop-0.2.0-mac-x64.dmg');
    expect(pick('win32', 'x64', false)).toBe('Scout-Desktop-0.2.0-win-x64.exe');
    expect(
      pickDownload({ assets: [], platform: 'linux', arch: 'x64', appImage: true }, RELEASE_PAGE),
    ).toBe(RELEASE_PAGE);
    expect(
      pickDownload(
        {
          assets: [asset('Scout-Desktop-0.2.0-mac-arm64.dmg')],
          platform: 'darwin',
          arch: 'x64',
          appImage: false,
        },
        RELEASE_PAGE,
      ),
    ).toBe(RELEASE_PAGE);
    expect(
      pickDownload(
        { assets: ASSETS, platform: 'freebsd', arch: 'x64', appImage: false },
        RELEASE_PAGE,
      ),
    ).toBe(RELEASE_PAGE);
  });

  test('Linux on arm64 gets the release page, never the x86_64 AppImage or the amd64 deb, and its own package once a release carries one', () => {
    const linux = (arch: string, appImage: boolean, assets = ASSETS) =>
      pickDownload({ assets, platform: 'linux', arch, appImage }, RELEASE_PAGE);
    const armAppImage = asset('Scout-Desktop-0.2.0-linux-arm64.AppImage');
    const armDeb = asset('Scout-Desktop-0.2.0-linux-arm64.deb');
    const withArm = [armAppImage, armDeb, ...ASSETS];

    expect(linux('arm64', true)).toBe(RELEASE_PAGE);
    expect(linux('arm64', false)).toBe(RELEASE_PAGE);
    expect(linux('arm64', true, withArm)).toBe(armAppImage.browser_download_url);
    expect(linux('arm64', false, withArm)).toBe(armDeb.browser_download_url);
    expect(linux('x64', true, withArm).split('/').pop()).toBe(
      'Scout-Desktop-0.2.0-linux-x86_64.AppImage',
    );
    expect(linux('x64', false, withArm).split('/').pop()).toBe(
      'Scout-Desktop-0.2.0-linux-amd64.deb',
    );
  });

  test('Windows on arm64 gets the x64 exe, which it runs under emulation', () => {
    expect(
      pickDownload(
        { assets: ASSETS, platform: 'win32', arch: 'arm64', appImage: false },
        RELEASE_PAGE,
      )
        .split('/')
        .pop(),
    ).toBe('Scout-Desktop-0.2.0-win-x64.exe');
  });
});
