import { deriveCoverDisplayGeometry } from '../coverDisplay';
import { stampClearingsIntoGrid } from '../coverEdit';
import { coverDisplayMetadataOf, coverMetadataOf } from '../terrainPackage';
import type { CoverEditRequest, CoverEditResponse } from './coverEditProtocol';

/** Pure worker body, exported separately so cover processing is unit-testable. */
export function processCoverEdit(request: CoverEditRequest): CoverEditResponse {
  try {
    // The request grid is a renderer-created private copy whose buffer has been
    // transferred to this one-shot worker. Mutating it avoids two additional
    // full-grid copies while preserving the immutable public stamping helpers.
    const stamped = stampClearingsIntoGrid(
      request.grid,
      request.clearings,
      { inPlace: true }
    );
    const coverMetadata = coverMetadataOf(stamped.grid);
    if (stamped.changed === 0) {
      return {
        ok: true,
        changed: 0,
        gridData: stamped.grid.data as Uint8Array,
        coverMetadata,
      };
    }
    const display = request.deriveDisplay
      ? deriveCoverDisplayGeometry(stamped.grid)
      : undefined;
    const displayGeometry = display
      ? Float32Array.from(display.geometry)
      : undefined;
    return {
      ok: true,
      changed: stamped.changed,
      gridData: stamped.grid.data as Uint8Array,
      coverMetadata,
      displayGeometry,
      displayMetadata: display && displayGeometry
        ? coverDisplayMetadataOf(displayGeometry, display.stats)
        : undefined,
    };
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : 'Unable to edit ground cover.',
    };
  }
}
