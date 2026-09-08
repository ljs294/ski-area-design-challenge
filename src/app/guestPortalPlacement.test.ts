import { describe, expect, it } from 'vitest';
import type { NetworkNode, SkiNetwork } from '../network';
import { placeGuestPortal } from './guestPortalPlacement';

function network(): SkiNetwork {
  const connected: NetworkNode = { id: 'n:connected', lngLat: [-121.5, 46.9], elevM: 1_000,
    kind: 'lift-base' as const, liftBases: ['lift-a'], liftTops: [], trailIds: [], nodeIds: [], pathIds: [],
    outgoing: ['l:lift-a'], incoming: [] };
  const isolated: NetworkNode = { ...connected, id: 'n:isolated', lngLat: [-121.50001, 46.9],
    kind: 'user-node' as const, liftBases: [], outgoing: [] };
  return { nodes: [isolated, connected], edges: [], nodeById: new Map<string, NetworkNode>([[isolated.id, isolated], [connected.id, connected]]),
    edgeById: new Map(), trailEdgeIds: new Map(), pathEdgeIds: new Map(), liftEdgeIds: new Map(),
    frame: { lng0: -121.5, lat0: 46.9, mPerLng: 75_000, mPerLat: 111_320 },
    diagnostics: { crossingCount: 0, tJunctionCount: 0, endpointMergeCount: 0, liftSnapCount: 0,
      liftSplitCount: 0, mergedSplitCount: 0, droppedPartIds: [], unresolvedElevationTrailIds: [],
      unresolvedLiftIds: [], orphanTrailIds: [], isolatedLiftIds: [], componentCount: 1, traverseEdgeCount: 0,
      unanchoredTrailIds: [], unresolvedAnchorTrailIds: [], unresolvedAnchorPathIds: [],
      overreachingAnchorIds: [], degeneratePathIds: [] } };
}

describe('Guest Entrance placement', () => {
  it('snaps only to a connected network node', () => {
    const result = placeGuestPortal(network(), [-121.50001, 46.9]);
    expect(result.error).toBeNull();
    expect(result.portal?.nodeId).toBe('n:connected');
    expect(result.portal?.openUntilTick).toBe(Number.MAX_SAFE_INTEGER);
  });

  it('rejects a click outside the network snap distance', () => {
    expect(placeGuestPortal(network(), [-122, 47]).portal).toBeNull();
  });
});
