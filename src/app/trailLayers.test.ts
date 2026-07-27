import { describe, expect, it } from 'vitest';
import { haversineMeters } from '../geo';
import { paintPreviewGeoJSON } from './trailLayers';

const CURSOR: [number, number] = [-121.5, 46.93];

describe('trail paint preview geometry', () => {
  it('renders a stationary dab plus an accurate geographic brush guide', () => {
    const data = paintPreviewGeoJSON({ path: [CURSOR], cursor: CURSOR, brushWidthM: 40 });
    expect(data.features.map((feature) => feature.properties?.kind))
      .toEqual(['paint', 'guide', 'crosshair']);
    const stroke = data.features[0].geometry as GeoJSON.Polygon;
    const strokeRing = stroke.coordinates[0] as [number, number][];
    expect(strokeRing).toHaveLength(49);
    expect(strokeRing[0]).toEqual(strokeRing.at(-1));
    expect(haversineMeters(strokeRing[0], strokeRing[24])).toBeCloseTo(40, 0);
    const guide = data.features[1].geometry as GeoJSON.Polygon;
    const ring = guide.coordinates[0] as [number, number][];
    expect(ring).toHaveLength(49);
    expect(ring[0]).toEqual(ring.at(-1));
    expect(haversineMeters(ring[0], ring[24])).toBeCloseTo(40, 0);
  });

  it.each([0, 46.93, 70])('keeps a moving corridor 40 m wide at latitude %s', (latitude) => {
    const start: [number, number] = [-121.5, latitude];
    const metersLng = 111_320 * Math.cos(latitude * Math.PI / 180);
    const end: [number, number] = [start[0] + 100 / metersLng, latitude];
    const data = paintPreviewGeoJSON({ path: [start, end], cursor: end, brushWidthM: 40 });
    const corridor = data.features[0].geometry as GeoJSON.Polygon;
    const ring = corridor.coordinates[0] as [number, number][];
    const minLat = Math.min(...ring.map((point) => point[1]));
    const maxLat = Math.max(...ring.map((point) => point[1]));
    expect(haversineMeters([start[0], minLat], [start[0], maxLat])).toBeCloseTo(40, 0);
    expect(ring[0]).toEqual(ring.at(-1));
  });

  it('produces a finite closed corridor through turns and backtracking', () => {
    const metersLng = 111_320 * Math.cos(CURSOR[1] * Math.PI / 180);
    const path: [number, number][] = [CURSOR,
      [CURSOR[0] + 60 / metersLng, CURSOR[1]],
      [CURSOR[0] + 60 / metersLng, CURSOR[1] + 40 / 111_320],
      [CURSOR[0] + 10 / metersLng, CURSOR[1] + 5 / 111_320]];
    const data = paintPreviewGeoJSON({ path, cursor: path.at(-1)!, brushWidthM: 30 });
    const ring = (data.features[0].geometry as GeoJSON.Polygon).coordinates[0];
    expect(ring.length).toBeGreaterThan(path.length * 2);
    expect(ring[0]).toEqual(ring.at(-1));
    expect(ring.flat().every(Number.isFinite)).toBe(true);
  });

  it('can retain only the hover guide after a stroke is acknowledged', () => {
    const data = paintPreviewGeoJSON({ path: [], cursor: CURSOR, brushWidthM: 20 });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual(['guide', 'crosshair']);
  });

  it('clears all preview geometry when the cursor leaves the map', () => {
    expect(paintPreviewGeoJSON({ path: [], cursor: null, brushWidthM: 20 }).features).toEqual([]);
  });
});
