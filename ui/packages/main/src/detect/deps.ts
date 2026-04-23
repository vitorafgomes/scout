import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import { readFile as fsReadFile } from 'node:fs/promises';
import { homedir as osHomedir } from 'node:os';
import type { DetectDeps, ExecResult } from './types.js';

function execWithSpawn(
  cmd: string,
  args: readonly string[],
  timeoutMs: number,
): Promise<ExecResult> {
  return new Promise((resolve) => {
    const child = spawn(cmd, [...args], {
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
      shell: false,
    });

    let stdout = '';
    let stderr = '';
    let settled = false;

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      child.kill('SIGKILL');
      resolve({ stdout, stderr, exitCode: 1 });
    }, timeoutMs);

    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8');
    });

    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });

    child.on('close', (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve({ stdout, stderr, exitCode: code ?? 1 });
    });

    child.on('error', () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve({ stdout, stderr, exitCode: 1 });
    });
  });
}

export function createProductionDeps(): DetectDeps {
  return {
    async exists(filePath: string): Promise<boolean> {
      try {
        await access(filePath);
        return true;
      } catch {
        return false;
      }
    },

    exec(cmd: string, args: readonly string[], timeoutMs: number): Promise<ExecResult> {
      return execWithSpawn(cmd, args, timeoutMs);
    },

    async readFile(filePath: string): Promise<string> {
      return fsReadFile(filePath, 'utf8');
    },

    env(name: string): string | undefined {
      return process.env[name];
    },

    homedir(): string {
      return osHomedir();
    },

    platform(): NodeJS.Platform {
      return process.platform;
    },
  };
}
