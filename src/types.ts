// Mountain Planner - TypeScript Type Definitions
import type { LatLonBounds } from './elevation';
// Type-only, so this doesn't create a runtime circular import even though
// vectorFeatures.ts imports feature types (RoadFeature etc.) from here.
import type { HydratedVectorFeatures } from './vectorFeatures';

export interface ClimateMonth {
  tempHigh: number; // Fahrenheit
  tempLow: number;  // Fahrenheit
  snowProbability: number; // 0.0 - 1.0
  avgWindSpeed: number; // km/h
}

export interface ClimateProfile {
  monthly: ClimateMonth[];
}

export type AreaSizeMeters = 2000 | 4000 | 8000;

// Real-world map features pulled from OpenStreetMap (via Overpass) for a
// terrain's exact ingest bounds. Geometry is stored raw as [lon, lat] pairs
// — the same "store raw, derive display form at hydrate time" split used
// for sampleHeights/displayHeights — so re-projecting to a different
// mapSize or fixing a projection bug never requires re-fetching data.
export type RoadClass = 'major' | 'minor' | 'path';
export type WaterLineClass = 'river' | 'stream';
export type LandCoverClass = 'forest' | 'grass' | 'rock' | 'scrub';

export interface RoadFeature {
  id: string;
  name?: string;
  roadClass: RoadClass;
  points: [number, number][]; // [lon, lat]
}

export interface WaterLineFeature {
  id: string;
  name?: string;
  waterClass: WaterLineClass;
  points: [number, number][]; // [lon, lat]
}

// Polygon rings: index 0 is the outer boundary, any further rings are holes
// (matches OSM multipolygon relation convention for lakes).
export interface WaterPolygonFeature {
  id: string;
  name?: string;
  rings: [number, number][][];
}

export interface LandCoverFeature {
  id: string;
  landCoverClass: LandCoverClass;
  rings: [number, number][][];
}

export interface PeakFeature {
  id: string;
  name: string;
  elevationMeters?: number;
  lon: number;
  lat: number;
}

export interface VectorFeatureSet {
  roads: RoadFeature[];
  waterLines: WaterLineFeature[];
  waterPolygons: WaterPolygonFeature[];
  landCover: LandCoverFeature[];
  peaks: PeakFeature[];
}

// Persisted shape — written to disk exactly as-is. Only the raw sampled grid
// is stored; the display grid is always recomputed on load (deterministic,
// cheap, and avoids multi-megabyte save files).
export interface TerrainRecord {
  schemaVersion: 2 | 3;
  key: string; // stable slug, see terrainStorageClient.ts
  mountainName: string;
  latitude: number; // center
  longitude: number; // center
  areaSizeMeters: AreaSizeMeters;
  // Exact lat/lon bounds used for this ingest — persisted (not recomputed
  // from center+areaSizeMeters) so vector features always reproject in
  // perfect alignment with the elevation grid they were fetched alongside.
  // Optional only because schemaVersion 2 records predate this field;
  // hydrateTerrainRecord falls back to recomputing it for those.
  bounds?: LatLonBounds;
  sampleGridSize: number; // fixed 64
  sampleHeights: number[]; // row-major, sampleGridSize^2, meters
  climate: ClimateProfile;
  // Absent on schemaVersion 2 records and on any record ingested before a
  // vector fetch succeeded — renderer treats missing as "no overlays".
  vectorFeatures?: VectorFeatureSet;
  sourceType: 'live' | 'preset' | 'preset-real';
  createdAt: string; // ISO
  updatedAt: string; // ISO
}

// Runtime shape used by the renderer — a TerrainRecord plus the
// bicubic-upscaled display grid built by terrainIngest.ts after download/load.
export interface TerrainDB extends TerrainRecord {
  displayGridSize: number; // fixed 512
  displayHeights: number[]; // bicubic-upscaled, row-major, displayGridSize^2
  widthMeters: number; // = areaSizeMeters
  heightMeters: number; // = areaSizeMeters
  bounds: LatLonBounds; // always resolved by hydrateTerrainRecord, unlike the optional field on TerrainRecord
  hydratedFeatures: HydratedVectorFeatures; // projected + tile-indexed vectorFeatures, ready for the renderer
}

// Lightweight listing entry (no height data) for the Content Manager.
export type TerrainSummary = Pick<
  TerrainRecord,
  'key' | 'mountainName' | 'latitude' | 'longitude' | 'areaSizeMeters' | 'sourceType' | 'createdAt' | 'updatedAt'
>;
