import { app, ipcMain } from 'electron';
import { createHash, randomUUID } from 'crypto';
import fs from 'fs';
import path from 'path';
import {
  GUEST_SIMULATION_LOAD_CHECKPOINT_CHANNEL,
  GUEST_SIMULATION_SAVE_CHECKPOINT_CHANNEL,
  type GuestSimulationCheckpointLoadRequest,
  type GuestSimulationCheckpointLoadResponse,
  type GuestSimulationCheckpointSaveRequest,
  type GuestSimulationCheckpointSaveResponse,
} from '../src/ipcContract';

interface CheckpointMetadata {
  version: 1;
  gameSaveUpdatedAt: string;
  checksumSha256: string;
  byteLength: number;
}

const activeSaves = new Set<string>();

function sidecarDir(): string {
  const directory = path.join(app.getPath('userData'), 'guest-simulation');
  fs.mkdirSync(directory, { recursive: true });
  return directory;
}

function safeStem(saveKey: string): string | null {
  if (!saveKey || !/^[A-Za-z0-9._-]+$/.test(saveKey)) return null;
  const directory = sidecarDir();
  const stem = path.resolve(directory, saveKey);
  return stem.startsWith(directory + path.sep) ? stem : null;
}

function checksum(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

function checkpointPaths(stem: string, generation: 'current' | 'previous') {
  return { bytes: `${stem}.${generation}.bin`, metadata: `${stem}.${generation}.json` };
}

function readGeneration(stem: string, generation: 'current' | 'previous', gameSaveUpdatedAt: string):
  Extract<GuestSimulationCheckpointLoadResponse, { status: 'ready' }> | null {
  const files = checkpointPaths(stem, generation);
  if (!fs.existsSync(files.bytes) && !fs.existsSync(files.metadata)) return null;
  const metadata = JSON.parse(fs.readFileSync(files.metadata, 'utf8')) as Partial<CheckpointMetadata>;
  const payload = fs.readFileSync(files.bytes);
  if (metadata.version !== 1 || metadata.gameSaveUpdatedAt !== gameSaveUpdatedAt || metadata.byteLength !== payload.byteLength ||
    typeof metadata.checksumSha256 !== 'string' || checksum(payload) !== metadata.checksumSha256) {
    throw new Error(`${generation} guest checkpoint failed content validation`);
  }
  return { status: 'ready', source: generation, bytes: new Uint8Array(payload),
    checksumSha256: metadata.checksumSha256 };
}

export function registerGuestSimulationStorageHandlers(): void {
  ipcMain.handle(GUEST_SIMULATION_SAVE_CHECKPOINT_CHANNEL,
    async (_event, request: GuestSimulationCheckpointSaveRequest): Promise<GuestSimulationCheckpointSaveResponse> => {
      const stem = safeStem(request.saveKey);
      if (!stem) return { ok: false, error: 'Invalid save key' };
      if (activeSaves.has(stem)) return { ok: false, error: 'A guest checkpoint save is already in progress.' };
      activeSaves.add(stem);
      try {
        const bytes = new Uint8Array(request.bytes);
        const digest = checksum(bytes);
        const metadata: CheckpointMetadata = { version: 1, gameSaveUpdatedAt: request.gameSaveUpdatedAt,
          checksumSha256: digest, byteLength: bytes.byteLength };
        const current = checkpointPaths(stem, 'current');
        const previous = checkpointPaths(stem, 'previous');
        const nonce = `${process.pid}.${randomUUID()}`;
        const temporary = { bytes: `${stem}.${nonce}.bin.tmp`, metadata: `${stem}.${nonce}.json.tmp` };
        await fs.promises.writeFile(temporary.bytes, bytes);
        await fs.promises.writeFile(temporary.metadata, JSON.stringify(metadata), 'utf8');
        if (fs.existsSync(current.bytes) && fs.existsSync(current.metadata)) {
          const stagedPrevious = { bytes: `${stem}.${nonce}.previous.bin.tmp`, metadata: `${stem}.${nonce}.previous.json.tmp` };
          await fs.promises.copyFile(current.bytes, stagedPrevious.bytes);
          await fs.promises.copyFile(current.metadata, stagedPrevious.metadata);
          await fs.promises.rm(previous.bytes, { force: true }); await fs.promises.rm(previous.metadata, { force: true });
          await fs.promises.rename(stagedPrevious.bytes, previous.bytes); await fs.promises.rename(stagedPrevious.metadata, previous.metadata);
        }
        await fs.promises.rm(current.bytes, { force: true }); await fs.promises.rm(current.metadata, { force: true });
        await fs.promises.rename(temporary.bytes, current.bytes);
        await fs.promises.rename(temporary.metadata, current.metadata);
        return { ok: true, checksumSha256: digest, byteLength: bytes.byteLength };
      } catch (error) {
        return { ok: false, error: error instanceof Error ? error.message : 'Unable to save guest checkpoint' };
      } finally {
        activeSaves.delete(stem);
      }
    });

  ipcMain.handle(GUEST_SIMULATION_LOAD_CHECKPOINT_CHANNEL,
    async (_event, request: GuestSimulationCheckpointLoadRequest): Promise<GuestSimulationCheckpointLoadResponse> => {
      const stem = safeStem(request.saveKey);
      if (!stem) return { status: 'corrupt', error: 'Invalid save key' };
      let found = false;
      const errors: string[] = [];
      for (const generation of ['current', 'previous'] as const) {
        try {
          const result = readGeneration(stem, generation, request.gameSaveUpdatedAt);
          if (result) return result;
        } catch (error) {
          found = true;
          errors.push(error instanceof Error ? error.message : `${generation} checkpoint is corrupt`);
        }
      }
      return found ? { status: 'corrupt', error: errors.join('; ') } : { status: 'missing' };
    });
}
