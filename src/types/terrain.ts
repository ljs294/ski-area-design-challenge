import type {
  CoverDisplayMetadata,
  CoverGeometryMetadata,
  CoverGrid,
  CoverMetadata,
  OriginalCoverMetadata,
  SiteCoverGrid,
} from './cover';
import type { LatLonBounds } from './geo';
import type { VectorFeatureSet } from './vectorFeatures';

export interface SurroundElevation {
  bounds: LatLonBounds;
  width: number;
  height: number;
  heights: number[];
}

export interface ClimateMonth {
  tempHigh: number;
  tempLow: number;
  snowProbability: number;
  avgWindSpeed: number;
}

export interface ClimateProfile {
  monthly: ClimateMonth[];
}

export type AreaSizeMeters = number;

export interface LocalImageryMetadata {
  bounds: LatLonBounds;
  width: number;
  height: number;
  mimeType: 'image/jpeg';
  byteLength: number;
  checksum: string;
  acquisitionYear: number;
  sceneIds: number[];
  attribution: string;
}

export interface ContourMetadata {
  intervalM: number;
  segmentCount: number;
  byteLength: number;
  checksum: string;
  gridSize: number;
}

export type TerrainPackagePhase =
  | 'elevation'
  | 'ground-cover'
  | 'imagery'
  | 'decoding'
  | 'deriving'
  | 'vectorizing-cover'
  | 'saving'
  | 'verifying';

export interface TerrainPackageProgress {
  phase: TerrainPackagePhase;
  message: string;
  completed: number;
  total: number;
}

export interface TerrainPackageManifest {
  schemaVersion: 1 | 2 | 3;
  terrainKey: string;
  complete: boolean;
  elevationByteLength: number;
  elevationChecksum: string;
  cover?: CoverMetadata;
  originalCover?: OriginalCoverMetadata;
  coverGeometry?: CoverGeometryMetadata;
  coverDisplay?: CoverDisplayMetadata;
  contours?: ContourMetadata;
  imagery?: LocalImageryMetadata;
  assets: {
    elevation: string;
    cover: string;
    originalCover?: string;
    coverGeometry: string;
    coverDisplay?: string;
    contours: string;
    imagery?: string;
  };
  preparedAt: string;
}

export interface TerrainPackageValidation {
  ok: boolean;
  errors: string[];
}

/** Persisted terrain package shape. Derived display data is rebuilt on load. */
export interface TerrainRecord {
  schemaVersion: 2 | 3 | 4 | 5 | 6;
  key: string;
  mountainName: string;
  latitude: number;
  longitude: number;
  areaSizeMeters: AreaSizeMeters;
  bounds?: LatLonBounds;
  sampleGridSize: number;
  sampleHeights: number[];
  surround?: SurroundElevation;
  coverGrid?: CoverGrid;
  coverMetadata?: CoverMetadata;
  originalCoverGrid?: SiteCoverGrid;
  originalCoverMetadata?: OriginalCoverMetadata;
  coverBoundarySegments?: number[];
  coverGeometryMetadata?: CoverGeometryMetadata;
  coverDisplayGeometry?: number[] | Float32Array;
  coverDisplayMetadata?: CoverDisplayMetadata;
  localImagery?: Uint8Array | number[];
  localImageryMetadata?: LocalImageryMetadata;
  contourSegments?: number[];
  contourMetadata?: ContourMetadata;
  packageManifest?: TerrainPackageManifest;
  climate: ClimateProfile;
  vectorFeatures?: VectorFeatureSet;
  sourceType: 'live' | 'preset' | 'preset-real';
  createdAt: string;
  updatedAt: string;
}

export type TerrainSummary = Pick<
  TerrainRecord,
  'key' | 'mountainName' | 'latitude' | 'longitude' | 'areaSizeMeters' | 'sourceType' | 'createdAt' | 'updatedAt'
>;
