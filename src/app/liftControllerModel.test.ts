import { describe, expect, it } from 'vitest';
import type { SavedLift } from '../types/lifts';
import { IDLE_LIFT_TOOL, liftFromDraft, reduceLiftTool } from './liftControllerModel';
import type { DraftLift } from './liftControllerModel';

const A: [number, number] = [-121.5, 46.9];
const B: [number, number] = [-121.49, 46.91];

describe('lift controller model', () => {
  it('moves through placement, review, sampling, and cancellation explicitly', () => {
    const armed = reduceLiftTool(IDLE_LIFT_TOOL, { type: 'arm' });
    const anchored = reduceLiftTool(armed, { type: 'anchor', point: A });
    expect(reduceLiftTool(anchored, { type: 'move', point: B }))
      .toMatchObject({ phase: 'anchored', cursor: B });

    const review = reduceLiftTool(anchored, { type: 'review', points: [A, B], name: 'Lift 1' });
    expect(review).toMatchObject({ phase: 'review', draft: {
      elevStatus: 'pending', chairSize: 2, status: 'planning', name: 'Lift 1',
    } });
    const sampled = reduceLiftTool(review, {
      type: 'sample-succeeded', elevations: [1000, 1100],
    });
    expect(sampled).toMatchObject({ phase: 'review', draft: {
      elev: [1000, 1100], elevStatus: 'ok',
    } });
    expect(reduceLiftTool(sampled, { type: 'sample-failed' }))
      .toMatchObject({ phase: 'review', draft: { elevStatus: 'error' } });
    expect(reduceLiftTool(sampled, { type: 'cancel' })).toBe(IDLE_LIFT_TOOL);
  });

  it('ignores phase-inapplicable actions rather than manufacturing state', () => {
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'move', point: B })).toBe(IDLE_LIFT_TOOL);
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'sample-failed' })).toBe(IDLE_LIFT_TOOL);
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'patch', patch: { name: 'wrong' } }))
      .toBe(IDLE_LIFT_TOOL);
  });

  it('orients the committed entity bottom-to-top and supplies a blank fallback name', () => {
    const draft: DraftLift = {
      points: [A, B],
      elev: [1200, 1000],
      elevStatus: 'ok',
      chairSize: 4,
      status: 'complete',
      name: '  ',
    };
    const existing = [{ name: 'Lift 1' }] as SavedLift[];

    const lift = liftFromDraft(draft, existing, 'lift-new', '2026-01-01T00:00:00.000Z');

    expect(lift).toMatchObject({
      id: 'lift-new',
      name: 'Lift 2',
      points: [B, A],
      endpointElevM: [1000, 1200],
      chairSize: 4,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
    });
    expect(lift.verticalM).toBe(200);
    expect(lift.lengthM).toBeGreaterThan(200);
  });
});
