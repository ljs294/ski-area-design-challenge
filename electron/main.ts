import { app, BrowserWindow, ipcMain, session, utilityProcess, type UtilityProcess } from 'electron';
import path from 'path';
import { fileURLToPath } from 'url';
import { registerTerrainStorageHandlers } from './ipcTerrainStorage';
import { registerGameSaveStorageHandlers } from './ipcGameSaveStorage';
import { registerGuestSimulationStorageHandlers } from './ipcGuestSimulationStorage';
import { registerWeatherStorageHandlers } from './ipcWeatherStorage';
import { registerOverpassRequestIdentity } from './overpassRequestIdentity';
import {
  WINDOW_GET_MODE_CHANNEL,
  WINDOW_SET_MODE_CHANNEL,
  EXIT_CHANNEL,
  WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL,
  WINDOW_CLOSE_CHECKPOINT_COMPLETE_CHANNEL,
} from '../src/ipcContract';
import type { WindowMode } from '../src/ipcContract';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

process.env['ELECTRON_DISABLE_SECURITY_WARNINGS'] = 'true';

let mainWindow: BrowserWindow | null = null;
let closeCheckpointPending = false;
let closeCheckpointComplete = false;
let closeCheckpointTimer: ReturnType<typeof setTimeout> | null = null;
let quitAfterCheckpoint = false;
let weatherServiceProcess: UtilityProcess | null = null;

async function ensureWeatherPreparationService(): Promise<void> {
  try {
    const response = await fetch('http://127.0.0.1:8787/health', { signal: AbortSignal.timeout(500) });
    if (response.ok) return;
  } catch { /* start the bundled service below */ }
  const entry = app.isPackaged
    ? path.join(process.resourcesPath, 'weather-service', 'utility-entry.cjs')
    : path.join(__dirname, '../weather-service/utility-entry.cjs');
  weatherServiceProcess = utilityProcess.fork(entry, [], {
    env: { ...process.env, WEATHER_SERVICE_MODE: process.env.WEATHER_SERVICE_MODE ?? 'live',
      WEATHER_CACHE_DIR: path.join(app.getPath('userData'), 'weather-builder-cache') },
    stdio: 'pipe',
  });
  weatherServiceProcess.stdout?.on('data', (chunk) => process.stdout.write(chunk));
  weatherServiceProcess.stderr?.on('data', (chunk) => process.stderr.write(chunk));
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, 100));
    try {
      const response = await fetch('http://127.0.0.1:8787/health', { signal: AbortSignal.timeout(250) });
      if (response.ok) return;
    } catch { /* wait for the child to bind */ }
  }
  console.error('Weather preparation service did not become reachable.');
}

function finishCloseCheckpoint(win: BrowserWindow): void {
  if (closeCheckpointTimer) {
    clearTimeout(closeCheckpointTimer);
    closeCheckpointTimer = null;
  }
  closeCheckpointPending = false;
  closeCheckpointComplete = true;
  if (quitAfterCheckpoint) app.quit();
  else if (!win.isDestroyed()) win.close();
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1024,
    height: 768,
    minWidth: 800,
    minHeight: 600,
    useContentSize: true,
    resizable: true,
    backgroundColor: '#f4f3ec', // Subway Builder cream-beige matte background
    webPreferences: {
      // Renderer is a pure web app (MapLibre + React). It reaches the main
      // process only through the contextBridge API in preload.mjs — never Node
      // or ipcRenderer directly. contextIsolation stays on; sandbox is disabled
      // because the preload is an ES module (Electron only loads ESM preloads
      // when unsandboxed). The renderer itself gets no Node access.
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      preload: path.join(__dirname, 'preload.mjs'),
    },
  });

  // GRAPHICS_LAB=1 (see `npm run dev:lab`) boots straight into the two-map
  // graphics dev tool, bypassing the menu.
  const labHash = process.env.GRAPHICS_LAB ? 'graphics-lab' : process.env.WEATHER_LAB ? 'weather-lab' : '';
  if (process.env.VITE_DEV_SERVER_URL) {
    mainWindow.loadURL(process.env.VITE_DEV_SERVER_URL + (labHash ? `#${labHash}` : ''));
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'), labHash ? { hash: labHash } : undefined);
  }

  mainWindow.on('closed', () => {
    mainWindow = null;
    closeCheckpointPending = false;
    closeCheckpointComplete = false;
    quitAfterCheckpoint = false;
    if (closeCheckpointTimer) clearTimeout(closeCheckpointTimer);
    closeCheckpointTimer = null;
  });

  mainWindow.on('close', (event) => {
    if (closeCheckpointComplete) return;
    event.preventDefault();
    if (closeCheckpointPending || !mainWindow) return;
    closeCheckpointPending = true;
    mainWindow.webContents.send(WINDOW_REQUEST_CLOSE_CHECKPOINT_CHANNEL);
    // A renderer or storage failure must never make the application unclosable.
    closeCheckpointTimer = setTimeout(() => {
      if (mainWindow) finishCloseCheckpoint(mainWindow);
    }, 3000);
  });
}

/** Read the window's current mode for the Settings panel to reflect on open. */
function getWindowMode(win: BrowserWindow): WindowMode {
  if (win.isSimpleFullScreen()) return 'borderless';
  if (win.isFullScreen()) return 'fullscreen';
  return 'windowed';
}

/**
 * Apply a window mode. Fullscreen is the OS's native fullscreen; borderless is
 * a chromeless full-work-area window ("simple fullscreen"), which on some
 * platforms behaves like fullscreen — an acceptable best-effort per the plan.
 */
function applyWindowMode(win: BrowserWindow, mode: WindowMode): void {
  switch (mode) {
    case 'windowed':
      win.setSimpleFullScreen(false);
      win.setFullScreen(false);
      break;
    case 'fullscreen':
      win.setSimpleFullScreen(false);
      win.setFullScreen(true);
      break;
    case 'borderless':
      win.setFullScreen(false);
      win.setSimpleFullScreen(true);
      break;
  }
}

app.whenReady().then(async () => {
  await ensureWeatherPreparationService();
  registerOverpassRequestIdentity(session.defaultSession.webRequest, app.getVersion());
  registerTerrainStorageHandlers();
  registerGameSaveStorageHandlers();
  registerGuestSimulationStorageHandlers();
  registerWeatherStorageHandlers();
  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

app.on('before-quit', () => {
  weatherServiceProcess?.kill();
  weatherServiceProcess = null;
});

ipcMain.handle(WINDOW_GET_MODE_CHANNEL, (): WindowMode => {
  return mainWindow ? getWindowMode(mainWindow) : 'windowed';
});

ipcMain.handle(WINDOW_SET_MODE_CHANNEL, (_e, mode: WindowMode): WindowMode => {
  if (mainWindow) applyWindowMode(mainWindow, mode);
  return mainWindow ? getWindowMode(mainWindow) : 'windowed';
});

// Close the app from the main-menu Exit sign.
ipcMain.on(EXIT_CHANNEL, () => {
  quitAfterCheckpoint = true;
  app.quit();
});

ipcMain.on(WINDOW_CLOSE_CHECKPOINT_COMPLETE_CHANNEL, (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win || win !== mainWindow || !closeCheckpointPending) return;
  finishCloseCheckpoint(win);
});
