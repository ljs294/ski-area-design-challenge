import { describe, expect, it } from 'vitest';
import { trailHeadPreview } from './useTrailMapInput';
import type { DraftTrail, TrailTool } from './trailControllerModel';
import type { TrailHeadAnchor } from './trailHeadAnchor';

const head: TrailHeadAnchor = { kind: 'lift', liftId: 'lift-head', end: 'top',
  point: [-121.5, 46.93] as [number, number] };
const tail: TrailHeadAnchor = { kind: 'lift', liftId: 'lift-tail', end: 'base',
  point: [-121.49, 46.92] as [number, number] };

const draft = {
  anchor: head, tailAnchor: tail,
} as DraftTrail;

describe('trail construction preview phases', () => {
  it('shows a candidate only during endpoint snap selection', () => {
    const placeHead: TrailTool = { phase: 'place-head', candidate: head, error: null };
    const placeTail: TrailTool = {
      phase: 'place-tail', candidate: tail, mode: 'paint', polygons: [], areaM2: 0,
      activeAreaM2: null, canUndo: false, pending: false, error: null,
      anchor: head, hasUserStroke: true,
    };

    expect(trailHeadPreview(placeHead)).toEqual({ candidate: head.point, head: null, tail: null });
    expect(trailHeadPreview(placeTail)).toEqual({ candidate: tail.point, head: head.point, tail: null });
  });

  it.each([
    { phase: 'analyzing', polygons: [], areaM2: 0, anchor: head, tailAnchor: tail },
    { phase: 'review', draft },
    { phase: 'idle' },
  ] satisfies TrailTool[])('does not leave a snap candidate in the %s phase', (tool: TrailTool) => {
    expect(trailHeadPreview(tool).candidate).toBeNull();
  });
});
