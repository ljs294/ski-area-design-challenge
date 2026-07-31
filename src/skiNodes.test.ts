import { describe, expect, it } from 'vitest';
import { haversineMeters } from './geo';
import {
  anchorTargetId,
  describeAnchor,
  DEFAULT_PATH_WIDTH_M,
  MAX_PATH_WIDTH_M,
  MIN_PATH_WIDTH_M,
  nextNodeName,
  nextPathName,
  pathLengthM,
  sanitizeAnchor,
  sanitizeNodes,
  sanitizePaths,
} from './skiNodes';
import type { AnchorRef, SavedNode, SavedPath } from './skiNodes';

// Lat/lng offsets around lat 46.93, lng -121.5; 0.001 deg lat is ~111 m.
const A: [number, number] = [-121.5, 46.93];
const B: [number, number] = [-121.5, 46.929];
const C: [number, number] = [-121.5, 46.928];

describe('pathLengthM', () => {
  it('sums a 2-point line', () => {
    expect(pathLengthM([A, B])).toBeCloseTo(haversineMeters(A, B), 8);
    expect(pathLengthM([A, B])).toBeCloseTo(111, 0);
  });

  it('sums a multi-point line', () => {
    expect(pathLengthM([A, B, C])).toBeCloseTo(haversineMeters(A, B) + haversineMeters(B, C), 8);
    expect(pathLengthM([A, B, C])).toBeCloseTo(222, 0);
  });

  it('is zero for fewer than two points', () => {
    expect(pathLengthM([A])).toBe(0);
    expect(pathLengthM([])).toBe(0);
  });
});

describe('sanitizeAnchor', () => {
  it('accepts a valid lift anchor', () => {
    const raw = { kind: 'lift', liftId: 'l1', end: 'top', point: A };
    expect(sanitizeAnchor(raw)).toEqual(raw);
  });

  it('accepts a valid trail anchor', () => {
    const raw = { kind: 'trail', trailId: 't1', point: A };
    expect(sanitizeAnchor(raw)).toEqual(raw);
  });

  it('accepts a valid path anchor', () => {
    const raw = { kind: 'path', pathId: 'p1', point: A };
    expect(sanitizeAnchor(raw)).toEqual(raw);
  });

  it('accepts a valid node anchor', () => {
    const raw = { kind: 'node', nodeId: 'n1', point: A };
    expect(sanitizeAnchor(raw)).toEqual(raw);
  });

  it('rejects an unknown kind', () => {
    expect(sanitizeAnchor({ kind: 'bogus', point: A })).toBeUndefined();
  });

  it('rejects a missing id for the kind', () => {
    expect(sanitizeAnchor({ kind: 'lift', end: 'top', point: A })).toBeUndefined();
    expect(sanitizeAnchor({ kind: 'trail', point: A })).toBeUndefined();
  });

  it('rejects a non-finite point', () => {
    expect(sanitizeAnchor({ kind: 'node', nodeId: 'n1', point: [NaN, 1] })).toBeUndefined();
    expect(sanitizeAnchor({ kind: 'node', nodeId: 'n1', point: [1] })).toBeUndefined();
    expect(sanitizeAnchor({ kind: 'node', nodeId: 'n1' })).toBeUndefined();
  });

  it('rejects a lift anchor missing `end`', () => {
    expect(sanitizeAnchor({ kind: 'lift', liftId: 'l1', point: A })).toBeUndefined();
  });

  it('rejects a lift anchor with a bad `end` value', () => {
    expect(sanitizeAnchor({ kind: 'lift', liftId: 'l1', end: 'middle', point: A })).toBeUndefined();
  });

  it('rejects non-object input', () => {
    expect(sanitizeAnchor(null)).toBeUndefined();
    expect(sanitizeAnchor('lift')).toBeUndefined();
    expect(sanitizeAnchor(undefined)).toBeUndefined();
  });
});

describe('anchorTargetId / describeAnchor', () => {
  it('extracts the referenced entity id per kind', () => {
    expect(anchorTargetId({ kind: 'lift', liftId: 'l1', end: 'top', point: A })).toBe('l1');
    expect(anchorTargetId({ kind: 'trail', trailId: 't1', point: A })).toBe('t1');
    expect(anchorTargetId({ kind: 'path', pathId: 'p1', point: A })).toBe('p1');
    expect(anchorTargetId({ kind: 'node', nodeId: 'n1', point: A })).toBe('n1');
  });

  it('describes each anchor kind generically', () => {
    expect(describeAnchor({ kind: 'lift', liftId: 'l1', end: 'top', point: A })).toBe('Lift top');
    expect(describeAnchor({ kind: 'lift', liftId: 'l1', end: 'base', point: A })).toBe('Lift base');
    expect(describeAnchor({ kind: 'trail', trailId: 't1', point: A })).toBe('On a run');
    expect(describeAnchor({ kind: 'path', pathId: 'p1', point: A })).toBe('On a path');
    expect(describeAnchor({ kind: 'node', nodeId: 'n1', point: A })).toBe('Node');
  });
});

describe('sanitizeNodes', () => {
  const valid = { id: 'n1', name: 'Node 1', point: A, elevM: 2000, createdAt: '2026-01-01T00:00:00.000Z' };

  it('drops records with a bad id, name, or point', () => {
    expect(sanitizeNodes([
      null,
      { id: 1, name: 'x', point: A },
      { id: 'x', name: 1, point: A },
      { id: 'x', name: 'x', point: [NaN, 1] },
      { id: 'x', name: 'x', point: A },
    ])).toHaveLength(1);
  });

  it('keeps a valid node', () => {
    const out = sanitizeNodes([valid]);
    expect(out).toHaveLength(1);
    expect(out[0]).toMatchObject({ id: 'n1', name: 'Node 1', point: A, elevM: 2000 });
  });

  it('coerces a bad anchor to undefined while keeping the node', () => {
    const out = sanitizeNodes([{ ...valid, anchor: { kind: 'bogus' } }]);
    expect(out).toHaveLength(1);
    expect(out[0].anchor).toBeUndefined();
  });

  it('keeps a valid anchor', () => {
    const anchor: AnchorRef = { kind: 'lift', liftId: 'l1', end: 'base', point: A };
    const out = sanitizeNodes([{ ...valid, anchor }]);
    expect(out[0].anchor).toEqual(anchor);
  });

  it('coerces a non-finite elevM to null', () => {
    expect(sanitizeNodes([{ ...valid, elevM: NaN }])[0].elevM).toBeNull();
    expect(sanitizeNodes([{ ...valid, elevM: null }])[0].elevM).toBeNull();
    expect(sanitizeNodes([{ ...valid, elevM: 'high' }])[0].elevM).toBeNull();
  });

  it('defaults createdAt when missing/invalid', () => {
    const { createdAt: _drop, ...noCreatedAt } = valid;
    expect(typeof sanitizeNodes([noCreatedAt])[0].createdAt).toBe('string');
  });
});

describe('sanitizePaths', () => {
  const fromAnchor: AnchorRef = { kind: 'lift', liftId: 'l1', end: 'base', point: A };
  const toAnchor: AnchorRef = { kind: 'node', nodeId: 'n1', point: C };
  const valid: SavedPath = {
    id: 'p1',
    name: 'Path 1',
    points: [A, B, C],
    pointElevM: [2000, 1950, 1900],
    widthM: 6,
    from: fromAnchor,
    to: toAnchor,
    lengthM: 999999, // deliberately wrong — sanitize must recompute
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('drops a path missing `from` or `to`', () => {
    expect(sanitizePaths([{ ...valid, from: undefined }])).toEqual([]);
    expect(sanitizePaths([{ ...valid, to: undefined }])).toEqual([]);
    expect(sanitizePaths([{ ...valid, from: { kind: 'bogus' } }])).toEqual([]);
  });

  it('drops a path with fewer than two distinct points', () => {
    expect(sanitizePaths([{ ...valid, points: [A] }])).toEqual([]);
    expect(sanitizePaths([{ ...valid, points: [A, A, A] }])).toEqual([]);
  });

  it('dedupes coincident points and keeps pointElevM aligned', () => {
    const points = [A, A, B, C];
    const pointElevM = [2000, 2000, 1950, 1900];
    const out = sanitizePaths([{ ...valid, points, pointElevM }]);
    expect(out).toHaveLength(1);
    expect(out[0].points).toEqual([A, B, C]);
    expect(out[0].pointElevM).toHaveLength(out[0].points.length);
    expect(out[0].pointElevM).toEqual([2000, 1950, 1900]);
  });

  it('drops pointElevM entirely when it does not match points length', () => {
    const out = sanitizePaths([{ ...valid, pointElevM: [1] }]);
    expect(out[0].pointElevM).toEqual([]);
  });

  it('recomputes lengthM even when the stored value is wrong', () => {
    const out = sanitizePaths([valid]);
    expect(out[0].lengthM).not.toBe(999999);
    expect(out[0].lengthM).toBeCloseTo(pathLengthM(valid.points), 8);
  });

  it('clamps widthM into range, defaulting when absent/non-finite', () => {
    expect(sanitizePaths([{ ...valid, widthM: 999 }])[0].widthM).toBe(MAX_PATH_WIDTH_M);
    expect(sanitizePaths([{ ...valid, widthM: -5 }])[0].widthM).toBe(MIN_PATH_WIDTH_M);
    expect(sanitizePaths([{ ...valid, widthM: NaN }])[0].widthM).toBe(DEFAULT_PATH_WIDTH_M);
    const { widthM: _drop, ...noWidth } = valid;
    expect(sanitizePaths([noWidth])[0].widthM).toBe(DEFAULT_PATH_WIDTH_M);
  });

  it('defaults status when missing/invalid', () => {
    expect(sanitizePaths([{ ...valid, status: 'bogus' }])[0].status).toBe('complete');
    const { status: _drop, ...noStatus } = valid;
    expect(sanitizePaths([noStatus])[0].status).toBe('complete');
    expect(sanitizePaths([{ ...valid, status: 'planning' }])[0].status).toBe('planning');
  });

  it('reads closed strictly', () => {
    expect(sanitizePaths([{ ...valid, closed: true }])[0].closed).toBe(true);
    expect(sanitizePaths([{ ...valid, closed: 'yes' }])[0].closed).toBe(false);
    expect(sanitizePaths([valid])[0].closed).toBe(false);
  });

  it('round-trips a fully valid path unchanged except for recomputed lengthM', () => {
    const out = sanitizePaths([valid])[0];
    expect(out).toEqual({ ...valid, lengthM: pathLengthM(valid.points), closed: false });
  });
});

describe('nextNodeName / nextPathName', () => {
  it('skips taken node names', () => {
    const node = (name: string) => ({ name }) as SavedNode;
    expect(nextNodeName([])).toBe('Node 1');
    expect(nextNodeName([node('Node 1'), node('Node 3')])).toBe('Node 2');
  });

  it('skips taken path names', () => {
    const path = (name: string) => ({ name }) as SavedPath;
    expect(nextPathName([])).toBe('Path 1');
    expect(nextPathName([path('Path 1'), path('Path 3')])).toBe('Path 2');
  });
});
