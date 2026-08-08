import { describe, expect, it } from 'vitest';
import { analyzeSnowmakingSystems, hazenWilliamsHeadLossFt, pumpHeadFt,
  snowgunStageForWetBulb } from './snowmakingHydraulics';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe } from './types/snowmaking';

const FT_M = 0.3048;
const LAT = 46.9;
const M_PER_LNG = 111320 * Math.cos(LAT * Math.PI / 180);
function point(eastFt: number): [number, number] {
  return [-121.5 + eastFt * FT_M / M_PER_LNG, LAT];
}
function node(id: string, kind: SavedSnowmakingNode['kind'], eastFt: number,
  elevFt: number, sourceId = 'pond-1'): SavedSnowmakingNode {
  return { id, name: id, kind, point: point(eastFt), elevM: elevFt * FT_M,
    ...(kind === 'intake' ? { source: { kind: 'pond' as const, pondId: sourceId } } : {}),
    createdAt: '2026-01-01T00:00:00.000Z' };
}
function pipe(id: string, from: SavedSnowmakingNode, to: SavedSnowmakingNode,
  diameterIn = 8): SavedSnowmakingPipe {
  return { id, name: id, diameterIn: diameterIn as 8, vertices: [
    { point: from.point, elevM: from.elevM, nodeId: from.id },
    { point: to.point, elevM: to.elevM, nodeId: to.id },
  ], segments: [{ id: `${id}:segment:0`, startVertexIndex: 0, endVertexIndex: 1,
    startPumpPort: from.kind === 'pump' ? 'discharge' : null,
    endPumpPort: to.kind === 'pump' ? 'suction' : null }],
  lengthM: Math.hypot((to.point[0] - from.point[0]) * M_PER_LNG,
    (to.elevM ?? 0) - (from.elevM ?? 0)), verticalM: Math.abs((to.elevM ?? 0) - (from.elevM ?? 0)),
  createdAt: '2026-01-01T00:00:00.000Z' };
}
function gun(id: string, hydrant: SavedSnowmakingNode): SavedSnowgun {
  return { id, variantId: 'HKD_ImpulseR5_10s', point: hydrant.point, elevM: hydrant.elevM,
    hydrantId: hydrant.id, createdAt: '2026-01-01T00:00:00.000Z' };
}

describe('snowmaking hydraulic formulas', () => {
  it('locks the gameplay equations and gun stages', () => {
    expect(hazenWilliamsHeadLossFt(1000, 1000, 8)).toBeCloseTo(21.6507, 4);
    expect(pumpHeadFt(3000, 0.85, 4800)).toBeCloseTo(2103.75, 5);
    expect(snowgunStageForWetBulb(29)).toBeNull();
    expect(snowgunStageForWetBulb(28)?.waterFlowGpm).toBe(18);
    expect(snowgunStageForWetBulb(9)?.waterFlowGpm).toBe(58);
  });
});

describe('analyzeSnowmakingSystems', () => {
  const intake = node('intake', 'intake', 0, 0);
  const pump = node('pump', 'pump', 10, 0);
  const junction = node('junction', 'junction', 100, 0);
  const hydrantA = node('hydrant-a', 'hydrant', 200, 33.3);
  const hydrantB = node('hydrant-b', 'hydrant', 200, -20);
  const nodes = [intake, pump, junction, hydrantA, hydrantB];
  const pipes = [pipe('suction', intake, pump), pipe('trunk', pump, junction),
    pipe('branch-a', junction, hydrantA), pipe('branch-b', junction, hydrantB)];
  const guns = [gun('gun-a', hydrantA), gun('gun-b', hydrantB)];
  const base = { nodes, pipes, guns, selectedGunIds: guns.map((entry) => entry.id),
    selectedIntakeNodeIds: ['intake'], wetBulbF: 9,
    pumpSettings: { pump: { on: true, horsepowerHp: 500, efficiency: 0.85 } },
    sourceResourcesByIntakeId: { intake: { sourceKey: 'pond:pond-1', name: 'Pond 1',
      capacityM3: 1000 } } };

  it('adds branch demand, balances head, and reports source runtime', () => {
    const result = analyzeSnowmakingSystems(base);
    expect(result.status).toBe('complete');
    expect(result.summary.requestedDemandGpm).toBe(116);
    expect(result.systems[0].segments.find((entry) => entry.pipeId === 'trunk')?.flowGpm)
      .toBeCloseTo(116, 2);
    expect(result.systems[0].segments.find((entry) => entry.pipeId === 'branch-a')?.flowGpm)
      .toBeCloseTo(58, 2);
    expect(result.systems[0].pumps[0]).toMatchObject({ nodeId: 'pump', status: 'boosting' });
    expect(result.sources[0].runtimeHours).toBeCloseTo(37.956, 2);
  });

  it('allows gravity-only operation and reports insufficient pressure as a solved result', () => {
    const highIntake = node('high-intake', 'intake', 0, 600, 'high');
    const hydrant = node('gravity-gun', 'hydrant', 200, 0);
    const gravity = analyzeSnowmakingSystems({ nodes: [highIntake, hydrant],
      pipes: [pipe('gravity', highIntake, hydrant)], guns: [gun('gravity-gun', hydrant)],
      selectedGunIds: ['gravity-gun'], selectedIntakeNodeIds: ['high-intake'], wetBulbF: 9,
      pumpSettings: {} });
    expect(gravity.status).toBe('complete');
    expect(gravity.systems[0].guns[0].status).toBe('ready');

    const low = analyzeSnowmakingSystems({ ...base,
      pumpSettings: { pump: { on: false, horsepowerHp: null, efficiency: 0.85 } } });
    expect(low.status).toBe('complete');
    expect(low.systems[0].guns.every((entry) => entry.status === 'insufficient-pressure')).toBe(true);
  });

  it('splits equal parallel pipes equally and solves loops', () => {
    const source = node('source', 'intake', 0, 350, 'parallel');
    const hydrant = node('parallel-gun', 'hydrant', 200, 0);
    const result = analyzeSnowmakingSystems({ nodes: [source, hydrant],
      pipes: [pipe('parallel-a', source, hydrant), pipe('parallel-b', source, hydrant)],
      guns: [gun('parallel-gun', hydrant)], selectedGunIds: ['parallel-gun'],
      selectedIntakeNodeIds: ['source'], wetBulbF: 9, pumpSettings: {} });
    expect(result.status).toBe('complete');
    const flows = result.systems[0].segments.map((entry) => entry.flowGpm);
    expect(flows[0]).toBeCloseTo(29, 2);
    expect(flows[1]).toBeCloseTo(29, 2);
  });

  it('balances interacting fixed-head sources and reports receiving flow', () => {
    const upper = node('upper-source', 'intake', 0, 605, 'upper');
    const lower = node('lower-source', 'intake', 0, 600, 'lower');
    const merge = node('merge', 'junction', 100, 590);
    const hydrant = node('shared-gun', 'hydrant', 200, 0);
    const result = analyzeSnowmakingSystems({ nodes: [upper, lower, merge, hydrant],
      pipes: [pipe('upper-main', upper, merge), pipe('lower-main', lower, merge),
        pipe('shared-main', merge, hydrant)], guns: [gun('shared-gun', hydrant)],
      selectedGunIds: ['shared-gun'], selectedIntakeNodeIds: ['upper-source', 'lower-source'],
      wetBulbF: 9, pumpSettings: {}, sourceResourcesByIntakeId: {
        'upper-source': { sourceKey: 'pond:upper', name: 'Upper', capacityM3: 100 },
        'lower-source': { sourceKey: 'pond:lower', name: 'Lower', capacityM3: 100 },
      } });
    expect(result.status).toBe('complete');
    expect(result.systems[0].sources.map((source) => source.withdrawalGpm)
      .reduce((sum, flow) => sum + flow, 0)).toBeCloseTo(58, 2);
    expect(result.sources.some((source) => source.status === 'receiving')).toBe(true);
    expect(result.systems[0].segments.find((entry) => entry.pipeId === 'shared-main')?.flowGpm)
      .toBeCloseTo(58, 2);
  });

  it('keeps independent component failures local', () => {
    const source = node('source-2', 'intake', 500, 350, 'second');
    const hydrant = node('hydrant-2', 'hydrant', 700, 0);
    const secondPipe = pipe('second', source, hydrant);
    const result = analyzeSnowmakingSystems({ ...base,
      nodes: [...nodes, source, hydrant], pipes: [...pipes, secondPipe],
      guns: [...guns, gun('gun-2', hydrant)], selectedGunIds: ['gun-a', 'gun-2'],
      selectedIntakeNodeIds: ['source-2'] });
    expect(result.status).toBe('partial');
    expect(result.systems.some((system) => system.status === 'failed')).toBe(true);
    expect(result.systems.some((system) => system.summary.analyzedGunCount === 1)).toBe(true);
  });

  it('reports missing elevation and unconfigured legacy pump ports', () => {
    const legacyPipes = pipes.map((entry) => ({ ...entry, segments: undefined }));
    const legacy = analyzeSnowmakingSystems({ ...base, pipes: legacyPipes });
    expect(legacy.status).toBe('failed');
    expect(legacy.diagnostics.map((entry) => entry.code)).toContain('unconfigured-pump-ports');

    const broken = { ...pipes[2], vertices: pipes[2].vertices.map((vertex, index) =>
      index ? { ...vertex, elevM: null } : vertex) };
    const unresolved = analyzeSnowmakingSystems({ ...base,
      pipes: pipes.map((entry) => entry.id === broken.id ? broken : entry) });
    expect(unresolved.status).toBe('failed');
    expect(unresolved.diagnostics.map((entry) => entry.code)).toContain('missing-elevation');
  });
});
