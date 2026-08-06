// Mountain Planner - TypeScript Type Definitions
import type { LatLonBounds } from './types/geo';
// Type-only for the same reason: skiNodes.ts imports TrailStatus from here.
import type { AnchorRef, SavedNode, SavedPath } from './skiNodes';
// Type-only, same reasoning as the skiNodes import above.
import type { SavedSnowmakingNode } from './snowmakingNodes';

/**
 * A coarse elevation grid covering the buffer area around a resort's high-res
 * core. Row-major, row 0 = north edge (same orientation as sampleHeights).
 * Cells USGS reports as nodata are stored as SURROUND_NODATA and treated as
 * "no data" (transparent) at render time.
 */
export interface SurroundElevation {
  bounds: LatLonBounds;
  width: number;
  height: number;
  heights: number[];
}

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
  data: CoverGridData; // row-major WorldCoverClassCode values; persisted as UInt8 binary
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
  /** OSM channel width, normalized to metres when the tag is usable. */
  widthM?: number;
  /** Parsed OSM channel width in metres, when the source supplied a usable width tag. */
  sourceWidthM?: number;
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
  schemaVersion: 2 | 3 | 4 | 5 | 6;
  key: string; // stable slug, see terrainStorageClient.ts
  mountainName: string;
  latitude: number; // center
  longitude: number; // center
  areaSizeMeters: AreaSizeMeters;
  // Exact lat/lon bounds used for this ingest — persisted (not recomputed
  // from center+areaSizeMeters) so vector features always reproject in
  // perfect alignment with the elevation grid they were fetched alongside.
  // Optional only because schemaVersion 2 records predate this field.
  bounds?: LatLonBounds;
  sampleGridSize: number; // actual square elevation raster dimension
  sampleHeights: number[]; // row-major, sampleGridSize^2, meters
  /**
   * Medium-resolution elevation ring covering the play box plus a fixed margin
   * (PERIMETER_MARGIN_M) on every side, so the 3D view shows real neighbouring
   * relief — hillshaded like the core, terminating in a clean floating-clip edge
   * — instead of a cliff at the property line. Elevation only: ground cover /
   * slope / aspect stay clamped to the box. Fully offline: fetched once at
   * capture time and rendered by the resort-dem protocol outside `bounds`. Sized
   * (see PERIMETER_GRID_SIZE) to embed directly in the record JSON as a few MB —
   * no binary sidecar. Optional: packages captured before this feature (and
   * presets without a bundled ring) simply render without a surround.
   */
  surround?: SurroundElevation;
  /** Present on schema-v4 prepared resort packages; stored as .cover.bin. */
  coverGrid?: CoverGrid;
  coverMetadata?: CoverMetadata;
  /** Unmodified ESA grid retained by schema-v6 packages for provenance/recovery. */
  originalCoverGrid?: SiteCoverGrid;
  originalCoverMetadata?: OriginalCoverMetadata;
  /** Flat [x1,y1,x2,y2,classCode] tuples in normalized site coordinates. */
  coverBoundarySegments?: number[];
  coverGeometryMetadata?: CoverGeometryMetadata;
  /** Flat normalized polygon stream; see coverDisplay.ts. Required by schema v5. */
  coverDisplayGeometry?: number[] | Float32Array;
  coverDisplayMetadata?: CoverDisplayMetadata;
  /** Optional matching NAIP JPEG; stored outside JSON as .imagery.jpg. */
  localImagery?: Uint8Array | number[];
  localImageryMetadata?: LocalImageryMetadata;
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
  /** Operating condition. Absent/false = open. A closed lift stays in the
   *  network graph but is not traversable by open-only route queries. */
  closed?: boolean;
  createdAt: string; // ISO
}

export interface SavedFixedGripLift extends SavedLiftBase {
  liftClass: 'fixed-grip';
  chairSize: ChairSize; // default 2 (double)
}

export type SavedLift = SavedFixedGripLift;

// Player-built road classes. The discriminator and explicit width keep the save
// format ready for additional road types without changing the v1 UI.
export type RoadType = 'two-lane';

export interface EarthworkEstimate {
  /** Gross material removed from the original DEM. */
  cutM3: number;
  /** Gross material added above the original DEM. */
  fillM3: number;
  /** Positive means excess cut; negative means additional fill is required. */
  balanceM3: number;
}

export interface SavedRoad {
  id: string;
  name: string;
  roadType: RoadType;
  widthM: number;
  /** Multi-point road centerline in drawing order. */
  points: [number, number][];
  /** Horizontal centerline length, recomputed during hydration. */
  lengthM: number;
  /** New roads are terrain-graded; absent/false identifies legacy roads. */
  terrainGraded?: boolean;
  /** Estimated volumes represented by the committed DEM edit. */
  earthwork?: EarthworkEstimate;
  createdAt: string;
}

/** A player-built dam and the full-pool snowmaking pond analyzed at build time. */
export interface SavedDam {
  id: string;
  name: string;
  /** Straight crest endpoints, both at crestElevationM. */
  points: [[number, number], [number, number]];
  crestElevationM: number;
  streamId: string;
  streamName: string;
  sourceWidthM: number;
  inflowM3s: number;
  /** Outer full-pool ring followed by optional holes. */
  pondRings: [number, number][][];
  areaM2: number;
  averageDepthM: number;
  capacityM3: number;
  /** Mean crest-to-ground structural height sampled along the dam alignment. */
  averageDamHeightM?: number;
  maxDamHeightM: number;
  /** Top of the embankment: full pool plus freeboard. Absent on dams built
   * before the game graded them, which were a line on the map. */
  damCrestElevationM?: number;
  /** Deck polygon along the built stretch of the alignment. */
  crestRing?: [number, number][];
  /** Graded embankment footprint: outer toe ring followed by optional holes. */
  footprintRings?: [number, number][][];
  /** Length of alignment standing above natural ground. */
  builtLengthM?: number;
  disturbedAreaM2?: number;
  earthwork?: EarthworkEstimate;
  /** True once the embankment has been cut into the terrain package. */
  terrainGraded?: boolean;
  createdAt: string;
}

/** A player-drawn standalone pond. It has no natural inflow. */
export interface SavedPond {
  id: string;
  name: string;
  /** Closed full-pool boundary in [longitude, latitude] order. */
  boundary: [number, number][];
  topElevationM: number;
  areaM2: number;
  averageDepthM: number;
  maxDepthM: number;
  capacityM3: number;
  /** Whether this pond's capacity is available to the resort snowmaking system. */
  isSnowmaking?: boolean;
  /** How far the floor was dug below full pool. Absent on schema-v9 ponds. */
  excavationDepthM?: number;
  /** Top of the berm holding the pool in: full pool plus freeboard. */
  crestElevationM?: number;
  /** Tallest point of the berm, natural ground to crest. */
  maxBermHeightM?: number;
  /** Shoreline length that needed a berm rather than a cut. */
  bermLengthM?: number;
  maxCutDepthM?: number;
  /** Ground area the cut and fill faces touched. */
  disturbedAreaM2?: number;
  /** True when construction permanently regraded the local elevation package. */
  terrainGraded?: boolean;
  /** Estimated volumes represented by the committed DEM edit. */
  earthwork?: EarthworkEstimate;
  createdAt: string;
}

// Ski-run difficulty designation. Mirrors the four slope-angle bands in
// terrainProtocols.ts (Green <16°, Blue <24°, Black <37°, Red ≥37°) — the same
// ratings the slope overlay paints — so a run's recommended grade always agrees
// with the terrain shading beneath it. See src/trails.ts.
export type TrailDifficulty = 'green' | 'blue' | 'black' | 'red';

// Build state of a run, mirroring LiftStatus: 'planning' (dashed) vs 'complete'.
export type TrailStatus = 'planning' | 'complete';

export interface SavedTrailPart {
  /** Outer ring followed by optional holes. */
  polygon: [number, number][][];
  /** Ordered, durable graph segments for this connected footprint. */
  segments?: SavedTrailSegment[];
  /**
   * Flattened centerline caches retained for rendering/profile code and legacy
   * save compatibility. `segments` is authoritative and hydration rebuilds
   * these arrays, so they cannot drift from the persisted topology.
   */
  centerline: [number, number][];
  centerlineElevM: number[];
}

export interface SavedTrailSegment {
  id: string;
  centerline: [number, number][];
  centerlineElevM: number[];
  fromJunctionId: string;
  toJunctionId: string;
}

// A ski run painted with the brush tool. The run is a filled polygon (its
// skiable footprint) with a centerline "spine" the profile + downhill
// simulation walk. Geometry is stored raw as [lng, lat]; cached stats
// (length/vertical/slope/difficulty) are recomputed from it on load by
// sanitizeTrails so they can never drift from the geometry.
export interface SavedTrail {
  id: string;
  name: string; // default "Run N"
  /** Every connected painted footprint component. */
  parts: SavedTrailPart[];
  brushWidthM: number; // brush diameter used to paint it
  areaM2: number;
  lengthM: number; // sum of all 3D centerline segments
  verticalM: number | null; // highest − lowest centerline elevation
  avgSlopeDeg: number;
  maxSlopeDeg: number;
  difficulty: TrailDifficulty; // automatically derived from the terrain
  /** True when construction permanently regraded the local elevation package. */
  terrainGraded?: boolean;
  /** Estimated volumes represented by the committed DEM edit. */
  earthwork?: EarthworkEstimate;
  status: TrailStatus; // 'planning' (dashed) or 'complete' (solid)
  /** Operating condition. Absent/false = open. Every network segment derived
   *  from this run inherits it. */
  closed?: boolean;
  /** Declared start connection. New runs pin station 0 to a lift terminal or
   * an existing trail centerline; legacy saves may contain another variant. */
  anchor?: AnchorRef;
  createdAt: string; // ISO
}

// A player's resort design. The first-class "game" unit, distinct from the raw
// TerrainRecord it will eventually reference for offline rendering. Camera +
// site are persisted so Load/Continue restores the exact view. `terrainKey`
// is reserved for the offline-terrain layer that is not built yet — the map
// still streams tiles online for now.
export interface GameSave {
  schemaVersion: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;
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
  /** Player-built roads. Optional only for legacy schema v1/v2 saves. */
  roads?: SavedRoad[];
  /** Player-built snowmaking dams. Optional for schema-v1-v7 saves. */
  dams?: SavedDam[];
  /** Player-drawn standalone ponds. Optional for schema-v1-v8 saves. */
  ponds?: SavedPond[];
  /** Free-standing map pins. Optional so legacy saves load unchanged. */
  nodes?: SavedNode[];
  /** Footpaths connecting nodes/lifts/runs. Optional so legacy saves load unchanged. */
  paths?: SavedPath[];
  /** Durable graph junctions. Optional only for schema-v1-v4 saves. */
  junctions?: import('./skiNodes').SavedJunction[];
  /** Snowmaking network nodes. Optional for schema-v1–v10 saves. */
  snowmakingNodes?: SavedSnowmakingNode[];
  /** Player-entered average lake depths in metres, keyed by the terrain's stable OSM water id. */
  lakeDepthOverrides?: Record<string, number>;
  /** Player-entered lake names keyed by the terrain's stable OSM water id. */
  lakeNameOverrides?: Record<string, string>;
  /** Player-entered channel widths in metres, keyed by stable OSM waterway id. */
  streamWidthOverrides?: Record<string, number>;
  createdAt: string; // ISO
  updatedAt: string; // ISO
  /** Most recent successful exit checkpoint. Optional for legacy saves. */
  lastPlayedAt?: string; // ISO
}

// Lightweight listing entry for the Load Game modal.
export type GameSaveSummary = Pick<
  GameSave,
  'key' | 'name' | 'mountainId' | 'terrainKey' | 'createdAt' | 'updatedAt' | 'lastPlayedAt'
>;
