import type { IpcMain } from 'electron';
import { unlink } from 'node:fs/promises';
import path from 'node:path';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ClaimDecisionRequest,
  type ClaimStatus,
  type ExperimentStartRequest,
  type MandateDto,
  type MandateResponse,
  type MandateSaveRequest,
  type OperatorProjectDto,
  type OperatorProjectRequest,
  type OperatorProjectsRequest,
  type OperatorProjectsResponse,
  type PackageMetadataRequest,
  type PrivacyStatus,
  type ProposalDecisionRequest,
  type PublishPackageRequest,
  type ReadinessDto,
  type SceneLockRequest,
  type SceneMediaKind,
  type SceneMediaRequest,
  type SceneMediaResponse,
  type SceneOrderRequest,
  type SceneRequest,
  type StartProjectRequest,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import { copyOwnerMedia, isSafeId, type ChooseOwnerFile } from '../operator/media.js';
import type { Logger } from '../util/logger.js';

export type OperatorClient = Pick<
  ControlPlaneClient,
  | 'getMandate'
  | 'saveMandate'
  | 'listProjects'
  | 'getProject'
  | 'startProject'
  | 'decideClaim'
  | 'produceProject'
  | 'reorderScenes'
  | 'setSceneLocked'
  | 'regenerateScene'
  | 'attachSceneMedia'
  | 'getMediaDirectory'
  | 'getReadiness'
  | 'assembleProject'
  | 'confirmMediaRights'
  | 'editPackageMetadata'
  | 'approvePackage'
  | 'publishPackage'
  | 'decideProposal'
  | 'startExperiment'
  | 'adoptExperimentWinner'
>;

const DEFAULT_PROJECTS_LIMIT = 50;
const MAX_PROJECTS_LIMIT = 200;
const MAX_PILLARS = 5;
const MAX_PILLAR_LENGTH = 80;
const MIN_TOPIC_LENGTH = 3;
const MAX_TOPIC_LENGTH = 200;
const CLAIM_STATUSES: readonly ClaimStatus[] = ['supported', 'unsupported', 'waived'];
const MEDIA_KINDS: readonly SceneMediaKind[] = ['audio', 'visual'];
const MAX_TITLE_LENGTH = 100;
const MAX_DESCRIPTION_LENGTH = 5000;
const MAX_TAG_LENGTH = 100;
const MAX_TAGS_LENGTH = 500;
const PRIVACY_STATUSES: readonly PrivacyStatus[] = ['public', 'private', 'unlisted'];
const ISO_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,3})?)?Z$/;
const MIN_ARM_HOURS = 24;
const MAX_ARM_HOURS = 168;

function throwSerialized(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function invalid(message: string): never {
  throw new ScoutError('operator/invalid-request', message);
}

function text(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

function list(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.map(text).filter((item) => item.length > 0);
}

function requiredId(value: unknown, what: string): string {
  const id = text(value);
  if (id.length === 0) invalid(`${what} is required.`);
  return id;
}

function sceneOrder(value: unknown): string[] {
  const ids =
    Array.isArray(value) && value.every((id) => typeof id === 'string') ? value.map(text) : [];
  if (ids.length === 0 || ids.includes('') || new Set(ids).size !== ids.length) {
    invalid('The new order lists every scene id once.');
  }
  return ids;
}

function validMandate(req: MandateSaveRequest | undefined): MandateSaveRequest {
  const mandate: MandateSaveRequest = {
    objective: text(req?.objective),
    audience: text(req?.audience),
    pillars: list(req?.pillars),
    cadence: text(req?.cadence),
    language: text(req?.language),
    voiceAndStyle: text(req?.voiceAndStyle),
    blockedTopics: list(req?.blockedTopics),
    targetMetric: text(req?.targetMetric),
    targetValue: text(req?.targetValue),
  };
  if (mandate.objective.length === 0) invalid('The objective is required.');
  if (mandate.language.length === 0) invalid('The language is required.');
  if (mandate.pillars.length > MAX_PILLARS) invalid(`At most ${MAX_PILLARS} pillars.`);
  if (mandate.pillars.some((pillar) => pillar.length > MAX_PILLAR_LENGTH)) {
    invalid(`Each pillar is at most ${MAX_PILLAR_LENGTH} characters.`);
  }
  return mandate;
}

/** How YouTube counts tags: their characters, two more for a tag with a space, and a comma between tags. */
function youTubeTagsLength(tags: readonly string[]): number {
  return (
    tags.reduce((total, tag) => total + tag.length + (tag.includes(' ') ? 2 : 0), 0) +
    Math.max(0, tags.length - 1)
  );
}

function validMetadata(req: PackageMetadataRequest | undefined): PackageMetadataRequest {
  const metadata: PackageMetadataRequest = {
    projectId: requiredId(req?.projectId, 'The project id'),
    title: text(req?.title),
    description: text(req?.description),
    tags: list(req?.tags),
  };
  const errors: string[] = [];
  if (metadata.title.length === 0 || metadata.title.length > MAX_TITLE_LENGTH) {
    errors.push(`The title is 1 to ${MAX_TITLE_LENGTH} characters.`);
  }
  if (metadata.description.length > MAX_DESCRIPTION_LENGTH) {
    errors.push(`The description is at most ${MAX_DESCRIPTION_LENGTH} characters.`);
  }
  if (metadata.tags.some((tag) => tag.length > MAX_TAG_LENGTH)) {
    errors.push(`Each tag is 1 to ${MAX_TAG_LENGTH} characters.`);
  }
  if (youTubeTagsLength(metadata.tags) > MAX_TAGS_LENGTH) {
    errors.push(`The tags are at most ${MAX_TAGS_LENGTH} characters together.`);
  }
  if (errors.length > 0) invalid(errors.join(' '));
  return metadata;
}

function validPublication(req: PublishPackageRequest | undefined): PublishPackageRequest {
  const projectId = requiredId(req?.projectId, 'The project id');
  const privacyStatus = req?.privacyStatus as PrivacyStatus;
  if (!PRIVACY_STATUSES.includes(privacyStatus)) invalid('Choose public, private or unlisted.');
  if (typeof req?.madeForKids !== 'boolean') invalid('Say whether the video is made for kids.');
  if (typeof req.syntheticMedia !== 'boolean') {
    invalid('Say whether the video contains realistic synthetic or altered media.');
  }
  const publishAt = req.publishAt ?? null;
  if (publishAt !== null) {
    if (privacyStatus !== 'private') {
      invalid('A scheduled time needs private; YouTube makes the video public at that time.');
    }
    if (
      typeof publishAt !== 'string' ||
      !ISO_UTC.test(publishAt) ||
      !Number.isFinite(Date.parse(publishAt))
    ) {
      invalid('The scheduled time is not an ISO UTC time.');
    }
    if (Date.parse(publishAt) <= Date.now()) invalid('The scheduled time must be in the future.');
  }
  return {
    projectId,
    privacyStatus,
    publishAt,
    madeForKids: req.madeForKids,
    syntheticMedia: req.syntheticMedia,
  };
}

/** Registers the Production and Assembly pages' channels, which reach the control plane's Operator service only. */
export function registerOperatorHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<OperatorClient>,
  logger: Logger,
  chooseFile: ChooseOwnerFile,
): () => void {
  ipcMain.handle(CHANNEL.operatorMandate, async (): Promise<MandateResponse> => {
    try {
      return { mandate: await (await getClient()).getMandate() };
    } catch (err: unknown) {
      logger.warn('operator: mandate failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  ipcMain.handle(
    CHANNEL.operatorSaveMandate,
    async (_event, req: MandateSaveRequest | undefined): Promise<MandateDto> => {
      try {
        return await (await getClient()).saveMandate(validMandate(req));
      } catch (err: unknown) {
        logger.warn('operator: saveMandate failed', { err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorProjects,
    async (_event, req: OperatorProjectsRequest | undefined): Promise<OperatorProjectsResponse> => {
      try {
        const asked = req?.limit;
        const limit =
          typeof asked === 'number' && Number.isInteger(asked) && asked > 0
            ? Math.min(asked, MAX_PROJECTS_LIMIT)
            : DEFAULT_PROJECTS_LIMIT;
        return { projects: await (await getClient()).listProjects(limit) };
      } catch (err: unknown) {
        logger.warn('operator: projects failed', { err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorProject,
    async (_event, req: OperatorProjectRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        return await (await getClient()).getProject(requiredId(req?.id, 'The project id'));
      } catch (err: unknown) {
        logger.warn('operator: project failed', { id: req?.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorStartProject,
    async (_event, req: StartProjectRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const topic = text(req?.topic);
        if (topic.length < MIN_TOPIC_LENGTH || topic.length > MAX_TOPIC_LENGTH) {
          invalid(`The topic must be ${MIN_TOPIC_LENGTH} to ${MAX_TOPIC_LENGTH} characters.`);
        }
        return await (await getClient()).startProject(topic);
      } catch (err: unknown) {
        logger.warn('operator: startProject failed', { err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorDecideClaim,
    async (_event, req: ClaimDecisionRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        const claimId = requiredId(req?.claimId, 'The claim id');
        const status = req?.status;
        if (!CLAIM_STATUSES.includes(status as ClaimStatus)) {
          invalid('A claim is decided as supported, unsupported or waived.');
        }
        const note = text(req?.note);
        if (status === 'waived' && note.length === 0)
          invalid('A waived claim needs a note saying why.');
        return await (
          await getClient()
        ).decideClaim(
          projectId,
          claimId,
          status as ClaimStatus,
          note.length > 0 ? note : undefined,
        );
      } catch (err: unknown) {
        logger.warn('operator: decideClaim failed', {
          projectId: req?.projectId,
          claimId: req?.claimId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorProduction,
    async (_event, req: OperatorProjectRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        return await (await getClient()).produceProject(requiredId(req?.id, 'The project id'));
      } catch (err: unknown) {
        logger.warn('operator: produce failed', { id: req?.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorSceneOrder,
    async (_event, req: SceneOrderRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        return await (await getClient()).reorderScenes(projectId, sceneOrder(req?.sceneIds));
      } catch (err: unknown) {
        logger.warn('operator: scene order failed', {
          projectId: req?.projectId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorSceneLock,
    async (_event, req: SceneLockRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        const sceneId = requiredId(req?.sceneId, 'The scene id');
        if (typeof req?.locked !== 'boolean') invalid('A scene is locked or unlocked.');
        return await (await getClient()).setSceneLocked(projectId, sceneId, req.locked);
      } catch (err: unknown) {
        logger.warn('operator: scene lock failed', {
          projectId: req?.projectId,
          sceneId: req?.sceneId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorSceneRegeneration,
    async (_event, req: SceneRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        const sceneId = requiredId(req?.sceneId, 'The scene id');
        return await (await getClient()).regenerateScene(projectId, sceneId);
      } catch (err: unknown) {
        logger.warn('operator: scene regeneration failed', {
          projectId: req?.projectId,
          sceneId: req?.sceneId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorSceneMedia,
    async (_event, req: SceneMediaRequest | undefined): Promise<SceneMediaResponse> => {
      try {
        const { projectId, sceneId, kind } = req ?? {};
        if (!isSafeId(projectId) || !isSafeId(sceneId)) {
          invalid('The project and scene ids are letters, digits, - and _ only.');
        }
        if (!MEDIA_KINDS.includes(kind as SceneMediaKind)) {
          invalid('Attach audio or a visual.');
        }
        const client = await getClient();
        const directory = await client.getMediaDirectory(projectId);
        const source = await chooseFile(kind as SceneMediaKind);
        if (source === null) return { project: null };
        const fileName = await copyOwnerMedia(directory, sceneId, kind as SceneMediaKind, source);
        try {
          return {
            project: await client.attachSceneMedia(
              projectId,
              sceneId,
              kind as SceneMediaKind,
              fileName,
            ),
          };
        } catch (err: unknown) {
          await unlink(path.join(directory, fileName)).catch(() => undefined);
          throw err;
        }
      } catch (err: unknown) {
        logger.warn('operator: scene media failed', {
          projectId: req?.projectId,
          sceneId: req?.sceneId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(CHANNEL.operatorReadiness, async (): Promise<ReadinessDto> => {
    try {
      return await (await getClient()).getReadiness();
    } catch (err: unknown) {
      logger.warn('operator: readiness failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  const packageStep = (
    channel: string,
    name: string,
    call: (client: OperatorClient, id: string) => Promise<OperatorProjectDto>,
  ): void => {
    ipcMain.handle(
      channel,
      async (_event, req: OperatorProjectRequest | undefined): Promise<OperatorProjectDto> => {
        try {
          return await call(await getClient(), requiredId(req?.id, 'The project id'));
        } catch (err: unknown) {
          logger.warn(`operator: ${name} failed`, { id: req?.id, err: String(err) });
          throwSerialized(err);
        }
      },
    );
  };
  packageStep(CHANNEL.operatorAssembly, 'assembly', (client, id) => client.assembleProject(id));
  packageStep(CHANNEL.operatorMediaRights, 'media rights', (client, id) =>
    client.confirmMediaRights(id),
  );
  packageStep(CHANNEL.operatorPackageApproval, 'package approval', (client, id) =>
    client.approvePackage(id),
  );
  packageStep(CHANNEL.operatorExperimentAdoption, 'experiment adoption', (client, id) =>
    client.adoptExperimentWinner(id),
  );

  ipcMain.handle(
    CHANNEL.operatorPublication,
    async (_event, req: PublishPackageRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const { projectId, privacyStatus, publishAt, madeForKids, syntheticMedia } =
          validPublication(req);
        return await (
          await getClient()
        ).publishPackage(projectId, privacyStatus, publishAt, madeForKids, syntheticMedia);
      } catch (err: unknown) {
        logger.warn('operator: publication failed', {
          projectId: req?.projectId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorProposalDecision,
    async (_event, req: ProposalDecisionRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        const proposalId = requiredId(req?.proposalId, 'The proposal id');
        if (typeof req?.accept !== 'boolean') invalid('A proposal is accepted or rejected.');
        return await (await getClient()).decideProposal(projectId, proposalId, req.accept);
      } catch (err: unknown) {
        logger.warn('operator: proposal decision failed', {
          projectId: req?.projectId,
          proposalId: req?.proposalId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorExperiment,
    async (_event, req: ExperimentStartRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const projectId = requiredId(req?.projectId, 'The project id');
        const armHours = req?.armHours;
        if (
          typeof armHours !== 'number' ||
          !Number.isInteger(armHours) ||
          armHours < MIN_ARM_HOURS ||
          armHours > MAX_ARM_HOURS
        ) {
          invalid(`Each arm runs ${MIN_ARM_HOURS} to ${MAX_ARM_HOURS} hours.`);
        }
        return await (await getClient()).startExperiment(projectId, armHours);
      } catch (err: unknown) {
        logger.warn('operator: experiment failed', {
          projectId: req?.projectId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.operatorPackageMetadata,
    async (_event, req: PackageMetadataRequest | undefined): Promise<OperatorProjectDto> => {
      try {
        const { projectId, title, description, tags } = validMetadata(req);
        return await (await getClient()).editPackageMetadata(projectId, title, description, tags);
      } catch (err: unknown) {
        logger.warn('operator: package metadata failed', {
          projectId: req?.projectId,
          err: String(err),
        });
        throwSerialized(err);
      }
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.operatorMandate);
    ipcMain.removeHandler(CHANNEL.operatorSaveMandate);
    ipcMain.removeHandler(CHANNEL.operatorProjects);
    ipcMain.removeHandler(CHANNEL.operatorProject);
    ipcMain.removeHandler(CHANNEL.operatorStartProject);
    ipcMain.removeHandler(CHANNEL.operatorDecideClaim);
    ipcMain.removeHandler(CHANNEL.operatorProduction);
    ipcMain.removeHandler(CHANNEL.operatorSceneOrder);
    ipcMain.removeHandler(CHANNEL.operatorSceneLock);
    ipcMain.removeHandler(CHANNEL.operatorSceneRegeneration);
    ipcMain.removeHandler(CHANNEL.operatorSceneMedia);
    ipcMain.removeHandler(CHANNEL.operatorReadiness);
    ipcMain.removeHandler(CHANNEL.operatorAssembly);
    ipcMain.removeHandler(CHANNEL.operatorMediaRights);
    ipcMain.removeHandler(CHANNEL.operatorPackageMetadata);
    ipcMain.removeHandler(CHANNEL.operatorPackageApproval);
    ipcMain.removeHandler(CHANNEL.operatorPublication);
    ipcMain.removeHandler(CHANNEL.operatorProposalDecision);
    ipcMain.removeHandler(CHANNEL.operatorExperiment);
    ipcMain.removeHandler(CHANNEL.operatorExperimentAdoption);
  };
}
