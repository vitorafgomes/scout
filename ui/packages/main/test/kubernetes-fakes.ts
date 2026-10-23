import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createLogger, type Logger } from '../src/util/logger.js';

export const TOKEN = 'kube-token-0123456789-secret';
export const CLIENT_KEY = 'LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCmtleS1zZWNyZXQ=';
export const CLIENT_CERT = 'LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCmNlcnQtc2VjcmV0';
export const PASSWORD = 'basic-password-secret';
export const EXEC_ENV_VALUE = 'exec-env-value-secret';
export const SECRETS = [TOKEN, CLIENT_KEY, CLIENT_CERT, PASSWORD, EXEC_ENV_VALUE];

export const KUBECONFIG = `apiVersion: v1
kind: Config
current-context: dev
clusters:
- name: dev-cluster
  cluster:
    server: https://dev.example.com:6443
    certificate-authority-data: Q0EtZGF0YQ==
- name: prod-cluster
  cluster:
    server: https://prod.example.com
contexts:
- name: dev
  context:
    cluster: dev-cluster
    user: token-user
    namespace: shop
- name: prod
  context:
    cluster: prod-cluster
    user: cert-user
- name: eks
  context:
    cluster: prod-cluster
    user: exec-user
- name: gke
  context:
    cluster: prod-cluster
    user: provider-user
- name: legacy
  context:
    cluster: prod-cluster
    user: basic-user
users:
- name: token-user
  user:
    token: ${TOKEN}
- name: cert-user
  user:
    client-certificate-data: ${CLIENT_CERT}
    client-key-data: ${CLIENT_KEY}
- name: exec-user
  user:
    exec:
      apiVersion: client.authentication.k8s.io/v1beta1
      command: aws
      args: [eks, get-token, --cluster-name, prod]
      env:
      - name: AWS_PROFILE_SECRET
        value: ${EXEC_ENV_VALUE}
- name: provider-user
  user:
    auth-provider:
      name: gcp
- name: basic-user
  user:
    username: admin
    password: ${PASSWORD}
`;

export interface TempHome {
  home: string;
  write(relative: string, content: string): Promise<string>;
  dispose(): Promise<void>;
}

export async function tempHome(): Promise<TempHome> {
  const home = await mkdtemp(path.join(os.tmpdir(), 'scout-kube-'));
  return {
    home,
    async write(relative, content) {
      const file = path.join(home, relative);
      await mkdir(path.dirname(file), { recursive: true });
      await writeFile(file, content);
      return file;
    },
    dispose: () => rm(home, { recursive: true, force: true }),
  };
}

export function recordingLogger(): { logger: Logger; lines: string[] } {
  const lines: string[] = [];
  return { logger: createLogger('test', { level: 'debug', sink: (l) => lines.push(l) }), lines };
}
