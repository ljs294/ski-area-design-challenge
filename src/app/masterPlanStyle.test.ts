import { describe, expect, it } from 'vitest';
import { createGameBasemapStyle, createMasterPlanStyle, MASTER_PLAN_LAYER_IDS } from './masterPlanStyle';

describe('master-plan style', () => {
  it('owns the subdued satellite and technical context hierarchy', () => {
    const style = createMasterPlanStyle();
    const ids = style.layers.map((layer) => layer.id);
    expect(ids[0]).toBe('mp-paper');
    expect(ids.indexOf(MASTER_PLAN_LAYER_IDS.satellite)).toBeLessThan(ids.indexOf(MASTER_PLAN_LAYER_IDS.water));
    const satellite = style.layers.find((layer) => layer.id === MASTER_PLAN_LAYER_IDS.satellite);
    expect(satellite?.type).toBe('raster');
    expect((satellite as { paint?: { 'raster-opacity'?: number } }).paint?.['raster-opacity']).toBe(0.7);
  });
});

describe('game basemap style (offline)', () => {
  const style = createGameBasemapStyle();

  it('streams no tiles: satellite placeholder is the only source and is empty', () => {
    // The satellite placeholder is the only source and carries no network tiles;
    // setupAnalysisLayers swaps it to the local NAIP image at play time. (glyphs
    // stay remote by design — tiny, cached, screen-space label fonts.)
    expect(Object.keys(style.sources ?? {})).toEqual(['satellite']);
    const sourcesJson = JSON.stringify(style.sources);
    expect(sourcesJson).not.toMatch(/openfreemap|openmaptiles|arcgisonline|cartocdn/i);
    expect(sourcesJson).not.toMatch(/https?:\/\//i);
    expect((style.sources as Record<string, { tiles?: string[] }>).satellite.tiles).toEqual([]);
  });

  it('keeps the mp-satellite anchor for the local aerial swap', () => {
    const ids = style.layers.map((l) => l.id);
    expect(ids).toContain(MASTER_PLAN_LAYER_IDS.satellite);
    expect(ids[0]).toBe('mp-paper');
    const sat = style.layers.find((l) => l.id === MASTER_PLAN_LAYER_IDS.satellite);
    expect(sat?.type).toBe('raster');
    expect((sat as { layout?: { visibility?: string } }).layout?.visibility).toBe('none');
  });
});
