import { describe, expect, it, vi } from 'vitest';
import { TopologyDocument, type TopologyChange, type TopologyState } from './topologyDocument';
import { hydrateJunctions } from '../topology';
import { sanitizeTrails } from '../trails';
import type { SavedLift } from '../types/lifts';
import type { SavedNode, SavedPath } from '../types/topology';
import type { SavedTrail } from '../types/trails';

const rawTrail = {
  id: 'run', name: 'Run', parts: [{
    polygon: [[[-121.501, 46.931], [-121.499, 46.931], [-121.499, 46.929],
      [-121.501, 46.929], [-121.501, 46.931]]],
    centerline: [[-121.5, 46.931], [-121.5, 46.93], [-121.5, 46.929]],
    centerlineElevM: [300, 250, 200],
  }], brushWidthM: 30, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
};

const lift: SavedLift = {
  id: 'lift', name: 'Summit Express', liftClass: 'fixed-grip', chairSize: 4,
  points: [[-121.5, 46.929], [-121.5, 46.931]], endpointElevM: [200, 300],
  lengthM: 250, verticalM: 100, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
};

const node: SavedNode = {
  id: 'legacy-node', name: 'Old pin', point: [-121.5, 46.93], elevM: 250,
  createdAt: '2026-01-01T00:00:00.000Z',
};

function initialState(): TopologyState {
  const trails = sanitizeTrails([rawTrail]);
  return { trails, nodes: [node], paths: [], junctions: hydrateJunctions(trails, []) };
}

function ids(sequence = 0) {
  return () => `new-${++sequence}`;
}

function pathBetween(fromJunctionId: string, toJunctionId: string): SavedPath {
  return {
    id: 'path', name: 'Connector', points: [[-121.5, 46.931], [-121.5, 46.929]],
    pointElevM: [], widthM: 8,
    from: { kind: 'trail', trailId: 'run', point: [-121.5, 46.931] },
    to: { kind: 'trail', trailId: 'run', point: [-121.5, 46.929] },
    fromJunctionId, toJunctionId, lengthM: 200, status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };
}

describe('TopologyDocument snapshots', () => {
  it('exposes an immutable snapshot at revision zero', () => {
    const state = initialState();
    const document = new TopologyDocument(state);
    const snapshot = document.snapshot();

    expect(snapshot.revision).toBe(0);
    expect(snapshot.trails).toBe(state.trails);
    expect(Object.isFrozen(snapshot)).toBe(true);
  });

  it('increments the revision monotonically and leaves earlier snapshots alone', () => {
    const document = new TopologyDocument(initialState());
    const before = document.snapshot();

    const first = document.begin();
    first.patchTrail('run', { closed: true });
    first.commit();
    const second = document.begin();
    second.removeNode('legacy-node');
    second.commit();

    expect(document.snapshot().revision).toBe(2);
    expect(before.revision).toBe(0);
    expect(before.trails[0].closed).toBe(false);
    expect(document.snapshot().trails[0].closed).toBe(true);
    expect(before.nodes).toHaveLength(1);
  });

  it('seeds a clean load from the hydrated collections without publishing', () => {
    const change = vi.fn();
    const state = initialState();

    const document = new TopologyDocument(state, change);

    const snapshot = document.snapshot();
    expect(snapshot.revision).toBe(0);
    expect(snapshot.trails).toBe(state.trails);
    expect(snapshot.nodes).toBe(state.nodes);
    expect(snapshot.paths).toBe(state.paths);
    expect(snapshot.junctions).toBe(state.junctions);
    expect(change).not.toHaveBeenCalled();
  });
});

describe('TopologyDocument commands', () => {
  it('splits a run and publishes the trail and its junction together', () => {
    const changes: TopologyChange[] = [];
    const document = new TopologyDocument(initialState(), (change) => changes.push(change));
    const before = document.snapshot();

    const transaction = document.begin();
    const junction = transaction.splitTrail('run', [-121.5, 46.93], ids());
    const result = transaction.commit();

    expect(junction).not.toBeNull();
    expect(result).toEqual({ ok: true, revision: 1, changed: true });
    expect(changes).toHaveLength(1);
    const { snapshot, changed } = changes[0];
    expect(changed).toEqual({ trails: true, nodes: false, paths: false, junctions: true });
    expect(snapshot.junctions).toHaveLength(before.junctions.length + 1);
    expect(snapshot.junctions.some((entry) => entry.id === junction?.id)).toBe(true);
    expect(snapshot.trails[0].parts[0].segments).toHaveLength(2);
    expect(snapshot.trails[0].parts[0].segments?.[0].toJunctionId).toBe(junction?.id);
  });

  it('reuses an existing junction without moving any collection', () => {
    const change = vi.fn();
    const document = new TopologyDocument(initialState(), change);
    const split = document.begin();
    const junction = split.splitTrail('run', [-121.5, 46.93], ids())!;
    split.commit();
    change.mockClear();

    const again = document.begin();
    const reused = again.splitTrail('run', junction.point, ids(10));
    const reported = again.changed;
    const result = again.commit();

    expect(reused?.id).toBe(junction.id);
    expect(reported).toEqual({ trails: false, nodes: false, paths: false, junctions: false });
    expect(result).toEqual({ ok: true, revision: 1, changed: false });
    expect(change).not.toHaveBeenCalled();
  });

  it('dissolves a junction, merging the segments it separated', () => {
    const document = new TopologyDocument(initialState(), () => {});
    const split = document.begin();
    const junction = split.splitTrail('run', [-121.5, 46.93], ids())!;
    split.commit();

    const removal = document.begin();
    expect(removal.removeJunction(junction.id)).toBe(true);
    removal.commit();

    const snapshot = document.snapshot();
    expect(snapshot.junctions.some((entry) => entry.id === junction.id)).toBe(false);
    expect(snapshot.trails[0].parts[0].segments).toHaveLength(1);
  });

  it('materializes a lift-terminal junction once and reuses it afterwards', () => {
    const document = new TopologyDocument(initialState(), () => {});

    const first = document.begin();
    const terminal = first.liftTerminalJunction([lift], 'lift', 'top', [-121.5, 46.931], ids());
    first.commit();
    const second = document.begin();
    const again = second.liftTerminalJunction([lift], 'lift', 'top', [-121.5, 46.931], ids(10));
    const result = second.commit();

    expect(terminal.liftTerminal).toEqual({ liftId: 'lift', end: 'top' });
    expect(again.id).toBe(terminal.id);
    expect(result).toEqual({ ok: true, revision: 1, changed: false });
  });

  it('removes a legacy free-standing node', () => {
    const document = new TopologyDocument(initialState(), () => {});

    const transaction = document.begin();
    transaction.removeNode('legacy-node');
    const result = transaction.commit();

    expect(result.ok).toBe(true);
    expect(document.snapshot().nodes).toEqual([]);
  });

  it('adds, updates, and removes a connector path', () => {
    const changes: TopologyChange[] = [];
    const document = new TopologyDocument(initialState(), (change) => changes.push(change));
    const junctions = document.snapshot().junctions;
    const path = pathBetween(junctions[0].id, junctions[1].id);

    const add = document.begin();
    add.addPath(path);
    add.commit();
    const update = document.begin();
    update.patchPath('path', { closed: true });
    update.commit();
    const remove = document.begin();
    remove.removePath('path');
    remove.commit();

    expect(changes.map((change) => change.snapshot.paths.length)).toEqual([1, 1, 0]);
    expect(changes[1].snapshot.paths[0].closed).toBe(true);
    expect(changes.every((change) => change.changed.paths)).toBe(true);
    expect(document.snapshot().revision).toBe(3);
  });

  it('publishes a confirmed run and every junction it materialized in one snapshot', () => {
    const changes: TopologyChange[] = [];
    const document = new TopologyDocument(initialState(), (change) => changes.push(change));

    const transaction = document.begin();
    const head = transaction.liftTerminalJunction([lift], 'lift', 'top', [-121.5, 46.931], ids());
    const tail = transaction.splitTrail('run', [-121.5, 46.93], ids(10))!;
    const trail: SavedTrail = {
      ...sanitizeTrails([{ ...rawTrail, id: 'branch', name: 'Branch' }])[0],
      parts: sanitizeTrails([{ ...rawTrail, id: 'branch', name: 'Branch' }])[0].parts.map((part) => ({
        ...part,
        segments: [{ id: 'branch:0:segment:0', centerline: part.centerline,
          centerlineElevM: part.centerlineElevM, fromJunctionId: head.id, toJunctionId: tail.id }],
      })),
    };
    transaction.addTrail(trail);
    const result = transaction.commit();

    expect(result).toEqual({ ok: true, revision: 1, changed: true });
    expect(changes).toHaveLength(1);
    const { snapshot } = changes[0];
    expect(snapshot.trails.map((entry) => entry.id)).toEqual(['run', 'branch']);
    const known = new Set(snapshot.junctions.map((entry) => entry.id));
    expect(known.has(head.id)).toBe(true);
    expect(known.has(tail.id)).toBe(true);
  });

  it('patches a run and prunes only the junctions a removal orphans', () => {
    const document = new TopologyDocument(initialState(), () => {});
    const terminal = document.begin();
    const liftTerminal = terminal.liftTerminalJunction([lift], 'lift', 'top', [-121.5, 46.931], ids());
    terminal.commit();
    const patch = document.begin();
    patch.patchTrail('run', { name: 'Renamed' });
    patch.commit();
    expect(document.snapshot().trails[0].name).toBe('Renamed');

    const removal = document.begin();
    expect(removal.removeTrail('run')).toBe(true);
    removal.commit();

    const snapshot = document.snapshot();
    expect(snapshot.trails).toEqual([]);
    expect(snapshot.junctions.map((entry) => entry.id)).toEqual([liftTerminal.id]);
  });

  it('keeps a junction a surviving connector path still references', () => {
    const document = new TopologyDocument(initialState(), () => {});
    const junctions = document.snapshot().junctions;
    const setup = document.begin();
    setup.addPath(pathBetween(junctions[0].id, junctions[1].id));
    setup.commit();

    const removal = document.begin();
    removal.removeTrail('run');
    removal.commit();

    expect(document.snapshot().junctions.map((entry) => entry.id))
      .toEqual([junctions[0].id, junctions[1].id]);
  });
});

describe('TopologyDocument atomicity', () => {
  it('rejects a transaction built against a superseded revision, writing nothing', () => {
    const changes: TopologyChange[] = [];
    const document = new TopologyDocument(initialState(), (change) => changes.push(change));

    const stale = document.begin();
    stale.splitTrail('run', [-121.5, 46.93], ids());
    stale.addPath(pathBetween('a', 'b'));
    const winner = document.begin();
    winner.patchTrail('run', { closed: true });
    winner.commit();
    const result = stale.commit();

    expect(result).toEqual({ ok: false, reason: 'stale' });
    expect(changes).toHaveLength(1);
    const snapshot = document.snapshot();
    expect(snapshot.revision).toBe(1);
    expect(snapshot.paths).toEqual([]);
    expect(snapshot.trails[0].parts[0].segments).toHaveLength(1);
    expect(snapshot.trails[0].closed).toBe(true);
  });

  it('publishes one coherent snapshot rather than separate intermediate collections', () => {
    const observed: { trails: number; junctions: number }[] = [];
    const document = new TopologyDocument(initialState(), ({ snapshot }) => {
      observed.push({
        trails: snapshot.trails[0].parts[0].segments?.length ?? 0,
        junctions: snapshot.junctions.length,
      });
    });
    const before = document.snapshot().junctions.length;

    const transaction = document.begin();
    transaction.splitTrail('run', [-121.5, 46.93], ids());
    transaction.splitTrail('run', [-121.5, 46.9305], ids(10));
    transaction.commit();

    expect(observed).toEqual([{ trails: 3, junctions: before + 2 }]);
  });

  it('leaves the document untouched when a transaction is abandoned', () => {
    const change = vi.fn();
    const document = new TopologyDocument(initialState(), change);
    const before = document.snapshot();

    const transaction = document.begin();
    transaction.splitTrail('run', [-121.5, 46.93], ids());
    transaction.abort();

    expect(transaction.commit()).toEqual({ ok: false, reason: 'settled' });
    expect(document.snapshot()).toBe(before);
    expect(change).not.toHaveBeenCalled();
  });

  it('refuses to publish the same transaction twice', () => {
    const change = vi.fn();
    const document = new TopologyDocument(initialState(), change);

    const transaction = document.begin();
    transaction.removeNode('legacy-node');

    expect(transaction.commit()).toEqual({ ok: true, revision: 1, changed: true });
    expect(transaction.commit()).toEqual({ ok: false, reason: 'settled' });
    expect(change).toHaveBeenCalledTimes(1);
  });
});
