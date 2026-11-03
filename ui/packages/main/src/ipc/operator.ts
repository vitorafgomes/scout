import type { IpcMain } from 'electron';
import {
  CHANNEL,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ClaimDecisionRequest,
  type ClaimStatus,
  type MandateDto,
  type MandateResponse,
  type MandateSaveRequest,
  type OperatorProjectDto,
  type OperatorProjectRequest,
  type OperatorProjectsRequest,
  type OperatorProjectsResponse,
  type StartProjectRequest,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { Logger } from '../util/logger.js';

export type OperatorClient = Pick<
  ControlPlaneClient,
  'getMandate' | 'saveMandate' | 'listProjects' | 'getProject' | 'startProject' | 'decideClaim'
>;

const DEFAULT_PROJECTS_LIMIT = 50;
const MAX_PROJECTS_LIMIT = 200;
const MAX_PILLARS = 5;
const MAX_PILLAR_LENGTH = 80;
const MIN_TOPIC_LENGTH = 3;
const MAX_TOPIC_LENGTH = 200;
const CLAIM_STATUSES: readonly ClaimStatus[] = ['supported', 'unsupported', 'waived'];

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

/** Registers the Production screen's channels, which reach the control plane's Operator service only. */
export function registerOperatorHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<OperatorClient>,
  logger: Logger,
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

  return () => {
    ipcMain.removeHandler(CHANNEL.operatorMandate);
    ipcMain.removeHandler(CHANNEL.operatorSaveMandate);
    ipcMain.removeHandler(CHANNEL.operatorProjects);
    ipcMain.removeHandler(CHANNEL.operatorProject);
    ipcMain.removeHandler(CHANNEL.operatorStartProject);
    ipcMain.removeHandler(CHANNEL.operatorDecideClaim);
  };
}
