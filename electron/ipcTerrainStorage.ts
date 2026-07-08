import { app, ipcMain } from 'electron';
import fs from 'fs';
import path from 'path';
import {
  TERRAIN_SAVE_CHANNEL,
  TERRAIN_LOAD_CHANNEL,
  TERRAIN_LIST_CHANNEL,
  TERRAIN_DELETE_CHANNEL,
} from '../src/ipcContract';
import type {
  TerrainSaveRequest,
  TerrainSaveResponse,
  TerrainLoadRequest,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteRequest,
  TerrainDeleteResponse,
} from '../src/ipcContract';
import type { TerrainRecord, TerrainSummary } from '../src/types';

function terrainsDir(): string {
  const dir = path.join(app.getPath('userData'), 'terrains');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function indexFilePath(): string {
  return path.join(terrainsDir(), 'index.json');
}

/**
 * Resolve a user-supplied key to a file path, guaranteeing the result stays
 * inside the terrains directory regardless of what the key contains.
 */
function recordFilePath(key: string): string | null {
  const dir = terrainsDir();
  const resolved = path.resolve(dir, `${key}.json`);
  if (!resolved.startsWith(dir + path.sep) && resolved !== dir) return null;
  return resolved;
}

function readIndex(): TerrainSummary[] {
  try {
    const raw = fs.readFileSync(indexFilePath(), 'utf-8');
    return JSON.parse(raw);
  } catch {
    return [];
  }
}

function writeIndex(summaries: TerrainSummary[]): void {
  fs.writeFileSync(indexFilePath(), JSON.stringify(summaries, null, 2), 'utf-8');
}

function toSummary(record: TerrainRecord): TerrainSummary {
  return {
    key: record.key,
    mountainName: record.mountainName,
    latitude: record.latitude,
    longitude: record.longitude,
    areaSizeMeters: record.areaSizeMeters,
    sourceType: record.sourceType,
    createdAt: record.createdAt,
    updatedAt: record.updatedAt,
  };
}

export function registerTerrainStorageHandlers(): void {
  ipcMain.handle(TERRAIN_SAVE_CHANNEL, (_event, req: TerrainSaveRequest): TerrainSaveResponse => {
    try {
      const filePath = recordFilePath(req.record.key);
      if (!filePath) return { ok: false, error: 'Invalid terrain key' };

      fs.writeFileSync(filePath, JSON.stringify(req.record), 'utf-8');

      const index = readIndex().filter((s) => s.key !== req.record.key);
      index.push(toSummary(req.record));
      writeIndex(index);

      return { ok: true, key: req.record.key };
    } catch (e) {
      return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
    }
  });

  ipcMain.handle(TERRAIN_LOAD_CHANNEL, (_event, req: TerrainLoadRequest): TerrainLoadResponse => {
    const filePath = recordFilePath(req.key);
    if (!filePath) return null;
    try {
      const raw = fs.readFileSync(filePath, 'utf-8');
      return JSON.parse(raw);
    } catch {
      return null;
    }
  });

  ipcMain.handle(TERRAIN_LIST_CHANNEL, (): TerrainListResponse => {
    return readIndex();
  });

  ipcMain.handle(TERRAIN_DELETE_CHANNEL, (_event, req: TerrainDeleteRequest): TerrainDeleteResponse => {
    const filePath = recordFilePath(req.key);
    if (!filePath) return { ok: false };
    try {
      fs.rmSync(filePath, { force: true });
      writeIndex(readIndex().filter((s) => s.key !== req.key));
      return { ok: true };
    } catch {
      return { ok: false };
    }
  });
}
