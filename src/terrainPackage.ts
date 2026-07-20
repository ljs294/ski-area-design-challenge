import type {
  CoverMetadata,
  CoverGeometryMetadata,
  SiteCoverGrid,
  TerrainPackageManifest,
  TerrainPackageValidation,
  TerrainRecord,
} from './types';

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

export function coverBytes(grid: SiteCoverGrid): Uint8Array {
  return Uint8Array.from(grid.data);
}

export function coverMetadataOf(grid: SiteCoverGrid): CoverMetadata {
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

export function manifestOf(record: TerrainRecord): TerrainPackageManifest {
  const heightBytes = float32Bytes(record.sampleHeights);
  return {
    schemaVersion: 1,
    terrainKey: record.key,
    complete: !!record.coverGrid?.complete,
    elevationByteLength: heightBytes.byteLength,
    elevationChecksum: checksumBytes(heightBytes),
    cover: record.coverGrid ? coverMetadataOf(record.coverGrid) : record.coverMetadata,
    coverGeometry: record.coverBoundarySegments
      ? coverGeometryMetadataOf(record.coverBoundarySegments)
      : record.coverGeometryMetadata,
    contours: record.contourSegments
      ? contourMetadataOf(record.contourSegments, record.contourMetadata?.gridSize ?? 512, record.contourMetadata?.intervalM ?? 6.096)
      : record.contourMetadata,
    assets: {
      elevation: `${record.key}.heights.bin`,
      cover: `${record.key}.cover.bin`,
      coverGeometry: `${record.key}.cover-geometry.bin`,
      contours: `${record.key}.contours.bin`,
    },
    preparedAt: new Date().toISOString(),
  };
}

export function validateTerrainPackage(record: TerrainRecord): TerrainPackageValidation {
  const errors: string[] = [];
  const manifest = record.packageManifest;
  if (!manifest) errors.push('Package manifest is missing.');
  if (!record.coverGrid) errors.push('Ground-cover grid is missing.');
  if (!record.coverBoundarySegments || !record.coverGeometryMetadata) errors.push('Prepared cover boundaries are missing.');
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
  if (manifest) {
    const heightBytes = float32Bytes(record.sampleHeights);
    if (heightBytes.byteLength !== manifest.elevationByteLength) errors.push('Elevation byte length does not match.');
    if (checksumBytes(heightBytes) !== manifest.elevationChecksum) errors.push('Elevation checksum does not match.');
    if (!manifest.complete) errors.push('Package was not marked complete.');
  }
  return { ok: errors.length === 0, errors };
}
