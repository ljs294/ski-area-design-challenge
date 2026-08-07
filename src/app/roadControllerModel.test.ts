import { describe, expect, it } from 'vitest';
import type { SavedRoad } from '../types';
import { IDLE_ROAD_TOOL, reduceRoadTool, roadFromDraft } from './roadControllerModel';

const A: [number, number] = [-121.5, 46.9];
const B: [number, number] = [-121.49, 46.91];

describe('road controller model', () => {
  it('moves through drawing, undo, review, failure, and cancellation', () => {
    const armed = reduceRoadTool(IDLE_ROAD_TOOL, { type: 'arm', roadType: 'two-lane' });
    const one = reduceRoadTool(armed, { type: 'add-point', point: A });
    expect(reduceRoadTool(one, { type: 'move', point: B }))
      .toMatchObject({ phase: 'drawing', cursor: B });
    expect(reduceRoadTool(one, { type: 'undo' })).toEqual(armed);

    const two = reduceRoadTool(one, { type: 'add-point', point: B });
    const review = reduceRoadTool(two, { type: 'review', name: 'Road 1' });
    expect(review).toMatchObject({ phase: 'review', draft: {
      points: [A, B], gradingStatus: 'pending', gradingPolygons: [], earthwork: null,
    } });
    expect(reduceRoadTool(review, { type: 'grade-failed', error: 'too steep' }))
      .toMatchObject({ phase: 'review', draft: {
        gradingStatus: 'error', gradingError: 'too steep',
      } });
    expect(reduceRoadTool(review, { type: 'cancel' })).toBe(IDLE_ROAD_TOOL);
  });

  it('does not enter review before two committed points', () => {
    const armed = reduceRoadTool(IDLE_ROAD_TOOL, { type: 'arm', roadType: 'two-lane' });
    const one = reduceRoadTool(armed, { type: 'add-point', point: A });
    expect(reduceRoadTool(one, { type: 'review', name: 'Road 1' })).toBe(one);
  });

  it('builds the saved entity with derived geometry and a fallback name', () => {
    const drawing = reduceRoadTool(
      reduceRoadTool(
        reduceRoadTool(IDLE_ROAD_TOOL, { type: 'arm', roadType: 'two-lane' }),
        { type: 'add-point', point: A },
      ),
      { type: 'add-point', point: B },
    );
    const review = reduceRoadTool(drawing, { type: 'review', name: '  ' });
    if (review.phase !== 'review') throw new Error('review expected');
    const road = roadFromDraft(
      review.draft,
      [{ name: 'Road 1' }] as SavedRoad[],
      'road-new',
      '2026-01-01T00:00:00.000Z',
    );
    expect(road).toMatchObject({
      id: 'road-new', name: 'Road 2', roadType: 'two-lane', widthM: 7,
      points: [A, B], terrainGraded: true,
    });
    expect(road.lengthM).toBeGreaterThan(0);
  });
});
