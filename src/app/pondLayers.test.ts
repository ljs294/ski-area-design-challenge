import { describe, expect, it } from 'vitest';
import { pondDraftGeoJSON, pondsToGeoJSON } from './pondLayers';

describe('standalone pond map layers', () => {
  it('renders saved ponds as clickable polygons', () => {
    const data = pondsToGeoJSON([{ id: 'pond-1', name: 'Pond 1',
      boundary: [[0, 0], [0, 1], [1, 0], [0, 0]], topElevationM: 10,
      areaM2: 1000, averageDepthM: 1, maxDepthM: 2, capacityM3: 1000, createdAt: 'now' }]);
    expect(data.features[0].properties?.id).toBe('pond-1');
    expect(data.features[0].properties?.renderHeightM).toBe(1);
    expect(data.features[0].geometry.type).toBe('Polygon');
    if (data.features[0].geometry.type === 'Polygon')
      expect(data.features[0].geometry.coordinates[0].length).toBeGreaterThan(4);
  });

  it('uses an open dashed boundary while drawing and a polygon during review', () => {
    const points: [number, number][] = [[0, 0], [0, 1], [1, 0]];
    expect(pondDraftGeoJSON({ points, cursor: [1, 1], closed: false }).features[0].geometry.type)
      .toBe('LineString');
    const review = pondDraftGeoJSON({ points, cursor: null, closed: true,
      topElevationM: 10, averageDepthM: 2 });
    expect(review.features[0].geometry.type)
      .toBe('Polygon');
    expect(review.features[0].properties?.renderHeightM).toBe(2);
  });
});
