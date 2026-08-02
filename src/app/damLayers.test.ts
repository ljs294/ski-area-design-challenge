import { describe, expect, it } from 'vitest';
import { damDraftGeoJSON, damsToGeoJSON } from './damLayers';

const rings: [number, number][][] = [[[0, 0], [0, 1], [1, 1], [1, 0], [0, 0]]];

describe('dam pond map geometry', () => {
  it('smooths and elevates saved full-pool polygons', () => {
    const data = damsToGeoJSON([{ id: 'dam-1', name: 'Dam 1', points: [[0, 0], [1, 0]],
      crestElevationM: 105, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
      inflowM3s: 0.3, pondRings: rings, areaM2: 1000, averageDepthM: 5,
      capacityM3: 5000, maxDamHeightM: 4, createdAt: 'now' }]);
    const pond = data.features.find((feature) => feature.properties?.kind === 'pond')!;
    expect(pond.properties?.renderHeightM).toBe(5);
    if (pond.geometry.type === 'Polygon') expect(pond.geometry.coordinates[0].length).toBe(17);
  });

  it('uses the design depth for the elevated review preview', () => {
    const data = damDraftGeoJSON({ points: [[0, 0], [1, 0]], cursor: null, pondRings: rings,
      crestElevationM: 105, averageDepthM: 4 });
    expect(data.features[0].properties?.renderHeightM).toBe(4);
  });
});
