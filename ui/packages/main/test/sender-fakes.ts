import type { IpcMainInvokeEvent } from 'electron';

import type { ScoutWindow, SenderHooks, SenderKind } from '../src/ipc/sender-check.js';
import { appPageCheck } from '../src/quick-ask/quick-ask.js';

const INDEX_HTML =
  '/opt/Scout Desktop/resources/app.asar/packages/renderer/dist/browser/index.html';
const APP_PAGE = `file://${INDEX_HTML.replaceAll(' ', '%20')}`;

interface FakeFrame {
  readonly frameTreeNodeId: number;
  readonly url: string;
}

interface FakeContents {
  readonly mainFrame: FakeFrame;
}

function fakeContents(id: number, route: string): FakeContents {
  return { mainFrame: { frameTreeNodeId: id, url: `${APP_PAGE}#/${route}` } };
}

/** One fake webContents per Scout window, each on Scout's own page. */
export const WINDOWS = {
  main: fakeContents(1, 'chat'),
  'quick-ask': fakeContents(2, 'quick-ask'),
  'floating-button': fakeContents(3, 'floating-button'),
  operator: fakeContents(4, 'operator'),
} as const satisfies Record<Exclude<ScoutWindow, 'unknown'>, FakeContents>;

/** A call from the window's main frame on Scout's own page. */
export function fromMainFrame(sender: FakeContents): IpcMainInvokeEvent {
  return { sender, senderFrame: sender.mainFrame } as unknown as IpcMainInvokeEvent;
}

function fromSubFrame(sender: FakeContents): IpcMainInvokeEvent {
  const frame = { frameTreeNodeId: sender.mainFrame.frameTreeNodeId + 100, url: APP_PAGE };
  return { sender, senderFrame: frame } as unknown as IpcMainInvokeEvent;
}

function fromOtherPage(sender: FakeContents): IpcMainInvokeEvent {
  const frame = { frameTreeNodeId: sender.mainFrame.frameTreeNodeId, url: 'https://example.com/' };
  return { sender, senderFrame: frame } as unknown as IpcMainInvokeEvent;
}

export const MAIN_EVENT = fromMainFrame(WINDOWS.main);
export const OPERATOR_EVENT = fromMainFrame(WINDOWS.operator);

/** Main's window identities over the fake windows, with the real check of Scout's own page. */
export const SENDER_HOOKS: SenderHooks = {
  windowOf: (sender) => {
    const found = Object.entries(WINDOWS).find(([, contents]) => contents === sender);
    return found === undefined ? 'unknown' : (found[0] as ScoutWindow);
  },
  isAppPage: appPageCheck({ indexHtmlPath: INDEX_HTML }),
};

/** Every sender a guarded channel is tried from: a label, the event, and the kind main should read. */
export const SENDERS: readonly (readonly [string, IpcMainInvokeEvent, SenderKind])[] = [
  ['the main window', fromMainFrame(WINDOWS.main), 'main'],
  ['the quick-ask window', fromMainFrame(WINDOWS['quick-ask']), 'quick-ask'],
  ['the floating button', fromMainFrame(WINDOWS['floating-button']), 'floating-button'],
  ['the operator window', fromMainFrame(WINDOWS.operator), 'operator'],
  ["a renderer that is none of Scout's windows", fromMainFrame(fakeContents(9, 'chat')), 'unknown'],
  ['a sub-frame of the main window', fromSubFrame(WINDOWS.main), 'sub-frame'],
  ['a sub-frame of the quick-ask window', fromSubFrame(WINDOWS['quick-ask']), 'sub-frame'],
  ['a sub-frame of the operator window', fromSubFrame(WINDOWS.operator), 'sub-frame'],
  ['the main window when it shows another page', fromOtherPage(WINDOWS.main), 'other-page'],
  [
    'a main window event without its frame',
    { sender: WINDOWS.main } as unknown as IpcMainInvokeEvent,
    'sub-frame',
  ],
];
