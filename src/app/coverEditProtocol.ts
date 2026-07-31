import type { CoverClearing } from '../coverEdit';
import type { CoverDisplayMetadata, CoverGrid, CoverMetadata } from '../types';

export interface CoverEditRequest {
  grid: CoverGrid;
  clearings: CoverClearing[];
  deriveDisplay: boolean;
}

export type CoverEditResponse =
  | {
      ok: true;
      changed: number;
      gridData: Uint8Array;
      coverMetadata: CoverMetadata;
      displayGeometry?: Float32Array;
      displayMetadata?: CoverDisplayMetadata;
    }
  | { ok: false; error: string };
