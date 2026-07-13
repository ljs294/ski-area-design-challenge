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

// Serialized [[west, south], [east, north]] site rectangle. Mirrors SiteBox in
// src/app/sitePicker.ts, redeclared here so the persisted save format does not
// depend on a renderer-only module (types.ts is imported by the main process).
export interface SavedSiteBox {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
  areaKm2: number;
}

// Ski lift classes. Only fixed-grip chairlifts exist today; the discriminator
// leaves room for detachables, gondolas, and surface lifts later.
export type LiftClass = 'fixed-grip';

// Carrier size for chairlifts: single through quad (the realistic fixed-grip range).
export type ChairSize = 1 | 2 | 3 | 4;

// Build state of a lift. 'planning' is a proposed line (rendered dashed);
// 'complete' is built (rendered solid). New lifts start in 'planning'.
export type LiftStatus = 'planning' | 'complete';

interface SavedLiftBase {
  id: string;
  name: string; // default "Lift N"
  liftClass: LiftClass;
  /** Exactly two [lng, lat] points: [bottom terminal, top terminal] once
   *  elevations are known (drawn order until then). Straight line only. */
  points: [[number, number], [number, number]];
  /** Sampled terminal elevations in meters, parallel to `points`. null means
   *  sampling failed (offline) — backfilled on next load. */
  endpointElevM: [number | null, number | null];
  lengthM: number; // slope length; horizontal-only when elevations unknown
  verticalM: number | null; // |top - bottom|; null while elevations unresolved
  capacityPph: number; // user-chosen hourly capacity (persons per hour)
  status: LiftStatus; // 'planning' (dashed) or 'complete' (solid)
  createdAt: string; // ISO
}

export interface SavedFixedGripLift extends SavedLiftBase {
  liftClass: 'fixed-grip';
  chairSize: ChairSize; // default 2 (double)
}

export type SavedLift = SavedFixedGripLift;

// A player's resort design. The first-class "game" unit, distinct from the raw
// TerrainRecord it will eventually reference for offline rendering. Camera +
// site are persisted so Load/Continue restores the exact view. `terrainKey`
// and `trails` are reserved for the offline-terrain + design/simulation
// layers that are not built yet — the map still streams tiles online for now.
export interface GameSave {
  schemaVersion: 1;
  key: string; // uuid
  name: string; // resort name
  mountainId?: string; // preset id if started from a curated mountain
  terrainKey?: string; // reserved: ref into TerrainRecord storage for offline render
  center: [number, number]; // [lng, lat]
  zoom: number;
  bearing: number;
  pitch: number;
  is3D: boolean;
  site: SavedSiteBox | null; // locked property box, if one was drawn
  lifts: SavedLift[]; // ski lifts drawn on the map
  trails: unknown[]; // reserved for future trail lines
  createdAt: string; // ISO
  updatedAt: string; // ISO
}

// Lightweight listing entry for the Load Game modal.
export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt'
>;
