// Compatibility facade for aggregate UI and IPC consumers. Domain and model
// modules import directly from src/types/<domain>.ts.
export type { AnchorRef } from './types/anchors';
export type { ConstructionStatus } from './types/construction';
export type {
  CoverClassCode, CoverDisplayMetadata, CoverGeometryMetadata, CoverGrid,
  CoverGridData, CoverMetadata, LandCoverClass, OriginalCoverMetadata,
  SiteCoverGrid, TerrainCoverClass, TerrainCoverCode, TerrainCoverGrid,
  TerrainCoverProvenance, WorldCoverClassCode,
} from './types/cover';
export type { EarthworkEstimate } from './types/earthwork';
export type { GameSave, GameSaveSummary, SavedSiteBox } from './types/gameSave';
export type { LatLonBounds } from './types/geo';
export type {
  ChairSize, LiftClass, LiftStatus, SavedFixedGripLift, SavedLift, SavedLiftBase,
} from './types/lifts';
export type { RoadType, SavedRoad } from './types/roads';
export type { SavedSnowGrid, SnowGrid, SnowSurfaceCode } from './types/snow';
export type {
  SavedDam, SavedPond, SavedSnowgun, SavedSnowmakingNode, SnowgunVariantId,
  SnowmakingNodeKind, SnowmakingSourceRef,
} from './types/snowmaking';
export type {
  AreaSizeMeters, ClimateMonth, ClimateProfile, ContourMetadata,
  LocalImageryMetadata, SurroundElevation, TerrainPackageManifest,
  TerrainPackagePhase, TerrainPackageProgress, TerrainPackageValidation,
  TerrainRecord, TerrainSummary,
} from './types/terrain';
export type { SavedJunction, SavedNode, SavedPath, SavedTrailSegment } from './types/topology';
export type {
  SavedTrail, SavedTrailPart, TrailDifficulty, TrailStatus,
} from './types/trails';
export type {
  LandCoverFeature, OsmLandCoverClass, PeakFeature, RoadClass, RoadFeature,
  VectorFeatureSet, WaterLineClass, WaterLineFeature, WaterPolygonFeature,
} from './types/vectorFeatures';
