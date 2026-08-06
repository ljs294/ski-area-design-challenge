import { describe, expect, it } from 'vitest';
import { LOCAL_ROAD_PAINT, playerRoadFeatures, playerRoadGeoJSON,
  roadDraftGeoJSON } from './roadLayers';
import type { SavedRoad } from '../types';

describe('road draft GeoJSON', () => {
  it('renders vertices and a cursor segment without persisting the cursor', () => {
    const a: [number, number] = [-121.5, 46.93];
    const b: [number, number] = [-121.49, 46.94];
    const data = roadDraftGeoJSON({ points: [a], cursor: b });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual(['road', 'vertex']);
    expect((data.features[0].geometry as GeoJSON.LineString).coordinates).toEqual([a, b]);
  });

  it('returns no line until a segment exists', () => {
    const data = roadDraftGeoJSON({ points: [[-121.5, 46.93]], cursor: null });
    expect(data.features).toHaveLength(1);
    expect(data.features[0].properties?.kind).toBe('vertex');
  });

  it('includes bounded grading and infeasible station overlays during review', () => {
    const a: [number, number] = [-121.5, 46.93];
    const b: [number, number] = [-121.49, 46.94];
    const polygon: [number, number][][] = [[a, [b[0], a[1]], b,
      [a[0], b[1]], a]];
    const data = roadDraftGeoJSON({ points: [a, b], cursor: null,
      gradingPolygons: [polygon], infeasibleLines: [[a, b]] });
    expect(data.features.map((feature) => feature.properties?.kind))
      .toContain('grade');
    expect(data.features.map((feature) => feature.properties?.kind))
      .toContain('infeasible');
  });

  it('emits confirmed roads in the dedicated player-road collection', () => {
    const road: SavedRoad = { id: 'r1', name: 'Access Road', roadType: 'two-lane', widthM: 7,
      points: [[-121.5, 46.93], [-121.49, 46.94]], lengthM: 1000, createdAt: 'now' };
    const feature = playerRoadFeatures([road])[0];
    expect(feature.properties).toMatchObject({ kind: 'road', class: 'minor', playerBuilt: true,
      roadId: 'r1', name: 'Access Road' });
    expect(playerRoadGeoJSON([road])).toEqual({
      type: 'FeatureCollection', features: [feature],
    });
    expect(LOCAL_ROAD_PAINT).toMatchObject({ 'line-color': '#55534e', 'line-opacity': 0.72 });
  });
});
