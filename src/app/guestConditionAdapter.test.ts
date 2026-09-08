import { describe, expect, it } from 'vitest';
import type { NetworkEdge, SkiNetwork } from '../network';
import type { SnowGrid } from '../types/snow';
import { isConditionSnapshot } from '../guestSimulation/conditions.ts';
import {
  DEFAULT_DESCENT_GROOMING_QUALITY,
  buildGuestConditionSnapshot,
  conditionSnapshotInputFromSkiNetwork,
  conditionSnapshotFromSkiNetwork,
} from './guestConditionAdapter';

function trail(id: string, difficulty: 'green' | 'blue' | 'black' | 'red', path: [number, number][]): NetworkEdge {
  return { id, kind: 'trail', path, difficulty } as unknown as NetworkEdge;
}

function path(id: string): NetworkEdge {
  return { id, kind: 'path', path: [[0, 0], [1, 0]], difficulty: 'green' } as unknown as NetworkEdge;
}

function lift(id: string): NetworkEdge {
  return { id, kind: 'lift', path: [[0, 0], [0, 1]], capacityPph: 1_200 } as unknown as NetworkEdge;
}

function network(edges: readonly NetworkEdge[]): SkiNetwork {
  return { edges: [...edges] } as unknown as SkiNetwork;
}

function grid(surface: readonly number[], depthM: readonly number[]): SnowGrid {
  return { bounds: { west: 0, south: 0, east: 1, north: 1 }, width: 3, height: 2,
    surface: new Uint8Array([...surface, ...surface]), depthM: new Float32Array([...depthM, ...depthM]) };
}

describe('guestConditionAdapter', () => {
  it('creates a stable revisioned snapshot independent of network edge order', () => {
    const edges = [trail('t:z', 'red', [[0, 0.5], [1, 0.5]]), path('x:a'), lift('l:one')];
    const first = conditionSnapshotFromSkiNetwork(network(edges), null, { revision: 7, tick: 90, sampleCount: 3 });
    const second = conditionSnapshotFromSkiNetwork(network([...edges].reverse()), null, { revision: 7, tick: 90, sampleCount: 3 });
    expect(first).toEqual(second);
    expect(first.revision).toBe(7);
    expect(first.tick).toBe(90);
    expect(first.edges.map((edge) => edge.edgeId)).toEqual(['l:one', 't:z', 'x:a']);
    expect(first.checksum).toBe(second.checksum);
    expect(isConditionSnapshot(first)).toBe(true);
    expect(Object.isFrozen(first)).toBe(true);
  });

  it('samples descent geometry and derives coverage, depth, and snow quality', () => {
    const snapshot = conditionSnapshotFromSkiNetwork(network([
      trail('t:sampled', 'blue', [[0, 0.5], [1, 0.5]]),
    ]), grid([1, 0, 1], [0.3, 0, 0.3]), { sampleCount: 3 });
    const edge = snapshot.edges[0]!;
    expect(edge.baseDifficulty).toBe(0.45);
    expect(edge.grooming.quality).toBe(DEFAULT_DESCENT_GROOMING_QUALITY);
    expect(edge.coverage.fraction).toBeCloseTo(2 / 3);
    expect(edge.coverage.depthCm).toBeCloseTo(20);
    expect(edge.snowQuality.quality).toBeGreaterThan(0);
    expect(edge.snowQuality.quality).toBeLessThan(0.96);
  });

  it('uses explicit safe defaults for optional snow and non-descent edges', () => {
    const snapshot = buildGuestConditionSnapshot(network([
      trail('t:no-snow-provider', 'green', [[0, 0], [1, 0]]), path('x:connector'), lift('l:lift'),
    ]));
    const descent = snapshot.edges.find((edge) => edge.edgeId === 't:no-snow-provider')!;
    const connector = snapshot.edges.find((edge) => edge.edgeId === 'x:connector')!;
    const chair = snapshot.edges.find((edge) => edge.edgeId === 'l:lift')!;
    expect(descent.coverage.fraction).toBe(1);
    expect(descent.coverage.depthCm).toBe(0);
    expect(descent.snowQuality.quality).toBe(0.5);
    expect(connector.baseDifficulty).toBe(0.1);
    expect(connector.grooming.quality).toBe(1);
    expect(connector.coverage.fraction).toBe(1);
    expect(chair.baseDifficulty).toBe(0);
    expect(chair.grooming.quality).toBe(1);
    expect(chair.snowQuality.quality).toBe(1);
    expect(chair.coverage.fraction).toBe(1);
  });

  it('returns a domain input when integration needs to inspect or compose it first', () => {
    const input = conditionSnapshotInputFromSkiNetwork(network([trail('t:input', 'black', [[0, 0], [1, 0]])]), null,
      { revision: 3, tick: 12 });
    expect(input.version).toBe(1);
    expect(input.revision).toBe(3);
    expect(input.tick).toBe(12);
    expect(input.edges).toHaveLength(1);
    expect(input.edges[0]?.edgeId).toBe('t:input');
  });

  it('rejects unsafe sampling and grooming options before producing a snapshot', () => {
    const source = network([trail('t:bad-options', 'blue', [[0, 0], [1, 0]])]);
    expect(() => conditionSnapshotFromSkiNetwork(source, null, { sampleCount: 1 })).toThrow(/sampleCount/);
    expect(() => conditionSnapshotFromSkiNetwork(source, null, { descentGroomingQuality: 1.1 })).toThrow(/grooming quality/);
    expect(() => conditionSnapshotFromSkiNetwork(source, null, { coverageThresholdM: -1 })).toThrow(/threshold/);
  });
});
