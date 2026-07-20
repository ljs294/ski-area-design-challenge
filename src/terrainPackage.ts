import type {
  CoverMetadata,
  CoverGeometryMetadata,
  CoverDisplayMetadata,
  CoverGrid,
  LocalImageryMetadata,
  OriginalCoverMetadata,
  SiteCoverGrid,
  TerrainPackageManifest,
  TerrainPackageValidation,
  TerrainRecord,
} from './types';
import { COVER_DISPLAY_VERTEX_BUDGET, inspectCoverDisplayGeometry, type CoverDisplayStats } from './coverDisplay';

/** Small deterministic checksum used to detect truncated/corrupt package files. */
export function checksumBytes(bytes: ArrayLike<number>): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) {
    hash ^= bytes[i] & 0xff;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a32-${hash.toString(16).padStart(8, '0')}`;
}

export function float32Bytes(values: ArrayLike<number>): Uint8Array {
  const floats = Float32Array.from(values);
  return new Uint8Array(floats.buffer, floats.byteOffset, floats.byteLength);
}

export function coverBytes(grid: CoverGrid): Uint8Array {
  return Uint8Array.from(grid.data);
}

export function coverMetadataOf(grid: CoverGrid): CoverMetadata {
  const bytes = coverBytes(grid);
  const { data: _data, ...base } = grid;
  return { ...base, byteLength: bytes.byteLength, checksum: checksumBytes(bytes) };
}

export function contourMetadataOf(segments: number[], gridSize: number, intervalM: number) {
  const bytes = float32Bytes(segments);
  return { intervalM, segmentCount: Math.floor(segments.length / 5), byteLength: bytes.byteLength, checksum: checksumBytes(bytes), gridSize };
}

export function coverGeometryMetadataOf(segments: number[]): CoverGeometryMetadata {
  const bytes = float32Bytes(segments);
  return { segmentCount: Math.floor(segments.length / 5), byteLength: bytes.byteLength, checksum: checksumBytes(bytes) };
}

export function originalCoverMetadataOf(grid: SiteCoverGrid): OriginalCoverMetadata {
  return coverMetadataOf(grid) as OriginalCoverMetadata;
}

export function imageryMetadataOf(
  bytes: ArrayLike<number>,
  base: Omit<LocalImageryMetadata, 'byteLength' | 'checksum'>
): LocalImageryMetadata {
  const value = Uint8Array.from(bytes);
  return { ...base, byteLength: value.byteLength, checksum: checksumBytes(value) };
}

export function coverDisplayMetadataOf(
  geometry: number[],
  stats: CoverDisplayStats
): CoverDisplayMetadata {
  const bytes = float32Bytes(geometry);
  return { ...stats, byteLength: bytes.byteLength, checksum: checksumBytes(bytes) };
}

export function manifestOf(record: TerrainRecord): TerrainPackageManifest {
  const heightBytes = float32Bytes(record.sampleHeights);
  return {
    schemaVersion: record.schemaVersion >= 6 ? 3 : record.coverDisplayGeometry ? 2 : 1,
    terrainKey: record.key,
    complete: !!record.coverGrid?.complete,
    elevationByteLength: heightBytes.byteLength,
    elevationChecksum: checksumBytes(heightBytes),
    cover: record.coverGrid ? coverMetadataOf(record.coverGrid) : record.coverMetadata,
    originalCover: record.originalCoverGrid ? originalCoverMetadataOf(record.originalCoverGrid) : record.originalCoverMetadata,
    coverGeometry: record.coverBoundarySegments
      ? coverGeometryMetadataOf(record.coverBoundarySegments)
      : record.coverGeometryMetadata,
    coverDisplay: record.coverDisplayGeometry && record.coverDisplayMetadata
      ? coverDisplayMetadataOf(record.coverDisplayGeometry, record.coverDisplayMetadata)
      : record.coverDisplayMetadata,
    contours: record.contourSegments
      ? contourMetadataOf(record.contourSegments, record.contourMetadata?.gridSize ?? 512, record.contourMetadata?.intervalM ?? 6.096)
      : record.contourMetadata,
    imagery: record.localImagery && record.localImageryMetadata
      ? imageryMetadataOf(record.localImagery, record.localImageryMetadata)
      : record.localImageryMetadata,
    assets: {
      elevation: `${record.key}.heights.bin`,
      cover: `${record.key}.cover.bin`,
      originalCover: record.originalCoverGrid || record.originalCoverMetadata ? `${record.key}.cover-original.bin` : undefined,
      coverGeometry: `${record.key}.cover-geometry.bin`,
      coverDisplay: `${record.key}.cover-display.bin`,
      contours: `${record.key}.contours.bin`,
      imagery: record.localImagery || record.localImageryMetadata ? `${record.key}.imagery.jpg` : undefined,
    },
    preparedAt: new Date().toISOString(),
  };
}

type BoundsLike = { west: number; south: number; east: number; north: number };

/** Largest single-edge difference (degrees) between two extents; 0 = identical. */
export function boundsOffsetDegrees(a: BoundsLike, b: BoundsLike): number {
  return Math.max(
    Math.abs(a.west - b.west),
    Math.abs(a.east - b.east),
    Math.abs(a.south - b.south),
    Math.abs(a.north - b.north),
  );
}

// Sub-pixel float noise is fine; anything larger means two layers were pinned
// to different footprints (~0.11 m at this threshold). The exportImage
// extent-snap bug offset the elevation from the cover by ~0.008° (~900 m).
const MAX_LAYER_OFFSET_DEG = 1e-6;

export function validateTerrainPackage(record: TerrainRecord): TerrainPackageValidation {
  const errors: string[] = [];
  const manifest = record.packageManifest;
  if (!manifest) errors.push('Package manifest is missing.');
  if (!record.coverGrid) errors.push('Ground-cover grid is missing.');
  // Alignment guard — runs on every download. Elevation, contours, and cover
  // are all rendered through record.bounds / coverGrid.bounds; if those two
  // extents diverge the terrain, topo, and ground cover slide apart on the map.
  if (record.bounds && record.coverGrid) {
    const offset = boundsOffsetDegrees(record.bounds, record.coverGrid.bounds);
    if (offset > MAX_LAYER_OFFSET_DEG) {
      errors.push(`Ground-cover extent is offset from the terrain extent by ${offset.toExponential(2)}° — map layers would be misaligned.`);
    }
  }
  if (!record.coverBoundarySegments || !record.coverGeometryMetadata) errors.push('Prepared cover boundaries are missing.');
  if (record.schemaVersion >= 5 && (!record.coverDisplayGeometry || !record.coverDisplayMetadata)) {
    errors.push('Prepared vector ground cover is missing.');
  }
  if (record.schemaVersion >= 5 && (!manifest?.coverDisplay || !manifest.assets.coverDisplay)) {
    errors.push('Vector ground-cover manifest asset is missing.');
  }
  if (record.schemaVersion >= 6 && (!record.originalCoverGrid || !record.originalCoverMetadata)) {
    errors.push('Original WorldCover recovery grid is missing.');
  }
  if (record.schemaVersion >= 6 && (!manifest?.originalCover || !manifest.assets.originalCover)) {
    errors.push('Original WorldCover manifest asset is missing.');
  }
  if (record.schemaVersion >= 6 && record.coverGrid?.source !== 'usgs-four-class-v1') {
    errors.push('Schema-v6 terrain cover is not the four-class product.');
  }
  if (!record.contourSegments || !record.contourMetadata) errors.push('Prepared contours are missing.');
  if (record.coverGrid) {
    const expected = record.coverGrid.width * record.coverGrid.height;
    if (record.coverGrid.data.length !== expected) errors.push('Ground-cover grid dimensions do not match its data.');
    if (!record.coverGrid.complete || record.coverGrid.nodataCount > 0) errors.push('Ground-cover data is incomplete.');
    const metadata = coverMetadataOf(record.coverGrid);
    if (manifest?.cover && metadata.checksum !== manifest.cover.checksum) errors.push('Ground-cover checksum does not match.');
  }
  if (record.contourSegments && record.contourMetadata) {
    const metadata = contourMetadataOf(record.contourSegments, record.contourMetadata.gridSize, record.contourMetadata.intervalM);
    if (metadata.byteLength !== record.contourMetadata.byteLength || metadata.checksum !== record.contourMetadata.checksum) errors.push('Contour cache checksum does not match.');
    if (manifest?.contours && metadata.checksum !== manifest.contours.checksum) errors.push('Contour manifest checksum does not match.');
  }
  if (record.coverBoundarySegments && record.coverGeometryMetadata) {
    const metadata = coverGeometryMetadataOf(record.coverBoundarySegments);
    if (metadata.byteLength !== record.coverGeometryMetadata.byteLength || metadata.checksum !== record.coverGeometryMetadata.checksum) errors.push('Cover-boundary cache checksum does not match.');
    if (manifest?.coverGeometry && metadata.checksum !== manifest.coverGeometry.checksum) errors.push('Cover-boundary manifest checksum does not match.');
  }
  if (record.originalCoverGrid) {
    const expected = record.originalCoverGrid.width * record.originalCoverGrid.height;
    if (record.originalCoverGrid.data.length !== expected) errors.push('Original WorldCover dimensions do not match its data.');
    const metadata = originalCoverMetadataOf(record.originalCoverGrid);
    if (record.originalCoverMetadata && (metadata.byteLength !== record.originalCoverMetadata.byteLength || metadata.checksum !== record.originalCoverMetadata.checksum)) errors.push('Original WorldCover cache checksum does not match.');
    if (manifest?.originalCover && metadata.checksum !== manifest.originalCover.checksum) errors.push('Original WorldCover manifest checksum does not match.');
  }
  if (record.localImagery && record.localImageryMetadata) {
    const metadata = imageryMetadataOf(record.localImagery, record.localImageryMetadata);
    if (metadata.byteLength !== record.localImageryMetadata.byteLength || metadata.checksum !== record.localImageryMetadata.checksum) errors.push('Local imagery checksum does not match.');
    if (manifest?.imagery && metadata.checksum !== manifest.imagery.checksum) errors.push('Local imagery manifest checksum does not match.');
  }
  if (record.coverDisplayGeometry && record.coverDisplayMetadata) {
    const metadata = coverDisplayMetadataOf(record.coverDisplayGeometry, record.coverDisplayMetadata);
    if (metadata.byteLength !== record.coverDisplayMetadata.byteLength || metadata.checksum !== record.coverDisplayMetadata.checksum) errors.push('Vector ground-cover cache checksum does not match.');
    if (manifest?.coverDisplay && metadata.checksum !== manifest.coverDisplay.checksum) errors.push('Vector ground-cover manifest checksum does not match.');
    try {
      const inspected = inspectCoverDisplayGeometry(record.coverDisplayGeometry);
      if (inspected.polygonCount !== metadata.polygonCount || inspected.ringCount !== metadata.ringCount || inspected.vertexCount !== metadata.vertexCount) errors.push('Vector ground-cover counts do not match its geometry.');
      if (inspected.vertexCount > COVER_DISPLAY_VERTEX_BUDGET) errors.push('Vector ground-cover vertex budget was exceeded.');
    } catch {
      errors.push('Vector ground-cover geometry is malformed.');
    }
  }
  if (manifest) {
    const heightBytes = float32Bytes(record.sampleHeights);
    if (heightBytes.byteLength !== manifest.elevationByteLength) errors.push('Elevation byte length does not match.');
    if (checksumBytes(heightBytes) !== manifest.elevationChecksum) errors.push('Elevation checksum does not match.');
    if (!manifest.complete) errors.push('Package was not marked complete.');
  }
  return { ok: errors.length === 0, errors };
}
