import { describe, expect, it } from 'vitest';
import { analyzeSnowmakingSystem, hazenWilliamsHeadLossFt, pumpHeadFt,
  snowgunStageForWetBulb } from './snowmakingHydraulics';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe } from './types/snowmaking';

const FT_M = 0.3048;
const LAT = 46.9;
const M_PER_LNG = 111320 * Math.cos(LAT * Math.PI / 180);
function point(eastFt: number): [number, number] {
  return [-121.5 + eastFt * FT_M / M_PER_LNG, LAT];
}
function node(id: string, kind: SavedSnowmakingNode['kind'], eastFt: number, elevFt: number): SavedSnowmakingNode {
  return { id, name: id, kind, point: point(eastFt), elevM: elevFt * FT_M,
    ...(kind === 'intake' ? { source: { kind: 'pond' as const, pondId: 'pond-1' } } : {}),
    createdAt: '2026-01-01T00:00:00.000Z' };
}
function pipe(id: string, from: SavedSnowmakingNode, to: SavedSnowmakingNode,
  diameterIn = 8): SavedSnowmakingPipe {
  return { id, name: id, diameterIn: diameterIn as 8, vertices: [
    { point: from.point, elevM: from.elevM, nodeId: from.id },
    { point: to.point, elevM: to.elevM, nodeId: to.id },
  ], lengthM: Math.hypot((to.point[0] - from.point[0]) * M_PER_LNG,
    (to.elevM ?? 0) - (from.elevM ?? 0)), verticalM: Math.abs((to.elevM ?? 0) - (from.elevM ?? 0)),
  createdAt: '2026-01-01T00:00:00.000Z' };
}
function gun(id: string, hydrant: SavedSnowmakingNode): SavedSnowgun {
  return { id, variantId: 'HKD_ImpulseR5_10s', point: hydrant.point, elevM: hydrant.elevM,
    hydrantId: hydrant.id, createdAt: '2026-01-01T00:00:00.000Z' };
}

describe('snowmaking hydraulic formulas', () => {
  it('locks the PRD gameplay equations', () => {
    expect(hazenWilliamsHeadLossFt(1000, 1000, 8)).toBeCloseTo(21.6507, 4);
    expect(hazenWilliamsHeadLossFt(100, 4800, 8)).toBeCloseTo(39.5486, 4);
    expect(pumpHeadFt(3000, 0.85, 4800)).toBeCloseTo(2103.75, 5);
    expect(pumpHeadFt(3000, 0.85, 4800) / 2.31).toBeCloseTo(910.714, 3);
  });

  it('selects R5 stages at every wet-bulb boundary', () => {
    expect(snowgunStageForWetBulb(29)).toBeNull();
    expect(snowgunStageForWetBulb(28)?.waterFlowGpm).toBe(18);
    expect(snowgunStageForWetBulb(24)?.waterFlowGpm).toBe(28);
    expect(snowgunStageForWetBulb(19)?.waterFlowGpm).toBe(38);
    expect(snowgunStageForWetBulb(14)?.waterFlowGpm).toBe(48);
    expect(snowgunStageForWetBulb(9)?.waterFlowGpm).toBe(58);
    expect(snowgunStageForWetBulb(-20)?.waterFlowGpm).toBe(58);
  });
});

describe('analyzeSnowmakingSystem', () => {
  const intake = node('intake', 'intake', 0, 0);
  const pump = node('pump', 'pump', 0, 0);
  const junction = node('junction', 'junction', 100, 0);
  const hydrantA = node('hydrant-a', 'hydrant', 200, 33.3);
  const hydrantB = node('hydrant-b', 'hydrant', 200, -20);
  const nodes = [intake, pump, junction, hydrantA, hydrantB];
  const pipes = [pipe('suction', intake, pump), pipe('trunk', pump, junction),
    pipe('branch-a', junction, hydrantA), pipe('branch-b', junction, hydrantB)];
  const guns = [gun('gun-a', hydrantA), gun('gun-b', hydrantB)];
  const base = { nodes, pipes, guns, selectedPipeIds: pipes.map((entry) => entry.id),
    selectedGunIds: guns.map((entry) => entry.id), wetBulbF: 9,
    pumpSettings: { pump: { on: true, horsepowerHp: 500, efficiency: 0.85 } },
    sourceCapacitiesM3: { intake: 1000 } };

  it('aggregates trunk demand, applies signed static head, and reports storage runtime', () => {
    const result = analyzeSnowmakingSystem(base);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.summary.totalDemandGpm).toBe(116);
    expect(result.summary.waterUseGalPerHour).toBe(6960);
    expect(result.summary.sourceRuntimeHours).toBeCloseTo(37.956, 2);
    expect(result.pumps[0]).toMatchObject({ nodeId: 'pump', on: true, flowGpm: 116,
      horsepowerHp: 500, efficiency: 0.85 });
    expect(result.spans.find((span) => span.pipeId === 'trunk')?.flowGpm).toBe(116);
    expect(result.spans.find((span) => span.pipeId === 'branch-a')?.flowGpm).toBe(58);
    expect(result.spans.find((span) => span.pipeId === 'branch-a')!.staticHeadFt).toBeCloseTo(33.3, 1);
    expect(result.spans.find((span) => span.pipeId === 'branch-b')!.staticHeadFt).toBeCloseTo(-20, 1);
  });

  it('applies a branch booster only to its descendants', () => {
    const booster = node('booster', 'pump', 150, 0);
    const branchWithBooster = [pipe('boost-in', junction, booster), pipe('boost-out', booster, hydrantA)];
    const result = analyzeSnowmakingSystem({ ...base, nodes: [...nodes, booster],
      pipes: [...pipes.filter((entry) => entry.id !== 'branch-a'), ...branchWithBooster],
      selectedPipeIds: ['suction', 'trunk', 'boost-in', 'boost-out', 'branch-b'],
      pumpSettings: { ...base.pumpSettings,
        booster: { on: true, horsepowerHp: 50, efficiency: 1 } } });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    const a = result.guns.find((entry) => entry.gunId === 'gun-a')!;
    const b = result.guns.find((entry) => entry.gunId === 'gun-b')!;
    expect(a.pressurePsi).toBeGreaterThan(b.pressurePsi);
    expect(result.pumps.find((entry) => entry.nodeId === 'booster')?.flowGpm).toBe(58);

    const boosterOff = analyzeSnowmakingSystem({ ...base, nodes: [...nodes, booster],
      pipes: [...pipes.filter((entry) => entry.id !== 'branch-a'), ...branchWithBooster],
      selectedPipeIds: ['suction', 'trunk', 'boost-in', 'boost-out', 'branch-b'],
      pumpSettings: { ...base.pumpSettings,
        booster: { on: false, horsepowerHp: null, efficiency: 0.85 } } });
    expect(boosterOff.ok).toBe(true);
    if (boosterOff.ok) expect(boosterOff.pumps.find((entry) => entry.nodeId === 'booster'))
      .toMatchObject({ on: false, flowGpm: 58, horsepowerHp: null, headAddedFt: 0 });
  });

  it('rejects cycles, missing pumps, and unresolved elevation', () => {
    const cycle = pipe('cycle', hydrantA, hydrantB);
    const cyclic = analyzeSnowmakingSystem({ ...base, pipes: [...pipes, cycle],
      selectedPipeIds: [...base.selectedPipeIds, cycle.id] });
    expect(cyclic.ok).toBe(false);
    if (!cyclic.ok) expect(cyclic.diagnostics.map((entry) => entry.code)).toContain('cyclic-network');

    const noPump = analyzeSnowmakingSystem({ ...base, pumpSettings: {} });
    expect(noPump.ok).toBe(false);
    if (!noPump.ok) expect(noPump.diagnostics.map((entry) => entry.code)).toContain('no-active-pump');

    const broken = { ...pipes[2], vertices: pipes[2].vertices.map((vertex, index) =>
      index ? { ...vertex, elevM: null } : vertex) };
    const unresolved = analyzeSnowmakingSystem({ ...base,
      pipes: pipes.map((entry) => entry.id === broken.id ? broken : entry) });
    expect(unresolved.ok).toBe(false);
    if (!unresolved.ok) expect(unresolved.diagnostics.map((entry) => entry.code)).toContain('missing-elevation');
  });

  it('flags insufficient pressure and too-warm guns without inventing delivered flow', () => {
    const low = analyzeSnowmakingSystem({ ...base,
      pumpSettings: { pump: { on: true, horsepowerHp: 1, efficiency: 0.85 } } });
    expect(low.ok).toBe(true);
    if (low.ok) expect(low.guns.every((entry) => entry.status === 'insufficient-pressure')).toBe(true);

    const warm = analyzeSnowmakingSystem({ ...base, wetBulbF: 29 });
    expect(warm.ok).toBe(true);
    if (warm.ok) {
      expect(warm.summary.totalDemandGpm).toBe(0);
      expect(warm.guns.every((entry) => entry.status === 'too-warm')).toBe(true);
    }
  });
});
