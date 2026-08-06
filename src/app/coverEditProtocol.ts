import type { CoverClearing } from '../coverEdit';
import type { CoverDisplayMetadata, CoverGrid, CoverMetadata } from '../types';

export interface CoverEditPayload {
  grid: CoverGrid;
  clearings: CoverClearing[];
  deriveDisplay: boolean;
}

export interface CoverEditRequest extends CoverEditPayload {
  id: number;
}

export type CoverEditEngineResult =
  | {
      ok: true;
      changed: number;
      gridData: Uint8Array;
      coverMetadata: CoverMetadata;
      displayGeometry?: Float32Array;
      displayMetadata?: CoverDisplayMetadata;
    }
  | { ok: false; error: string };

export type CoverEditResponse = CoverEditEngineResult & { id: number };
