import type { LatLonBounds } from './geo';

export type LandCoverClass =
  | 'tree-cover'
  | 'shrubland'
  | 'grassland'
  | 'cropland'
  | 'built-up'
  | 'bare-sparse'
  | 'snow-ice'
  | 'permanent-water'
  | 'herbaceous-wetland'
  | 'mangroves'
  | 'moss-lichen'
  | 'nodata';

/** Native ESA WorldCover class codes. 255 is reserved for missing/unknown data. */
export type WorldCoverClassCode = 10 | 20 | 30 | 40 | 50 | 60 | 70 | 80 | 90 | 95 | 100 | 255;
export type TerrainCoverClass = 'forest' | 'alpine' | 'grassland' | 'water';
export type TerrainCoverCode = 1 | 2 | 3 | 4 | 255;
export type CoverClassCode = WorldCoverClassCode | TerrainCoverCode;
export type CoverGridData = number[] | Uint8Array;

export interface SiteCoverGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  /** Approximate source-cell size at the site's center latitude. */
  cellSizeM: number;
  data: CoverGridData;
  complete: boolean;
  nodataCount: number;
  source: 'esa-worldcover-2021-v200';
  vintage: '2021';
}

export interface TerrainCoverProvenance {
  processingVersion: 'four-class-v1';
  confidence: 'high' | 'reduced';
  method: 'naip-worldcover' | 'worldcover-fallback';
  attribution: string[];
  naip?: {
    sceneIds: number[];
    sceneNames: string[];
    acquisitionYear: number;
    agency: 'USDA' | 'USGS';
    resolutionM: number;
    license: 'us-government-public-domain';
  };
  worldCover: {
    vintage: '2021';
    license: 'cc-by-4.0';
  };
}

export interface TerrainCoverGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  cellSizeM: number;
  data: CoverGridData;
  complete: boolean;
  nodataCount: number;
  source: 'usgs-four-class-v1';
  vintage: string;
  treelineM: { north: number; east: number; south: number; west: number; site: number };
  provenance: TerrainCoverProvenance;
}

export type CoverGrid = SiteCoverGrid | TerrainCoverGrid;

export type CoverMetadata = (
  | Omit<SiteCoverGrid, 'data'>
  | Omit<TerrainCoverGrid, 'data'>
) & { byteLength: number; checksum: string };

export interface OriginalCoverMetadata extends Omit<SiteCoverGrid, 'data'> {
  byteLength: number;
  checksum: string;
}

/** Exact cell-edge boundaries for canopy/shrub cover, stored as Float32 tuples. */
export interface CoverGeometryMetadata {
  segmentCount: number;
  byteLength: number;
  checksum: string;
}

/** Persisted, generalized display polygons encoded as normalized Float32 data. */
export interface CoverDisplayMetadata {
  polygonCount: number;
  ringCount: number;
  vertexCount: number;
  byteLength: number;
  checksum: string;
  smoothingM: number;
  simplifyM: number;
  minFeatureM2: number;
}
