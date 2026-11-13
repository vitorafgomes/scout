import type { IpcMain } from 'electron';
import {
  CHANNEL,
  FEATURE_IDS,
  ScoutError,
  encodeIpcErrorMessage,
  serializeError,
  type ActionDecisionRequest,
  type FeatureDto,
  type FeatureEnableRequest,
  type FeatureWriteGrantRequest,
  type FeatureId,
  type FeatureIdRequest,
  type FeatureSetCredentialsRequest,
  type FeatureSetupRequest,
  type FeaturesListResponse,
  type PendingActionDto,
  type PendingActionsResponse,
  type WritesListRequest,
  type WritesListResponse,
} from '@scout/shared';
import type { ControlPlaneClient } from '../control-plane/client.js';
import type { Logger } from '../util/logger.js';
import { senderCheck, type SenderHooks, type SenderKind } from './sender-check.js';

export type FeaturesClient = Pick<
  ControlPlaneClient,
  | 'listFeatures'
  | 'setFeatureCredentials'
  | 'setupFeature'
  | 'deleteFeatureCredentials'
  | 'enableFeature'
  | 'disableFeature'
  | 'setWriteGrant'
  | 'listPendingActions'
  | 'approveAction'
  | 'rejectAction'
  | 'listWrites'
>;

/** What main does when a write grant or the approval queue changes. */
export interface FeaturesHooks extends SenderHooks {
  readonly onWriteGrantChanged?: () => void;
  readonly onActionsChanged?: () => void;
  readonly prepareWriteGrant?: (id: FeatureId) => Promise<void>;
  readonly afterWriteRevoke?: (id: FeatureId) => Promise<void>;
}

const GRANT_WINDOWS: readonly SenderKind[] = ['main'];
const DECIDE_WINDOWS: readonly SenderKind[] = ['main', 'quick-ask', 'operator'];

const CREDENTIALS_REFUSED = 'Credentials can be entered only in the Scout window.';
const DEFAULT_WRITES_LIMIT = 50;
const MAX_WRITES_LIMIT = 500;

function throwSerialized(err: unknown): never {
  const serialized = serializeError(err);
  throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
}

function assertFeatureId(id: string): asserts id is FeatureId {
  if (!FEATURE_IDS.includes(id as FeatureId)) {
    throw new ScoutError('features/invalid-id', `unknown feature "${id}"`);
  }
}

export function registerFeaturesHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  getClient: () => Promise<FeaturesClient>,
  logger: Logger,
  hooks: FeaturesHooks,
): () => void {
  const refuseUnlessFrom = senderCheck(hooks, logger);
  const onlyMainWindow = (
    event: unknown,
    channel: string,
    message = 'Writes can be allowed only from the Scout window.',
  ): void =>
    refuseUnlessFrom(
      GRANT_WINDOWS,
      event,
      channel,
      () => new ScoutError('features/not-main-window', message),
    );
  const undoPreparedGrant = async (id: FeatureId): Promise<void> => {
    await hooks.afterWriteRevoke?.(id).catch((err: unknown) => {
      logger.warn('features: undoing the grant check failed', {
        id,
        code: serializeError(err).code,
      });
    });
  };
  const grantChangedBy = (feature: FeatureDto): FeatureDto => {
    if (feature.writeClass !== 'none') hooks.onWriteGrantChanged?.();
    return feature;
  };
  ipcMain.handle(CHANNEL.featuresList, async (): Promise<FeaturesListResponse> => {
    try {
      const features = await (await getClient()).listFeatures();
      return { features };
    } catch (err: unknown) {
      logger.warn('features: list failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  ipcMain.handle(
    CHANNEL.featureSetCredentials,
    async (event, req: FeatureSetCredentialsRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureSetCredentials, CREDENTIALS_REFUSED);
      try {
        assertFeatureId(req.id);
        return await (await getClient()).setFeatureCredentials(req.id, req.body);
      } catch (err: unknown) {
        logger.warn('features: setCredentials failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureSetup,
    async (event, req: FeatureSetupRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureSetup, CREDENTIALS_REFUSED);
      try {
        assertFeatureId(req.id);
        return await (await getClient()).setupFeature(req.id, req.credentials, req.settings);
      } catch (err: unknown) {
        logger.warn('features: setup failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureDeleteCredentials,
    async (event, req: FeatureIdRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureDeleteCredentials);
      try {
        assertFeatureId(req.id);
        return grantChangedBy(await (await getClient()).deleteFeatureCredentials(req.id));
      } catch (err: unknown) {
        logger.warn('features: deleteCredentials failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureEnable,
    async (event, req: FeatureEnableRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureEnable);
      let confirmWrites = false;
      try {
        assertFeatureId(req.id);
        if (req.confirmWrites === true) await hooks.prepareWriteGrant?.(req.id);
        confirmWrites = req.confirmWrites === true;
        return grantChangedBy(await (await getClient()).enableFeature(req.id, confirmWrites));
      } catch (err: unknown) {
        logger.warn('features: enable failed', { id: req.id, err: String(err) });
        if (confirmWrites) {
          await undoPreparedGrant(req.id);
          hooks.onWriteGrantChanged?.();
        }
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureDisable,
    async (event, req: FeatureIdRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureDisable);
      try {
        assertFeatureId(req.id);
        return grantChangedBy(await (await getClient()).disableFeature(req.id));
      } catch (err: unknown) {
        logger.warn('features: disable failed', { id: req.id, err: String(err) });
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(
    CHANNEL.featureWriteGrant,
    async (event, req: FeatureWriteGrantRequest): Promise<FeatureDto> => {
      onlyMainWindow(event, CHANNEL.featureWriteGrant);
      let prepared = false;
      try {
        assertFeatureId(req.id);
        const granted = req.granted === true;
        if (granted) {
          await hooks.prepareWriteGrant?.(req.id);
          prepared = true;
        }
        const updated = await (
          await getClient()
        ).setWriteGrant(req.id, granted, req.confirmWrites === true);
        if (!granted) {
          await hooks.afterWriteRevoke?.(req.id).catch((err: unknown) => {
            logger.warn('features: revoke step failed', {
              id: req.id,
              code: serializeError(err).code,
            });
          });
        }
        hooks.onWriteGrantChanged?.();
        return updated;
      } catch (err: unknown) {
        logger.warn('features: writeGrant failed', { id: req.id, err: String(err) });
        if (prepared) await undoPreparedGrant(req.id);
        throwSerialized(err);
      }
    },
  );

  ipcMain.handle(CHANNEL.actionsList, async (): Promise<PendingActionsResponse> => {
    try {
      return { actions: await (await getClient()).listPendingActions() };
    } catch (err: unknown) {
      logger.warn('actions: list failed', { err: String(err) });
      throwSerialized(err);
    }
  });

  const decide =
    (decision: 'approve' | 'reject', channel: string) =>
    async (event: unknown, req: ActionDecisionRequest): Promise<PendingActionDto> => {
      refuseUnlessFrom(
        DECIDE_WINDOWS,
        event,
        channel,
        () =>
          new ScoutError(
            'actions/not-approval-window',
            'Actions can be approved or rejected only from the Scout, quick-ask or operator window.',
          ),
      );
      try {
        const id = req?.id;
        if (typeof id !== 'string' || id.length === 0) {
          throw new ScoutError('actions/invalid-id', 'an action decision needs the action id');
        }
        const reason =
          typeof req.reason === 'string' && req.reason.trim().length > 0
            ? req.reason.trim()
            : undefined;
        const client = await getClient();
        const decided =
          decision === 'approve'
            ? await client.approveAction(id, reason)
            : await client.rejectAction(id, reason);
        hooks.onActionsChanged?.();
        return decided;
      } catch (err: unknown) {
        logger.warn(`actions: ${decision} failed`, { id: req?.id, err: String(err) });
        throwSerialized(err);
      }
    };
  ipcMain.handle(CHANNEL.actionsApprove, decide('approve', CHANNEL.actionsApprove));
  ipcMain.handle(CHANNEL.actionsReject, decide('reject', CHANNEL.actionsReject));

  ipcMain.handle(
    CHANNEL.writesList,
    async (_event, req: WritesListRequest | undefined): Promise<WritesListResponse> => {
      try {
        const asked = req?.limit;
        const limit =
          typeof asked === 'number' && Number.isInteger(asked) && asked > 0
            ? Math.min(asked, MAX_WRITES_LIMIT)
            : DEFAULT_WRITES_LIMIT;
        return { entries: await (await getClient()).listWrites(limit) };
      } catch (err: unknown) {
        logger.warn('writes: list failed', { err: String(err) });
        throwSerialized(err);
      }
    },
  );

  return () => {
    ipcMain.removeHandler(CHANNEL.featureWriteGrant);
    ipcMain.removeHandler(CHANNEL.actionsList);
    ipcMain.removeHandler(CHANNEL.actionsApprove);
    ipcMain.removeHandler(CHANNEL.actionsReject);
    ipcMain.removeHandler(CHANNEL.writesList);
    ipcMain.removeHandler(CHANNEL.featuresList);
    ipcMain.removeHandler(CHANNEL.featureSetCredentials);
    ipcMain.removeHandler(CHANNEL.featureSetup);
    ipcMain.removeHandler(CHANNEL.featureDeleteCredentials);
    ipcMain.removeHandler(CHANNEL.featureEnable);
    ipcMain.removeHandler(CHANNEL.featureDisable);
    logger.info('features: IPC handlers unregistered');
  };
}
