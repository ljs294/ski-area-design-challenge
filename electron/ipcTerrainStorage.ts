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
 * Resolve a user-supplied key + extension to a file path, guaranteeing the
 * result stays inside the terrains directory regardless of what the key
 * contains.
 */
function safeFilePath(key: string, extension: string): string | null {
  const dir = terrainsDir();
  const resolved = path.resolve(dir, `${key}${extension}`);
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
      const metaPath = safeFilePath(req.record.key, '.json');
      const heightsPath = safeFilePath(req.record.key, '.heights.bin');
      if (!metaPath || !heightsPath) return { ok: false, error: 'Invalid terrain key' };

      // sampleHeights is stored as raw Float32 binary, not JSON text — at
      // the grid sizes this app now requests (up to 2000x2000+), a plain
      // JSON number array runs ~18 bytes/point vs 4 bytes/point raw
      // binary, a ~4.5x difference that matters once files run into the
      // tens of megabytes.
      const { sampleHeights, ...metadata } = req.record;
      fs.writeFileSync(heightsPath, Buffer.from(Float32Array.from(sampleHeights).buffer));
      fs.writeFileSync(metaPath, JSON.stringify(metadata), 'utf-8');

      const index = readIndex().filter((s) => s.key !== req.record.key);
      index.push(toSummary(req.record));
      writeIndex(index);

      return { ok: true, key: req.record.key };
    } catch (e) {
      return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
    }
  });

  ipcMain.handle(TERRAIN_LOAD_CHANNEL, (_event, req: TerrainLoadRequest): TerrainLoadResponse => {
    const metaPath = safeFilePath(req.key, '.json');
    const heightsPath = safeFilePath(req.key, '.heights.bin');
    if (!metaPath || !heightsPath) return null;
    try {
      const metadata = JSON.parse(fs.readFileSync(metaPath, 'utf-8'));

      // Back-compat: terrains saved before the binary split have
      // sampleHeights embedded directly in the metadata JSON and no
      // .heights.bin file — fall back to reading it from there.
      if (!fs.existsSync(heightsPath)) {
        return metadata as TerrainRecord;
      }

      const buf = fs.readFileSync(heightsPath);
      const floats = new Float32Array(buf.buffer, buf.byteOffset, buf.byteLength / 4);
      return { ...metadata, sampleHeights: Array.from(floats) };
    } catch {
      return null;
    }
  });

  ipcMain.handle(TERRAIN_LIST_CHANNEL, (): TerrainListResponse => {
    return readIndex();
  });

  ipcMain.handle(TERRAIN_DELETE_CHANNEL, (_event, req: TerrainDeleteRequest): TerrainDeleteResponse => {
    const metaPath = safeFilePath(req.key, '.json');
    const heightsPath = safeFilePath(req.key, '.heights.bin');
    if (!metaPath || !heightsPath) return { ok: false };
    try {
      fs.rmSync(metaPath, { force: true });
      fs.rmSync(heightsPath, { force: true });
      writeIndex(readIndex().filter((s) => s.key !== req.key));
      return { ok: true };
    } catch {
      return { ok: false };
    }
  });
}
