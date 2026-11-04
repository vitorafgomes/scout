import { randomBytes } from 'node:crypto';
import { constants, createReadStream } from 'node:fs';
import { copyFile, mkdir, realpath, stat, unlink } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { ScoutError, type SceneMediaKind } from '@scout/shared';

/** The scheme the renderer previews operator media through: `scout-media://<projectId>/<fileName>`. */
export const MEDIA_SCHEME = 'scout-media';

/** The reserved host of `scout-media://voice-samples/<fileName>`; a project id is 32 hex characters, never this. */
export const VOICE_SAMPLES_HOST = 'voice-samples';

/** Registered before the app is ready; deliberately without `bypassCSP`, so the page CSP still decides. */
export const MEDIA_SCHEME_PRIVILEGES = {
  scheme: MEDIA_SCHEME,
  privileges: { standard: true, secure: true, stream: true },
};

const MEDIA_TYPES: Readonly<Record<string, string>> = {
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.m4a': 'audio/mp4',
  '.ogg': 'audio/ogg',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.mp4': 'video/mp4',
};

const OWNER_MEDIA: Readonly<
  Record<
    SceneMediaKind,
    {
      readonly label: string;
      readonly extensions: readonly string[];
      readonly maxBytes: number;
      readonly wrongType: string;
      readonly tooLarge: string;
    }
  >
> = {
  audio: {
    label: 'Narration',
    extensions: ['mp3', 'wav', 'm4a', 'ogg'],
    maxBytes: 100 * 1024 * 1024,
    wrongType: 'Narration must be an mp3, wav, m4a or ogg file.',
    tooLarge: 'Narration files are at most 100 MB.',
  },
  visual: {
    label: 'Pictures',
    extensions: ['png', 'jpg', 'jpeg', 'webp'],
    maxBytes: 20 * 1024 * 1024,
    wrongType: 'A picture must be a png, jpg, jpeg or webp file.',
    tooLarge: 'Pictures are at most 20 MB.',
  },
};

const SAFE_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** True for a project or scene id that is safe inside a URL host and a file name. */
export function isSafeId(value: unknown): value is string {
  return typeof value === 'string' && SAFE_ID.test(value);
}

function isSafeMediaName(name: string): boolean {
  return (
    name.length > 0 &&
    name.length <= 120 &&
    !name.startsWith('.') &&
    !/[/\\\0]/.test(name) &&
    !name.includes('..') &&
    Object.hasOwn(MEDIA_TYPES, path.extname(name).toLowerCase())
  );
}

function isVoiceSampleName(name: string): boolean {
  return (
    isSafeMediaName(name) && MEDIA_TYPES[path.extname(name).toLowerCase()]!.startsWith('audio/')
  );
}

const DIRECTORY_TTL_MS = 60_000;

async function confinedMediaFile(
  directory: string,
  fileName: string,
  acceptable: (name: string) => boolean,
): Promise<{ file: string; size: number } | null> {
  if (!path.isAbsolute(directory)) return null;
  const root = await realpath(directory);
  const file = await realpath(path.join(root, fileName));
  if (path.dirname(file) !== root || !acceptable(path.basename(file))) return null;
  const info = await stat(file);
  return info.isFile() ? { file, size: info.size } : null;
}

/** The single byte range a `Range` header asks for; null to serve the whole file, 'unsatisfiable' for a 416. */
function byteRange(
  header: string | null,
  size: number,
): { start: number; end: number } | 'unsatisfiable' | null {
  const match = header === null ? null : /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (match === null || (match[1] === '' && match[2] === '')) return null;
  if (match[1] === '') {
    const suffix = Number(match[2]);
    return suffix === 0 || size === 0
      ? 'unsatisfiable'
      : { start: Math.max(0, size - suffix), end: size - 1 };
  }
  const start = Number(match[1]);
  const end = match[2] === '' ? size - 1 : Number(match[2]);
  if (start >= size || start > end) return 'unsatisfiable';
  return { start, end: Math.min(end, size - 1) };
}

function stream(file: string, start: number, end: number): ReadableStream<Uint8Array> {
  return Readable.toWeb(
    createReadStream(file, { start, end }),
  ) as unknown as ReadableStream<Uint8Array>;
}

/**
 * Serves a file of a project's media directory, or an audio file of the voice-samples directory under the host
 * `voice-samples`, each resolved through the control plane and kept a minute (forgotten on any error), streamed whole
 * or as the one byte range asked for; anything else is a 404.
 */
export function createMediaProtocolHandler(
  mediaDirectory: (projectId: string) => Promise<string>,
  now: () => number = Date.now,
  voiceSamplesDirectory: () => Promise<string> = async () => '',
): (request: Request) => Promise<Response> {
  const directories = new Map<string, { directory: string; at: number }>();
  const directoryOf = async (host: string): Promise<string> => {
    const known = directories.get(host);
    if (known !== undefined && now() - known.at < DIRECTORY_TTL_MS) return known.directory;
    const directory = await (host === VOICE_SAMPLES_HOST
      ? voiceSamplesDirectory()
      : mediaDirectory(host));
    if (path.isAbsolute(directory)) directories.set(host, { directory, at: now() });
    return directory;
  };
  return async (request) => {
    let host: string | null = null;
    try {
      const parsed = new URL(request.url);
      if (parsed.protocol !== `${MEDIA_SCHEME}:`) return new Response(null, { status: 404 });
      host = decodeURIComponent(parsed.hostname);
      const fileName = decodeURIComponent(parsed.pathname.slice(1));
      const acceptable = host === VOICE_SAMPLES_HOST ? isVoiceSampleName : isSafeMediaName;
      if (!isSafeId(host) || !acceptable(fileName)) {
        return new Response(null, { status: 404 });
      }
      const target = await confinedMediaFile(await directoryOf(host), fileName, acceptable);
      if (target === null) return new Response(null, { status: 404 });
      const type = MEDIA_TYPES[path.extname(target.file).toLowerCase()]!;
      const range = byteRange(request.headers.get('range'), target.size);
      if (range === 'unsatisfiable') {
        return new Response(null, {
          status: 416,
          headers: { 'content-range': `bytes */${target.size}`, 'accept-ranges': 'bytes' },
        });
      }
      if (range === null) {
        return new Response(target.size === 0 ? null : stream(target.file, 0, target.size - 1), {
          status: 200,
          headers: {
            'content-type': type,
            'content-length': String(target.size),
            'accept-ranges': 'bytes',
          },
        });
      }
      return new Response(stream(target.file, range.start, range.end), {
        status: 206,
        headers: {
          'content-type': type,
          'content-length': String(range.end - range.start + 1),
          'content-range': `bytes ${range.start}-${range.end}/${target.size}`,
          'accept-ranges': 'bytes',
        },
      });
    } catch {
      if (host !== null) directories.delete(host);
      return new Response(null, { status: 404 });
    }
  };
}

function refuse(message: string): never {
  throw new ScoutError('operator/invalid-request', message);
}

/**
 * Copies a file the owner chose into the project's media directory under a generated name
 * and answers that name; the original name and path never leave main.
 */
export async function copyOwnerMedia(
  directory: string,
  sceneId: string,
  kind: SceneMediaKind,
  source: string,
): Promise<string> {
  const rule = OWNER_MEDIA[kind];
  const extension = path.extname(source).slice(1).toLowerCase();
  if (!rule.extensions.includes(extension)) refuse(rule.wrongType);
  const unavailable = new ScoutError(
    'operator/media-unavailable',
    "Scout could not copy the file into the project's media folder.",
  );
  if (!path.isAbsolute(directory)) throw unavailable;
  const info = await stat(source).catch(() => {
    throw unavailable;
  });
  if (!info.isFile()) refuse('Choose a file, not a folder.');
  if (info.size > rule.maxBytes) refuse(rule.tooLarge);
  const name = `owner-${sceneId}-${randomBytes(6).toString('hex')}.${extension}`;
  const target = path.join(directory, name);
  try {
    await mkdir(directory, { recursive: true });
    await copyFile(source, target, constants.COPYFILE_EXCL);
  } catch {
    await unlink(target).catch(() => undefined);
    throw unavailable;
  }
  return name;
}

/** The slice of Electron's dialog the attach flow uses. */
export interface OpenFileDialog {
  showOpenDialog(options: {
    properties: ['openFile'];
    filters: { name: string; extensions: string[] }[];
  }): Promise<{ canceled: boolean; filePaths: string[] }>;
}

/** Asks the owner for one file of the kind; answers its path, or null when the owner cancels. */
export type ChooseOwnerFile = (kind: SceneMediaKind) => Promise<string | null>;

/** Opens the native file dialog filtered to the kind's extensions. */
export function chooseWithDialog(dialog: OpenFileDialog): ChooseOwnerFile {
  return async (kind) => {
    const rule = OWNER_MEDIA[kind];
    const result = await dialog.showOpenDialog({
      properties: ['openFile'],
      filters: [{ name: rule.label, extensions: [...rule.extensions] }],
    });
    return result.canceled ? null : (result.filePaths[0] ?? null);
  };
}
