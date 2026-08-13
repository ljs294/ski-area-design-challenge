import type { LatLonBounds } from './geo';

export type SnowSurfaceCode =
  | 'P'
  | 'PP'
  | 'MG'
  | 'HP'
  | 'IS'
  | 'CO'
  | 'FG'
  | 'LG'
  | 'SC'
  | 'WG'
  | 'WP';

/** Compact, versioned snow state stored with one resort design. */
export interface SavedSnowGrid {
  version: 1;
  bounds: LatLonBounds;
  width: number;
  height: number;
  /** Little-endian packed cells, encoded as base64. */
  cells: string;
}

/** In-memory simulation grid. Surface value 0 means no snow. */
export interface SnowGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  depthM: Float32Array;
  surface: Uint8Array;
}
