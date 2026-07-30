// Runs in the renderer's isolated preload context. Exposes a minimal, safe
// desktop API over contextBridge so the sandboxed renderer never touches Node
// or ipcRenderer directly. Bundled to preload.cjs (CommonJS) so it loads under
// the default sandbox — only `electron` is required here, no Node built-ins.
import { contextBridge, ipcRenderer } from 'electron';
import {
  TERRAIN_SAVE_CHANNEL,
  TERRAIN_LOAD_CHANNEL,
  TERRAIN_LIST_CHANNEL,
  TERRAIN_DELETE_CHANNEL,
  GAMESAVE_SAVE_CHANNEL,
  GAMESAVE_LOAD_CHANNEL,
  GAMESAVE_LIST_CHANNEL,
  GAMESAVE_DELETE_CHANNEL,
  GAMESAVE_CAPTURE_PREVIEW_CHANNEL,
  GAMESAVE_LOAD_PREVIEW_CHANNEL,
  WINDOW_GET_MODE_CHANNEL,
  WINDOW_SET_MODE_CHANNEL,
  EXIT_CHANNEL,
  WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL,
  WINDOW_CLOSE_CHECKPOINT_COMPLETE_CHANNEL,
} from '../src/ipcContract';

const api = {
  isDesktop: true as const,
  terrain: {
    save: (record: unknown) => ipcRenderer.invoke(TERRAIN_SAVE_CHANNEL, { record }),
    load: (key: string) => ipcRenderer.invoke(TERRAIN_LOAD_CHANNEL, { key }),
    loadPackage: (key: string) => ipcRenderer.invoke(TERRAIN_LOAD_CHANNEL, { key }),
    repairPackage: (record: unknown) => ipcRenderer.invoke(TERRAIN_SAVE_CHANNEL, { record }),
    list: () => ipcRenderer.invoke(TERRAIN_LIST_CHANNEL),
    delete: (key: string) => ipcRenderer.invoke(TERRAIN_DELETE_CHANNEL, { key }),
  },
  games: {
    save: (save: unknown) => ipcRenderer.invoke(GAMESAVE_SAVE_CHANNEL, { save }),
    load: (key: string) => ipcRenderer.invoke(GAMESAVE_LOAD_CHANNEL, { key }),
    list: () => ipcRenderer.invoke(GAMESAVE_LIST_CHANNEL),
    delete: (key: string) => ipcRenderer.invoke(GAMESAVE_DELETE_CHANNEL, { key }),
    capturePreview: (key: string) => ipcRenderer.invoke(GAMESAVE_CAPTURE_PREVIEW_CHANNEL, { key }),
    loadPreview: (key: string) => ipcRenderer.invoke(GAMESAVE_LOAD_PREVIEW_CHANNEL, { key }),
  },
  window: {
    getMode: () => ipcRenderer.invoke(WINDOW_GET_MODE_CHANNEL),
    setMode: (mode: string) => ipcRenderer.invoke(WINDOW_SET_MODE_CHANNEL, mode),
  },
  lifecycle: {
    onCloseCheckpointRequested: (listener: () => void) => {
      const wrapped = () => listener();
      ipcRenderer.on(WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL, wrapped);
      return () => ipcRenderer.removeListener(WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL, wrapped);
    },
    completeCloseCheckpoint: () => ipcRenderer.send(WINDOW_CLOSE_CHECKPOINT_COMPLETE_CHANNEL),
  },
  exit: () => ipcRenderer.send(EXIT_CHANNEL),
};

contextBridge.exposeInMainWorld('desktop', api);

export type DesktopApi = typeof api;
