import type { IpcMain } from 'electron';
import { CHANNEL, ScoutError, encodeIpcErrorMessage, serializeError } from '@scout/shared';
import type { Logger } from '../util/logger.js';

/** Scout's windows, as main knows them; `unknown` is any webContents main did not create as one of them. */
export type ScoutWindow = 'main' | 'quick-ask' | 'floating-button' | 'operator' | 'unknown';

/** Who sent an IPC call: one of Scout's windows from its own page, or a frame that is not a window's own page. */
export type SenderKind = ScoutWindow | 'sub-frame' | 'other-page';

/** What main knows about its windows, so a handler can tell who sent a call. */
export interface SenderHooks {
  /** Which of Scout's windows `sender`, the webContents an IPC call came from, belongs to. */
  readonly windowOf: (sender: unknown) => ScoutWindow;
  /** True when `url` is Scout's own page, the file the windows load or the dev server. */
  readonly isAppPage: (url: string) => boolean;
}

interface SenderFrame {
  readonly frameTreeNodeId: number;
  readonly url: string;
}

interface SenderEvent {
  readonly sender?: { readonly mainFrame?: SenderFrame } | null;
  readonly senderFrame?: SenderFrame | null;
}

/** Refuses an IPC call unless `allowed` names its sender, with the typed error `refusal` makes. */
export type RefuseUnlessFrom = (
  allowed: readonly SenderKind[],
  event: unknown,
  channel: string,
  refusal: () => ScoutError,
) => void;

/**
 * Classifies the sender of an IPC call: one of Scout's windows only when the call came from that window's main frame
 * on Scout's own page. A refusal is logged at warn with the channel and the sender kind, never the request, and
 * thrown in the shape the renderer reads.
 */
export function senderCheck(hooks: SenderHooks, logger: Logger): RefuseUnlessFrom {
  const senderKind = (event: unknown): SenderKind => {
    const { sender, senderFrame } = (event ?? {}) as SenderEvent;
    const window = hooks.windowOf(sender);
    if (window === 'unknown') return window;
    const mainFrame = sender?.mainFrame;
    if (senderFrame == null || mainFrame == null) return 'sub-frame';
    if (senderFrame.frameTreeNodeId !== mainFrame.frameTreeNodeId) return 'sub-frame';
    return hooks.isAppPage(senderFrame.url) ? window : 'other-page';
  };
  return (allowed, event, channel, refusal) => {
    const sender = senderKind(event);
    if (allowed.includes(sender)) return;
    logger.warn('ipc: call refused for its sender', { channel, sender });
    const serialized = serializeError(refusal());
    throw Object.assign(new Error(encodeIpcErrorMessage(serialized)), { code: serialized.code });
  };
}

/** Who may call one guarded channel, and the error everyone else gets. */
export interface ChannelGuard {
  readonly allowed: readonly SenderKind[];
  readonly refusal: () => ScoutError;
}

type IpcHandlers = Pick<IpcMain, 'handle' | 'removeHandler'>;

/** An `ipcMain` whose handlers for the channels in `guards` first refuse any sender the guard does not allow. */
export function guardChannels(
  ipcMain: IpcHandlers,
  refuseUnlessFrom: RefuseUnlessFrom,
  guards: Readonly<Record<string, ChannelGuard>>,
): IpcHandlers {
  return {
    removeHandler: (channel) => ipcMain.removeHandler(channel),
    handle: (channel, handler) => {
      const guard = guards[channel];
      if (guard === undefined) {
        ipcMain.handle(channel, handler);
        return;
      }
      ipcMain.handle(channel, async (event, ...args: unknown[]) => {
        refuseUnlessFrom(guard.allowed, event, channel, guard.refusal);
        return handler(event, ...args);
      });
    },
  };
}

/**
 * The channels outside `scout:features.*` that store or delete a credential, or grant or revoke access: only the main
 * window's wizard, Settings, AI Sources and Features screens call them.
 */
export const CREDENTIAL_CHANNELS: readonly string[] = [
  CHANNEL.vaultStore,
  CHANNEL.vaultDelete,
  CHANNEL.githubSaveToken,
  CHANNEL.githubSignIn,
  CHANNEL.githubSignOut,
  CHANNEL.githubBotCreate,
  CHANNEL.githubBotRemove,
  CHANNEL.githubBotSetWrites,
  CHANNEL.youtubeSaveApiKey,
  CHANNEL.youtubeClearApiKey,
  CHANNEL.youtubeSignIn,
  CHANNEL.youtubeSignOut,
  CHANNEL.discordCheckToken,
  CHANNEL.slackSave,
  CHANNEL.slackDisconnect,
  CHANNEL.usageAdminKeySave,
  CHANNEL.usageAdminKeyRemove,
  CHANNEL.chatgptSignIn,
  CHANNEL.chatgptSignOut,
  CHANNEL.kubernetesUse,
  CHANNEL.kubernetesDisconnect,
  CHANNEL.accessSet,
  CHANNEL.accessStartPairing,
];

const MAIN_WINDOW_ONLY: ChannelGuard = {
  allowed: ['main'],
  refusal: () =>
    new ScoutError(
      'ipc/not-main-window',
      'Credentials and access can be changed only in the Scout window.',
    ),
};

/** The guard for every channel in `CREDENTIAL_CHANNELS`: the main window only. */
export const CREDENTIAL_GUARDS: Readonly<Record<string, ChannelGuard>> = Object.fromEntries(
  CREDENTIAL_CHANNELS.map((channel) => [channel, MAIN_WINDOW_ONLY]),
);

/** Letting the quick-ask shortcut go while one is recorded: the main window's Settings only. */
export const SHORTCUT_GUARDS: Readonly<Record<string, ChannelGuard>> = {
  [CHANNEL.localSuspendShortcut]: {
    allowed: ['main'],
    refusal: () =>
      new ScoutError(
        'ipc/not-main-window',
        'The quick-ask shortcut can be recorded only in the Scout window.',
      ),
  },
};

/** Bringing the Scout window forward: only the operator window's Back to Scout asks. */
export const SHOW_MAIN_GUARDS: Readonly<Record<string, ChannelGuard>> = {
  [CHANNEL.appShowMainWindow]: {
    allowed: ['operator'],
    refusal: () =>
      new ScoutError(
        'operator/not-operator-window',
        'Only the operator window brings the Scout window forward.',
      ),
  },
};

/** Quick ask's guarded channels: only the floating button asks for its card. */
export const QUICK_ASK_GUARDS: Readonly<Record<string, ChannelGuard>> = {
  [CHANNEL.localBubbleCard]: {
    allowed: ['floating-button'],
    refusal: () =>
      new ScoutError('ipc/not-floating-button', 'Only the floating button shows its card.'),
  },
};
