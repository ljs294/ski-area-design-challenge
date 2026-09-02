import type { SavedJunction } from './topology';
import type { SavedTrail, TrailDifficulty, TrailStatus } from './trails';

export const TRAIL_PRESENTATION_VERSION = 1 as const;

type LngLat = [number, number];

export interface TrailPresentationInput {
  trails: SavedTrail[];
  junctions: SavedJunction[];
}

export interface TrailPresentationRoute {
  featureId: string;
  trailId: string;
  name: string;
  label: string;
  difficulty: TrailDifficulty;
  status: TrailStatus;
  closed: boolean;
  coordinates: LngLat[];
}

export interface TrailPresentationLabel extends Omit<TrailPresentationRoute, 'coordinates'> {
  geometry: { type: 'LineString'; coordinates: LngLat[] } |
    { type: 'Point'; coordinates: LngLat };
}

export interface TrailJunctionResolution {
  junctionId: string;
  throughSegmentIds: [string, string] | null;
  yieldingSegmentIds: string[];
  clearanceM: number;
}

export interface TrailPresentationResult {
  version: typeof TRAIL_PRESENTATION_VERSION;
  surface: LngLat[][][];
  routes: TrailPresentationRoute[];
  labels: TrailPresentationLabel[];
  junctions: TrailJunctionResolution[];
}
