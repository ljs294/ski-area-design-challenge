import { app, ipcMain } from 'electron';
import fs from 'fs';
import path from 'path';
import {
  TERRAIN_SAVE_CHANNEL,
  TERRAIN_SAVE_COVER_CHANNEL,
  TERRAIN_SAVE_CONTEXT_CHANNEL,
  TERRAIN_LOAD_CHANNEL,
  TERRAIN_LIST_CHANNEL,
  TERRAIN_DELETE_CHANNEL,
} from '../src/ipcContract';
import type {
  TerrainSaveRequest,
  TerrainSaveResponse,
  TerrainCoverSaveRequest,
  TerrainCoverSaveResponse,
  TerrainMapContextSaveRequest,
  TerrainMapContextSaveResponse,
  TerrainLoadRequest,
  TerrainLoadResponse,
  TerrainListResponse,
  TerrainDeleteRequest,
  TerrainDeleteResponse,
} from '../src/ipcContract';
import type { TerrainRecord, TerrainSummary } from '../src/types';
import { checksumBytes } from '../src/terrainPackage';

const fsp = fs.promises;

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

function float32View(buffer: Buffer): Float32Array {
  const bytes = buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength);
  return new Float32Array(bytes);
}

function uint8View(buffer: Buffer): Uint8Array {
  return new Uint8Array(buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength));
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    await fsp.access(filePath);
    return true;
  } catch {
    return false;
  }
}

export function registerTerrainStorageHandlers(): void {
  ipcMain.handle(TERRAIN_SAVE_CHANNEL, async (_event, req: TerrainSaveRequest): Promise<TerrainSaveResponse> => {
    try {
      const metaPath = safeFilePath(req.record.key, '.json');
      const heightsPath = safeFilePath(req.record.key, '.heights.bin');
      const coverPath = safeFilePath(req.record.key, '.cover.bin');
      const originalCoverPath = safeFilePath(req.record.key, '.cover-original.bin');
      const coverGeometryPath = safeFilePath(req.record.key, '.cover-geometry.bin');
      const coverDisplayPath = safeFilePath(req.record.key, '.cover-display.bin');
      const contoursPath = safeFilePath(req.record.key, '.contours.bin');
      const imageryPath = safeFilePath(req.record.key, '.imagery.jpg');
      if (!metaPath || !heightsPath || !coverPath || !originalCoverPath || !coverGeometryPath || !coverDisplayPath || !contoursPath || !imageryPath) return { ok: false, error: 'Invalid terrain key' };

      // sampleHeights is stored as raw Float32 binary, not JSON text — at
      // the grid sizes this app now requests (up to 2000x2000+), a plain
      // JSON number array runs ~18 bytes/point vs 4 bytes/point raw
      // binary, a ~4.5x difference that matters once files run into the
      // tens of megabytes.
      const { sampleHeights, coverGrid, originalCoverGrid, coverBoundarySegments, coverDisplayGeometry, contourSegments, localImagery, ...metadata } = req.record;
      const nonce = `${process.pid}-${Date.now()}`;
      const metaTmp = `${metaPath}.${nonce}.tmp`;
      const heightsTmp = `${heightsPath}.${nonce}.tmp`;
      const coverTmp = `${coverPath}.${nonce}.tmp`;
      const originalCoverTmp = `${originalCoverPath}.${nonce}.tmp`;
      const coverGeometryTmp = `${coverGeometryPath}.${nonce}.tmp`;
      const coverDisplayTmp = `${coverDisplayPath}.${nonce}.tmp`;
      const contoursTmp = `${contoursPath}.${nonce}.tmp`;
      const imageryTmp = `${imageryPath}.${nonce}.tmp`;
      try {
        await Promise.all([
          fsp.writeFile(heightsTmp, Buffer.from(Float32Array.from(sampleHeights).buffer)),
          coverGrid ? fsp.writeFile(coverTmp, Buffer.from(Uint8Array.from(coverGrid.data))) : undefined,
          originalCoverGrid
            ? fsp.writeFile(originalCoverTmp, Buffer.from(Uint8Array.from(originalCoverGrid.data))) : undefined,
          coverBoundarySegments
            ? fsp.writeFile(coverGeometryTmp, Buffer.from(Float32Array.from(coverBoundarySegments).buffer)) : undefined,
          coverDisplayGeometry
            ? fsp.writeFile(coverDisplayTmp, Buffer.from(Float32Array.from(coverDisplayGeometry).buffer)) : undefined,
          contourSegments
            ? fsp.writeFile(contoursTmp, Buffer.from(Float32Array.from(contourSegments).buffer)) : undefined,
          localImagery ? fsp.writeFile(imageryTmp, Buffer.from(Uint8Array.from(localImagery))) : undefined,
          fsp.writeFile(metaTmp, JSON.stringify(metadata), 'utf-8'),
        ]);

        const verify = async (file: string, expectedBytes: number, expectedChecksum: string, label: string) => {
          const bytes = await fsp.readFile(file);
          if (bytes.byteLength !== expectedBytes || checksumBytes(bytes) !== expectedChecksum) {
            throw new Error(`${label} temporary file failed validation`);
          }
        };
        if (metadata.packageManifest) {
          await Promise.all([
            verify(heightsTmp, metadata.packageManifest.elevationByteLength,
              metadata.packageManifest.elevationChecksum, 'Elevation'),
            coverGrid && metadata.coverMetadata
              ? verify(coverTmp, metadata.coverMetadata.byteLength, metadata.coverMetadata.checksum, 'Ground cover') : undefined,
            originalCoverGrid && metadata.originalCoverMetadata
              ? verify(originalCoverTmp, metadata.originalCoverMetadata.byteLength,
                metadata.originalCoverMetadata.checksum, 'Original WorldCover') : undefined,
            coverBoundarySegments && metadata.coverGeometryMetadata
              ? verify(coverGeometryTmp, metadata.coverGeometryMetadata.byteLength,
                metadata.coverGeometryMetadata.checksum, 'Cover geometry') : undefined,
            coverDisplayGeometry && metadata.coverDisplayMetadata
              ? verify(coverDisplayTmp, metadata.coverDisplayMetadata.byteLength,
                metadata.coverDisplayMetadata.checksum, 'Vector ground cover') : undefined,
            contourSegments && metadata.contourMetadata
              ? verify(contoursTmp, metadata.contourMetadata.byteLength,
                metadata.contourMetadata.checksum, 'Contours') : undefined,
            localImagery && metadata.localImageryMetadata
              ? verify(imageryTmp, metadata.localImageryMetadata.byteLength,
                metadata.localImageryMetadata.checksum, 'Local imagery') : undefined,
          ]);
        }
        JSON.parse(await fsp.readFile(metaTmp, 'utf-8'));

        // Metadata is the commit marker: binary payloads land first, metadata last.
        await fsp.rm(heightsPath, { force: true });
        await fsp.rename(heightsTmp, heightsPath);
        if (coverGrid) {
          await fsp.rm(coverPath, { force: true });
          await fsp.rename(coverTmp, coverPath);
        } else {
          await fsp.rm(coverPath, { force: true });
        }
        if (originalCoverGrid) {
          await fsp.rm(originalCoverPath, { force: true });
          await fsp.rename(originalCoverTmp, originalCoverPath);
        } else {
          await fsp.rm(originalCoverPath, { force: true });
        }
        if (coverBoundarySegments) {
          await fsp.rm(coverGeometryPath, { force: true });
          await fsp.rename(coverGeometryTmp, coverGeometryPath);
        } else {
          await fsp.rm(coverGeometryPath, { force: true });
        }
        if (coverDisplayGeometry) {
          await fsp.rm(coverDisplayPath, { force: true });
          await fsp.rename(coverDisplayTmp, coverDisplayPath);
        } else {
          await fsp.rm(coverDisplayPath, { force: true });
        }
        if (contourSegments) {
          await fsp.rm(contoursPath, { force: true });
          await fsp.rename(contoursTmp, contoursPath);
        } else {
          await fsp.rm(contoursPath, { force: true });
        }
        if (localImagery) {
          await fsp.rm(imageryPath, { force: true });
          await fsp.rename(imageryTmp, imageryPath);
        } else {
          await fsp.rm(imageryPath, { force: true });
        }
        await fsp.rm(metaPath, { force: true });
        await fsp.rename(metaTmp, metaPath);
      } finally {
        await Promise.allSettled([
          fsp.rm(metaTmp, { force: true }), fsp.rm(heightsTmp, { force: true }),
          fsp.rm(coverTmp, { force: true }), fsp.rm(originalCoverTmp, { force: true }),
          fsp.rm(coverGeometryTmp, { force: true }), fsp.rm(coverDisplayTmp, { force: true }),
          fsp.rm(contoursTmp, { force: true }), fsp.rm(imageryTmp, { force: true }),
        ]);
      }

      const index = readIndex().filter((s) => s.key !== req.record.key);
      index.push(toSummary(req.record));
      writeIndex(index);

      return { ok: true, key: req.record.key };
    } catch (e) {
      return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving terrain' };
    }
  });

  ipcMain.handle(
    TERRAIN_SAVE_COVER_CHANNEL,
    async (_event, req: TerrainCoverSaveRequest): Promise<TerrainCoverSaveResponse> => {
      if (!req || typeof req.key !== 'string' || req.key.length === 0) {
        return { ok: false, error: 'Invalid terrain key' };
      }
      const metaPath = safeFilePath(req.key, '.json');
      const coverPath = safeFilePath(req.key, '.cover.bin');
      const coverDisplayPath = safeFilePath(req.key, '.cover-display.bin');
      if (!metaPath || !coverPath || !coverDisplayPath) {
        return { ok: false, error: 'Invalid terrain key' };
      }

      const nonce = `${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
      const metaTmp = `${metaPath}.${nonce}.tmp`;
      const coverTmp = `${coverPath}.${nonce}.tmp`;
      const coverDisplayTmp = `${coverDisplayPath}.${nonce}.tmp`;

      try {
        if (
          !req.coverGrid
          || !req.coverMetadata
          || !req.coverDisplayGeometry
          || !req.coverDisplayMetadata
          || !req.packageManifest
        ) {
          throw new Error('Ground-cover update is incomplete');
        }
        if (req.packageManifest.terrainKey !== req.key) {
          throw new Error('Ground-cover manifest terrain key does not match');
        }
        if (!req.packageManifest.complete || !req.coverGrid.complete) {
          throw new Error('Ground-cover update is not complete');
        }
        if (!Number.isInteger(req.coverGrid.width) || !Number.isInteger(req.coverGrid.height)) {
          throw new Error('Ground-cover grid dimensions are invalid');
        }

        const coverValues = req.coverGrid.data instanceof Uint8Array
          ? req.coverGrid.data
          : Uint8Array.from(req.coverGrid.data);
        const displayValues = req.coverDisplayGeometry instanceof Float32Array
          ? req.coverDisplayGeometry
          : Float32Array.from(req.coverDisplayGeometry);
        const coverBytes = Buffer.from(coverValues.buffer, coverValues.byteOffset, coverValues.byteLength);
        const displayBytes = Buffer.from(displayValues.buffer, displayValues.byteOffset, displayValues.byteLength);
        if (coverValues.length !== req.coverGrid.width * req.coverGrid.height) {
          throw new Error('Ground-cover grid dimensions do not match its data');
        }
        if (
          req.coverMetadata.width !== req.coverGrid.width
          || req.coverMetadata.height !== req.coverGrid.height
          || req.coverMetadata.source !== req.coverGrid.source
          || req.coverMetadata.complete !== req.coverGrid.complete
          || req.coverMetadata.nodataCount !== req.coverGrid.nodataCount
        ) {
          throw new Error('Ground-cover metadata does not match its grid');
        }
        if (
          coverBytes.byteLength !== req.coverMetadata.byteLength
          || checksumBytes(coverBytes) !== req.coverMetadata.checksum
        ) {
          throw new Error('Ground-cover payload does not match its metadata');
        }
        if (
          displayBytes.byteLength !== req.coverDisplayMetadata.byteLength
          || checksumBytes(displayBytes) !== req.coverDisplayMetadata.checksum
        ) {
          throw new Error('Vector ground-cover payload does not match its metadata');
        }
        if (
          !req.packageManifest.cover
          || req.packageManifest.cover.byteLength !== req.coverMetadata.byteLength
          || req.packageManifest.cover.checksum !== req.coverMetadata.checksum
        ) {
          throw new Error('Ground-cover manifest does not match its metadata');
        }
        if (
          !req.packageManifest.coverDisplay
          || req.packageManifest.coverDisplay.byteLength !== req.coverDisplayMetadata.byteLength
          || req.packageManifest.coverDisplay.checksum !== req.coverDisplayMetadata.checksum
        ) {
          throw new Error('Vector ground-cover manifest does not match its metadata');
        }
        if (Number.isNaN(Date.parse(req.updatedAt))) {
          throw new Error('Ground-cover update timestamp is invalid');
        }

        const existing = JSON.parse(await fsp.readFile(metaPath, 'utf-8')) as TerrainRecord;
        if (existing.key !== req.key) {
          throw new Error('Stored terrain metadata key does not match');
        }
        if (!existing.packageManifest) {
          throw new Error('Stored terrain package manifest is missing');
        }

        // Only the two edited assets and their manifest entries are replaced.
        // Immutable package assets retain their existing checksums and paths.
        const packageManifest = {
          ...existing.packageManifest,
          schemaVersion: req.packageManifest.schemaVersion,
          complete: req.packageManifest.complete,
          cover: req.coverMetadata,
          coverDisplay: req.coverDisplayMetadata,
          assets: {
            ...existing.packageManifest.assets,
            cover: `${req.key}.cover.bin`,
            coverDisplay: `${req.key}.cover-display.bin`,
          },
          preparedAt: req.packageManifest.preparedAt,
        };
        const metadata: TerrainRecord = {
          ...existing,
          coverMetadata: req.coverMetadata,
          coverDisplayMetadata: req.coverDisplayMetadata,
          packageManifest,
          updatedAt: req.updatedAt,
        };

        await Promise.all([
          fsp.writeFile(coverTmp, coverBytes),
          fsp.writeFile(coverDisplayTmp, displayBytes),
        ]);

        // Re-read the temporary files rather than trusting the source buffers.
        // Metadata remains the package commit marker and is not replaced until
        // both new assets have passed their byte-length/checksum checks.
        const [writtenCover, writtenDisplay] = await Promise.all([
          fsp.readFile(coverTmp),
          fsp.readFile(coverDisplayTmp),
        ]);
        if (
          writtenCover.byteLength !== req.coverMetadata.byteLength
          || checksumBytes(writtenCover) !== req.coverMetadata.checksum
        ) {
          throw new Error('Ground-cover temporary file failed validation');
        }
        if (
          writtenDisplay.byteLength !== req.coverDisplayMetadata.byteLength
          || checksumBytes(writtenDisplay) !== req.coverDisplayMetadata.checksum
        ) {
          throw new Error('Vector ground-cover temporary file failed validation');
        }

        await fsp.writeFile(metaTmp, JSON.stringify(metadata), 'utf-8');
        JSON.parse(await fsp.readFile(metaTmp, 'utf-8'));

        await fsp.rename(coverTmp, coverPath);
        await fsp.rename(coverDisplayTmp, coverDisplayPath);
        await fsp.rename(metaTmp, metaPath);

        const index = readIndex().filter((summary) => summary.key !== req.key);
        index.push(toSummary(metadata));
        writeIndex(index);

        return { ok: true, key: req.key };
      } catch (e) {
        return {
          ok: false,
          error: e instanceof Error ? e.message : 'Unknown error saving terrain cover',
        };
      } finally {
        await Promise.allSettled([
          fsp.rm(metaTmp, { force: true }),
          fsp.rm(coverTmp, { force: true }),
          fsp.rm(coverDisplayTmp, { force: true }),
        ]);
      }
    },
  );

  ipcMain.handle(
    TERRAIN_SAVE_CONTEXT_CHANNEL,
    async (_event, req: TerrainMapContextSaveRequest): Promise<TerrainMapContextSaveResponse> => {
      if (!req || typeof req.key !== 'string' || !req.vectorFeatures ||
          Number.isNaN(Date.parse(req.updatedAt))) {
        return { ok: false, error: 'Map-context update is invalid' };
      }
      const collections = ['roads', 'waterLines', 'waterPolygons', 'landCover', 'peaks'] as const;
      if (!collections.every((key) => Array.isArray(req.vectorFeatures[key]))) {
        return { ok: false, error: 'Map-context collections are incomplete' };
      }
      const metaPath = safeFilePath(req.key, '.json');
      if (!metaPath) return { ok: false, error: 'Invalid terrain key' };
      const metaTmp = `${metaPath}.${process.pid}-${Date.now()}.tmp`;
      try {
        const existing = JSON.parse(await fsp.readFile(metaPath, 'utf-8')) as TerrainRecord;
        if (existing.key !== req.key) return { ok: false, error: 'Stored terrain key does not match' };
        const metadata: TerrainRecord = { ...existing,
          vectorFeatures: req.vectorFeatures, updatedAt: req.updatedAt };
        await fsp.writeFile(metaTmp, JSON.stringify(metadata), 'utf-8');
        JSON.parse(await fsp.readFile(metaTmp, 'utf-8'));
        await fsp.rm(metaPath, { force: true });
        await fsp.rename(metaTmp, metaPath);
        const index = readIndex().filter((summary) => summary.key !== req.key);
        index.push(toSummary(metadata));
        writeIndex(index);
        return { ok: true, key: req.key };
      } catch (error) {
        return { ok: false,
          error: error instanceof Error ? error.message : 'Unable to save map context' };
      } finally {
        await fsp.rm(metaTmp, { force: true });
      }
    },
  );

  ipcMain.handle(TERRAIN_LOAD_CHANNEL, async (_event, req: TerrainLoadRequest): Promise<TerrainLoadResponse> => {
    const metaPath = safeFilePath(req.key, '.json');
    const heightsPath = safeFilePath(req.key, '.heights.bin');
    const coverPath = safeFilePath(req.key, '.cover.bin');
    const originalCoverPath = safeFilePath(req.key, '.cover-original.bin');
    const coverGeometryPath = safeFilePath(req.key, '.cover-geometry.bin');
    const coverDisplayPath = safeFilePath(req.key, '.cover-display.bin');
    const contoursPath = safeFilePath(req.key, '.contours.bin');
    const imageryPath = safeFilePath(req.key, '.imagery.jpg');
    if (!metaPath || !heightsPath || !coverPath || !originalCoverPath || !coverGeometryPath || !coverDisplayPath || !contoursPath || !imageryPath) return null;
    try {
      const metadata = JSON.parse(await fsp.readFile(metaPath, 'utf-8'));

      // Back-compat: terrains saved before the binary split have
      // sampleHeights embedded directly in the metadata JSON and no
      // .heights.bin file — fall back to reading it from there.
      if (!await fileExists(heightsPath)) {
        return metadata as TerrainRecord;
      }

      const buf = await fsp.readFile(heightsPath);
      if (metadata.packageManifest && (buf.byteLength !== metadata.packageManifest.elevationByteLength || checksumBytes(buf) !== metadata.packageManifest.elevationChecksum)) return null;
      const floats = float32View(buf);
      let coverGrid;
      if (metadata.coverMetadata) {
        if (!await fileExists(coverPath)) return null;
        const cover = await fsp.readFile(coverPath);
        if (cover.byteLength !== metadata.coverMetadata.byteLength || checksumBytes(cover) !== metadata.coverMetadata.checksum) return null;
        coverGrid = { ...metadata.coverMetadata, data: uint8View(cover) };
        delete coverGrid.byteLength;
        delete coverGrid.checksum;
      }
      let contourSegments;
      let coverBoundarySegments;
      let coverDisplayGeometry;
      if (metadata.coverGeometryMetadata) {
        if (!await fileExists(coverGeometryPath)) return null;
        const geometryBuffer = await fsp.readFile(coverGeometryPath);
        if (geometryBuffer.byteLength !== metadata.coverGeometryMetadata.byteLength || checksumBytes(geometryBuffer) !== metadata.coverGeometryMetadata.checksum) return null;
        coverBoundarySegments = float32View(geometryBuffer);
      }
      let originalCoverGrid;
      if (metadata.originalCoverMetadata) {
        if (!await fileExists(originalCoverPath)) return null;
        const original = await fsp.readFile(originalCoverPath);
        if (original.byteLength !== metadata.originalCoverMetadata.byteLength || checksumBytes(original) !== metadata.originalCoverMetadata.checksum) return null;
        originalCoverGrid = { ...metadata.originalCoverMetadata, data: uint8View(original) };
        delete originalCoverGrid.byteLength;
        delete originalCoverGrid.checksum;
      }
      if (metadata.coverDisplayMetadata) {
        if (!await fileExists(coverDisplayPath)) return null;
        const displayBuffer = await fsp.readFile(coverDisplayPath);
        if (displayBuffer.byteLength !== metadata.coverDisplayMetadata.byteLength || checksumBytes(displayBuffer) !== metadata.coverDisplayMetadata.checksum) return null;
        coverDisplayGeometry = float32View(displayBuffer);
      }
      if (metadata.contourMetadata) {
        if (!await fileExists(contoursPath)) return null;
        const contourBuffer = await fsp.readFile(contoursPath);
        if (contourBuffer.byteLength !== metadata.contourMetadata.byteLength || checksumBytes(contourBuffer) !== metadata.contourMetadata.checksum) return null;
        contourSegments = float32View(contourBuffer);
      }
      let localImagery;
      if (metadata.localImageryMetadata) {
        if (!await fileExists(imageryPath)) return null;
        const imagery = await fsp.readFile(imageryPath);
        if (imagery.byteLength !== metadata.localImageryMetadata.byteLength || checksumBytes(imagery) !== metadata.localImageryMetadata.checksum) return null;
        localImagery = uint8View(imagery);
      }
      return { ...metadata, sampleHeights: floats, ...(coverGrid ? { coverGrid } : {}), ...(originalCoverGrid ? { originalCoverGrid } : {}), ...(coverBoundarySegments ? { coverBoundarySegments } : {}), ...(coverDisplayGeometry ? { coverDisplayGeometry } : {}), ...(contourSegments ? { contourSegments } : {}), ...(localImagery ? { localImagery } : {}) };
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
    const coverPath = safeFilePath(req.key, '.cover.bin');
    const originalCoverPath = safeFilePath(req.key, '.cover-original.bin');
    const coverGeometryPath = safeFilePath(req.key, '.cover-geometry.bin');
    const coverDisplayPath = safeFilePath(req.key, '.cover-display.bin');
    const contoursPath = safeFilePath(req.key, '.contours.bin');
    const imageryPath = safeFilePath(req.key, '.imagery.jpg');
    if (!metaPath || !heightsPath || !coverPath || !originalCoverPath || !coverGeometryPath || !coverDisplayPath || !contoursPath || !imageryPath) return { ok: false };
    try {
      fs.rmSync(metaPath, { force: true });
      fs.rmSync(heightsPath, { force: true });
      fs.rmSync(coverPath, { force: true });
      fs.rmSync(originalCoverPath, { force: true });
      fs.rmSync(coverGeometryPath, { force: true });
      fs.rmSync(coverDisplayPath, { force: true });
      fs.rmSync(contoursPath, { force: true });
      fs.rmSync(imageryPath, { force: true });
      writeIndex(readIndex().filter((s) => s.key !== req.key));
      return { ok: true };
    } catch {
      return { ok: false };
    }
  });
}
