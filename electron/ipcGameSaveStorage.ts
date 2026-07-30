import { app, BrowserWindow, ipcMain } from 'electron';
import fs from 'fs';
import path from 'path';
import {
  GAMESAVE_SAVE_CHANNEL,
  GAMESAVE_LOAD_CHANNEL,
  GAMESAVE_LIST_CHANNEL,
  GAMESAVE_DELETE_CHANNEL,
  GAMESAVE_CAPTURE_PREVIEW_CHANNEL,
  GAMESAVE_LOAD_PREVIEW_CHANNEL,
} from '../src/ipcContract';
import type {
  GameSaveSaveRequest,
  GameSaveSaveResponse,
  GameSaveLoadRequest,
  GameSaveLoadResponse,
  GameSaveListResponse,
  GameSaveDeleteRequest,
  GameSaveDeleteResponse,
  GameSavePreviewRequest,
  GameSavePreviewCaptureResponse,
  GameSavePreviewLoadResponse,
} from '../src/ipcContract';
import type { GameSave, GameSaveSummary } from '../src/types';

// Game saves are small (camera + site + reserved design fields) so, unlike
// terrain, each save is a single JSON file. Structure mirrors ipcTerrainStorage.

function savesDir(): string {
  const dir = path.join(app.getPath('userData'), 'saves');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function indexFilePath(): string {
  return path.join(savesDir(), 'index.json');
}

/** Resolve key -> path, guaranteeing the result stays inside the saves dir. */
function safeFilePath(key: string): string | null {
  const dir = savesDir();
  const resolved = path.resolve(dir, `${key}.json`);
  if (!resolved.startsWith(dir + path.sep) && resolved !== dir) return null;
  return resolved;
}

function safePreviewPath(key: string): string | null {
  const dir = savesDir();
  const resolved = path.resolve(dir, `${key}.preview.jpg`);
  if (!resolved.startsWith(dir + path.sep) && resolved !== dir) return null;
  return resolved;
}

function readIndex(): GameSaveSummary[] {
  try {
    return JSON.parse(fs.readFileSync(indexFilePath(), 'utf-8'));
  } catch {
    return [];
  }
}

function writeIndex(summaries: GameSaveSummary[]): void {
  fs.writeFileSync(indexFilePath(), JSON.stringify(summaries, null, 2), 'utf-8');
}

function toSummary(save: GameSave): GameSaveSummary {
  return {
    key: save.key,
    name: save.name,
    mountainId: save.mountainId,
    terrainKey: save.terrainKey,
    createdAt: save.createdAt,
    updatedAt: save.updatedAt,
    lastPlayedAt: save.lastPlayedAt,
  };
}

export function registerGameSaveStorageHandlers(): void {
  ipcMain.handle(GAMESAVE_SAVE_CHANNEL, (_e, req: GameSaveSaveRequest): GameSaveSaveResponse => {
    try {
      const file = safeFilePath(req.save.key);
      if (!file) return { ok: false, error: 'Invalid save key' };
      fs.writeFileSync(file, JSON.stringify(req.save, null, 2), 'utf-8');
      const index = readIndex().filter((s) => s.key !== req.save.key);
      index.push(toSummary(req.save));
      writeIndex(index);
      return { ok: true, key: req.save.key };
    } catch (e) {
      return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving game' };
    }
  });

  ipcMain.handle(GAMESAVE_LOAD_CHANNEL, (_e, req: GameSaveLoadRequest): GameSaveLoadResponse => {
    const file = safeFilePath(req.key);
    if (!file) return null;
    try {
      return JSON.parse(fs.readFileSync(file, 'utf-8')) as GameSave;
    } catch {
      return null;
    }
  });

  ipcMain.handle(GAMESAVE_LIST_CHANNEL, (): GameSaveListResponse => readIndex());

  ipcMain.handle(GAMESAVE_DELETE_CHANNEL, (_e, req: GameSaveDeleteRequest): GameSaveDeleteResponse => {
    const file = safeFilePath(req.key);
    const preview = safePreviewPath(req.key);
    if (!file || !preview) return { ok: false };
    try {
      fs.rmSync(file, { force: true });
      fs.rmSync(preview, { force: true });
      writeIndex(readIndex().filter((s) => s.key !== req.key));
      return { ok: true };
    } catch {
      return { ok: false };
    }
  });

  ipcMain.handle(
    GAMESAVE_CAPTURE_PREVIEW_CHANNEL,
    async (event, req: GameSavePreviewRequest): Promise<GameSavePreviewCaptureResponse> => {
      const file = safePreviewPath(req.key);
      const win = BrowserWindow.fromWebContents(event.sender);
      if (!file || !win || win.isDestroyed()) {
        return { ok: false, error: 'Unable to resolve the game window or preview path.' };
      }
      const temp = `${file}.${process.pid}.${Date.now()}.tmp`;
      try {
        let image = await win.webContents.capturePage();
        const size = image.getSize();
        const longest = Math.max(size.width, size.height);
        if (longest > 1600) {
          const scale = 1600 / longest;
          image = image.resize({
            width: Math.max(1, Math.round(size.width * scale)),
            height: Math.max(1, Math.round(size.height * scale)),
            quality: 'best',
          });
        }
        fs.writeFileSync(temp, image.toJPEG(80));
        fs.renameSync(temp, file);
        return { ok: true };
      } catch (error) {
        try { fs.rmSync(temp, { force: true }); } catch { /* best effort */ }
        return {
          ok: false,
          error: error instanceof Error ? error.message : 'Unable to capture the resort preview.',
        };
      }
    }
  );

  ipcMain.handle(
    GAMESAVE_LOAD_PREVIEW_CHANNEL,
    (_event, req: GameSavePreviewRequest): GameSavePreviewLoadResponse => {
      const file = safePreviewPath(req.key);
      if (!file) return { ok: false, error: 'Invalid save key.' };
      try {
        if (!fs.existsSync(file)) return { ok: true, dataUrl: null };
        return { ok: true, dataUrl: `data:image/jpeg;base64,${fs.readFileSync(file).toString('base64')}` };
      } catch (error) {
        return {
          ok: false,
          error: error instanceof Error ? error.message : 'Unable to load the resort preview.',
        };
      }
    }
  );
}
