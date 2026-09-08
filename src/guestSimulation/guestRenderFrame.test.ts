import { describe, expect, it } from 'vitest';
import { guestMovementProjection } from './guestRenderFrame';
import type { GuestState } from './contracts';
import type { GuestSimulationEngineSnapshot } from './engine';

function projectionAt(tick: number) {
  const snapshot = {
    tick,
    network: {
      edges: [
        { id: 'connector-a', travelSeconds: 30 },
        { id: 'connector-b', travelSeconds: 40 },
      ],
    },
  } as unknown as GuestSimulationEngineSnapshot;
  const guest = { status: 'travelling-to-lift' } as GuestState;
  const movement = { tick: 100, payload: { kind: 'reach-lift' } } as unknown as GuestSimulationEngineSnapshot['pendingEvents'][number];
  const itinerary = { connectorEdgeIds: ['connector-a', 'connector-b'], travelToLiftSeconds: 70 } as unknown as GuestSimulationEngineSnapshot['itineraries'][number];
  return guestMovementProjection(snapshot, guest, movement, itinerary,
    new Map([['connector-a', 0], ['connector-b', 1]]));
}

describe('guest render frame movement projection', () => {
  it('selects the active connector edge and local progress across a route', () => {
    expect(projectionAt(30)).toEqual({ edgeIndex: 0, progress: 0 });
    expect(projectionAt(45)).toEqual({ edgeIndex: 0, progress: 0.5 });
    expect(projectionAt(70)).toEqual({ edgeIndex: 1, progress: 0.25 });
    expect(projectionAt(99)).toEqual({ edgeIndex: 1, progress: 0.975 });
    expect(projectionAt(100)).toEqual({ edgeIndex: 1, progress: 1 });
  });

  it('does not collapse a multi-edge route to global progress on the first edge', () => {
    const projection = projectionAt(70);
    expect(projection?.edgeIndex).toBe(1);
    expect(projection?.progress).toBeCloseTo(0.25);
  });
});
