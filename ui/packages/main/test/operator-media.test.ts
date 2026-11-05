import { afterEach, describe, expect, it } from 'bun:test';
import {
  constants,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  truncateSync,
  writeFileSync,
} from 'node:fs';
import { copyFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { ControlPlaneNotFoundError, ScoutError } from '@scout/shared';

import {
  MEDIA_SCHEME,
  MEDIA_SCHEME_PRIVILEGES,
  VOICE_SAMPLES_HOST,
  chooseWithDialog,
  copyOwnerMedia,
  createMediaProtocolHandler,
} from '../src/operator/media.js';

const tempDirs: string[] = [];

function tempDir(): string {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'scout-media-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const MP3 = Buffer.from('ID3 narration bytes');

function projectWithMedia(): { root: string; media: string } {
  const root = tempDir();
  const media = path.join(root, 'p1', 'media');
  mkdirSync(media, { recursive: true });
  writeFileSync(path.join(media, 'scene-s1.png'), PNG);
  writeFileSync(path.join(media, 'scene-s1.mp3'), MP3);
  writeFileSync(path.join(media, 'notes.txt'), 'not media');
  writeFileSync(path.join(root, 'p1', 'secret.png'), 'outside the media directory');
  writeFileSync(path.join(root, 'outside.png'), 'outside the project');
  return { root, media };
}

async function fetchMedia(
  url: string,
  directory: (projectId: string) => Promise<string>,
): Promise<{ status: number; type: string | null; body: Buffer }> {
  return read(await createMediaProtocolHandler(directory)(new Request(url)));
}

async function read(
  response: Response,
): Promise<{ status: number; type: string | null; body: Buffer }> {
  return {
    status: response.status,
    type: response.headers.get('content-type'),
    body: Buffer.from(await response.arrayBuffer()),
  };
}

function headers(response: Response, ...names: string[]): (string | null)[] {
  return names.map((name) => response.headers.get(name));
}

describe('the scout-media protocol', () => {
  it('is registered as a standard, secure, streaming scheme and never bypasses the page CSP', () => {
    expect(MEDIA_SCHEME_PRIVILEGES).toEqual({
      scheme: 'scout-media',
      privileges: { standard: true, secure: true, stream: true },
    });
  });

  it("serves an image and an audio file of the project's media directory with their content type", async () => {
    const { media } = projectWithMedia();
    const asked: string[] = [];
    const directory = async (id: string): Promise<string> => {
      asked.push(id);
      return media;
    };

    const image = await fetchMedia(`${MEDIA_SCHEME}://p1/scene-s1.png`, directory);
    const audio = await fetchMedia(`${MEDIA_SCHEME}://p1/scene-s1.mp3`, directory);

    expect([image.status, image.type, image.body.equals(PNG)]).toEqual([200, 'image/png', true]);
    expect([audio.status, audio.type, audio.body.equals(MP3)]).toEqual([200, 'audio/mpeg', true]);
    expect(asked).toEqual(['p1', 'p1']);
  });

  it('serves the assembled video as video/mp4, streamed in byte ranges, and its jpg thumbnail, confined to the media directory like every file', async () => {
    const { root, media } = projectWithMedia();
    const MP4 = Buffer.from('....ftypisom assembled video bytes');
    writeFileSync(path.join(media, 'video.mp4'), MP4);
    writeFileSync(path.join(media, 'thumbnail.jpg'), PNG);
    writeFileSync(path.join(root, 'p1', 'other.mp4'), MP4);
    const handler = createMediaProtocolHandler(async () => media);

    const whole = await read(await handler(new Request(`${MEDIA_SCHEME}://p1/video.mp4`)));
    const ranged = await handler(
      new Request(`${MEDIA_SCHEME}://p1/video.mp4?v=2026-11-03T10%3A00%3A00Z`, {
        headers: { range: 'bytes=4-11' },
      }),
    );
    const thumbnail = await fetchMedia(`${MEDIA_SCHEME}://p1/thumbnail.jpg`, async () => media);
    const escaped = await fetchMedia(`${MEDIA_SCHEME}://p1/..%2Fother.mp4`, async () => media);

    expect([whole.status, whole.type, whole.body.equals(MP4)]).toEqual([200, 'video/mp4', true]);
    expect([ranged.status, ranged.headers.get('content-range')]).toEqual([
      206,
      `bytes 4-11/${MP4.length}`,
    ]);
    expect((await read(ranged)).body.toString()).toBe(MP4.subarray(4, 12).toString());
    expect([thumbnail.status, thumbnail.type]).toEqual([200, 'image/jpeg']);
    expect(escaped.status).toBe(404);
  });

  it('serves a proposed thumbnail named thumbnail-proposal-<id>.png as image/png from the media directory', async () => {
    const { media } = projectWithMedia();
    writeFileSync(path.join(media, 'thumbnail-proposal-pr_2-a.png'), PNG);

    const proposal = await fetchMedia(
      `${MEDIA_SCHEME}://p1/thumbnail-proposal-pr_2-a.png?v=2026-11-04T09%3A00%3A00Z`,
      async () => media,
    );

    expect([proposal.status, proposal.type, proposal.body.equals(PNG)]).toEqual([
      200,
      'image/png',
      true,
    ]);
  });

  it('streams the whole file without a Range header, saying its length and that it accepts byte ranges', async () => {
    const { media } = projectWithMedia();
    const handler = createMediaProtocolHandler(async () => media);

    const response = await handler(new Request(`${MEDIA_SCHEME}://p1/scene-s1.mp3`));

    expect(headers(response, 'content-length', 'accept-ranges', 'content-range')).toEqual([
      String(MP3.length),
      'bytes',
      null,
    ]);
    const whole = await read(response);
    expect([whole.status, whole.type, whole.body.equals(MP3)]).toEqual([200, 'audio/mpeg', true]);
  });

  it('answers a byte range with 206, the bytes asked for, Content-Range and Content-Length', async () => {
    const { media } = projectWithMedia();
    const handler = createMediaProtocolHandler(async () => media);
    const ranged = (range: string): Request =>
      new Request(`${MEDIA_SCHEME}://p1/scene-s1.mp3`, { headers: { range } });

    const middle = await handler(ranged('bytes=4-12'));
    const open = await handler(ranged('bytes=4-'));
    const suffix = await handler(ranged('bytes=-5'));
    const past = await handler(ranged(`bytes=4-${MP3.length + 50}`));

    expect(headers(middle, 'content-range', 'content-length', 'accept-ranges')).toEqual([
      `bytes 4-12/${MP3.length}`,
      '9',
      'bytes',
    ]);
    expect(headers(open, 'content-range')).toEqual([`bytes 4-${MP3.length - 1}/${MP3.length}`]);
    expect(headers(suffix, 'content-range')).toEqual([
      `bytes ${MP3.length - 5}-${MP3.length - 1}/${MP3.length}`,
    ]);
    expect(headers(past, 'content-range')).toEqual([`bytes 4-${MP3.length - 1}/${MP3.length}`]);
    const bodies = await Promise.all([middle, open, suffix, past].map(read));
    expect(bodies.map((b) => [b.status, b.type, b.body.toString()])).toEqual([
      [206, 'audio/mpeg', MP3.subarray(4, 13).toString()],
      [206, 'audio/mpeg', MP3.subarray(4).toString()],
      [206, 'audio/mpeg', MP3.subarray(MP3.length - 5).toString()],
      [206, 'audio/mpeg', MP3.subarray(4).toString()],
    ]);
  });

  it('answers 416 with the file size to a range it cannot satisfy, and ignores a Range it cannot read', async () => {
    const { media } = projectWithMedia();
    const handler = createMediaProtocolHandler(async () => media);
    const ranged = (range: string): Request =>
      new Request(`${MEDIA_SCHEME}://p1/scene-s1.mp3`, { headers: { range } });

    const refused = await Promise.all(
      [`bytes=${MP3.length}-`, 'bytes=12-4', 'bytes=-0'].map((r) => handler(ranged(r))),
    );
    const ignored = await handler(ranged('items=0-4'));

    expect(refused.map((r) => [r.status, r.headers.get('content-range')])).toEqual(
      refused.map(() => [416, `bytes */${MP3.length}`]),
    );
    const whole = await read(ignored);
    expect([whole.status, whole.body.equals(MP3)]).toEqual([200, true]);
  });

  it('serves the file whatever query string the renderer appends to bust its cache', async () => {
    const { media } = projectWithMedia();

    const image = await fetchMedia(
      `${MEDIA_SCHEME}://p1/scene-s1.png?v=2026-11-03T09%3A05%3A00Z`,
      async () => media,
    );

    expect([image.status, image.type, image.body.equals(PNG)]).toEqual([200, 'image/png', true]);
  });

  it('asks the control plane for a project media directory once a minute, and again after an error', async () => {
    const { media } = projectWithMedia();
    const asked: string[] = [];
    let clock = 1_000;
    const handler = createMediaProtocolHandler(
      async (id) => {
        asked.push(id);
        return media;
      },
      () => clock,
    );
    const get = async (file: string): Promise<number> =>
      (await handler(new Request(`${MEDIA_SCHEME}://p1/${file}`))).status;

    const first = await get('scene-s1.png');
    const cached = await get('scene-s1.mp3');
    clock += 59_999;
    const stillCached = await get('scene-s1.png');
    const askedWithinAMinute = asked.length;
    clock += 1;
    const afterAMinute = await get('scene-s1.png');
    const missing = await get('scene-s9.png');
    const afterTheError = await get('scene-s1.png');

    expect([first, cached, stillCached, afterAMinute, missing, afterTheError]).toEqual([
      200, 200, 200, 200, 404, 200,
    ]);
    expect(askedWithinAMinute).toBe(1);
    expect(asked).toEqual(['p1', 'p1', 'p1']);
  });

  it('answers 404 to an encoded ../, a separator, an unknown extension, a project id with a slash and a dot-segment path, even though each names a real file', async () => {
    const { media } = projectWithMedia();
    const directory = async (): Promise<string> => media;
    const refused = [
      `${MEDIA_SCHEME}://p1/%2E%2E%2Fsecret.png`,
      `${MEDIA_SCHEME}://p1/..%2Fsecret.png`,
      `${MEDIA_SCHEME}://p1/..%5Csecret.png`,
      `${MEDIA_SCHEME}://p1/sub%2Fscene-s1.png`,
      `${MEDIA_SCHEME}://p1/notes.txt`,
      `${MEDIA_SCHEME}://p1%2Fx/scene-s1.png`,
      `${MEDIA_SCHEME}://p1/../secret.png`,
      `${MEDIA_SCHEME}://p1/.hidden.png`,
      `${MEDIA_SCHEME}://p1/`,
      'file:///etc/hostname',
    ];

    const statuses = await Promise.all(
      refused.map(async (url) => (await fetchMedia(url, directory)).status),
    );

    expect(statuses).toEqual(refused.map(() => 404));
  });

  it('answers 404 to a media name that is a symlink to a file outside the directory', async () => {
    const { root, media } = projectWithMedia();
    symlinkSync(path.join(root, 'outside.png'), path.join(media, 'linked.png'));

    const linked = await fetchMedia(`${MEDIA_SCHEME}://p1/linked.png`, async () => media);

    expect(linked.status).toBe(404);
    expect(linked.body.toString()).not.toContain('outside the project');
  });

  it('answers 404 when the control plane knows no such project, or names no absolute directory', async () => {
    const { root, media } = projectWithMedia();
    const missing = async (): Promise<string> => {
      throw new ControlPlaneNotFoundError('Project p9 not found');
    };
    const relative = async (): Promise<string> => path.relative(root, media);
    const cwd = process.cwd();
    process.chdir(root);
    try {
      const statuses = [
        (await fetchMedia(`${MEDIA_SCHEME}://p9/scene-s1.png`, missing)).status,
        (await fetchMedia(`${MEDIA_SCHEME}://p1/scene-s1.png`, async () => '')).status,
        (await fetchMedia(`${MEDIA_SCHEME}://p1/scene-s1.png`, relative)).status,
      ];

      expect(statuses).toEqual([404, 404, 404]);
    } finally {
      process.chdir(cwd);
    }
  });
});

describe('the voice-samples host of scout-media', () => {
  const SAMPLE = Buffer.from('RIFF voice sample bytes');

  function samplesAndProject(): { root: string; media: string; samples: string } {
    const { root, media } = projectWithMedia();
    const samples = path.join(root, 'voice-samples');
    mkdirSync(samples);
    writeFileSync(path.join(samples, 'piper-en_GB-aru-medium-3.wav'), SAMPLE);
    writeFileSync(path.join(samples, 'banner.png'), PNG);
    writeFileSync(path.join(media, 'x.wav'), 'a project file, not a sample');
    writeFileSync(path.join(root, 'x.wav'), 'outside the samples directory');
    return { root, media, samples };
  }

  it('voice-samples host serves a sample and refuses an escape', async () => {
    const { media, samples } = samplesAndProject();
    const projectsAsked: string[] = [];
    const handler = createMediaProtocolHandler(
      async (id) => {
        projectsAsked.push(id);
        return media;
      },
      Date.now,
      async () => samples,
    );
    const get = (file: string, range?: string): Promise<Response> =>
      handler(
        new Request(
          `${MEDIA_SCHEME}://${VOICE_SAMPLES_HOST}/${file}`,
          range === undefined ? undefined : { headers: { range } },
        ),
      );

    const whole = await read(await get('piper-en_GB-aru-medium-3.wav'));
    const ranged = await get('piper-en_GB-aru-medium-3.wav', 'bytes=0-3');
    const rangedHeaders = headers(ranged, 'content-range', 'content-length');
    const rangedBody = await read(ranged);
    const refused = await Promise.all(
      [
        'missing.wav',
        '../x.wav',
        '%2E%2E%2Fx.wav',
        '..%2Fx.wav',
        '..%5Cx.wav',
        'sub%2Fpiper-en_GB-aru-medium-3.wav',
        'x.wav',
        'banner.png',
      ].map(async (file) => (await get(file)).status),
    );
    const projectFile = (await handler(new Request(`${MEDIA_SCHEME}://p1/x.wav`))).status;

    expect([whole.status, whole.type, whole.body.equals(SAMPLE)]).toEqual([200, 'audio/wav', true]);
    expect([rangedBody.status, rangedBody.body.toString(), ...rangedHeaders]).toEqual([
      206,
      'RIFF',
      `bytes 0-3/${SAMPLE.length}`,
      '4',
    ]);
    expect(refused).toEqual([404, 404, 404, 404, 404, 404, 404, 404]);
    expect(projectFile).toBe(200);
    expect(projectsAsked).toEqual(['p1']);
  });

  it('answers 404 to a sample when the control plane names no absolute directory or fails', async () => {
    const { media } = samplesAndProject();
    const statusWith = async (samplesDirectory: () => Promise<string>): Promise<number> =>
      (
        await createMediaProtocolHandler(
          async () => media,
          Date.now,
          samplesDirectory,
        )(new Request(`${MEDIA_SCHEME}://${VOICE_SAMPLES_HOST}/piper-en_GB-aru-medium-3.wav`))
      ).status;

    const statuses = [
      await statusWith(async () => ''),
      await statusWith(async () => 'voice-samples'),
      await statusWith(async () => {
        throw new Error('nothing listening');
      }),
    ];

    expect(statuses).toEqual([404, 404, 404]);
  });
});

describe('copyOwnerMedia', () => {
  function ownerFile(name: string, bytes: Buffer | string = PNG): string {
    const file = path.join(tempDir(), name);
    writeFileSync(file, bytes);
    return file;
  }

  async function refusal(promise: Promise<unknown>): Promise<ScoutError | string> {
    return promise.then(
      () => 'copied without an error',
      (err: unknown) => err as ScoutError,
    );
  }

  it("copies the owner's file into the media directory under owner-<scene>-<random> with the lower-cased extension, never the original name", async () => {
    const media = path.join(tempDir(), 'p1', 'media');
    const source = ownerFile('My Holiday Photo.PNG');

    const first = await copyOwnerMedia(media, 's1', 'visual', source);
    const second = await copyOwnerMedia(media, 's1', 'visual', source);

    expect(first).toMatch(/^owner-s1-[0-9a-f]{12}\.png$/);
    expect(second).toMatch(/^owner-s1-[0-9a-f]{12}\.png$/);
    expect(second).not.toBe(first);
    expect(readFileSync(path.join(media, first)).equals(PNG)).toBe(true);
    expect(readdirSync(media).sort()).toEqual([first, second].sort());
  });

  it("refuses a file whose extension is not one of the kind's, and copies nothing", async () => {
    const media = tempDir();
    const cases = [
      { kind: 'audio' as const, source: ownerFile('voice.png') },
      { kind: 'visual' as const, source: ownerFile('picture.mp3', MP3) },
      { kind: 'visual' as const, source: ownerFile('picture.png.exe') },
      { kind: 'visual' as const, source: ownerFile('picture.svg', '<svg/>') },
    ];

    const errors = await Promise.all(
      cases.map(({ kind, source }) => refusal(copyOwnerMedia(media, 's1', kind, source))),
    );

    expect(errors.map((e) => (e as ScoutError).code)).toEqual(
      cases.map(() => 'operator/invalid-request'),
    );
    expect((errors[0] as ScoutError).message).toBe(
      'Narration must be an mp3, wav, m4a or ogg file.',
    );
    expect((errors[1] as ScoutError).message).toBe(
      'A picture must be a png, jpg, jpeg or webp file.',
    );
    expect(readdirSync(media)).toEqual([]);
  });

  it('refuses audio over 100 MB and an image over 20 MB, and copies nothing', async () => {
    const media = tempDir();
    const audio = ownerFile('long.mp3', '');
    truncateSync(audio, 100 * 1024 * 1024 + 1);
    const image = ownerFile('huge.png', '');
    truncateSync(image, 20 * 1024 * 1024 + 1);
    const atCap = ownerFile('fits.png', '');
    truncateSync(atCap, 20 * 1024 * 1024);

    const audioError = await refusal(copyOwnerMedia(media, 's1', 'audio', audio));
    const imageError = await refusal(copyOwnerMedia(media, 's1', 'visual', image));
    const copied = await copyOwnerMedia(media, 's1', 'visual', atCap);

    expect((audioError as ScoutError).message).toBe('Narration files are at most 100 MB.');
    expect((imageError as ScoutError).message).toBe('Pictures are at most 20 MB.');
    expect(readdirSync(media)).toEqual([copied]);
  });

  it('copies as a reflink where the filesystem supports one, and refuses an existing target, leaving it as it was', async () => {
    const media = tempDir();
    const source = ownerFile('photo.png');
    const modes: number[] = [];
    const copied = await copyOwnerMedia(media, 's1', 'visual', source, async (from, to, mode) => {
      modes.push(mode);
      await copyFile(from, to, mode);
    });
    const occupied: string[] = [];
    const refused = await refusal(
      copyOwnerMedia(media, 's1', 'visual', source, async (from, to, mode) => {
        writeFileSync(to, 'already here');
        occupied.push(to);
        await copyFile(from, to, mode);
      }),
    );

    expect(modes).toEqual([constants.COPYFILE_EXCL | constants.COPYFILE_FICLONE]);
    expect(readFileSync(path.join(media, copied)).equals(PNG)).toBe(true);
    expect((refused as ScoutError).code).toBe('operator/media-unavailable');
    expect(readFileSync(occupied[0]!, 'utf8')).toBe('already here');
  });

  it('refuses a directory chosen as the file and a media directory that is not absolute', async () => {
    const media = tempDir();
    const folder = path.join(tempDir(), 'album.png');
    mkdirSync(folder);
    const source = ownerFile('photo.png');

    const folderError = await refusal(copyOwnerMedia(media, 's1', 'visual', folder));
    const relativeError = await refusal(copyOwnerMedia('media', 's1', 'visual', source));

    expect((folderError as ScoutError).message).toBe('Choose a file, not a folder.');
    expect((relativeError as ScoutError).code).toBe('operator/media-unavailable');
    expect(readdirSync(media)).toEqual([]);
  });

  it('copies a recording into the media directory as owner-recording-<random>.<ext>, never the original name', async () => {
    const media = path.join(tempDir(), 'p1', 'media');
    const bytes = Buffer.from('ftyp recording bytes');
    const sources = ['Meetup talk.MP4', 'talk.mkv', 'talk.webm', 'talk.MOV'].map((name) =>
      ownerFile(name, bytes),
    );

    const copied: string[] = [];
    for (const source of sources) {
      copied.push(await copyOwnerMedia(media, 'recording', 'recording', source));
    }

    expect(copied.map((name) => name.replace(/[0-9a-f]{12}/, '<random>'))).toEqual([
      'owner-recording-<random>.mp4',
      'owner-recording-<random>.mkv',
      'owner-recording-<random>.webm',
      'owner-recording-<random>.mov',
    ]);
    expect(readFileSync(path.join(media, copied[0]!)).equals(bytes)).toBe(true);
    expect(readdirSync(media).sort()).toEqual([...copied].sort());
  });

  it('refuses a recording over 8 GB or of another type, and copies nothing', async () => {
    const media = tempDir();
    const huge = ownerFile('long talk.mp4', '');
    truncateSync(huge, 8 * 1024 * 1024 * 1024 + 1);
    const picture = ownerFile('talk.png');
    const audio = ownerFile('talk.mp3', MP3);
    const avi = ownerFile('talk.avi', 'RIFF');

    const errors = await Promise.all(
      [huge, picture, audio, avi].map((source) =>
        refusal(copyOwnerMedia(media, 'recording', 'recording', source)),
      ),
    );

    expect(errors.map((e) => [(e as ScoutError).code, (e as ScoutError).message])).toEqual([
      ['operator/invalid-request', 'Recordings are at most 8 GB.'],
      ['operator/invalid-request', 'A recording must be an mp4, mkv, webm or mov file.'],
      ['operator/invalid-request', 'A recording must be an mp4, mkv, webm or mov file.'],
      ['operator/invalid-request', 'A recording must be an mp4, mkv, webm or mov file.'],
    ]);
    expect(readdirSync(media)).toEqual([]);
  });

  it('a copy that fails reaches the caller without the file path and leaves no partial file', async () => {
    const media = tempDir();
    const missing = path.join(tempDir(), 'gone.png');

    const error = await refusal(copyOwnerMedia(media, 's1', 'visual', missing));

    expect((error as ScoutError).code).toBe('operator/media-unavailable');
    expect((error as ScoutError).message).toBe(
      "Scout could not copy the file into the project's media folder.",
    );
    expect((error as ScoutError).message).not.toContain(missing);
    expect(readdirSync(media)).toEqual([]);
  });
});

describe('chooseWithDialog', () => {
  it("opens one file dialog filtered to the kind's extensions and answers the chosen path, or null when cancelled", async () => {
    const opened: unknown[] = [];
    const answers = [
      { canceled: false, filePaths: ['/home/owner/voice.mp3'] },
      { canceled: true, filePaths: [] },
    ];
    const choose = chooseWithDialog({
      showOpenDialog: async (options: unknown) => {
        opened.push(options);
        return answers.shift()!;
      },
    });

    const chosen = await choose('audio');
    const cancelled = await choose('visual');

    expect([chosen, cancelled]).toEqual(['/home/owner/voice.mp3', null]);
    expect(opened).toEqual([
      {
        properties: ['openFile'],
        filters: [{ name: 'Narration', extensions: ['mp3', 'wav', 'm4a', 'ogg'] }],
      },
      {
        properties: ['openFile'],
        filters: [{ name: 'Pictures', extensions: ['png', 'jpg', 'jpeg', 'webp'] }],
      },
    ]);
  });

  it('offers Recordings filtered to mp4, mkv, webm and mov', async () => {
    const opened: unknown[] = [];
    const choose = chooseWithDialog({
      showOpenDialog: async (options: unknown) => {
        opened.push(options);
        return { canceled: false, filePaths: ['/home/owner/Videos/talk.mp4'] };
      },
    });

    const chosen = await choose('recording');

    expect(chosen).toBe('/home/owner/Videos/talk.mp4');
    expect(opened).toEqual([
      {
        properties: ['openFile'],
        filters: [{ name: 'Recordings', extensions: ['mp4', 'mkv', 'webm', 'mov'] }],
      },
    ]);
  });
});

describe('the renderer content security policy', () => {
  function directives(policy: string, name: string): string[] {
    return policy
      .split(';')
      .map((d) => d.trim())
      .filter((d) => d.startsWith(`${name} `))
      .map((d) => d.slice(name.length + 1));
  }

  it('allows scout-media: for images and media, and no other new source, in the header main sets and the page meta tag', () => {
    const main = readFileSync(path.join(import.meta.dirname, '../src/main.ts'), 'utf8');
    const html = readFileSync(
      path.join(import.meta.dirname, '../../renderer/src/index.html'),
      'utf8',
    );
    const header = [...main.matchAll(/^\s*["']((?:img|media)-src .*)["'],$/gm)]
      .map((m) => m[1]!)
      .join(';');
    const meta = /content="(default-src[^"]*)"/.exec(html)![1]!;

    expect(directives(header, 'img-src')).toEqual([
      "'self' data: scout-media:",
      "'self' data: scout-media:",
    ]);
    expect(directives(header, 'media-src')).toEqual(['scout-media:', 'scout-media:']);
    expect(directives(meta, 'img-src')).toEqual(["'self' data: scout-media:"]);
    expect(directives(meta, 'media-src')).toEqual(['scout-media:']);
  });
});
