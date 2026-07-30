import type { CoverDisplayStats } from '../coverDisplay';
import type { Polygon } from '../coverEdit';
import type { CoverGrid } from '../types';

export interface CoverEditRequest {
  grid: CoverGrid;
  polygons: Polygon[];
  deriveDisplay: boolean;
}

export type CoverEditResponse =
  | {
      ok: true;
      changed: number;
      gridData: Uint8Array;
      displayGeometry?: Float32Array;
      displayStats?: CoverDisplayStats;
    }
  | { ok: false; error: string };
