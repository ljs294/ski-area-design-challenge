import { describe, expect, it } from 'vitest';
import { nodePathDraftGeoJSON, nodePathsToGeoJSON } from './nodePathLayers';
import type { SavedJunction } from '../skiNodes';

const junction = (id: string, lng: number): SavedJunction =>
  ({ id, point: [lng, 46.93], elevM: null, createdAt: '2026-01-01T00:00:00.000Z' });

describe('nodePathsToGeoJSON', () => {
  // The map label, the trails list and the run review panel all print the same
  // number for a node. That number is the junction's position in the saved list,
  // so it has to be derived here from order and nowhere else.
  it('numbers junction features by their 1-based position', () => {
    const junctions = [junction('a', -121.5), junction('b', -121.51), junction('c', -121.52)];
    const features = nodePathsToGeoJSON([], [], junctions)
      .features.filter((f) => f.properties?.kind === 'junction');
    expect(features.map((f) => [f.properties?.id, f.properties?.number]))
      .toEqual([['a', 1], ['b', 2], ['c', 3]]);
  });

  it('emits nothing for a resort with no nodes, paths or pins', () => {
    expect(nodePathsToGeoJSON([], [], []).features).toEqual([]);
  });
});

describe('nodePathDraftGeoJSON', () => {
  // Add-node picks on one click and commits on another; the marker for the
  // picked spot is the only thing on the map between the two.
  it('marks a picked-but-uncommitted target separately from the hover ring', () => {
    const features = nodePathDraftGeoJSON({
      points: [], cursor: null, highlight: [[-121.5, 46.93]], pick: [-121.51, 46.94],
    }).features;
    expect(features.map((f) => f.properties?.kind)).toEqual(['highlight', 'pick']);
    expect(features[1].geometry).toEqual({ type: 'Point', coordinates: [-121.51, 46.94] });
  });

  it('draws no pick marker before anything is picked', () => {
    expect(nodePathDraftGeoJSON({ points: [], cursor: null, pick: null }).features).toEqual([]);
  });
});
