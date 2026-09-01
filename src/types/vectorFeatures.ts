// Raw real-world features fetched from OpenStreetMap. Geometry remains in
// [longitude, latitude] order so rendering projections stay derived data.
export type RoadClass = 'major' | 'minor' | 'path';
export type RoadSurfaceClass = 'paved' | 'unpaved' | 'unknown';
export type WaterLineClass = 'river' | 'stream';
export type OsmLandCoverClass = 'forest' | 'grass' | 'rock' | 'scrub';

export interface RoadFeature {
  id: string;
  name?: string;
  roadClass: RoadClass;
  /** Normalized OSM metadata. Optional so existing terrain packages hydrate unchanged. */
  highway?: string;
  surfaceClass?: RoadSurfaceClass;
  sourceWidthM?: number;
  lanes?: number;
  lanesForward?: number;
  lanesBackward?: number;
  oneWay?: boolean;
  points: [number, number][];
}

export interface WaterLineFeature {
  id: string;
  name?: string;
  waterClass: WaterLineClass;
  /** OSM channel width, normalized to metres when the tag is usable. */
  widthM?: number;
  /** Parsed OSM channel width in metres, when the source supplied a usable width tag. */
  sourceWidthM?: number;
  points: [number, number][];
}

/** Index 0 is the outer ring; subsequent rings are holes. */
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

/** An OpenStreetMap building footprint. Index 0 is the outer ring. */
export interface BuildingFeature {
  id: string;
  name?: string;
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
  /** Optional so terrain packages prepared before building ingest still hydrate. */
  buildings?: BuildingFeature[];
}
