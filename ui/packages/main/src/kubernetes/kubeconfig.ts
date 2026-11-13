import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { load } from 'js-yaml';
import type { KubernetesAuthKind, KubernetesContextInfo, KubernetesDetection } from '@scout/shared';
import type { Logger } from '../util/logger.js';

export interface KubeconfigLocation {
  env: Readonly<Record<string, string | undefined>>;
  homedir: string;
  logger: Logger;
}

type Entry = Record<string, unknown>;

/**
 * The kubeconfig kubectl would read first: the first entry of KUBECONFIG, otherwise
 * ~/.kube/config.
 */
export function locateKubeconfig(env: KubeconfigLocation['env'], homedir: string): string {
  // ponytail: a merged multi-file KUBECONFIG is not supported; only its first entry is read.
  const first = (env['KUBECONFIG'] ?? '')
    .split(path.delimiter)
    .map((entry) => entry.trim())
    .find((entry) => entry.length > 0);
  return first ?? path.join(homedir, '.kube', 'config');
}

/**
 * Lists the contexts of the kubeconfig on this computer with their server and how each
 * authenticates. Never returns or logs a token, key, certificate, password or exec env value.
 */
export async function detectKubeconfig(location: KubeconfigLocation): Promise<KubernetesDetection> {
  const file = locateKubeconfig(location.env, location.homedir);
  let text: string;
  try {
    text = await readFile(file, 'utf8');
  } catch (err: unknown) {
    if ((err as NodeJS.ErrnoException).code === 'ENOENT') {
      return { path: null, contexts: [], error: null };
    }
    location.logger.warn('kubernetes: kubeconfig could not be read', {
      path: file,
      code: (err as NodeJS.ErrnoException).code ?? 'unknown',
    });
    return unreadable(file);
  }

  let doc: unknown;
  try {
    doc = load(text);
  } catch {
    location.logger.warn('kubernetes: kubeconfig is not valid YAML', { path: file });
    return unreadable(file);
  }
  if (!isEntry(doc)) {
    location.logger.warn('kubernetes: kubeconfig is not a mapping', { path: file });
    return unreadable(file);
  }

  const clusters = byName(doc['clusters'], 'cluster');
  const users = byName(doc['users'], 'user');
  const contexts = list(doc['contexts']).flatMap((entry): KubernetesContextInfo[] => {
    const name = str(entry['name']);
    const context = isEntry(entry['context']) ? entry['context'] : {};
    if (name === null) return [];
    const clusterName = str(context['cluster']) ?? '';
    const namespace = str(context['namespace']);
    const user = users.get(str(context['user']) ?? '') ?? {};
    const auth = authKind(user);
    const execCommand = auth === 'exec' ? describeExec(user['exec']) : null;
    return [
      {
        name,
        cluster: clusterName,
        server: str(clusters.get(clusterName)?.['server']) ?? '',
        auth,
        ...(namespace === null ? {} : { namespace }),
        ...(execCommand === null ? {} : { execCommand }),
      },
    ];
  });
  return { path: file, contexts, error: null };
}

function unreadable(file: string): KubernetesDetection {
  return {
    path: file,
    contexts: [],
    error: `The Kubernetes configuration at ${file} could not be read.`,
  };
}

function authKind(user: Entry): KubernetesAuthKind {
  if (isEntry(user['exec'])) return 'exec';
  if (isEntry(user['auth-provider'])) return 'auth-provider';
  if (user['token'] !== undefined || user['tokenFile'] !== undefined) return 'token';
  if (user['client-certificate-data'] !== undefined || user['client-certificate'] !== undefined) {
    return 'client-certificate';
  }
  if (user['username'] !== undefined || user['password'] !== undefined) return 'basic';
  return 'unknown';
}

function describeExec(exec: unknown): string | null {
  if (!isEntry(exec)) return null;
  const command = str(exec['command']);
  if (command === null) return null;
  const args: unknown = exec['args'];
  const strings = Array.isArray(args) ? args.filter((a): a is string => typeof a === 'string') : [];
  return [command, ...redactSensitiveArgs(strings)].join(' ');
}

const SENSITIVE_FLAG = /secret|token|password|passwd|key|credential/i;

/** Replaces the value of any `--flag value` or `--flag=value` whose flag name looks secret. */
function redactSensitiveArgs(args: readonly string[]): string[] {
  const shown: string[] = [];
  let redactNext = false;
  for (const arg of args) {
    if (redactNext && !arg.startsWith('-')) {
      shown.push('***');
      redactNext = false;
      continue;
    }
    redactNext = false;
    const flag = /^(-{1,2}[^=]+)(=.*)?$/.exec(arg);
    if (flag === null || !SENSITIVE_FLAG.test(flag[1]!)) {
      shown.push(arg);
    } else if (flag[2] === undefined) {
      shown.push(arg);
      redactNext = true;
    } else {
      shown.push(`${flag[1]}=***`);
    }
  }
  return shown;
}

function byName(value: unknown, field: string): Map<string, Entry> {
  const map = new Map<string, Entry>();
  for (const entry of list(value)) {
    const name = str(entry['name']);
    const inner = entry[field];
    if (name !== null && isEntry(inner)) map.set(name, inner);
  }
  return map;
}

function list(value: unknown): Entry[] {
  return Array.isArray(value) ? value.filter(isEntry) : [];
}

function isEntry(value: unknown): value is Entry {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function str(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}
