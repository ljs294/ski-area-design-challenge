import { app, BrowserWindow, ipcMain } from 'electron';
import path from 'path';
import { fileURLToPath } from 'url';
import { registerTerrainStorageHandlers } from './ipcTerrainStorage';
import { registerGameSaveStorageHandlers } from './ipcGameSaveStorage';
import { WINDOW_GET_MODE_CHANNEL, WINDOW_SET_MODE_CHANNEL, EXIT_CHANNEL } from '../src/ipcContract';
import type { WindowMode } from '../src/ipcContract';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

process.env['ELECTRON_DISABLE_SECURITY_WARNINGS'] = 'true';

let mainWindow: BrowserWindow | null = null;

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
  const labHash = process.env.GRAPHICS_LAB ? 'graphics-lab' : '';
  if (process.env.VITE_DEV_SERVER_URL) {
    mainWindow.loadURL(process.env.VITE_DEV_SERVER_URL + (labHash ? `#${labHash}` : ''));
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'), labHash ? { hash: labHash } : undefined);
  }

  mainWindow.on('closed', () => {
    mainWindow = null;
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

app.whenReady().then(() => {
  registerTerrainStorageHandlers();
  registerGameSaveStorageHandlers();
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

ipcMain.handle(WINDOW_GET_MODE_CHANNEL, (): WindowMode => {
  return mainWindow ? getWindowMode(mainWindow) : 'windowed';
});

ipcMain.handle(WINDOW_SET_MODE_CHANNEL, (_e, mode: WindowMode): WindowMode => {
  if (mainWindow) applyWindowMode(mainWindow, mode);
  return mainWindow ? getWindowMode(mainWindow) : 'windowed';
});

// Close the app from the main-menu Exit sign.
ipcMain.on(EXIT_CHANNEL, () => {
  app.quit();
});
