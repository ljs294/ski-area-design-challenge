import { describe, expect, it } from 'vitest';
import type { SavedJunction, SavedTrailPart } from '../types';
import { buildSavedTrail, createTrailDraft, IDLE_TRAIL_TOOL,
  reduceTrailTool, type TrailTool } from './trailControllerModel';

const head = { kind: 'lift', liftId: 'lift-1', end: 'top',
  point: [0, 0] as [number, number] } as const;
const tail = { kind: 'lift', liftId: 'lift-2', end: 'base',
  point: [0, 0.002] as [number, number] } as const;
const part: SavedTrailPart = {
  polygon: [[[-0.001, -0.001], [0.001, -0.001], [0.001, 0.003],
    [-0.001, 0.003], [-0.001, -0.001]]],
  centerline: [head.point, tail.point], centerlineElevM: [1100, 1000],
};

describe('trail controller model', () => {
  it('moves through head, paint, tail, analysis, and review without phase leaks', () => {
    let tool: TrailTool = reduceTrailTool(IDLE_TRAIL_TOOL, { type: 'arm' });
    tool = reduceTrailTool(tool, { type: 'head-candidate', candidate: head });
    expect(tool).toMatchObject({ phase: 'place-head', candidate: head, error: null });
    tool = reduceTrailTool(tool, { type: 'begin-paint', anchor: head });
    tool = reduceTrailTool(tool, { type: 'paint-patch', patch: { hasUserStroke: true,
      pending: false, polygons: [part.polygon], areaM2: 500 } });
    tool = reduceTrailTool(tool, { type: 'place-tail' });
    tool = reduceTrailTool(tool, { type: 'tail-candidate', candidate: tail, error: null });
    tool = reduceTrailTool(tool, { type: 'analyze', tailAnchor: tail });
    expect(tool).toMatchObject({ phase: 'analyzing', anchor: head, tailAnchor: tail });
    const draft = createTrailDraft([part], 500, 24, [], head, tail);
    tool = reduceTrailTool(tool, { type: 'review', draft });
    expect(reduceTrailTool(tool, { type: 'review-patch', patch: { name: 'Glade' } }))
      .toMatchObject({ phase: 'review', draft: { name: 'Glade' } });
    expect(reduceTrailTool(tool, { type: 'cancel' })).toEqual(IDLE_TRAIL_TOOL);
  });

  it('returns an analyzing failure to the preserved painted footprint', () => {
    let tool = reduceTrailTool(IDLE_TRAIL_TOOL, { type: 'begin-paint', anchor: head });
    tool = reduceTrailTool(tool, { type: 'paint-patch', patch: {
      polygons: [part.polygon], areaM2: 500, hasUserStroke: true } });
    tool = reduceTrailTool(tool, { type: 'place-tail' });
    tool = reduceTrailTool(tool, { type: 'analyze', tailAnchor: tail });
    tool = reduceTrailTool(tool, { type: 'analysis-failed', error: 'no centerline',
      canUndo: true, hasUserStroke: true });
    expect(tool).toMatchObject({ phase: 'place-tail', polygons: [part.polygon],
      areaM2: 500, canUndo: true, hasUserStroke: true, error: 'no centerline' });
  });

  it('builds a topology-segmented trail and rejects reversed lift endpoints', () => {
    const draft = createTrailDraft([part], 500, 24, [], head, tail);
    const headJunction = { id: 'head', point: head.point, elevM: 1100,
      createdAt: '2026-01-01T00:00:00.000Z' } satisfies SavedJunction;
    const tailJunction = { id: 'tail', point: tail.point, elevM: 1000,
      createdAt: '2026-01-01T00:00:00.000Z' } satisfies SavedJunction;
    const built = buildSavedTrail(draft, [], 'trail-1', '2026-01-01T00:00:00.000Z',
      headJunction, tailJunction);
    expect(built?.trail).toMatchObject({ id: 'trail-1', name: 'Run 1',
      status: 'planning', terrainGraded: false,
      parts: [{ segments: [{ fromJunctionId: 'head', toJunctionId: 'tail' }] }] });
    expect(buildSavedTrail({ ...draft, anchor: { ...head, end: 'base' } }, [],
      'bad', '2026-01-01T00:00:00.000Z', headJunction, tailJunction)).toBeNull();
  });
});
