import { app, BrowserWindow, ipcMain } from 'electron';
import path from 'path';

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
      nodeIntegration: true,
      contextIsolation: false, // Direct access to ipcRenderer in main.ts
    },
  });

  if (process.env.VITE_DEV_SERVER_URL) {
    mainWindow.loadURL(process.env.VITE_DEV_SERVER_URL);
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

app.whenReady().then(() => {
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

// IPC communication to close the window from the main menu exit button
ipcMain.on('exit-game', () => {
  app.quit();
});
