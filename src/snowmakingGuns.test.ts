import { describe, expect, it } from 'vitest';
import {
  HKD_IMPULSE_R5,
  SNOWGUN_HOSE_REACH_M,
  SNOWGUN_VARIANTS,
  reconcileSnowgunConnections,
  sanitizeSnowguns,
  snowgunCatalogValue,
  snowgunLabel,
} from './snowmakingGuns';
import type { SavedSnowgun, SavedSnowmakingNode } from './types/snowmaking';

const hydrant = (id: string, longitude: number): SavedSnowmakingNode => ({
  id, name: id, kind: 'hydrant', labelNumber: 1, point: [longitude, 0], elevM: 0,
  createdAt: '2026-08-07T00:00:00.000Z',
});
const gun = (id: string, longitude: number, hydrantId: string | null = null): SavedSnowgun => ({
  id, variantId: 'HKD_ImpulseR5_10s', point: [longitude, 0], elevM: 0, hydrantId,
  createdAt: '2026-08-07T00:00:00.000Z',
});

describe('snowgun catalog', () => {
  it('contains the exact R5 stages and four launch variants', () => {
    expect(HKD_IMPULSE_R5.stages.map((stage) =>
      [stage.wetBulbF, stage.waterFlowGpm, stage.airFlowCfm])).toEqual([
      [28, 18, 56], [24, 28, 56], [19, 38, 56], [14, 48, 16], [9, 58, 16],
    ]);
    expect(SNOWGUN_VARIANTS.map((variant) =>
      [variant.id, variant.throwFt, variant.priceUsd])).toEqual([
      ['HKD_ImpulseR5_10s', 30, 7000], ['HKD_ImpulseR5_10t', 30, 7000],
      ['HKD_ImpulseR5_20t', 80, 8000], ['HKD_ImpulseR5_30t', 125, 9000],
    ]);
    expect(snowgunCatalogValue([
      { variantId: 'HKD_ImpulseR5_10s' }, { variantId: 'HKD_ImpulseR5_20t' },
    ])).toBe(15000);
  });
});

describe('snowgun hookup reconciliation', () => {
  it('preserves valid hookups and assigns nearest free hydrants in gun order', () => {
    const nodes = [hydrant('h1', 0), hydrant('h2', 0.0001)];
    const result = reconcileSnowgunConnections([
      gun('g1', 0.00009, 'h1'), gun('g2', 0.00009), gun('g3', 0.00009),
    ], nodes);
    expect(result.map((item) => item.hydrantId)).toEqual(['h1', 'h2', null]);
  });

  it('accepts exactly 50 feet and rejects a farther hookup', () => {
    const longitudeAtReach = SNOWGUN_HOSE_REACH_M / 111_195;
    const nodes = [hydrant('h1', 0)];
    expect(reconcileSnowgunConnections([gun('edge', longitudeAtReach)], nodes)[0].hydrantId)
      .toBe('h1');
    expect(reconcileSnowgunConnections([gun('far', longitudeAtReach + 0.00001)], nodes)[0].hydrantId)
      .toBeNull();
  });

  it('frees an invalid hookup and preserves a disconnected installed gun', () => {
    const result = reconcileSnowgunConnections([gun('g1', 0, 'deleted')], []);
    expect(result[0]).toMatchObject({ id: 'g1', hydrantId: null });
  });

  it('uses the exclusive hydrant number as the connected gun label', () => {
    const h7 = { ...hydrant('h7', 0), labelNumber: 7 };
    expect(snowgunLabel(gun('g7', 0, h7.id), [h7])).toBe('H7');
    expect(snowgunLabel(gun('free', 0), [h7])).toBe('Disconnected');
  });
});

describe('snowgun hydration', () => {
  it('drops malformed variants and duplicate network ids while repairing references', () => {
    const nodes = [hydrant('h1', 0)];
    const raw = [
      gun('g1', 0, 'missing'),
      { ...gun('bad', 0), variantId: 'unknown' },
      { ...gun('bad-date', 0), createdAt: 'eventually' },
      { ...gun('bad-point', 0), point: [181, 0] },
      gun('h1', 0),
    ];
    expect(sanitizeSnowguns(raw, nodes, [])).toEqual([
      expect.objectContaining({ id: 'g1', hydrantId: 'h1' }),
    ]);
  });
});
