import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import path from 'node:path';
import { ControlPlaneUnauthenticatedError, ControlPlaneUnavailableError } from '@scout/shared';
import { findRepoRoot } from '../worker/resolve-binary.js';

export interface ControlPlaneClientOptions {
  socketPath: string;
  token: string;
  protoDir?: string;
  deadlineMs?: number;
  platform?: NodeJS.Platform;
}

export interface ProviderStatusMessage {
  id: string;
  installed: boolean;
  running: boolean;
}

export interface ListProvidersResponse {
  providers: ProviderStatusMessage[];
}

export interface ControlPlaneClient {
  listProviders(): Promise<ListProvidersResponse>;
  close(): void;
}

type UnaryMethod = (
  request: object,
  metadata: grpc.Metadata,
  options: grpc.CallOptions,
  callback: (err: grpc.ServiceError | null, response: ListProvidersResponse) => void,
) => grpc.ClientUnaryCall;

const TOKEN_HEADER = 'x-scout-token';

// ponytail: the protos are read from the source tree; packaging them with the app is phase 2.
function defaultProtoDir(): string {
  return path.join(findRepoRoot(process.cwd()), 'src', 'Scout.ControlPlane.Contracts', 'protos');
}

function mapCallError(err: grpc.ServiceError): Error {
  switch (err.code) {
    case grpc.status.UNAVAILABLE:
    case grpc.status.DEADLINE_EXCEEDED:
      return new ControlPlaneUnavailableError(`Control plane call failed: ${err.details}`, {
        cause: err,
      });
    case grpc.status.UNAUTHENTICATED:
      return new ControlPlaneUnauthenticatedError(undefined, { cause: err });
    default:
      return err;
  }
}

/**
 * gRPC client for Scout.Host.ControlPlane over its Unix socket. Every call carries the
 * session token as metadata and is bounded by a deadline.
 */
export function createControlPlaneClient(options: ControlPlaneClientOptions): ControlPlaneClient {
  const { socketPath, token, deadlineMs = 10_000, platform = process.platform } = options;

  if (platform === 'win32') {
    throw new ControlPlaneUnavailableError(
      'The control-plane host listens on a Windows named pipe, which this gRPC client does not dial yet.',
    );
  }

  const protoDir = options.protoDir ?? defaultProtoDir();

  const definition = protoLoader.loadSync(path.join(protoDir, 'providers.proto'), {
    enums: String,
    longs: Number,
    defaults: true,
  });
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: { controlplane: { v1: { Providers: grpc.ServiceClientConstructor } } };
  };
  const client = new loaded.scout.controlplane.v1.Providers(
    `unix:${path.resolve(socketPath)}`,
    grpc.credentials.createInsecure(),
  );
  const listProvidersMethod = (client['ListProviders'] as UnaryMethod).bind(client);

  function waitForReady(deadline: number): Promise<void> {
    return new Promise((resolve, reject) => {
      client.waitForReady(deadline, (err) => {
        if (err == null) resolve();
        else
          reject(
            new ControlPlaneUnavailableError(`Control plane not reachable at ${socketPath}`, {
              cause: err,
            }),
          );
      });
    });
  }

  return {
    async listProviders(): Promise<ListProvidersResponse> {
      const deadline = Date.now() + deadlineMs;
      await waitForReady(deadline);
      const metadata = new grpc.Metadata();
      metadata.set(TOKEN_HEADER, token);
      return new Promise((resolve, reject) => {
        listProvidersMethod({}, metadata, { deadline }, (err, response) => {
          if (err != null) reject(mapCallError(err));
          else resolve(response);
        });
      });
    },
    close(): void {
      client.close();
    },
  };
}
