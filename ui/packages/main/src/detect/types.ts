export interface ExecResult {
  readonly stdout: string;
  readonly stderr: string;
  readonly exitCode: number;
}

export interface DetectDeps {
  exists(path: string): Promise<boolean>;
  exec(cmd: string, args: readonly string[], timeoutMs: number): Promise<ExecResult>;
  readFile(path: string): Promise<string>;
  env(name: string): string | undefined;
  homedir(): string;
  platform(): NodeJS.Platform;
}
