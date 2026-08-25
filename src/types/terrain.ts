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
  sampleHeights: number[] | Float32Array;
  surround?: SurroundElevation;
  coverGrid?: CoverGrid;
  coverMetadata?: CoverMetadata;
  originalCoverGrid?: SiteCoverGrid;
  originalCoverMetadata?: OriginalCoverMetadata;
  coverBoundarySegments?: number[] | Float32Array;
  coverGeometryMetadata?: CoverGeometryMetadata;
  coverDisplayGeometry?: number[] | Float32Array;
  coverDisplayMetadata?: CoverDisplayMetadata;
  localImagery?: Uint8Array | number[];
  localImageryMetadata?: LocalImageryMetadata;
  contourSegments?: number[] | Float32Array;
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

/**
 * In-memory package shape used after storage hydration. Persisted packages may
 * still contain JSON number arrays; storage adapters normalize large binary
 * assets to typed arrays before handing them to rendering and workers.
 */
export interface RuntimeTerrainPackage extends Omit<
  TerrainRecord,
  | 'sampleHeights'
  | 'coverBoundarySegments'
  | 'coverDisplayGeometry'
  | 'contourSegments'
  | 'localImagery'
> {
  sampleHeights: Float32Array;
  coverBoundarySegments?: Float32Array;
  coverDisplayGeometry?: Float32Array;
  contourSegments?: Float32Array;
  localImagery?: Uint8Array;
}

export type TerrainAssetName =
  | 'elevation'
  | 'cover'
  | 'originalCover'
  | 'coverGeometry'
  | 'coverDisplay'
  | 'contours'
  | 'imagery';

export type TerrainAssetMask = ReadonlySet<TerrainAssetName>;

export type TerrainPackageMetadata = Omit<
  TerrainRecord,
  | 'sampleHeights'
  | 'coverGrid'
  | 'originalCoverGrid'
  | 'coverBoundarySegments'
  | 'coverDisplayGeometry'
  | 'contourSegments'
  | 'localImagery'
>;

export interface TerrainAssetRevision {
  terrainRevision: string;
  integrityReceipt?: string;
}

export interface TerrainAssetStore {
  listSummaries(): Promise<TerrainSummary[]>;
  loadMetadata(key: string): Promise<TerrainPackageMetadata | null>;
  loadAssets(key: string, assetMask: TerrainAssetMask): Promise<Partial<RuntimeTerrainPackage> | null>;
  commitAssets(
    key: string,
    expectedRevision: string,
    changedAssets: Partial<RuntimeTerrainPackage>,
  ): Promise<TerrainAssetRevision>;
}
