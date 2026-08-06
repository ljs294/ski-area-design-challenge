import { describe, expect, it, vi } from 'vitest';
import {
  hitGuardLayers,
  MAP_HIT_PRIORITY,
  MAP_LAYER_ORDER,
  orderContributions,
  orderHitContributions,
  type MapContribution,
  type MapHitContribution,
  type MapHitFamilyId,
} from './mapContribution';

/** The layer ids the live map delegates each family's clicks to. */
const HIT_LAYERS: Record<MapHitFamilyId, string[]> = {
  snowmaking: ['snowmaking-node-hit'],
  lift: ['lift-line-casing', 'lift-terminals'],
  trail: ['trail-fill'],
  dam: ['dam-crest-hit', 'dam-pool-fill'],
  pond: ['pond-fill-hit'],
  stream: ['local-water-line-hit'],
  lake: ['local-water-fill'],
};

function hitContributions(): MapHitContribution[] {
  return MAP_HIT_PRIORITY.map((id) => ({ id, layerIds: HIT_LAYERS[id], select: vi.fn() }));
}

function contribution(id: MapContribution['id'], log: string[]): MapContribution {
  return {
    id,
    install: () => log.push(`install:${id}`),
    setCaptureTransient: (hidden) => log.push(`${hidden ? 'hide' : 'restore'}:${id}`),
  };
}

describe('map layer order', () => {
  it('is the required bottom-to-top order', () => {
    expect([...MAP_LAYER_ORDER]).toEqual([
      'analysis', 'site-boundary', 'road', 'dam', 'pond', 'ski-node-path',
      'trail', 'lift', 'snowmaking',
    ]);
  });

  it('installs every family bottom-to-top however they were registered', () => {
    const log: string[] = [];
    const registered = [...MAP_LAYER_ORDER]
      .reverse()
      .map((id) => contribution(id, log));

    for (const entry of orderContributions(registered)) entry.install();

    expect(log).toEqual(MAP_LAYER_ORDER.map((id) => `install:${id}`));
  });

  it('refuses a set that is missing a family', () => {
    const log: string[] = [];
    const incomplete = MAP_LAYER_ORDER
      .filter((id) => id !== 'trail')
      .map((id) => contribution(id, log));

    expect(() => orderContributions(incomplete)).toThrow('Missing map layer contribution trail');
  });

  it('refuses a family registered twice', () => {
    const log: string[] = [];
    const duplicated = [...MAP_LAYER_ORDER.map((id) => contribution(id, log)),
      contribution('lift', log)];

    expect(() => orderContributions(duplicated)).toThrow('Duplicate map layer contribution lift');
  });

  it('walks capture hide and restore in the same order, skipping families with no transient', () => {
    const log: string[] = [];
    const registered: MapContribution[] = MAP_LAYER_ORDER.map((id) =>
      id === 'analysis' || id === 'site-boundary'
        ? { id, install: () => log.push(`install:${id}`) }
        : contribution(id, log));
    const ordered = orderContributions(registered);

    for (const entry of ordered) entry.setCaptureTransient?.(true);
    for (const entry of ordered) entry.setCaptureTransient?.(false);

    const transient = ['road', 'dam', 'pond', 'ski-node-path', 'trail', 'lift', 'snowmaking'];
    expect(log).toEqual([
      ...transient.map((id) => `hide:${id}`),
      ...transient.map((id) => `restore:${id}`),
    ]);
  });
});

describe('map hit priority', () => {
  it('is the required top-to-bottom order', () => {
    expect([...MAP_HIT_PRIORITY]).toEqual([
      'snowmaking', 'lift', 'trail', 'dam', 'pond', 'stream', 'lake',
    ]);
  });

  it('mirrors the paint order apart from the documented dam/pond inversion', () => {
    const painted = [...MAP_LAYER_ORDER]
      .reverse()
      .filter((id) => (MAP_HIT_PRIORITY as readonly string[]).includes(id));

    // A standalone pond is drawn over a dam's pool, but a dam picks first:
    // its crest is the structure you click, and the pool it impounds is not.
    expect(painted).toEqual(['snowmaking', 'lift', 'trail', 'pond', 'dam']);
    expect(MAP_HIT_PRIORITY.filter((id) => (MAP_LAYER_ORDER as readonly string[]).includes(id)))
      .toEqual(['snowmaking', 'lift', 'trail', 'dam', 'pond']);
  });

  it('gives the topmost family no guard at all', () => {
    expect(hitGuardLayers('snowmaking', hitContributions())).toEqual([]);
  });

  it('guards each family with every layer that picks ahead of it, in priority order', () => {
    const contributions = hitContributions();

    expect(hitGuardLayers('lift', contributions)).toEqual(['snowmaking-node-hit']);
    expect(hitGuardLayers('trail', contributions)).toEqual([
      'snowmaking-node-hit', 'lift-line-casing', 'lift-terminals',
    ]);
    expect(hitGuardLayers('dam', contributions)).toEqual([
      'snowmaking-node-hit', 'lift-line-casing', 'lift-terminals', 'trail-fill',
    ]);
    expect(hitGuardLayers('pond', contributions)).toEqual([
      'snowmaking-node-hit', 'lift-line-casing', 'lift-terminals', 'trail-fill',
      'dam-crest-hit', 'dam-pool-fill',
    ]);
    expect(hitGuardLayers('stream', contributions)).toEqual([
      'snowmaking-node-hit', 'lift-line-casing', 'lift-terminals', 'trail-fill',
      'dam-crest-hit', 'dam-pool-fill', 'pond-fill-hit',
    ]);
    expect(hitGuardLayers('lake', contributions)).toEqual([
      'snowmaking-node-hit', 'lift-line-casing', 'lift-terminals', 'trail-fill',
      'dam-crest-hit', 'dam-pool-fill', 'pond-fill-hit', 'local-water-line-hit',
    ]);
  });

  it('makes a run yield to a lift crossing it', () => {
    const guard = hitGuardLayers('trail', hitContributions());

    expect(guard).toContain('lift-line-casing');
    expect(guard).toContain('lift-terminals');
  });

  it('leaves exactly one family unguarded against any given pair', () => {
    const contributions = hitContributions();

    for (const higher of MAP_HIT_PRIORITY) {
      for (const lower of MAP_HIT_PRIORITY) {
        if (higher === lower) continue;
        const higherFirst = MAP_HIT_PRIORITY.indexOf(higher) < MAP_HIT_PRIORITY.indexOf(lower);
        const lowerGuard = hitGuardLayers(lower, contributions);
        const yields = HIT_LAYERS[higher].every((layerId) => lowerGuard.includes(layerId));
        expect(yields).toBe(higherFirst);
      }
    }
  });

  it('orders hit contributions top-to-bottom and refuses an incomplete set', () => {
    const registered = [...hitContributions()].reverse();

    expect(orderHitContributions(registered).map((entry) => entry.id))
      .toEqual([...MAP_HIT_PRIORITY]);
    expect(() => orderHitContributions(registered.filter((entry) => entry.id !== 'lake')))
      .toThrow('Missing map hit contribution lake');
  });

  it('rejects an unknown family rather than guarding it with everything', () => {
    expect(() => hitGuardLayers('gondola' as MapHitFamilyId, hitContributions()))
      .toThrow('Unknown map hit family gondola');
  });
});
