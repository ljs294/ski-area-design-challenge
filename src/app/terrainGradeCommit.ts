import type { TerrainRecord } from '../types';
import { contourMetadataOf, manifestOf, validateTerrainPackage } from '../terrainPackage';

export interface TerrainGradePatch {
  patchIndices: ArrayLike<number>;
  patchHeights: ArrayLike<number>;
  contourSegments: ArrayLike<number>;
  contourGridSize: number;
  contourIntervalM: number;
  baseElevationChecksum: string;
}

/** Build and validate the exact terrain record represented by a grading
 * preview. Persistence remains the caller's responsibility. */
export function applyTerrainGradeToRecord(
  record: TerrainRecord,
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

  const sampleHeights = record.sampleHeights.slice();
  for (let i = 0; i < result.patchIndices.length; i++) {
    const index = result.patchIndices[i];
    const height = result.patchHeights[i];
    if (!Number.isInteger(index) || index < 0 || index >= sampleHeights.length ||
        !Number.isFinite(height)) {
      throw new Error('The terrain grading patch contains invalid elevation data.');
    }
    sampleHeights[index] = height;
  }

  const contourSegments = Array.from(result.contourSegments);
  if (contourSegments.length % 5 !== 0 || !contourSegments.every(Number.isFinite)) {
    throw new Error('The terrain grading preview contains invalid contours.');
  }

  let upgraded: TerrainRecord = {
    ...record,
    sampleHeights,
    contourSegments,
    contourMetadata: contourMetadataOf(
      contourSegments,
      result.contourGridSize,
      result.contourIntervalM
    ),
    updatedAt,
  };
  upgraded = { ...upgraded, packageManifest: manifestOf(upgraded) };
  const validation = validateTerrainPackage(upgraded);
  if (!validation.ok) throw new Error(validation.errors.join(' '));
  return upgraded;
}
