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

export type AreaSizeMeters = number;

// Real-world map features pulled from OpenStreetMap (via Overpass) for a
// terrain's exact ingest bounds. Geometry is stored raw as [lon, lat] pairs
// — the same "store raw, derive display form at hydrate time" split used
// for sampleHeights/displayHeights — so re-projecting to a different
// mapSize or fixing a projection bug never requires re-fetching data.
export type RoadClass = 'major' | 'minor' | 'path';
export type WaterLineClass = 'river' | 'stream';
export type OsmLandCoverClass = 'forest' | 'grass' | 'rock' | 'scrub';
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

export interface SiteCoverGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  /** Approximate source-cell size at the site's center latitude. */
  cellSizeM: number;
  data: number[]; // row-major WorldCoverClassCode values; persisted as UInt8 binary
  complete: boolean;
  nodataCount: number;
  source: 'esa-worldcover-2021-v200';
  vintage: '2021';
}

export interface CoverMetadata extends Omit<SiteCoverGrid, 'data'> {
  byteLength: number;
  checksum: string;
}

export interface ContourMetadata {
  intervalM: number;
  segmentCount: number;
  byteLength: number;
  checksum: string;
  gridSize: number;
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

export type TerrainPackagePhase =
  | 'elevation'
  | 'ground-cover'
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
  schemaVersion: 1 | 2;
  terrainKey: string;
  complete: boolean;
  elevationByteLength: number;
  elevationChecksum: string;
  cover?: CoverMetadata;
  coverGeometry?: CoverGeometryMetadata;
  coverDisplay?: CoverDisplayMetadata;
  contours?: ContourMetadata;
  assets: {
    elevation: string;
    cover: string;
    coverGeometry: string;
    coverDisplay?: string;
    contours: string;
  };
  preparedAt: string;
}

export interface TerrainPackageValidation {
  ok: boolean;
  errors: string[];
}

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
  landCoverClass: OsmLandCoverClass;
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
  schemaVersion: 2 | 3 | 4 | 5;
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
  sampleGridSize: number; // actual square elevation raster dimension
  sampleHeights: number[]; // row-major, sampleGridSize^2, meters
  /** Present on schema-v4 prepared resort packages; stored as .cover.bin. */
  coverGrid?: SiteCoverGrid;
  coverMetadata?: CoverMetadata;
  /** Flat [x1,y1,x2,y2,classCode] tuples in normalized site coordinates. */
  coverBoundarySegments?: number[];
  coverGeometryMetadata?: CoverGeometryMetadata;
  /** Flat normalized polygon stream; see coverDisplay.ts. Required by schema v5. */
  coverDisplayGeometry?: number[];
  coverDisplayMetadata?: CoverDisplayMetadata;
  /** Flat [x1,y1,x2,y2,levelMeters] tuples in normalized site coordinates. */
  contourSegments?: number[];
  contourMetadata?: ContourMetadata;
  packageManifest?: TerrainPackageManifest;
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

// Carrier size for chairlifts: double through quad (the realistic fixed-grip range).
export type ChairSize = 2 | 3 | 4;

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
  status: LiftStatus; // 'planning' (dashed) or 'complete' (solid)
  createdAt: string; // ISO
}

export interface SavedFixedGripLift extends SavedLiftBase {
  liftClass: 'fixed-grip';
  chairSize: ChairSize; // default 2 (double)
}

export type SavedLift = SavedFixedGripLift;

// Ski-run difficulty designation. Mirrors the four slope-angle bands in
// terrainProtocols.ts (Green <16°, Blue <24°, Black <37°, Red ≥37°) — the same
// ratings the slope overlay paints — so a run's recommended grade always agrees
// with the terrain shading beneath it. See src/trails.ts.
export type TrailDifficulty = 'green' | 'blue' | 'black' | 'red';

// Build state of a run, mirroring LiftStatus: 'planning' (dashed) vs 'complete'.
export type TrailStatus = 'planning' | 'complete';

// A ski run painted with the brush tool. The run is a filled polygon (its
// skiable footprint) with a centerline "spine" the profile + downhill
// simulation walk. Geometry is stored raw as [lng, lat]; cached stats
// (length/vertical/slope/difficulty) are recomputed from it on load by
// sanitizeTrails so they can never drift from the geometry.
export interface SavedTrail {
  id: string;
  name: string; // default "Run N"
  /** Painted footprint. Ring 0 is the outer boundary; any others are holes.
   *  Each ring is a closed loop of [lng, lat] pairs. */
  polygon: [number, number][][];
  /** Centerline the brush was dragged along, [lng, lat], ordered top → bottom.
   *  Also the stations the elevation profile is sampled at. */
  spine: [number, number][];
  brushWidthM: number; // brush diameter used to paint it
  /** Terrain elevations sampled at each `spine` station, meters, parallel to
   *  `spine`. Empty when sampling failed offline (backfilled on next load). */
  spineElevM: number[];
  lengthM: number; // spine slope length (sum of 3D segment lengths)
  verticalM: number | null; // top − bottom along the spine; null if unresolved
  avgSlopeDeg: number; // run-length-weighted mean pitch along the spine
  maxSlopeDeg: number; // steepest segment pitch along the spine
  difficulty: TrailDifficulty; // user-chosen; defaults to the slope recommendation
  status: TrailStatus; // 'planning' (dashed) or 'complete' (solid)
  createdAt: string; // ISO
}

// A player's resort design. The first-class "game" unit, distinct from the raw
// TerrainRecord it will eventually reference for offline rendering. Camera +
// site are persisted so Load/Continue restores the exact view. `terrainKey`
// is reserved for the offline-terrain layer that is not built yet — the map
// still streams tiles online for now.
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
  trails: SavedTrail[]; // ski runs painted on the map
  createdAt: string; // ISO
  updatedAt: string; // ISO
}

// Lightweight listing entry for the Load Game modal.
export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt'
>;
