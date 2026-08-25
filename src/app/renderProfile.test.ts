import { describe, expect, it } from 'vitest';
import { isRenderQuality, pixelRatioFor, renderProfileFor } from './renderProfile';

describe('render profiles', () => {
  it('recognizes the four manual tiers without accepting unknown stored values', () => {
    expect(['performance', 'standard', 'high', 'ultra'].every(isRenderQuality)).toBe(true);
    expect(isRenderQuality('automatic')).toBe(false);
  });

  it('uses the agreed presentation and memory budgets', () => {
    expect(renderProfileFor('performance')).toMatchObject({
      maxBackingPixels: 4 * 1024 * 1024,
      terrainMaxZoom: 14,
      imageryMaxSide: 2048,
      coverMode: 'raster',
      derivedCacheBytes: 64 * 1024 * 1024,
      hillshade: 'none',
      menu: 'css',
    });
    expect(renderProfileFor('standard').coverVertexBudget).toBe(150_000);
    expect(renderProfileFor('ultra').tileWorkerCount).toBe(2);
  });

  it('caps 1080p ratios by the tier preference', () => {
    const viewport = { width: 1920, height: 1080, devicePixelRatio: 1, maxCanvasSize: 8192 };
    expect(pixelRatioFor('performance', viewport)).toBe(1);
    expect(pixelRatioFor('standard', viewport)).toBe(1);
    expect(pixelRatioFor('high', viewport)).toBe(1.5);
    expect(pixelRatioFor('ultra', viewport)).toBe(2);
  });

  it('caps 4K backing pixels even when the device DPR is high', () => {
    const viewport = { width: 3840, height: 2160, devicePixelRatio: 3, maxCanvasSize: 8192 };
    for (const quality of ['performance', 'standard', 'high', 'ultra'] as const) {
      const ratio = pixelRatioFor(quality, viewport);
      const pixels = viewport.width * viewport.height * ratio * ratio;
      expect(pixels).toBeLessThanOrEqual(renderProfileFor(quality).maxBackingPixels + 1);
    }
  });

  it('honors the WebGL per-axis canvas limit', () => {
    expect(pixelRatioFor('ultra', {
      width: 3840,
      height: 2160,
      devicePixelRatio: 3,
      maxCanvasSize: 4096,
    })).toBeCloseTo(4096 / 3840, 8);
  });
});
