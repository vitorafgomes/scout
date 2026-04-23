import { contextBridge, ipcRenderer } from 'electron';
import { buildBridge } from './build-bridge.js';

const bridge = buildBridge(ipcRenderer);

contextBridge.exposeInMainWorld('scout', bridge);
