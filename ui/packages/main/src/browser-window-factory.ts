import type { BrowserWindowConstructorOptions, BrowserWindow } from 'electron';
import type { Logger } from './util/logger.js';

export interface CreateMainWindowDeps {
  createWindow: (opts: BrowserWindowConstructorOptions) => BrowserWindow;
  logger: Logger;
  preloadPath: string | undefined;
  indexHtmlPath: string;
  devServerUrl?: string;
}

export function createMainWindow(deps: CreateMainWindowDeps): BrowserWindow {
  const { createWindow, logger, preloadPath, indexHtmlPath, devServerUrl } = deps;

  const window = createWindow({
    width: 1080,
    height: 720,
    minWidth: 960,
    minHeight: 640,
    show: false,
    backgroundColor: '#111111',
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      allowRunningInsecureContent: false,
      experimentalFeatures: false,
      ...(preloadPath != null ? { preload: preloadPath } : {}),
    },
  });

  window.once('ready-to-show', () => {
    window.show();
  });

  window.webContents.setWindowOpenHandler((_details) => ({ action: 'deny' }));

  window.webContents.on('will-navigate', (e, url) => {
    e.preventDefault();
    logger.warn('navigation blocked', { url });
  });

  if (devServerUrl != null) {
    void window.loadURL(devServerUrl);
  } else {
    void window.loadFile(indexHtmlPath);
  }

  return window;
}
