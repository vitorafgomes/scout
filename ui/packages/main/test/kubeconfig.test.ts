import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import path from 'node:path';
import { detectKubeconfig, locateKubeconfig } from '../src/kubernetes/kubeconfig.js';
import {
  KUBECONFIG,
  SECRETS,
  recordingLogger,
  tempHome,
  type TempHome,
} from './kubernetes-fakes.js';

describe('locateKubeconfig', () => {
  it('uses the first KUBECONFIG entry when it is set', () => {
    const env = { KUBECONFIG: ['/a/config', '/b/config'].join(path.delimiter) };

    expect(locateKubeconfig(env, '/home/me')).toBe('/a/config');
  });

  it('falls back to ~/.kube/config when KUBECONFIG is unset or empty', () => {
    expect(locateKubeconfig({}, '/home/me')).toBe(path.join('/home/me', '.kube', 'config'));
    expect(locateKubeconfig({ KUBECONFIG: '' }, '/home/me')).toBe(
      path.join('/home/me', '.kube', 'config'),
    );
  });
});

describe('detectKubeconfig', () => {
  let home: TempHome;

  beforeEach(async () => {
    home = await tempHome();
  });

  afterEach(async () => {
    await home.dispose();
  });

  it('finds ~/.kube/config and lists its contexts with server and auth kind', async () => {
    const file = await home.write('.kube/config', KUBECONFIG);

    const detection = await detectKubeconfig({
      env: {},
      homedir: home.home,
      logger: recordingLogger().logger,
    });

    expect(detection).toEqual({
      path: file,
      error: null,
      contexts: [
        {
          name: 'dev',
          cluster: 'dev-cluster',
          server: 'https://dev.example.com:6443',
          namespace: 'shop',
          auth: 'token',
        },
        {
          name: 'prod',
          cluster: 'prod-cluster',
          server: 'https://prod.example.com',
          auth: 'client-certificate',
        },
        {
          name: 'eks',
          cluster: 'prod-cluster',
          server: 'https://prod.example.com',
          auth: 'exec',
          execCommand: 'aws eks get-token --cluster-name prod',
        },
        {
          name: 'gke',
          cluster: 'prod-cluster',
          server: 'https://prod.example.com',
          auth: 'auth-provider',
        },
        {
          name: 'legacy',
          cluster: 'prod-cluster',
          server: 'https://prod.example.com',
          auth: 'basic',
        },
      ],
    });
  });

  it('reads the file KUBECONFIG names instead of ~/.kube/config', async () => {
    await home.write('.kube/config', KUBECONFIG);
    const other = await home.write(
      'other/config',
      'contexts:\n- name: only\n  context: {cluster: c, user: u}\n',
    );

    const detection = await detectKubeconfig({
      env: { KUBECONFIG: other },
      homedir: home.home,
      logger: recordingLogger().logger,
    });

    expect(detection.path).toBe(other);
    expect(detection.contexts.map((c) => c.name)).toEqual(['only']);
  });

  it('returns no path and no contexts when there is no kubeconfig', async () => {
    const detection = await detectKubeconfig({
      env: {},
      homedir: home.home,
      logger: recordingLogger().logger,
    });

    expect(detection).toEqual({ path: null, contexts: [], error: null });
  });

  it('says a malformed kubeconfig could not be read, without logging its contents', async () => {
    const shortSecretTheParserQuotesWhole = 's3cr3t';
    const file = await home.write(
      '.kube/config',
      `users:\n- token: ${shortSecretTheParserQuotesWhole}\n  : : [\n`,
    );
    const { logger, lines } = recordingLogger();

    const detection = await detectKubeconfig({ env: {}, homedir: home.home, logger });

    expect(detection).toEqual({
      path: file,
      contexts: [],
      error: `The Kubernetes configuration at ${file} could not be read.`,
    });
    expect(lines.length).toBe(1);
    expect(lines.join('\n')).not.toContain(shortSecretTheParserQuotesWhole);
  });

  it('never returns or logs a token, key, certificate, password or exec env value', async () => {
    await home.write('.kube/config', KUBECONFIG);
    const { logger, lines } = recordingLogger();

    const detection = await detectKubeconfig({ env: {}, homedir: home.home, logger });

    const visible = JSON.stringify(detection) + lines.join('\n');
    for (const secret of SECRETS) expect(visible).not.toContain(secret);
  });

  it('shows exec arguments but hides the value of a secret-looking flag', async () => {
    await home.write(
      '.kube/config',
      [
        'contexts:',
        '- name: sso',
        '  context: {cluster: c, user: u}',
        'users:',
        '- name: u',
        '  user:',
        '    exec:',
        '      command: get-token',
        '      args: [--client-secret, abc123, --token=xyz, --cluster-name, prod]',
      ].join('\n'),
    );

    const detection = await detectKubeconfig({
      env: {},
      homedir: home.home,
      logger: recordingLogger().logger,
    });

    const command = detection.contexts[0]?.execCommand;
    expect(command).toBe('get-token --client-secret *** --token=*** --cluster-name prod');
    expect(command).not.toContain('abc123');
    expect(command).not.toContain('xyz');
  });
});
