import { describe, expect, test } from 'bun:test';
import { buildModel, detectArch, detectPlatform, fetchLatestRelease, formatSize, pickDownloads } from './site.js';

const RELEASES_PAGE = 'https://github.com/vitorafgomes/scout/releases/latest';
const DOWNLOAD = 'https://github.com/vitorafgomes/scout/releases/download/v0.0.1/';

const asset = (name, size) => ({ name, size, browser_download_url: DOWNLOAD + name });

const RELEASE = {
    tag_name: 'v0.0.1',
    assets: [
        asset('SHA256SUMS-linux.txt', 200),
        asset('Scout-Desktop-0.0.1-win-x64.exe', 120 * 1024 * 1024),
        asset('Scout-Desktop-0.0.1-linux-amd64.deb', 187 * 1024 * 1024),
        asset('Scout-Desktop-0.0.1-mac-x64.dmg', 160 * 1024 * 1024),
        asset('Scout-Desktop-0.0.1-linux-x86_64.AppImage', 231 * 1024 * 1024),
        asset('Scout-Desktop-0.0.1-mac-arm64.dmg', 150 * 1024 * 1024),
    ],
};

describe('detectPlatform', () => {
    test('detectPlatform recognises Windows, macOS and Linux user agents', () => {
        expect(
            detectPlatform({
                userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36',
                platform: 'Win32',
            }),
        ).toBe('windows');
        expect(detectPlatform({ userAgent: '', platform: 'Win32' })).toBe('windows');
        expect(
            detectPlatform({
                userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.1 Safari/605.1.15',
                platform: 'MacIntel',
            }),
        ).toBe('mac');
        expect(detectPlatform({ userAgent: '', platform: 'MacIntel' })).toBe('mac');
        expect(
            detectPlatform({
                userAgent: 'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0',
                platform: 'Linux x86_64',
            }),
        ).toBe('linux');
        expect(detectPlatform({ userAgent: 'Mozilla/5.0 (X11; FreeBSD amd64)', platform: '' })).toBe('linux');
    });

    test('detectPlatform treats Android and unknown agents as no platform', () => {
        expect(
            detectPlatform({
                userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/131.0 Mobile Safari/537.36',
                platform: 'Linux armv8l',
            }),
        ).toBeNull();
        expect(
            detectPlatform({
                userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148',
                platform: 'iPhone',
            }),
        ).toBeNull();
        expect(detectPlatform({ userAgent: 'curl/8.11.0', platform: '' })).toBeNull();
        expect(detectPlatform({})).toBeNull();
    });

    test('detectPlatform treats an iPad with the desktop user agent as no platform, and a Mac without touch as macOS', () => {
        const desktopSafari =
            'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15';
        expect(detectPlatform({ userAgent: desktopSafari, platform: 'MacIntel', maxTouchPoints: 5 })).toBeNull();
        expect(detectPlatform({ userAgent: desktopSafari, platform: 'MacIntel', maxTouchPoints: 0 })).toBe('mac');
    });
});

describe('detectArch', () => {
    test('detectArch knows an arm64 visitor by the user agent, the platform or the architecture hint, and nothing else', () => {
        expect(
            detectArch({
                userAgent: 'Mozilla/5.0 (X11; Linux aarch64; rv:133.0) Gecko/20100101 Firefox/133.0',
                platform: 'Linux aarch64',
            }),
        ).toBe('arm64');
        expect(detectArch({ userAgent: '', platform: 'Linux aarch64' })).toBe('arm64');
        expect(detectArch({ userAgent: 'Mozilla/5.0 (X11; Linux arm64)', platform: '' })).toBe('arm64');
        expect(
            detectArch({
                userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36',
                platform: 'Linux x86_64',
                architecture: 'arm',
            }),
        ).toBe('arm64');
        expect(
            detectArch({
                userAgent: 'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0',
                platform: 'Linux x86_64',
                architecture: 'x86',
            }),
        ).toBeNull();
        expect(detectArch({})).toBeNull();
    });
});

describe('formatSize', () => {
    test('formatSize renders MB and KB', () => {
        expect(formatSize(231 * 1024 * 1024 + 300 * 1024)).toBe('231 MB');
        expect(formatSize(10 * 1024 * 1024)).toBe('10 MB');
        expect(formatSize(4.25 * 1024 * 1024)).toBe('4.3 MB');
        expect(formatSize(1024 * 1024)).toBe('1.0 MB');
        expect(formatSize(512 * 1024)).toBe('512 KB');
    });
});

describe('pickDownloads', () => {
    test('pickDownloads sorts the release assets into linux, mac and windows downloads', () => {
        expect(pickDownloads(RELEASE.assets)).toEqual({
            linux: [
                {
                    kind: 'AppImage',
                    name: 'Scout-Desktop-0.0.1-linux-x86_64.AppImage',
                    size: 231 * 1024 * 1024,
                    url: DOWNLOAD + 'Scout-Desktop-0.0.1-linux-x86_64.AppImage',
                },
                {
                    kind: '.deb package',
                    name: 'Scout-Desktop-0.0.1-linux-amd64.deb',
                    size: 187 * 1024 * 1024,
                    url: DOWNLOAD + 'Scout-Desktop-0.0.1-linux-amd64.deb',
                },
            ],
            mac: [
                {
                    kind: '.dmg disk image (Apple silicon)',
                    name: 'Scout-Desktop-0.0.1-mac-arm64.dmg',
                    size: 150 * 1024 * 1024,
                    url: DOWNLOAD + 'Scout-Desktop-0.0.1-mac-arm64.dmg',
                },
                {
                    kind: '.dmg disk image (Intel)',
                    name: 'Scout-Desktop-0.0.1-mac-x64.dmg',
                    size: 160 * 1024 * 1024,
                    url: DOWNLOAD + 'Scout-Desktop-0.0.1-mac-x64.dmg',
                },
            ],
            windows: [
                {
                    kind: 'Installer (.exe)',
                    name: 'Scout-Desktop-0.0.1-win-x64.exe',
                    size: 120 * 1024 * 1024,
                    url: DOWNLOAD + 'Scout-Desktop-0.0.1-win-x64.exe',
                },
            ],
        });
    });

    test('pickDownloads ignores blockmap, yml and other non-package assets', () => {
        const downloads = pickDownloads([
            asset('Scout-Desktop-0.0.1-win-x64.exe.blockmap', 100),
            asset('Scout-Desktop-0.0.1-mac-arm64.dmg.blockmap', 100),
            asset('latest.yml', 100),
            asset('latest-mac.yml', 100),
            asset('latest-linux.yml', 100),
            asset('builder-effective-config.yaml', 100),
            asset('SHA256SUMS-windows.txt', 100),
            asset('Scout-Desktop-0.0.1.tar.gz', 100),
        ]);
        expect(downloads).toEqual({ linux: [], mac: [], windows: [] });
    });
});

describe('buildModel', () => {
    test('buildModel takes the version from the release tag without the leading v', () => {
        expect(buildModel({ release: { tag_name: 'v1.4.0', assets: [] }, platform: null }).version).toBe('1.4.0');
        expect(buildModel({ release: { tag_name: 'v2.0.0-beta.1', assets: [] }, platform: null }).version).toBe('2.0.0-beta.1');
    });

    test('buildModel marks the detected platform and points the primary button at its first package', () => {
        const model = buildModel({ release: RELEASE, platform: 'mac' });

        expect(model.version).toBe('0.0.1');
        expect(model.detected).toBe('mac');
        expect(model.platforms.map((p) => [p.id, p.detected])).toEqual([
            ['linux', false],
            ['mac', true],
            ['windows', false],
        ]);
        expect(model.primary).toEqual({
            label: 'macOS',
            href: DOWNLOAD + 'Scout-Desktop-0.0.1-mac-arm64.dmg',
            file: 'Scout-Desktop-0.0.1-mac-arm64.dmg',
            size: '150 MB',
        });
        expect(model.platforms[0].files).toEqual([
            {
                kind: 'AppImage',
                href: DOWNLOAD + 'Scout-Desktop-0.0.1-linux-x86_64.AppImage',
                file: 'Scout-Desktop-0.0.1-linux-x86_64.AppImage',
                size: '231 MB',
            },
            {
                kind: '.deb package',
                href: DOWNLOAD + 'Scout-Desktop-0.0.1-linux-amd64.deb',
                file: 'Scout-Desktop-0.0.1-linux-amd64.deb',
                size: '187 MB',
            },
        ]);
    });

    test('buildModel with no detected platform marks none and offers the Linux package first', () => {
        const model = buildModel({ release: RELEASE, platform: null });

        expect(model.detected).toBeNull();
        expect(model.platforms.map((p) => p.detected)).toEqual([false, false, false]);
        expect(model.primary.label).toBe('Linux');
        expect(model.primary.href).toBe(DOWNLOAD + 'Scout-Desktop-0.0.1-linux-x86_64.AppImage');
    });

    test('buildModel sends a Linux arm64 visitor to the releases page, never to the x86_64 AppImage or the amd64 deb', () => {
        const model = buildModel({ release: RELEASE, platform: 'linux', arch: 'arm64' });

        expect(model.detected).toBe('linux');
        expect(model.primary).toEqual({ label: 'Linux', href: RELEASES_PAGE, file: '', size: '' });
        expect(model.platforms[0].files.map((f) => f.kind)).toEqual(['AppImage', '.deb package']);
    });

    test('buildModel with arm64 Linux packages in the release gives each visitor its own AppImage and labels the rows by architecture', () => {
        const release = {
            tag_name: 'v0.0.3',
            assets: [
                ...RELEASE.assets,
                asset('Scout-Desktop-0.0.1-linux-arm64.AppImage', 225 * 1024 * 1024),
                asset('Scout-Desktop-0.0.1-linux-arm64.deb', 181 * 1024 * 1024),
            ],
        };

        const primaryFor = (platform, arch) => buildModel({ release, platform, arch }).primary.file;
        expect(primaryFor('linux', null)).toBe('Scout-Desktop-0.0.1-linux-x86_64.AppImage');
        expect(primaryFor('linux', undefined)).toBe('Scout-Desktop-0.0.1-linux-x86_64.AppImage');
        expect(primaryFor(null, null)).toBe('Scout-Desktop-0.0.1-linux-x86_64.AppImage');
        expect(primaryFor('linux', 'arm64')).toBe('Scout-Desktop-0.0.1-linux-arm64.AppImage');
        expect(primaryFor('windows', 'arm64')).toBe('Scout-Desktop-0.0.1-win-x64.exe');

        const model = buildModel({ release, platform: 'linux', arch: 'arm64' });
        expect(model.primary).toEqual({
            label: 'Linux',
            href: DOWNLOAD + 'Scout-Desktop-0.0.1-linux-arm64.AppImage',
            file: 'Scout-Desktop-0.0.1-linux-arm64.AppImage',
            size: '225 MB',
        });
        expect(model.platforms[0].files.map((f) => [f.kind, f.file])).toEqual([
            ['AppImage (arm64)', 'Scout-Desktop-0.0.1-linux-arm64.AppImage'],
            ['AppImage (x86_64)', 'Scout-Desktop-0.0.1-linux-x86_64.AppImage'],
            ['.deb package (amd64)', 'Scout-Desktop-0.0.1-linux-amd64.deb'],
            ['.deb package (arm64)', 'Scout-Desktop-0.0.1-linux-arm64.deb'],
        ]);
        expect(model.platforms[1].files.map((f) => f.kind)).toEqual([
            '.dmg disk image (Apple silicon)',
            '.dmg disk image (Intel)',
        ]);
    });

    test('buildModel without a release sends every button to the releases page', () => {
        const model = buildModel({ release: null, platform: 'windows' });

        expect(model.version).toBe('');
        expect(model.primary).toEqual({ label: 'Windows', href: RELEASES_PAGE, file: '', size: '' });
        expect(model.platforms.find((p) => p.id === 'windows').detected).toBe(true);
        expect(model.platforms.flatMap((p) => p.files.map((f) => [p.id, f.kind, f.href, f.size]))).toEqual([
            ['linux', 'AppImage', RELEASES_PAGE, ''],
            ['linux', '.deb package', RELEASES_PAGE, ''],
            ['mac', '.dmg disk image (Apple silicon)', RELEASES_PAGE, ''],
            ['windows', 'Installer (.exe)', RELEASES_PAGE, ''],
        ]);
    });

    test('buildModel sends a platform the release lacks to the releases page', () => {
        const release = { tag_name: 'v0.0.2', assets: RELEASE.assets.filter((a) => !a.name.endsWith('.exe')) };
        const model = buildModel({ release, platform: 'windows' });

        expect(model.version).toBe('0.0.2');
        expect(model.primary).toEqual({ label: 'Windows', href: RELEASES_PAGE, file: '', size: '' });
        expect(model.platforms.find((p) => p.id === 'windows').files).toEqual([
            { kind: 'Installer (.exe)', href: RELEASES_PAGE, file: '', size: '' },
        ]);
    });
});

describe('fetchLatestRelease', () => {
    test('returns null when the API rate-limits or has no release, so the page falls back to the releases page', async () => {
        const rateLimited = async () => new Response('{"message":"API rate limit exceeded"}', { status: 403 });
        const noRelease = async () => new Response('{"message":"Not Found"}', { status: 404 });

        expect(await fetchLatestRelease(rateLimited)).toBeNull();
        expect(await fetchLatestRelease(noRelease)).toBeNull();
    });

    test('returns null when the network call itself fails', async () => {
        const offline = async () => {
            throw new TypeError('Failed to fetch');
        };

        expect(await fetchLatestRelease(offline)).toBeNull();
    });

    test('returns the release body on 200', async () => {
        const ok = async () => new Response('{"tag_name":"v1.4.0","assets":[]}', { status: 200 });

        expect(await fetchLatestRelease(ok)).toEqual({ tag_name: 'v1.4.0', assets: [] });
    });
});
