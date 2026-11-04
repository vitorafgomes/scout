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
  type CommentDecision,
  type CommentsViewDto,
  type MandateDto,
  type MandateSaveRequest,
  type OperatorProjectDto,
  type PendingActionDto,
  type PrivacyStatus,
  type ProjectMotion,
  type PublicationDto,
  type ReadinessDto,
  type SceneDto,
  type SceneMediaKind,
  type VideoPackageDto,
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
  produceProject(
    id: string,
    narrationVoice: string,
    visualBrief: string,
    motion: ProjectMotion,
  ): Promise<OperatorProjectDto>;
  delegateProject(
    id: string,
    narrationVoice: string,
    visualBrief: string,
    motion: ProjectMotion,
  ): Promise<OperatorProjectDto>;
  deleteProject(id: string): Promise<void>;
  reorderScenes(projectId: string, sceneIds: readonly string[]): Promise<OperatorProjectDto>;
  setSceneLocked(projectId: string, sceneId: string, locked: boolean): Promise<OperatorProjectDto>;
  regenerateScene(projectId: string, sceneId: string): Promise<OperatorProjectDto>;
  attachSceneMedia(
    projectId: string,
    sceneId: string,
    kind: SceneMediaKind | 'background',
    fileName: string,
  ): Promise<OperatorProjectDto>;
  getMediaDirectory(projectId: string): Promise<string>;
  getVoiceSamplesDirectory(): Promise<string>;
  getReadiness(): Promise<ReadinessDto>;
  assembleProject(id: string): Promise<OperatorProjectDto>;
  confirmMediaRights(id: string): Promise<OperatorProjectDto>;
  editPackageMetadata(
    projectId: string,
    title: string,
    description: string,
    tags: readonly string[],
  ): Promise<OperatorProjectDto>;
  approvePackage(id: string): Promise<OperatorProjectDto>;
  publishPackage(
    projectId: string,
    privacyStatus: PrivacyStatus,
    publishAt: string | null,
    madeForKids: boolean,
    syntheticMedia: boolean,
  ): Promise<OperatorProjectDto>;
  decideProposal(
    projectId: string,
    proposalId: string,
    accept: boolean,
  ): Promise<OperatorProjectDto>;
  startExperiment(projectId: string, armHours: number): Promise<OperatorProjectDto>;
  adoptExperimentWinner(id: string): Promise<OperatorProjectDto>;
  listComments(): Promise<CommentsViewDto>;
  requestCommentSync(): Promise<CommentsViewDto>;
  decideComment(commentId: string, decision: CommentDecision): Promise<CommentsViewDto>;
  approveReply(commentId: string, text: string): Promise<CommentsViewDto>;
  decideIdea(ideaId: string, use: boolean): Promise<CommentsViewDto>;
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
  scenes: SceneMessage[];
  package?: PackageMessage | null;
  publication?: PublicationMessage | null;
  backgroundFile?: string;
  delegated: boolean;
  narrationVoice: string;
  visualBrief: string;
  motion: string;
}

interface PublicationMessage {
  status: string;
  videoId?: string;
  privacyStatus: string;
  publishAt?: string;
  madeForKids: boolean;
  syntheticMedia: boolean;
  uploadedAt?: string;
  error?: string;
  snapshots: {
    window: string;
    takenAt: string;
    views: number;
    minutesWatched: number;
    averageViewDurationSeconds: number;
    averageViewPercentage: number;
    subscribersGained: number;
    subscribersLost: number;
    likes: number;
    comments: number;
    shares: number;
    impressions?: number;
    impressionsCtr?: number;
    retention: { elapsedRatio: number; audienceWatchRatio: number }[];
  }[];
  proposals: {
    id: string;
    kind: string;
    text: string;
    reason: string;
    status: string;
    thumbnailFile?: string;
  }[];
  experiment?: {
    id: string;
    kind: string;
    status: string;
    arms: {
      id: string;
      label: string;
      value: string;
      startedAt?: string;
      endedAt?: string;
      impressions: number;
      clicks: number;
      averageViewPercentage: number;
    }[];
    winnerArmId?: string;
    result?: string;
  } | null;
}

interface PackageMessage {
  status: string;
  videoFile?: string;
  shorts: { file: string; startMs: number; durationMs: number; hook: string }[];
  thumbnailFile?: string;
  checks: { name: string; result: string; detail: string }[];
  rightsConfirmed: boolean;
  approvedAt?: string;
  title: string;
  description: string;
  tags: string[];
  durationMs: number;
  error?: string;
  metadataEdited: boolean;
}

interface SceneMessage {
  id: string;
  order: number;
  narration: string;
  visualPrompt: string;
  locked: boolean;
  status: string;
  error?: string;
  audioSource: string;
  visualSource: string;
  visualQuery?: string;
  visualCredit?: string;
  audioFile?: string;
  visualFile?: string;
  durationMs: number;
}

interface CommentsViewMessage {
  comments: {
    id: string;
    threadId: string;
    videoId: string;
    videoTitle: string;
    author: string;
    text: string;
    publishedAt: string;
    likeCount: number;
    kind: string;
    theme: string;
    status: string;
    draft: string;
    approvedText?: string;
    replyId?: string;
    error?: string;
    decidedAt?: string;
    fetchedAt: string;
  }[];
  ideas: { id: string; text: string; commentId: string; status: string; projectId?: string }[];
  syncedAt?: string;
  syncRequestedAt?: string;
  syncError?: string;
}

interface ReadinessMessage {
  speechAvailable: boolean;
  imagesAvailable: boolean;
  stockPictures?: boolean;
  provider: string;
  ffmpegAvailable: boolean;
  youtubeSignedIn: boolean;
  notes: string[];
  voices?: { id: string; label: string; source: string; sampleFile?: string }[];
  reportedAt?: string;
}

type UnaryMethod = (
  request: object,
  metadata: grpc.Metadata,
  options: grpc.CallOptions,
  callback: (err: grpc.ServiceError | null, response: unknown) => void,
) => grpc.ClientUnaryCall;

const TOKEN_HEADER = 'x-scout-token';
const MAX_MESSAGE_BYTES = 16 * 1024 * 1024;
const CHANNEL_OPTIONS: grpc.ChannelOptions = {
  'grpc.max_receive_message_length': MAX_MESSAGE_BYTES,
  'grpc.max_send_message_length': MAX_MESSAGE_BYTES,
};
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
    scenes: message.scenes.map(toScene),
    package: message.package == null ? null : toPackage(message.package),
    publication: message.publication == null ? null : toPublication(message.publication),
    narrationVoice: message.narrationVoice,
    backgroundFile: message.backgroundFile ?? null,
    delegated: message.delegated,
    visualBrief: message.visualBrief,
    motion: message.motion === 'still' || message.motion === 'dynamic' ? message.motion : 'gentle',
  };
}

function toPublication(message: PublicationMessage): PublicationDto {
  const experiment = message.experiment ?? null;
  return {
    status: message.status,
    videoId: message.videoId ?? null,
    privacyStatus: message.privacyStatus,
    publishAt: message.publishAt ?? null,
    madeForKids: message.madeForKids,
    syntheticMedia: message.syntheticMedia,
    uploadedAt: message.uploadedAt ?? null,
    error: message.error ?? null,
    snapshots: message.snapshots.map((snapshot) => ({
      window: snapshot.window,
      takenAt: snapshot.takenAt,
      views: snapshot.views,
      minutesWatched: snapshot.minutesWatched,
      averageViewDurationSeconds: snapshot.averageViewDurationSeconds,
      averageViewPercentage: snapshot.averageViewPercentage,
      subscribersGained: snapshot.subscribersGained,
      subscribersLost: snapshot.subscribersLost,
      likes: snapshot.likes,
      comments: snapshot.comments,
      shares: snapshot.shares,
      impressions: snapshot.impressions ?? null,
      impressionsCtr: snapshot.impressionsCtr ?? null,
      retention: snapshot.retention.map(({ elapsedRatio, audienceWatchRatio }) => ({
        elapsedRatio,
        audienceWatchRatio,
      })),
    })),
    proposals: message.proposals.map((proposal) => ({
      id: proposal.id,
      kind: proposal.kind,
      text: proposal.text,
      reason: proposal.reason,
      status: proposal.status,
      thumbnailFile: proposal.thumbnailFile ?? null,
    })),
    experiment:
      experiment === null
        ? null
        : {
            id: experiment.id,
            kind: experiment.kind,
            status: experiment.status,
            arms: experiment.arms.map((arm) => ({
              id: arm.id,
              label: arm.label,
              value: arm.value,
              startedAt: arm.startedAt ?? null,
              endedAt: arm.endedAt ?? null,
              impressions: arm.impressions,
              clicks: arm.clicks,
              averageViewPercentage: arm.averageViewPercentage,
            })),
            winnerArmId: experiment.winnerArmId ?? null,
            result: experiment.result ?? null,
          },
  };
}

function toPackage(message: PackageMessage): VideoPackageDto {
  return {
    status: message.status,
    videoFile: message.videoFile ?? null,
    shorts: message.shorts.map(({ file, startMs, durationMs, hook }) => ({
      file,
      startMs,
      durationMs,
      hook,
    })),
    thumbnailFile: message.thumbnailFile ?? null,
    checks: message.checks.map(({ name, result, detail }) => ({ name, result, detail })),
    rightsConfirmed: message.rightsConfirmed,
    approvedAt: message.approvedAt ?? null,
    title: message.title,
    description: message.description,
    tags: message.tags,
    durationMs: message.durationMs,
    error: message.error ?? null,
    metadataEdited: message.metadataEdited,
  };
}

function toScene(message: SceneMessage): SceneDto {
  return {
    id: message.id,
    order: message.order,
    narration: message.narration,
    visualPrompt: message.visualPrompt,
    locked: message.locked,
    status: message.status,
    error: message.error ?? null,
    audioSource: message.audioSource,
    visualSource: message.visualSource,
    visualQuery: message.visualQuery ?? '',
    visualCredit: message.visualCredit ?? null,
    audioFile: message.audioFile ?? null,
    visualFile: message.visualFile ?? null,
    durationMs: message.durationMs,
  };
}

function toReadiness(message: ReadinessMessage): ReadinessDto {
  return {
    speechAvailable: message.speechAvailable,
    imagesAvailable: message.imagesAvailable,
    stockPictures: message.stockPictures ?? false,
    provider: message.provider,
    ffmpegAvailable: message.ffmpegAvailable,
    youtubeSignedIn: message.youtubeSignedIn,
    notes: message.notes,
    voices: (message.voices ?? []).map(({ id, label, source, sampleFile }) => ({
      id,
      label,
      source,
      sampleFile: sampleFile ?? null,
    })),
    reportedAt: message.reportedAt ?? null,
  };
}

function toComments(message: CommentsViewMessage): CommentsViewDto {
  return {
    comments: message.comments.map((comment) => ({
      id: comment.id,
      threadId: comment.threadId,
      videoId: comment.videoId,
      videoTitle: comment.videoTitle,
      author: comment.author,
      text: comment.text,
      publishedAt: comment.publishedAt,
      likeCount: comment.likeCount,
      kind: comment.kind,
      theme: comment.theme,
      status: comment.status,
      draft: comment.draft,
      approvedText: comment.approvedText ?? null,
      replyId: comment.replyId ?? null,
      error: comment.error ?? null,
      decidedAt: comment.decidedAt ?? null,
      fetchedAt: comment.fetchedAt,
    })),
    ideas: message.ideas.map((idea) => ({
      id: idea.id,
      text: idea.text,
      commentId: idea.commentId,
      status: idea.status,
      projectId: idea.projectId ?? null,
    })),
    syncedAt: message.syncedAt ?? null,
    syncRequestedAt: message.syncRequestedAt ?? null,
    syncError: message.syncError ?? null,
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
    CHANNEL_OPTIONS,
  );
  const features = new loaded.scout.controlplane.v1.Features(
    target,
    grpc.credentials.createInsecure(),
    CHANNEL_OPTIONS,
  );
  const operator = new loaded.scout.controlplane.v1.Operator(
    target,
    grpc.credentials.createInsecure(),
    CHANNEL_OPTIONS,
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
  const comments = async (method: string, request: object): Promise<CommentsViewDto> =>
    toComments(await operatorCall<CommentsViewMessage>(method, request));
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
    produceProject: (id, narrationVoice, visualBrief, motion) =>
      project('ProduceProject', { id, narrationVoice, visualBrief, motion }),
    delegateProject: (id, narrationVoice, visualBrief, motion) =>
      project('DelegateProject', { id, narrationVoice, visualBrief, motion }),
    async deleteProject(id): Promise<void> {
      await operatorCall<object>('DeleteProject', { id });
    },
    reorderScenes: (projectId, sceneIds) => project('ReorderScenes', { projectId, sceneIds }),
    setSceneLocked: (projectId, sceneId, locked) =>
      project('SetSceneLocked', { projectId, sceneId, locked }),
    regenerateScene: (projectId, sceneId) => project('RegenerateScene', { projectId, sceneId }),
    attachSceneMedia: (projectId, sceneId, kind, fileName) =>
      project('AttachSceneMedia', { projectId, sceneId, kind, fileName }),
    async getMediaDirectory(projectId): Promise<string> {
      return (await operatorCall<{ path: string }>('GetMediaDirectory', { id: projectId })).path;
    },
    async getVoiceSamplesDirectory(): Promise<string> {
      return (await operatorCall<{ path: string }>('GetVoiceSamplesDirectory', {})).path;
    },
    async getReadiness(): Promise<ReadinessDto> {
      return toReadiness(await operatorCall<ReadinessMessage>('GetReadiness', {}));
    },
    assembleProject: (id) => project('AssembleProject', { id }),
    confirmMediaRights: (id) => project('ConfirmMediaRights', { id }),
    editPackageMetadata: (projectId, title, description, tags) =>
      project('EditPackageMetadata', { projectId, title, description, tags }),
    approvePackage: (id) => project('ApprovePackage', { id }),
    publishPackage: (projectId, privacyStatus, publishAt, madeForKids, syntheticMedia) =>
      project('PublishPackage', {
        projectId,
        privacyStatus,
        publishAt: publishAt ?? undefined,
        madeForKids,
        syntheticMedia,
      }),
    decideProposal: (projectId, proposalId, accept) =>
      project('DecideProposal', { projectId, proposalId, accept }),
    startExperiment: (projectId, armHours) => project('StartExperiment', { projectId, armHours }),
    adoptExperimentWinner: (id) => project('AdoptExperimentWinner', { id }),
    listComments: () => comments('ListComments', {}),
    requestCommentSync: () => comments('RequestCommentSync', {}),
    decideComment: (commentId, decision) => comments('DecideComment', { commentId, decision }),
    approveReply: (commentId, text) => comments('ApproveReply', { commentId, text }),
    decideIdea: (ideaId, use) => comments('DecideIdea', { ideaId, use }),
    close(): void {
      closed = true;
      providers.close();
      features.close();
      operator.close();
    },
  };
}
