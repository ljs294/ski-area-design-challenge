import type { BuildingSiteAnalysisResult, BuildingSiteInput } from '../buildingSiteAnalysis';

/** Wire request for the cancellable building-site worker. */
export interface BuildingSiteRequest extends Omit<BuildingSiteInput,
  'heights' | 'baseElevationChecksum' | 'terrainRevision' | 'buildingGeometryKey'> {
  type?: 'analyze-building-site';
  id: number;
  heights: Float32Array;
  /** Revision of the terrain document used to produce this request. */
  terrainRevision: string | number;
  /** Elevation package checksum captured with the geometry. */
  elevationChecksum?: string;
  /** Stable authored building geometry/foundation identity. */
  geometryKey: string;
  /** Compatibility spelling used by terrain-grade requests. */
  baseElevationChecksum?: string;
}

export interface BuildingSiteIdentity {
  terrainRevision: string | number;
  elevationChecksum?: string;
  baseElevationChecksum?: string;
  geometryKey: string;
}

export type BuildingSiteResponse =
  | ({ id: number; ok: true; elevationChecksum: string } & Omit<BuildingSiteIdentity, 'elevationChecksum'> & { result: BuildingSiteAnalysisResult })
  | ({ id: number; ok: false; error: string } & Partial<BuildingSiteIdentity>);

export type BuildingSiteAnalysisRequest = BuildingSiteRequest;
export type BuildingSiteAnalysisResponse = BuildingSiteResponse;

/** Stable key used when a caller does not have a persisted building key yet. */
export function buildingSiteGeometryKey(
  center: [number, number], bearingDeg: number,
  dimensions: { lengthM: number; widthM: number; eaveHeightM?: number },
  foundationMode: 'flattened' | 'slope' = 'flattened',
): string {
  const value = JSON.stringify([center, bearingDeg, dimensions, foundationMode]);
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `building-site-${hash.toString(16).padStart(8, '0')}`;
}

export const buildingGeometryKey = buildingSiteGeometryKey;
