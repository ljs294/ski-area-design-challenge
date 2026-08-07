import { describe, expect, it } from 'vitest';
import {
  intakeNameFor,
  reconcileSnowmakingNodes,
  ringCenter,
  sanitizeSnowmakingNodes,
  SNOWMAKING_NODE_LABELS,
} from './snowmakingNodes';
import type { SavedDam, SavedPond, SavedSnowmakingNode } from './types/snowmaking';

// Minimal but realistic fixtures, mirroring lifts.test.ts/pondEarthwork.test.ts style.

function makePond(overrides: Partial<SavedPond> = {}): SavedPond {
  return {
    id: 'pond-1',
    name: 'Beaver Pond',
    boundary: [[-121.5, 46.9], [-121.499, 46.9], [-121.499, 46.901], [-121.5, 46.901], [-121.5, 46.9]],
    topElevationM: 1500,
    areaM2: 4000,
    averageDepthM: 3,
    maxDepthM: 4,
    capacityM3: 12000,
    createdAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

function makeDam(overrides: Partial<SavedDam> = {}): SavedDam {
  return {
    id: 'dam-1',
    name: 'North Dam',
    points: [[-121.51, 46.91], [-121.509, 46.91]],
    crestElevationM: 1600,
    streamId: 'stream-1',
    streamName: 'North Creek',
    sourceWidthM: 5,
    inflowM3s: 0.5,
    pondRings: [[[-121.512, 46.911], [-121.510, 46.911], [-121.510, 46.913], [-121.512, 46.913], [-121.512, 46.911]]],
    areaM2: 8000,
    averageDepthM: 5,
    capacityM3: 40000,
    maxDamHeightM: 12,
    createdAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

describe('ringCenter', () => {
  it('centers a closed square ring exactly, not skewed toward the repeated vertex', () => {
    const ring: [number, number][] = [[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]];
    expect(ringCenter(ring)).toEqual([1, 1]);
  });

  it('handles an already-open ring the same way', () => {
    const ring: [number, number][] = [[0, 0], [2, 0], [2, 2], [0, 2]];
    expect(ringCenter(ring)).toEqual([1, 1]);
  });

  it('returns [0,0] for an empty ring rather than throwing', () => {
    expect(ringCenter([])).toEqual([0, 0]);
  });
});

describe('intakeNameFor', () => {
  it('appends " Intake" to the source name', () => {
    expect(intakeNameFor('Beaver Pond')).toBe('Beaver Pond Intake');
  });
});

describe('SNOWMAKING_NODE_LABELS', () => {
  it('has a label for every kind', () => {
    expect(SNOWMAKING_NODE_LABELS).toEqual({
      intake: 'Intake',
      pump: 'Pump',
      junction: 'Junction',
      hydrant: 'Hydrant',
    });
  });
});

describe('reconcileSnowmakingNodes', () => {
  it('creates one intake per dam and one per snowmaking-eligible pond', () => {
    const dam = makeDam();
    const pond = makePond();
    const result = reconcileSnowmakingNodes([], [dam], [pond]);
    expect(result).toHaveLength(2);
    const damNode = result.find((n) => n.source?.kind === 'dam');
    const pondNode = result.find((n) => n.source?.kind === 'pond');
    expect(damNode).toMatchObject({
      name: 'North Dam Intake',
      kind: 'intake',
      elevM: 1600,
      source: { kind: 'dam', damId: 'dam-1' },
    });
    expect(damNode!.point).toEqual(ringCenter(dam.pondRings[0]));
    expect(pondNode).toMatchObject({
      name: 'Beaver Pond Intake',
      kind: 'intake',
      elevM: 1500,
      source: { kind: 'pond', pondId: 'pond-1' },
    });
    expect(pondNode!.point).toEqual(ringCenter(pond.boundary));
  });

  it('skips ponds with isSnowmaking === false', () => {
    const pond = makePond({ isSnowmaking: false });
    const result = reconcileSnowmakingNodes([], [], [pond]);
    expect(result).toHaveLength(0);
  });

  it('includes ponds with isSnowmaking omitted or explicitly true', () => {
    expect(reconcileSnowmakingNodes([], [], [makePond()])).toHaveLength(1);
    expect(reconcileSnowmakingNodes([], [], [makePond({ isSnowmaking: true })])).toHaveLength(1);
  });

  it('drops a node when its pond is deleted on the next reconcile', () => {
    const pond = makePond();
    const seeded = reconcileSnowmakingNodes([], [], [pond]);
    expect(seeded).toHaveLength(1);
    const afterDelete = reconcileSnowmakingNodes(seeded, [], []);
    expect(afterDelete).toHaveLength(0);
  });

  it('drops a node when its pond has isSnowmaking unticked', () => {
    const pond = makePond();
    const seeded = reconcileSnowmakingNodes([], [], [pond]);
    const unticked = { ...pond, isSnowmaking: false };
    const afterUntick = reconcileSnowmakingNodes(seeded, [], [unticked]);
    expect(afterUntick).toHaveLength(0);
  });

  it('recreates a node (with a new id) when the pond is re-ticked', () => {
    // Simpler behavior chosen deliberately: reconcile does not try to
    // remember a dropped node's identity across a delete/recreate cycle,
    // so re-ticking `isSnowmaking` mints a brand-new node/id rather than
    // resurrecting the old one. Nothing currently depends on id stability
    // across that round trip.
    const pond = makePond();
    const seeded = reconcileSnowmakingNodes([], [], [pond]);
    const unticked = { ...pond, isSnowmaking: false };
    const afterUntick = reconcileSnowmakingNodes(seeded, [], [unticked]);
    const reticked = { ...pond, isSnowmaking: true };
    const afterRetick = reconcileSnowmakingNodes(afterUntick, [], [reticked]);
    expect(afterRetick).toHaveLength(1);
    expect(afterRetick[0].id).not.toBe(seeded[0].id);
  });

  it('keeps a renamed node\'s custom name across a reconcile where its source is unchanged', () => {
    const pond = makePond();
    const seeded = reconcileSnowmakingNodes([], [], [pond]);
    const renamed: SavedSnowmakingNode[] = [{ ...seeded[0], name: 'Custom Hydrant Feed' }];
    const result = reconcileSnowmakingNodes(renamed, [], [pond]);
    expect(result).toBe(renamed);
    expect(result[0].name).toBe('Custom Hydrant Feed');
  });

  it('returns the exact same array reference when reconciling an already-consistent set', () => {
    const dam = makeDam();
    const pond = makePond();
    const seeded = reconcileSnowmakingNodes([], [dam], [pond]);
    const reconciled = reconcileSnowmakingNodes(seeded, [dam], [pond]);
    expect(reconciled).toBe(seeded);
  });

  it('leaves a source-less node alone and untouched by reconcile, even with empty dams/ponds', () => {
    const handPlaced: SavedSnowmakingNode = {
      id: 'hydrant-1',
      name: 'Mid-Mountain Hydrant',
      kind: 'hydrant',
      point: [-121.5, 46.9],
      elevM: 1800,
      createdAt: '2026-01-01T00:00:00.000Z',
    };
    const input = [handPlaced];
    const result = reconcileSnowmakingNodes(input, [], []);
    expect(result).toBe(input);
    expect(result[0]).toBe(handPlaced);

    // Still untouched even when dams/ponds are present and would otherwise
    // seed additional nodes.
    const dam = makeDam();
    const withDam = reconcileSnowmakingNodes(input, [dam], []);
    expect(withDam.find((n) => n.id === 'hydrant-1')).toBe(handPlaced);
  });
});

describe('sanitizeSnowmakingNodes', () => {
  const valid: SavedSnowmakingNode = {
    id: 'n1',
    name: 'North Dam Intake',
    kind: 'intake',
    point: [-121.5, 46.9],
    elevM: 1600,
    source: { kind: 'dam', damId: 'dam-1' },
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('passes valid input through unchanged', () => {
    expect(sanitizeSnowmakingNodes([valid])).toEqual([valid]);
  });

  it('rejects non-objects', () => {
    expect(sanitizeSnowmakingNodes([null, undefined, 'x', 5, true, []])).toEqual([]);
  });

  it('rejects rows missing id/name/point/kind/createdAt', () => {
    expect(sanitizeSnowmakingNodes([{ ...valid, id: undefined }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, name: undefined }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, point: undefined }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, point: [1, 'x'] }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, kind: undefined }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, kind: 'reservoir' }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, createdAt: undefined }])).toEqual([]);
    expect(sanitizeSnowmakingNodes([{ ...valid, createdAt: 12345 }])).toEqual([]);
  });

  it('accepts every valid kind literal', () => {
    for (const kind of ['intake', 'pump', 'junction', 'hydrant'] as const) {
      expect(sanitizeSnowmakingNodes([{ ...valid, kind }])).toEqual([{ ...valid, kind }]);
    }
  });

  it('defaults a missing/invalid elevM to null', () => {
    const result = sanitizeSnowmakingNodes([{ ...valid, elevM: undefined }]);
    expect(result[0].elevM).toBeNull();
    const result2 = sanitizeSnowmakingNodes([{ ...valid, elevM: 'high' }]);
    expect(result2[0].elevM).toBeNull();
  });

  it('strips a malformed source but keeps the node', () => {
    const wrongKind = { ...valid, source: { kind: 'reservoir', reservoirId: 'r1' } };
    const result = sanitizeSnowmakingNodes([wrongKind]);
    expect(result).toHaveLength(1);
    expect(result[0].source).toBeUndefined();

    const missingId = { ...valid, source: { kind: 'dam' } };
    const result2 = sanitizeSnowmakingNodes([missingId]);
    expect(result2).toHaveLength(1);
    expect(result2[0].source).toBeUndefined();

    const nonStringId = { ...valid, source: { kind: 'pond', pondId: 42 } };
    const result3 = sanitizeSnowmakingNodes([nonStringId]);
    expect(result3).toHaveLength(1);
    expect(result3[0].source).toBeUndefined();
  });

  it('passes through a node with no source at all', () => {
    const { source: _source, ...withoutSource } = valid;
    const result = sanitizeSnowmakingNodes([withoutSource]);
    expect(result).toHaveLength(1);
    expect(result[0].source).toBeUndefined();
  });
});
