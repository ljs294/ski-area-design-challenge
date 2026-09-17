import fs from 'node:fs';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import { describe, it } from 'vitest';
import type { TerrainRecord } from '../../src/types/terrain';
import { applyTerrainGradeToRecord } from '../../src/app/terrainGradeCommit';
import { checksumBytes, contourMetadataOf, float32Bytes } from '../../src/terrainPackage';
import { createLocalTerrainFrame } from '../src/terrainSurface';
import { createTerrainMeshPresentation } from '../src/terrainMeshPresentation';

function binary<T extends Float32Array | Uint8Array>(file: string,
  construct: (buffer: ArrayBuffer) => T): T | undefined {
  if (!fs.existsSync(file)) return undefined;
  const bytes = fs.readFileSync(file);
  const buffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  return construct(buffer);
}

function loadRecord(metadataPath: string): TerrainRecord {
  const metadata = JSON.parse(fs.readFileSync(metadataPath, 'utf8')) as TerrainRecord;
  const base = metadataPath.slice(0, -'.json'.length);
  metadata.sampleHeights = binary(`${base}.heights.bin`, (buffer) => new Float32Array(buffer)) ?? [];
  const cover = binary(`${base}.cover.bin`, (buffer) => new Uint8Array(buffer));
  if (cover && metadata.coverMetadata) {
    const { byteLength: _byteLength, checksum: _checksum, ...grid } = metadata.coverMetadata;
    metadata.coverGrid = { ...grid, data: cover };
  }
  const originalCover = binary(`${base}.cover-original.bin`, (buffer) => new Uint8Array(buffer));
  if (originalCover && metadata.originalCoverMetadata) {
    const { byteLength: _byteLength, checksum: _checksum, ...grid } = metadata.originalCoverMetadata;
    metadata.originalCoverGrid = { ...grid, data: originalCover };
  }
  metadata.coverBoundarySegments = binary(`${base}.cover-geometry.bin`,
    (buffer) => new Float32Array(buffer));
  metadata.coverDisplayGeometry = binary(`${base}.cover-display.bin`,
    (buffer) => new Float32Array(buffer));
  const contours = binary(`${base}.contours.bin`, (buffer) => new Float32Array(buffer));
  if (contours) metadata.contourSegments = contours;
  metadata.localImagery = binary(`${base}.imagery.jpg`, (buffer) => new Uint8Array(buffer));
  return metadata;
}

const metadataPath = process.env.THREE_TERRAIN_FIXTURE;

describe.skipIf(!metadataPath)('real-package terrain presentation measurement', () => {
it('records the complete CPU confirmation and activation path', async () => {
if (!metadataPath || path.extname(metadataPath).toLowerCase() !== '.json') {
  throw new Error('Set THREE_TERRAIN_FIXTURE to an absolute terrain metadata .json path.');
}
const source = loadRecord(path.resolve(metadataPath));
const center = Math.floor(source.sampleGridSize / 2) * source.sampleGridSize
  + Math.floor(source.sampleGridSize / 2);
const initialStart = performance.now();
const presentation = createTerrainMeshPresentation(source, createLocalTerrainFrame(source));
const initialPresentationMs = performance.now() - initialStart;
presentation.refineAt(source, 0, 0);
const samples: Array<{ workerInputCopyMs: number; recordConstructionAndValidationMs: number;
  authoritativeOwnershipMs: number; sparsePreparationMs: number; cpuActivationMs: number;
  longestPreparationSliceMs: number; confirmToCpuActivationMs: number }> = [];
let workerInput = new Float32Array(), edited = source;
let stage = presentation.prepare(source, [center]);
const verifiedHeights = Float32Array.from(source.sampleHeights);
verifiedHeights[center] = verifiedHeights[center]! + .25;
const contourSegments = source.contourSegments ?? [];
const contourGridSize = source.contourMetadata?.gridSize ?? 0;
const contourIntervalM = source.contourMetadata?.intervalM ?? 0;
const elevationChecksum = checksumBytes(float32Bytes(verifiedHeights));
const contourMetadata = contourMetadataOf(contourSegments, contourGridSize, contourIntervalM);
for (let iteration = 0; iteration < 20; iteration++) {
  const workerCopyStart = performance.now();
  workerInput = Float32Array.from(source.sampleHeights);
  const workerInputCopyMs = performance.now() - workerCopyStart;
  workerInput[center] = workerInput[center]! + .25;
  const recordStart = performance.now();
  edited = applyTerrainGradeToRecord(source, {
    patchIndices: Uint32Array.of(center), patchHeights: Float32Array.of(workerInput[center]!),
    gradedHeights: workerInput,
    contourSegments, contourGridSize, contourIntervalM,
    baseElevationChecksum: source.packageManifest?.elevationChecksum ?? '',
    elevationChecksum, contourMetadata,
  });
  const recordConstructionAndValidationMs = performance.now() - recordStart;
  const ownershipStart = performance.now();
  edited.sampleHeights.slice();
  edited.contourSegments?.slice();
  const authoritativeOwnershipMs = performance.now() - ownershipStart;
  const preparationStart = performance.now();
  stage = await presentation.prepareAsync(edited, [center]);
  const sparsePreparationMs = performance.now() - preparationStart;
  const activationStart = performance.now();
  presentation.activate(stage);
  const cpuActivationMs = performance.now() - activationStart;
  samples.push({ workerInputCopyMs, recordConstructionAndValidationMs, authoritativeOwnershipMs,
    sparsePreparationMs, cpuActivationMs,
    longestPreparationSliceMs: stage.longestPreparationSliceMs ?? sparsePreparationMs,
    confirmToCpuActivationMs: workerInputCopyMs
      + recordConstructionAndValidationMs + authoritativeOwnershipMs
      + sparsePreparationMs + cpuActivationMs });
}
const p95 = (key: keyof typeof samples[number]) => {
  const ordered = samples.map((sample) => sample[key]).sort((a, b) => a - b);
  return ordered[Math.ceil(ordered.length * .95) - 1]!;
};
const sourceBytes = source.sampleHeights.length * 4
  + (source.surround?.heights.length ?? 0) * 4 + (source.coverGrid?.data.length ?? 0);
const workerBytes = workerInput.byteLength;
const report = {
  fixture: { key: source.key, sampleGridSize: source.sampleGridSize,
    displayChunks: presentation.chunkCount },
  sampleCount: samples.length,
  millisecondsP95: { workerInputCopyMs: p95('workerInputCopyMs'),
    recordConstructionAndValidationMs: p95('recordConstructionAndValidationMs'),
    authoritativeOwnershipMs: p95('authoritativeOwnershipMs'),
    initialPresentationMs, sparsePreparationMs: p95('sparsePreparationMs'),
    longestPreparationSliceMs: p95('longestPreparationSliceMs'),
    cpuActivationMs: p95('cpuActivationMs'),
    confirmToCpuActivationMs: p95('confirmToCpuActivationMs') },
  presentation: { affectedChunks: stage.chunks.length, full: stage.full,
    stagedBytes: stage.uploadedBytes },
  estimatedPeakBytes: sourceBytes + workerBytes + edited.sampleHeights.length * 4 + stage.uploadedBytes,
  note: 'CPU preparation/activation only; a qualified physical-GPU run is required for upload and frame-tail acceptance.',
  scheduling: 'Sparse display preparation yields between affected chunks; reported preparation is wall time.',
};
console.log(JSON.stringify(report, null, 2));
presentation.dispose();
});
});
