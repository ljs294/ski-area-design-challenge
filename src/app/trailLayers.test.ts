import { describe, expect, it } from 'vitest';
import { haversineMeters } from '../geo';
import { paintPreviewGeoJSON } from './trailLayers';

const CURSOR: [number, number] = [-121.5, 46.93];

describe('trail paint preview geometry', () => {
  it('renders a stationary dab plus an accurate geographic brush guide', () => {
    const data = paintPreviewGeoJSON({ path: [CURSOR], cursor: CURSOR, brushWidthM: 40 });
    expect(data.features.map((feature) => feature.properties?.kind))
      .toEqual(['paint', 'guide', 'crosshair']);
    const stroke = data.features[0].geometry as GeoJSON.LineString;
    expect(stroke.coordinates).toEqual([CURSOR, CURSOR]);
    const guide = data.features[1].geometry as GeoJSON.Polygon;
    const ring = guide.coordinates[0] as [number, number][];
    expect(ring).toHaveLength(49);
    expect(ring[0]).toEqual(ring.at(-1));
    expect(haversineMeters(ring[0], ring[24])).toBeCloseTo(40, 0);
  });

  it('can retain only the hover guide after a stroke is acknowledged', () => {
    const data = paintPreviewGeoJSON({ path: [], cursor: CURSOR, brushWidthM: 20 });
    expect(data.features.map((feature) => feature.properties?.kind)).toEqual(['guide', 'crosshair']);
  });

  it('clears all preview geometry when the cursor leaves the map', () => {
    expect(paintPreviewGeoJSON({ path: [], cursor: null, brushWidthM: 20 }).features).toEqual([]);
  });
});
