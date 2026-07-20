import { describe, expect, it } from 'vitest';
import { createMasterPlanStyle, MASTER_PLAN_LAYER_IDS } from './masterPlanStyle';

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
