export interface PinnedFile {
  readonly url: string;
  readonly sha256: string;
}

export interface PinnedArchive extends PinnedFile {
  readonly root: string;
}

export interface MediaToolsTable {
  readonly ffmpeg: {
    readonly version: string;
    readonly release: string;
    readonly source: string;
    readonly archives: Readonly<Record<string, PinnedArchive>>;
  };
  readonly fonts: readonly (PinnedFile & { readonly file: string })[];
  readonly piper: {
    readonly version: string;
    readonly project: string;
    readonly release: string;
    readonly source: string;
    readonly archives: Readonly<Record<string, PinnedArchive>>;
  };
  readonly voices: {
    readonly repo: string;
    readonly commit: string;
    readonly items: readonly {
      readonly name: string;
      readonly dataset: string;
      readonly license: string;
      readonly attribution?: string;
      readonly files: readonly (PinnedFile & { readonly file: string })[];
    }[];
  };
}

export interface FetchMediaToolsOptions {
  readonly target: string;
  readonly table: MediaToolsTable;
  readonly resourcesDir: string;
  readonly cacheDir: string;
  readonly fetch: (url: string) => Promise<Response>;
  readonly extract: (archive: string, dir: string, members: readonly string[]) => unknown;
  readonly log: (line: string) => unknown;
}

export declare const TABLE_PATH: string;

export declare function archiveFor(
  table: MediaToolsTable,
  target: string,
): (PinnedArchive & { readonly members: Readonly<Record<string, string>> }) | null;

export declare function extractWithTar(
  archive: string,
  dir: string,
  members: readonly string[],
): void;

export declare function fetchMediaTools(options: FetchMediaToolsOptions): Promise<void>;
