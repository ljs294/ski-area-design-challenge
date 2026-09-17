import type { TerrainRecord } from '../types';
import type { DeepReadonly } from '../types/readonly';
import type { ContourMetadata } from '../types/terrain';
import { contourMetadataOf, manifestOf, manifestWithUpdatedElevation,
  validateTerrainElevationEdit, validateTerrainPackage } from '../terrainPackage';

export interface TerrainGradePatch {
  patchIndices: ArrayLike<number>;
  patchHeights: ArrayLike<number>;
  gradedHeights?: Float32Array;
  contourSegments: ArrayLike<number>;
  contourGridSize: number;
  contourIntervalM: number;
  baseElevationChecksum: string;
  elevationChecksum?: string;
  contourMetadata?: ContourMetadata;
}

/** Build and validate the exact terrain record represented by a grading
 * preview. Persistence remains the caller's responsibility. */
export function applyTerrainGradeToRecord(
  record: DeepReadonly<TerrainRecord>,
  result: TerrainGradePatch,
  updatedAt = new Date().toISOString()
): TerrainRecord {
  const activeChecksum = record.packageManifest?.elevationChecksum ?? '';
  if (!activeChecksum || result.baseElevationChecksum !== activeChecksum) {
    throw new Error('The terrain changed after this grading preview. Recalculate the grade and try again.');
  }
  if (result.patchIndices.length !== result.patchHeights.length) {
    throw new Error('The terrain grading patch is incomplete.');
  }

  const verifiedWorkerHeights = result.gradedHeights && result.elevationChecksum
    ? result.gradedHeights : null;
  if (verifiedWorkerHeights && verifiedWorkerHeights.length !== record.sampleHeights.length) {
    throw new Error('The terrain grading result has the wrong elevation dimensions.');
  }
  const sampleHeights = verifiedWorkerHeights ?? record.sampleHeights.slice();
  for (let i = 0; i < result.patchIndices.length; i++) {
    const index = result.patchIndices[i];
    const height = result.patchHeights[i];
    if (!Number.isInteger(index) || index < 0 || index >= sampleHeights.length ||
        !Number.isFinite(height)) {
      throw new Error('The terrain grading patch contains invalid elevation data.');
    }
    if (verifiedWorkerHeights) {
      if (Math.abs(sampleHeights[index]! - height) > 1e-5) {
        throw new Error('The terrain grading patch does not match its verified elevation grid.');
      }
    } else sampleHeights[index] = height;
  }

  const verifiedWorkerContours = !!result.elevationChecksum && !!result.contourMetadata;
  const contourSegments = verifiedWorkerContours
    ? (result.contourSegments instanceof Float32Array
      ? result.contourSegments : Float32Array.from(result.contourSegments))
    : Array.from(result.contourSegments);
  if (contourSegments.length % 5 !== 0
      || (!verifiedWorkerContours && !contourSegments.every(Number.isFinite))
      || (result.contourMetadata && (result.contourMetadata.segmentCount !== contourSegments.length / 5
        || result.contourMetadata.byteLength !== contourSegments.length * Float32Array.BYTES_PER_ELEMENT
        || result.contourMetadata.gridSize !== result.contourGridSize
        || result.contourMetadata.intervalM !== result.contourIntervalM))) {
    throw new Error('The terrain grading preview contains invalid contours.');
  }

  const contourMetadata = result.contourMetadata ?? contourMetadataOf(
    contourSegments,
    result.contourGridSize,
    result.contourIntervalM
  );
  let upgraded = {
    ...record,
    sampleHeights,
    contourSegments,
    contourMetadata,
    updatedAt,
  } as unknown as TerrainRecord;
  upgraded = { ...upgraded, packageManifest: result.elevationChecksum && result.contourMetadata
    ? manifestWithUpdatedElevation(upgraded, result.elevationChecksum, contourMetadata)
    : manifestOf(upgraded) };
  const validation = result.elevationChecksum && result.contourMetadata
    ? validateTerrainElevationEdit(upgraded)
    : validateTerrainPackage(upgraded);
  if (!validation.ok) throw new Error(validation.errors.join(' '));
  return upgraded;
}
