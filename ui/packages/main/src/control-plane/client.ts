import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import path from 'node:path';
import {
  ControlPlaneInvalidArgumentError,
  ControlPlaneNotFoundError,
  ControlPlaneNotImplementedError,
  ControlPlanePermissionDeniedError,
  ControlPlaneUnauthenticatedError,
  ControlPlaneUnavailableError,
  ControlPlaneWriteConfirmationError,
  type FeatureCredentialFieldDto,
  type FeatureDto,
  type FeatureId,
  type FeatureScopeAccess,
  type FeatureSettingFieldDto,
  type FeatureStatus,
  type FeatureWriteClass,
  type ClaimStatus,
  type MandateDto,
  type MandateSaveRequest,
  type OperatorProjectDto,
  type PendingActionDto,
  type WriteLogEntryDto,
} from '@scout/shared';

export interface ControlPlaneClientOptions {
  socketPath: string;
  token: string;
  protoDir: string;
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
  listFeatures(): Promise<FeatureDto[]>;
  setFeatureCredentials(
    id: FeatureId,
    credentials: Readonly<Record<string, string>>,
  ): Promise<FeatureDto>;
  setupFeature(
    id: FeatureId,
    credentials: Readonly<Record<string, string>>,
    settings: Readonly<Record<string, string | null>>,
  ): Promise<FeatureDto>;
  deleteFeatureCredentials(id: FeatureId): Promise<FeatureDto>;
  enableFeature(id: FeatureId, confirmWrites: boolean): Promise<FeatureDto>;
  disableFeature(id: FeatureId): Promise<FeatureDto>;
  setWriteGrant(id: FeatureId, granted: boolean, confirmWrites: boolean): Promise<FeatureDto>;
  listPendingActions(): Promise<PendingActionDto[]>;
  approveAction(id: string, reason?: string): Promise<PendingActionDto>;
  rejectAction(id: string, reason?: string): Promise<PendingActionDto>;
  listWrites(limit: number): Promise<WriteLogEntryDto[]>;
  getMandate(): Promise<MandateDto | null>;
  saveMandate(mandate: MandateSaveRequest): Promise<MandateDto>;
  listProjects(limit: number): Promise<OperatorProjectDto[]>;
  getProject(id: string): Promise<OperatorProjectDto>;
  startProject(topic: string): Promise<OperatorProjectDto>;
  decideClaim(
    projectId: string,
    claimId: string,
    status: ClaimStatus,
    note?: string,
  ): Promise<OperatorProjectDto>;
  close(): void;
}

interface FieldMessage {
  key: string;
  kind: string;
  label: string;
  helperText?: string;
  createLinkUrl?: string;
  createLinkLabel?: string;
  required?: boolean;
}

interface FeatureMessage {
  id: string;
  displayName: string;
  description: string;
  requiresCredentials: boolean;
  hasCredentials: boolean;
  enabled: boolean;
  status: string;
  lastError?: string;
  credentialFields: FieldMessage[];
  settingsFields: FieldMessage[];
  settings: Record<string, string>;
  scopeMatrix: { scope: string; actions: string[]; access: number }[];
  hasScopeMatrix: boolean;
  setupSteps: string[];
  hasSetupSteps: boolean;
  writeClass: number;
  granted: boolean;
  grantedAt?: string;
}

interface PendingActionMessage {
  id: string;
  featureId: string;
  action: string;
  target: string;
  summary: string;
  requestedAt: string;
  status: string;
  decidedAt?: string;
}

interface WriteLogEntryMessage {
  at: string;
  featureId: string;
  action: string;
  target: string;
  outcome: string;
  approvedBy?: string;
  actionId?: string;
}

interface MandateMessage {
  objective: string;
  audience: string;
  pillars: string[];
  cadence: string;
  language: string;
  voiceAndStyle: string;
  blockedTopics: string[];
  targetMetric: string;
  targetValue: string;
  updatedAt?: string;
}

interface ProjectMessage {
  id: string;
  topic: string;
  status: string;
  origin: string;
  createdAt: string;
  updatedAt: string;
  candidates: { title: string; reason: string; fit: number }[];
  script?: {
    hook: string;
    sections: { heading: string; narration: string }[];
    callToAction: string;
    titleVariants: string[];
    description: string;
    tags: string[];
    chapters: { at: string; title: string }[];
  } | null;
  claims: {
    id: string;
    text: string;
    risk: string;
    sourceUrls: string[];
    status: string;
    note?: string;
  }[];
  error?: string;
}

type UnaryMethod = (
  request: object,
  metadata: grpc.Metadata,
  options: grpc.CallOptions,
  callback: (err: grpc.ServiceError | null, response: unknown) => void,
) => grpc.ClientUnaryCall;

const TOKEN_HEADER = 'x-scout-token';
const WRITE_CONFIRMATION = 'Confirm the writes of ';

const STATUS: Readonly<Record<string, FeatureStatus>> = {
  ENABLED: 'enabled',
  NEEDS_CREDENTIALS: 'needs-credentials',
  DISABLED: 'disabled',
  ERROR: 'error',
};

const ACCESS: readonly FeatureScopeAccess[] = ['read', 'write', 'irreversible'];
const WRITE_CLASS: readonly FeatureWriteClass[] = ['none', 'descriptive', 'executive'];

function mapCallError(err: grpc.ServiceError): Error {
  switch (err.code) {
    case grpc.status.UNAVAILABLE:
    case grpc.status.DEADLINE_EXCEEDED:
      return new ControlPlaneUnavailableError(`Control plane call failed: ${err.details}`, {
        cause: err,
      });
    case grpc.status.UNAUTHENTICATED:
      return new ControlPlaneUnauthenticatedError(undefined, { cause: err });
    case grpc.status.UNIMPLEMENTED:
      return new ControlPlaneNotImplementedError(undefined, { cause: err });
    default:
      return err;
  }
}

function toCredentialField(field: FieldMessage): FeatureCredentialFieldDto {
  return {
    key: field.key,
    kind: field.kind as FeatureCredentialFieldDto['kind'],
    label: field.label,
    helperText: field.helperText ?? null,
    createLinkUrl: field.createLinkUrl ?? null,
    createLinkLabel: field.createLinkLabel ?? null,
  };
}

function toSettingField(field: FieldMessage): FeatureSettingFieldDto {
  return { ...toCredentialField(field), required: field.required === true };
}

function toFeatureDto(message: FeatureMessage): FeatureDto {
  return {
    id: message.id as FeatureId,
    displayName: message.displayName,
    description: message.description,
    requiresCredentials: message.requiresCredentials,
    hasCredentials: message.hasCredentials,
    enabled: message.enabled,
    status: STATUS[message.status] ?? 'error',
    lastError: message.lastError ?? null,
    credentialFields: message.credentialFields.map(toCredentialField),
    settingsFields: message.settingsFields.map(toSettingField),
    settings: message.settings,
    scopeMatrix: message.hasScopeMatrix
      ? message.scopeMatrix.map((row) => ({
          scope: row.scope,
          actions: row.actions,
          access: ACCESS[row.access] ?? 'irreversible',
        }))
      : null,
    setupSteps: message.hasSetupSteps ? message.setupSteps : null,
    writeClass: WRITE_CLASS[message.writeClass] ?? 'executive',
    granted: message.granted,
    grantedAt: message.grantedAt ?? null,
  };
}

function toPendingAction(message: PendingActionMessage): PendingActionDto {
  return {
    id: message.id,
    featureId: message.featureId,
    action: message.action,
    target: message.target,
    summary: message.summary,
    requestedAt: message.requestedAt,
    status: message.status,
    decidedAt: message.decidedAt ?? null,
  };
}

function toMandate(message: MandateMessage): MandateDto {
  return {
    objective: message.objective,
    audience: message.audience,
    pillars: message.pillars,
    cadence: message.cadence,
    language: message.language,
    voiceAndStyle: message.voiceAndStyle,
    blockedTopics: message.blockedTopics,
    targetMetric: message.targetMetric,
    targetValue: message.targetValue,
    updatedAt: message.updatedAt ?? null,
  };
}

function toProject(message: ProjectMessage): OperatorProjectDto {
  const script = message.script ?? null;
  return {
    id: message.id,
    topic: message.topic,
    status: message.status,
    origin: message.origin,
    createdAt: message.createdAt,
    updatedAt: message.updatedAt,
    candidates: message.candidates.map(({ title, reason, fit }) => ({ title, reason, fit })),
    script:
      script === null
        ? null
        : {
            hook: script.hook,
            sections: script.sections.map(({ heading, narration }) => ({ heading, narration })),
            callToAction: script.callToAction,
            titleVariants: script.titleVariants,
            description: script.description,
            tags: script.tags,
            chapters: script.chapters.map(({ at, title }) => ({ at, title })),
          },
    claims: message.claims.map((claim) => ({
      id: claim.id,
      text: claim.text,
      risk: claim.risk,
      sourceUrls: claim.sourceUrls,
      status: claim.status,
      note: claim.note ?? null,
    })),
    error: message.error ?? null,
  };
}

function toWriteLogEntry(message: WriteLogEntryMessage): WriteLogEntryDto {
  return {
    at: message.at,
    featureId: message.featureId,
    action: message.action,
    target: message.target,
    outcome: message.outcome,
    approvedBy: message.approvedBy ?? null,
    actionId: message.actionId ?? null,
  };
}

/**
 * gRPC client for Scout.Host.ControlPlane over its Unix socket. Every call carries the
 * session token as metadata and is bounded by a deadline.
 */
export function createControlPlaneClient(options: ControlPlaneClientOptions): ControlPlaneClient {
  const { socketPath, token, protoDir, deadlineMs = 10_000, platform = process.platform } = options;

  if (platform === 'win32') {
    throw new ControlPlaneUnavailableError(
      'The control-plane host listens on a Windows named pipe, which this gRPC client does not dial yet.',
    );
  }

  const definition = protoLoader.loadSync(
    [
      path.join(protoDir, 'providers.proto'),
      path.join(protoDir, 'features.proto'),
      path.join(protoDir, 'operator.proto'),
    ],
    { enums: String, longs: Number, defaults: true },
  );
  const loaded = grpc.loadPackageDefinition(definition) as unknown as {
    scout: {
      controlplane: {
        v1: {
          Providers: grpc.ServiceClientConstructor;
          Features: grpc.ServiceClientConstructor;
          Operator: grpc.ServiceClientConstructor;
        };
      };
    };
  };
  const target = `unix:${path.resolve(socketPath)}`;
  const providers = new loaded.scout.controlplane.v1.Providers(
    target,
    grpc.credentials.createInsecure(),
  );
  const features = new loaded.scout.controlplane.v1.Features(
    target,
    grpc.credentials.createInsecure(),
  );
  const operator = new loaded.scout.controlplane.v1.Operator(
    target,
    grpc.credentials.createInsecure(),
  );

  function waitForReady(client: grpc.Client, deadline: number): Promise<void> {
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

  let closed = false;

  async function call<T>(client: grpc.Client, method: string, request: object): Promise<T> {
    if (closed) {
      throw new ControlPlaneUnavailableError('The control-plane host has stopped');
    }
    const deadline = Date.now() + deadlineMs;
    await waitForReady(client, deadline);
    const metadata = new grpc.Metadata();
    metadata.set(TOKEN_HEADER, token);
    const unary = (client as unknown as Record<string, UnaryMethod>)[method]!.bind(client);
    return new Promise((resolve, reject) => {
      unary(request, metadata, { deadline }, (err, response) => {
        if (err != null) reject(mapCallError(err));
        else resolve(response as T);
      });
    });
  }

  const feature = async (method: string, request: object): Promise<FeatureDto> =>
    toFeatureDto(await call<FeatureMessage>(features, method, request));
  async function confirmable(method: string, request: object): Promise<FeatureDto> {
    try {
      return await feature(method, request);
    } catch (err: unknown) {
      const { code, details } = err as Partial<grpc.ServiceError>;
      if (code !== grpc.status.FAILED_PRECONDITION || !details?.startsWith(WRITE_CONFIRMATION)) {
        throw err;
      }
      throw new ControlPlaneWriteConfirmationError(details, { cause: err });
    }
  }
  async function operatorCall<T>(method: string, request: object): Promise<T> {
    try {
      return await call<T>(operator, method, request);
    } catch (err: unknown) {
      const { code, details } = err as Partial<grpc.ServiceError>;
      if (code === grpc.status.INVALID_ARGUMENT) {
        throw new ControlPlaneInvalidArgumentError(details ?? 'The request was not valid.', {
          cause: err,
        });
      }
      if (code === grpc.status.NOT_FOUND) {
        throw new ControlPlaneNotFoundError(details ?? 'Not found.', { cause: err });
      }
      if (code === grpc.status.PERMISSION_DENIED) {
        throw new ControlPlanePermissionDeniedError(details ?? 'The owner has not allowed this.', {
          cause: err,
        });
      }
      throw err;
    }
  }
  const project = async (method: string, request: object): Promise<OperatorProjectDto> =>
    toProject(await operatorCall<ProjectMessage>(method, request));
  const decide = async (method: string, id: string, reason?: string): Promise<PendingActionDto> =>
    toPendingAction(await call<PendingActionMessage>(features, method, { id, reason }));

  return {
    listProviders: () => call<ListProvidersResponse>(providers, 'ListProviders', {}),
    async listFeatures(): Promise<FeatureDto[]> {
      const response = await call<{ features: FeatureMessage[] }>(features, 'ListFeatures', {});
      return response.features.map(toFeatureDto);
    },
    setFeatureCredentials: (id, credentials) => feature('SetCredentials', { id, credentials }),
    setupFeature(id, credentials, settings) {
      const upserts: Record<string, string> = {};
      const clearedSettings: string[] = [];
      for (const [key, value] of Object.entries(settings)) {
        if (value === null) clearedSettings.push(key);
        else upserts[key] = value;
      }
      return feature('Setup', { id, credentials, settings: upserts, clearedSettings });
    },
    deleteFeatureCredentials: (id) => feature('DeleteCredentials', { id }),
    enableFeature: (id, confirmWrites) => confirmable('Enable', { id, confirmWrites }),
    disableFeature: (id) => feature('Disable', { id }),
    setWriteGrant: (id, granted, confirmWrites) =>
      confirmable('SetWriteGrant', { id, granted, confirmWrites }),
    async listPendingActions(): Promise<PendingActionDto[]> {
      const response = await call<{ actions: PendingActionMessage[] }>(
        features,
        'ListPendingActions',
        {},
      );
      return response.actions.map(toPendingAction);
    },
    approveAction: (id, reason) => decide('ApproveAction', id, reason),
    rejectAction: (id, reason) => decide('RejectAction', id, reason),
    async listWrites(limit): Promise<WriteLogEntryDto[]> {
      const response = await call<{ entries: WriteLogEntryMessage[] }>(features, 'ListWrites', {
        limit,
      });
      return response.entries.map(toWriteLogEntry);
    },
    async getMandate(): Promise<MandateDto | null> {
      try {
        const mandate = toMandate(await operatorCall<MandateMessage>('GetMandate', {}));
        return mandate.updatedAt === null ? null : mandate;
      } catch (err: unknown) {
        if (err instanceof ControlPlaneNotFoundError) return null;
        throw err;
      }
    },
    async saveMandate(mandate): Promise<MandateDto> {
      return toMandate(await operatorCall<MandateMessage>('SaveMandate', { ...mandate }));
    },
    async listProjects(limit): Promise<OperatorProjectDto[]> {
      const response = await operatorCall<{ projects: ProjectMessage[] }>('ListProjects', {
        limit,
      });
      return response.projects.map(toProject);
    },
    getProject: (id) => project('GetProject', { id }),
    startProject: (topic) => project('StartProject', { topic }),
    decideClaim: (projectId, claimId, status, note) =>
      project('DecideClaim', { projectId, claimId, status, note }),
    close(): void {
      closed = true;
      providers.close();
      features.close();
      operator.close();
    },
  };
}
