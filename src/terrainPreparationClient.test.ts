import { describe, expect, it } from 'vitest';
import { prepareTerrainCover } from './terrainPreparationClient';

describe('prepareTerrainCover', () => {
  it('keeps the synchronous compatibility path deterministic when workers are unavailable', async () => {
    const bounds = { west: -120, south: 40, east: -119.99998, north: 40.00002 };
    const phases: string[] = [];
    const result = await prepareTerrainCover({
      bounds,
      original: {
        bounds, width: 2, height: 2, cellSizeM: 1,
        data: new Uint8Array([10, 10, 30, 80]), complete: true, nodataCount: 0,
        source: 'esa-worldcover-2021-v200', vintage: '2021',
      },
      heights: new Float32Array([2000, 2001, 1999, 2000]),
      elevationWidth: 2,
      elevationHeight: 2,
      naip: null,
    }, { onProgress: (phase) => phases.push(phase) });

    expect(phases).toEqual(['classifying', 'vectorizing']);
    expect(result.cover.data).toHaveLength(result.cover.width * result.cover.height);
    expect(result.cover.complete).toBe(true);
    expect(result.display.stats.vertexCount).toBeLessThanOrEqual(250_000);
  });
});
