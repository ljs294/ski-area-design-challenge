import { app, BrowserWindow, ipcMain, session } from 'electron';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { registerDesignStorageHandlers } from '../../electron/ipcDesignStorage';
import { registerTerrainStorageHandlers } from '../../electron/ipcTerrainStorage';
import { registerOverpassRequestIdentity } from '../../electron/overpassRequestIdentity';
import {
  EXIT_CHANNEL, WINDOW_GET_MODE_CHANNEL, WINDOW_SET_MODE_CHANNEL,
} from '../../src/ipcContract';
import type { WindowMode } from '../../src/ipcContract';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
process.env.ELECTRON_DISABLE_SECURITY_WARNINGS = 'true';
app.setName('Ski Area Design Challenge - Three.js Edition');
app.setPath('userData', path.join(app.getPath('appData'), 'ski-area-design-challenge-threejs'));

let mainWindow: BrowserWindow | null = null;

function windowMode(win: BrowserWindow): WindowMode {
  if (win.isSimpleFullScreen()) return 'borderless';
  if (win.isFullScreen()) return 'fullscreen';
  return 'windowed';
}

function setWindowMode(win: BrowserWindow, mode: WindowMode): void {
  if (mode === 'windowed') { win.setSimpleFullScreen(false); win.setFullScreen(false); }
  else if (mode === 'fullscreen') { win.setSimpleFullScreen(false); win.setFullScreen(true); }
  else { win.setFullScreen(false); win.setSimpleFullScreen(true); }
}

function createWindow(): void {
  const win = new BrowserWindow({
    width: 1280, height: 800, minWidth: 800, minHeight: 600, useContentSize: true,
    backgroundColor: '#111b22',
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: false,
      preload: path.join(__dirname, 'preload.mjs') },
  });
  mainWindow = win;
  if (process.env.VITE_DEV_SERVER_URL) void win.loadURL(process.env.VITE_DEV_SERVER_URL);
  else void win.loadFile(path.join(__dirname, '../dist/index.html'));
  win.on('closed', () => { if (mainWindow === win) mainWindow = null; });
}

app.whenReady().then(() => {
  registerOverpassRequestIdentity(session.defaultSession.webRequest, app.getVersion());
  registerTerrainStorageHandlers();
  registerDesignStorageHandlers();
  createWindow();
  app.on('activate', () => { if (!BrowserWindow.getAllWindows().length) createWindow(); });
});

app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
ipcMain.handle(WINDOW_GET_MODE_CHANNEL, (): WindowMode => mainWindow ? windowMode(mainWindow) : 'windowed');
ipcMain.handle(WINDOW_SET_MODE_CHANNEL, (_event, mode: WindowMode): WindowMode => {
  if (mainWindow) setWindowMode(mainWindow, mode);
  return mainWindow ? windowMode(mainWindow) : 'windowed';
});
ipcMain.on(EXIT_CHANNEL, () => app.quit());
