import { deriveCoverDisplayGeometry } from '../coverDisplay';
import { stampPolygonsIntoGrid } from '../coverEdit';
import type { CoverEditRequest, CoverEditResponse } from './coverEditProtocol';

/** Pure worker body, exported separately so cover processing is unit-testable. */
export function processCoverEdit(request: CoverEditRequest): CoverEditResponse {
  try {
    const stamped = stampPolygonsIntoGrid(request.grid, request.polygons);
    if (stamped.changed === 0) {
      return {
        ok: true,
        changed: 0,
        gridData: Uint8Array.from(stamped.grid.data),
      };
    }
    const display = request.deriveDisplay
      ? deriveCoverDisplayGeometry(stamped.grid)
      : undefined;
    return {
      ok: true,
      changed: stamped.changed,
      gridData: Uint8Array.from(stamped.grid.data),
      displayGeometry: display ? Float32Array.from(display.geometry) : undefined,
      displayStats: display?.stats,
    };
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : 'Unable to edit ground cover.',
    };
  }
}
