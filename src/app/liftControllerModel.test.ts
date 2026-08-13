import { describe, expect, it } from 'vitest';
import type { SavedLift } from '../types/lifts';
import { IDLE_LIFT_TOOL, liftFromDraft, reduceLiftTool } from './liftControllerModel';
import type { DraftLift } from './liftControllerModel';

const A: [number, number] = [-121.5, 46.9];
const B: [number, number] = [-121.49, 46.91];

describe('lift controller model', () => {
  it('moves through choosing, placement, live sampling, review, and cancellation', () => {
    const choosing = reduceLiftTool(IDLE_LIFT_TOOL, { type: 'open' });
    expect(choosing).toEqual({ phase: 'choosing', liftTypeId: 'fixed-grip-double' });
    const selected = reduceLiftTool(choosing, { type: 'set-type', liftTypeId: 'gondola-10' });
    const armed = reduceLiftTool(selected, { type: 'start' });
    const anchored = reduceLiftTool(armed, { type: 'anchor', point: A });
    expect(anchored).toMatchObject({ phase: 'anchored', liftTypeId: 'gondola-10',
      anchorElevStatus: 'pending' });

    const withAnchor = reduceLiftTool(anchored, {
      type: 'anchor-sample-succeeded', elevation: 1000,
    });
    const moved = reduceLiftTool(withAnchor, { type: 'move', point: B });
    const sampledCursor = reduceLiftTool(moved, {
      type: 'cursor-sample-succeeded', elevation: 1200,
    });
    expect(sampledCursor).toMatchObject({ phase: 'anchored', cursor: B,
      elev: [1000, 1200], anchorElevStatus: 'ok', cursorElevStatus: 'ok' });

    const review = reduceLiftTool(sampledCursor, {
      type: 'review', points: [A, B], identifier: '1', name: 'Lift 1',
    });
    expect(review).toMatchObject({ phase: 'review', draft: {
      liftTypeId: 'gondola-10', elevStatus: 'pending', status: 'planning',
    } });
    const changed = reduceLiftTool(review, { type: 'set-type', liftTypeId: 'tram-80' });
    expect(changed).toMatchObject({ phase: 'review', draft: { liftTypeId: 'tram-80' } });
    expect(reduceLiftTool(changed, { type: 'cancel' })).toBe(IDLE_LIFT_TOOL);
  });

  it('represents live elevation failure without discarding placement', () => {
    const choosing = reduceLiftTool(IDLE_LIFT_TOOL, { type: 'open' });
    const armed = reduceLiftTool(choosing, { type: 'start' });
    const anchored = reduceLiftTool(armed, { type: 'anchor', point: A });
    const moved = reduceLiftTool(anchored, { type: 'move', point: B });
    expect(reduceLiftTool(moved, { type: 'cursor-sample-failed' }))
      .toMatchObject({ phase: 'anchored', cursor: B, elev: [null, null], cursorElevStatus: 'error' });
  });

  it('ignores phase-inapplicable actions', () => {
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'start' })).toBe(IDLE_LIFT_TOOL);
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'move', point: B })).toBe(IDLE_LIFT_TOOL);
    expect(reduceLiftTool(IDLE_LIFT_TOOL, { type: 'patch', patch: { name: 'wrong' } }))
      .toBe(IDLE_LIFT_TOOL);
  });

  it('commits the selected type, bottom-to-top geometry, and fallback identity', () => {
    const draft: DraftLift = {
      points: [A, B],
      elev: [1200, 1000],
      elevStatus: 'ok',
      liftTypeId: 'detachable-six-pack',
      status: 'complete',
      identifier: '  ',
      name: '  ',
    };
    const existing = [{ name: 'Lift 1' }] as SavedLift[];
    const lift = liftFromDraft(draft, existing, 'lift-new', '2026-01-01T00:00:00.000Z');

    expect(lift).toMatchObject({
      id: 'lift-new',
      identifier: '2',
      name: 'Lift 2',
      liftTypeId: 'detachable-six-pack',
      points: [B, A],
      endpointElevM: [1000, 1200],
      status: 'complete',
    });
    expect(lift.verticalM).toBe(200);
    expect(lift.lengthM).toBeGreaterThan(200);
  });
});
