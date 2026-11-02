const RELEASES_PAGE = 'https://github.com/vitorafgomes/scout/releases/latest';
const LATEST_RELEASE_API = 'https://api.github.com/repos/vitorafgomes/scout/releases/latest';

const PLATFORMS = [
    { id: 'linux', label: 'Linux', fallbackKinds: ['AppImage', '.deb package'] },
    { id: 'mac', label: 'macOS', fallbackKinds: ['.dmg disk image (Apple silicon)'] },
    { id: 'windows', label: 'Windows', fallbackKinds: ['Installer (.exe)'] },
];

const PACKAGE_RULES = [
    { suffix: '.AppImage', platform: 'linux', kind: () => 'AppImage' },
    { suffix: '.deb', platform: 'linux', kind: () => '.deb package' },
    { suffix: '.dmg', platform: 'mac', kind: dmgKind },
    { suffix: '.exe', platform: 'windows', kind: () => 'Installer (.exe)' },
];

function dmgKind(name) {
    if (name.includes('arm64')) return '.dmg disk image (Apple silicon)';
    if (name.includes('x64')) return '.dmg disk image (Intel)';
    return '.dmg disk image';
}

/**
 * Returns 'linux', 'mac' or 'windows' for a desktop browser, and null for Android, iOS or anything unknown.
 */
export function detectPlatform({ userAgent = '', platform = '' } = {}) {
    if (userAgent.includes('Android')) return null;
    if (userAgent.includes('Windows') || platform === 'Win32') return 'windows';
    if (userAgent.includes('Macintosh') || platform.startsWith('Mac')) return 'mac';
    if (userAgent.includes('Linux') || userAgent.includes('X11') || platform.startsWith('Linux')) return 'linux';
    return null;
}

/**
 * Formats a byte count as '231 MB', '4.3 MB' or '512 KB'.
 */
export function formatSize(bytes) {
    const megabytes = bytes / (1024 * 1024);
    if (megabytes >= 10) return `${Math.round(megabytes)} MB`;
    if (megabytes >= 1) return `${megabytes.toFixed(1)} MB`;
    return `${Math.round(bytes / 1024)} KB`;
}

/**
 * Sorts the assets of a GitHub release into the packages each platform can install; everything else is ignored.
 */
export function pickDownloads(assets) {
    const downloads = { linux: [], mac: [], windows: [] };
    const packages = assets
        .map((asset) => ({ asset, rank: PACKAGE_RULES.findIndex((rule) => asset.name.endsWith(rule.suffix)) }))
        .filter(({ rank }) => rank >= 0)
        .sort((a, b) => a.rank - b.rank || a.asset.name.localeCompare(b.asset.name));
    for (const { asset, rank } of packages) {
        const rule = PACKAGE_RULES[rank];
        downloads[rule.platform].push({
            kind: rule.kind(asset.name),
            name: asset.name,
            size: asset.size,
            url: asset.browser_download_url,
        });
    }
    return downloads;
}

/**
 * Builds what the page shows from the latest release, or from no release at all, where every link goes to the releases page.
 */
export function buildModel({ release, platform }) {
    const downloads = release ? pickDownloads(release.assets ?? []) : null;
    const platforms = PLATFORMS.map(({ id, label, fallbackKinds }) => {
        const released = downloads?.[id] ?? [];
        const files =
            released.length > 0
                ? released.map((d) => ({ kind: d.kind, href: d.url, file: d.name, size: formatSize(d.size) }))
                : fallbackKinds.map((kind) => ({ kind, href: RELEASES_PAGE, file: '', size: '' }));
        return { id, label, detected: id === platform, files };
    });
    const first = platforms.find((p) => p.detected) ?? platforms[0];
    return {
        version: release ? release.tag_name.replace(/^v/, '') : '',
        detected: first.detected ? platform : null,
        primary: { label: first.label, href: first.files[0].href, file: first.files[0].file, size: first.files[0].size },
        platforms,
    };
}

export async function fetchLatestRelease(fetchFn = fetch) {
    try {
        const response = await fetchFn(LATEST_RELEASE_API, { headers: { Accept: 'application/vnd.github+json' } });
        return response.status === 200 ? await response.json() : null;
    } catch {
        return null;
    }
}

function fileLink({ kind, href, size }) {
    const link = document.createElement('a');
    link.className = 'file';
    link.href = href;
    const name = document.createElement('span');
    name.className = 'file-kind';
    name.textContent = kind;
    const weight = document.createElement('span');
    weight.className = 'file-size';
    weight.textContent = size;
    link.append(name, weight);
    return link;
}

function render(model) {
    const primary = document.getElementById('primary-download');
    primary.href = model.primary.href;
    document.getElementById('primary-label').textContent = `Download for ${model.primary.label}`;
    if (model.version) {
        const details = [`Version ${model.version}`, model.primary.file, model.primary.size].filter(Boolean);
        const meta = document.getElementById('primary-meta');
        meta.textContent = details.join(' · ');
        meta.hidden = false;
        const latest = document.getElementById('latest-version');
        latest.textContent = `Latest release: v${model.version}.`;
        latest.hidden = false;
    }
    for (const platform of model.platforms) {
        const card = document.querySelector(`[data-platform="${platform.id}"]`);
        card.dataset.detected = String(platform.detected);
        card.querySelector('.badge').hidden = !platform.detected;
        card.querySelector('.files').replaceChildren(...platform.files.map(fileLink));
    }
}

if (typeof document !== 'undefined') {
    const platform = detectPlatform({ userAgent: navigator.userAgent, platform: navigator.platform });
    render(buildModel({ release: await fetchLatestRelease(), platform }));
}
