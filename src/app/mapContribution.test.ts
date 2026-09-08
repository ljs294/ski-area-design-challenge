import { describe, expect, it, vi } from 'vitest';
import {
  hitGuardLayers,
  MAP_HIT_RANK,
  MAP_HIT_PRIORITY,
  MAP_LAYER_ORDER,
  MAP_Z_ORDER,
  MapContributionRegistry,
  type ManagedMapContribution,
  type ManagedMapHitContribution,
  type MapHitContribution,
  type MapHitFamilyId,
  type MapHitHoverTarget,
} from './mapContribution';
import type maplibregl from 'maplibre-gl';

/** The layer ids the live map delegates each family's clicks to. */
const HIT_LAYERS: Record<MapHitFamilyId, string[]> = {
  guest: ['guest-simulation-dots'],
  snowmaking: ['snowmaking-node-hit'],
  building: ['building-hit'],
  lift: ['lift-line-hit', 'lift-terminals'],
  trail: ['trail-hit'],
  dam: ['dam-crest-hit', 'dam-pool-fill'],
  pond: ['pond-fill-hit'],
  road: ['road-hit', 'road-pavement'],
  stream: ['local-water-line-hit'],
  lake: ['local-water-fill'],
};

function hitContributions(): MapHitContribution[] {
  return MAP_HIT_PRIORITY.map((id) => ({ id, layerIds: HIT_LAYERS[id], select: vi.fn() }));
}

describe('map layer order', () => {
  it('is the required bottom-to-top order', () => {
    expect([...MAP_LAYER_ORDER]).toEqual([
      'analysis', 'site-boundary', 'road', 'dam', 'pond', 'ski-node-path',
      'trail', 'lift', 'building', 'snowmaking', 'guest',
    ]);
  });

});

describe('map hit priority', () => {
  it('is the required top-to-bottom order', () => {
    expect([...MAP_HIT_PRIORITY]).toEqual([
      'guest', 'snowmaking', 'building', 'lift', 'trail', 'dam', 'pond', 'road', 'stream', 'lake',
    ]);
  });

  it('mirrors the paint order apart from the documented dam/pond inversion', () => {
    const painted = [...MAP_LAYER_ORDER]
      .reverse()
      .filter((id) => (MAP_HIT_PRIORITY as readonly string[]).includes(id));

    // A standalone pond is drawn over a dam's pool, but a dam picks first:
    // its crest is the structure you click, and the pool it impounds is not.
    expect(painted).toEqual(['guest', 'snowmaking', 'building', 'lift', 'trail', 'pond', 'dam', 'road']);
    expect(MAP_HIT_PRIORITY.filter((id) => (MAP_LAYER_ORDER as readonly string[]).includes(id)))
      .toEqual(['guest', 'snowmaking', 'building', 'lift', 'trail', 'dam', 'pond', 'road']);
  });

  it('gives the topmost family no guard at all', () => {
    expect(hitGuardLayers('guest', hitContributions())).toEqual([]);
  });

  it('guards each family with every layer that picks ahead of it, in priority order', () => {
    const contributions = hitContributions();

    expect(hitGuardLayers('snowmaking', contributions)).toEqual(['guest-simulation-dots']);
    expect(hitGuardLayers('building', contributions)).toEqual(['guest-simulation-dots', 'snowmaking-node-hit']);
    expect(hitGuardLayers('lift', contributions)).toEqual(['guest-simulation-dots', 'snowmaking-node-hit', 'building-hit']);
    expect(hitGuardLayers('trail', contributions)).toEqual([
      'guest-simulation-dots', 'snowmaking-node-hit', 'building-hit', 'lift-line-hit', 'lift-terminals',
    ]);
    expect(hitGuardLayers('dam', contributions)).toEqual([
      'guest-simulation-dots', 'snowmaking-node-hit', 'building-hit', 'lift-line-hit', 'lift-terminals', 'trail-hit',
    ]);
    expect(hitGuardLayers('pond', contributions)).toEqual([
      'guest-simulation-dots', 'snowmaking-node-hit', 'building-hit', 'lift-line-hit', 'lift-terminals', 'trail-hit',
      'dam-crest-hit', 'dam-pool-fill',
    ]);
    expect(hitGuardLayers('stream', contributions)).toEqual([
      'guest-simulation-dots', 'snowmaking-node-hit', 'building-hit', 'lift-line-hit', 'lift-terminals', 'trail-hit',
      'dam-crest-hit', 'dam-pool-fill', 'pond-fill-hit', 'road-hit', 'road-pavement',
    ]);
    expect(hitGuardLayers('lake', contributions)).toEqual([
      'guest-simulation-dots', 'snowmaking-node-hit', 'building-hit', 'lift-line-hit', 'lift-terminals', 'trail-hit',
      'dam-crest-hit', 'dam-pool-fill', 'pond-fill-hit', 'road-hit', 'road-pavement',
      'local-water-line-hit',
    ]);
  });

  it('makes a run yield to a lift crossing it', () => {
    const guard = hitGuardLayers('trail', hitContributions());

    expect(guard).toContain('lift-line-hit');
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

  it('rejects an unknown family rather than guarding it with everything', () => {
    expect(() => hitGuardLayers('gondola' as MapHitFamilyId, hitContributions()))
      .toThrow('Unknown map hit family gondola');
  });
});

interface FakeBinding {
  type: string;
  layers: string[];
  listener: (event: unknown) => void;
}

class FakeMap {
  readonly calls: string[] = [];
  readonly canvas = { style: { cursor: '' } };
  readonly bindings: FakeBinding[] = [];
  guarded = false;

  getLayer(id: string): object | undefined {
    return id.startsWith('missing') ? undefined : {};
  }

  setLayoutProperty(id: string, _property: string, value: unknown): void {
    this.calls.push(`visibility:${id}:${String(value)}`);
  }

  getCanvas() {
    return this.canvas;
  }

  queryRenderedFeatures(): object[] {
    return this.guarded ? [{}] : [];
  }

  on(type: string, layers: string[], listener: (event: unknown) => void): void {
    this.bindings.push({ type, layers, listener });
  }

  off(type: string, layers: string[], listener: (event: unknown) => void): void {
    const index = this.bindings.findIndex((binding) => binding.type === type &&
      binding.listener === listener && binding.layers.join() === layers.join());
    if (index >= 0) this.bindings.splice(index, 1);
  }

  emit(type: string, layer: string, featureId = 'feature',
    properties: Record<string, unknown> = {}): void {
    this.emitFeatures(type, layer, [{ properties: { id: featureId, ...properties } }]);
  }

  emitFeatures(type: string, layer: string, features: Array<{ properties: Record<string, unknown> }>): void {
    for (const binding of [...this.bindings]) {
      if (binding.type === type && binding.layers.includes(layer)) {
        binding.listener({ point: { x: 1, y: 2 },
          lngLat: { lng: -121.5, lat: 46.9 }, features });
      }
    }
  }
}

function managedHits(selectLog: string[], hoverLog: string[] = []): Record<MapHitFamilyId, ManagedMapHitContribution> {
  return Object.fromEntries(MAP_HIT_PRIORITY.map((id) => [id, {
    id,
    priority: MAP_HIT_RANK[id],
    layerIds: HIT_LAYERS[id],
    select: (featureId: string, properties?: Readonly<Record<string, unknown>>) =>
      selectLog.push(`${id}:${featureId}${typeof properties?.segmentId === 'string'
        ? `:${properties.segmentId}` : ''}`),
    hover: (target: MapHitHoverTarget | null) =>
      hoverLog.push(`${id}:${target?.featureId ?? 'none'}`),
  }])) as unknown as Record<MapHitFamilyId, ManagedMapHitContribution>;
}

function managedContributions(
  log: string[],
  selectLog: string[] = [],
  hoverLog: string[] = [],
): ManagedMapContribution[] {
  const hits = managedHits(selectLog, hoverLog);
  return MAP_LAYER_ORDER.map((id) => ({
    id,
    zOrder: MAP_Z_ORDER[id],
    hits: id === 'analysis' ? [hits.stream, hits.lake]
      : id === 'road' ? [hits.road]
      : id === 'snowmaking' ? [hits.snowmaking]
      : id === 'building' ? [hits.building]
      : id === 'lift' ? [hits.lift]
      : id === 'trail' ? [hits.trail]
      : id === 'dam' ? [hits.dam]
      : id === 'pond' ? [hits.pond]
      : id === 'guest' ? [hits.guest]
      : [],
    install: ({ mapGeneration, styleGeneration }) =>
      log.push(`install:${id}:m${mapGeneration}s${styleGeneration}`),
    synchronizeData: ({ mapGeneration, styleGeneration }) =>
      log.push(`data:${id}:m${mapGeneration}s${styleGeneration}`),
    setCaptureTransient: id === 'analysis' ? undefined
      : (_context, hidden) => log.push(`${hidden ? 'hide' : 'restore'}:${id}`),
    cleanup: ({ mapGeneration, styleGeneration }) =>
      log.push(`cleanup:${id}:m${mapGeneration}s${styleGeneration}`),
  }));
}

describe('managed map contribution lifecycle', () => {
  it('passes reactive map/style generations and cleans the old style in reverse order', () => {
    const log: string[] = [];
    const map = new FakeMap();
    const registry = new MapContributionRegistry(managedContributions(log));

    registry.attach(map as unknown as maplibregl.Map);
    registry.synchronizeStyle();
    registry.synchronizeData('road');
    registry.synchronizeStyle();

    const installedEntries = MAP_LAYER_ORDER.length * 2;
    expect(log.slice(0, installedEntries)).toEqual(MAP_LAYER_ORDER.flatMap((id) => [
      `install:${id}:m1s1`, `data:${id}:m1s1`,
    ]));
    expect(log[installedEntries]).toBe('data:road:m1s1');
    const cleanupStart = installedEntries + 1;
    const reinstallStart = cleanupStart + MAP_LAYER_ORDER.length;
    expect(log.slice(cleanupStart, reinstallStart)).toEqual([...MAP_LAYER_ORDER].reverse()
      .map((id) => `cleanup:${id}:m1s1`));
    expect(log.slice(reinstallStart)).toEqual(MAP_LAYER_ORDER.flatMap((id) => [
      `install:${id}:m1s2`, `data:${id}:m1s2`,
    ]));
  });

  it('owns capture traversal and final cleanup', () => {
    const log: string[] = [];
    const map = new FakeMap();
    const registry = new MapContributionRegistry(managedContributions(log));
    registry.attach(map as unknown as maplibregl.Map);
    registry.synchronizeStyle();
    log.length = 0;

    registry.setCaptureTransients(true);
    registry.setCaptureTransients(false);
    registry.dispose();

    const transient = MAP_LAYER_ORDER.filter((id) => id !== 'analysis');
    expect(log).toEqual([
      ...transient.map((id) => `hide:${id}`),
      ...transient.map((id) => `restore:${id}`),
      ...[...MAP_LAYER_ORDER].reverse().map((id) => `cleanup:${id}:m1s1`),
    ]);
    expect(map.bindings).toHaveLength(0);
  });

  it('rejects a family whose declared z-order or hit priority drifts', () => {
    const missing = managedContributions([]).filter((entry) => entry.id !== 'trail');
    expect(() => new MapContributionRegistry(missing))
      .toThrow('Missing managed map layer contribution trail');
    const duplicate = [...managedContributions([]), managedContributions([])[0]];
    expect(() => new MapContributionRegistry(duplicate))
      .toThrow('Duplicate managed map layer contribution analysis');

    const wrongZ = managedContributions([]).map((entry) =>
      entry.id === 'road' ? { ...entry, zOrder: 99 } : entry);
    expect(() => new MapContributionRegistry(wrongZ)).toThrow('Invalid z-order');

    const wrongHit = managedContributions([]).map((entry) => entry.id === 'lift'
      ? { ...entry, hits: entry.hits?.map((hit) => ({ ...hit, priority: 99 })) }
      : entry);
    expect(() => new MapContributionRegistry(wrongHit)).toThrow('Invalid hit priority');

    const missingHit = managedContributions([]).map((entry) => entry.id === 'analysis'
      ? { ...entry, hits: entry.hits?.filter((hit) => hit.id !== 'lake') }
      : entry);
    expect(() => new MapContributionRegistry(missingHit))
      .toThrow('Missing managed map hit contribution lake');
  });
});

describe('managed map contribution visibility', () => {
  it('uses a crosshair only for the explicit snowgun selection presentation', () => {
    const map = new FakeMap();
    const registry = new MapContributionRegistry(managedContributions([]));
    registry.attach(map as unknown as maplibregl.Map);

    registry.setPresentation('dashboard-snowmaking-analysis');
    expect(map.canvas.style.cursor).toBe('');
    registry.setPresentation('dashboard-snowmaking-select');
    expect(map.canvas.style.cursor).toBe('crosshair');
    map.emit('mouseenter', 'snowmaking-node-hit');
    expect(map.canvas.style.cursor).toBe('pointer');
    map.emit('mouseleave', 'snowmaking-node-hit');
    expect(map.canvas.style.cursor).toBe('crosshair');
    registry.setPresentation(null);
    expect(map.canvas.style.cursor).toBe('');
  });

  it('temporarily suppresses normal layers and restores the latest preference', () => {
    const contributions = managedContributions([]);
    const modes: Array<string | null> = [];
    contributions[0].visibility = () => [{
      id: 'roads', label: 'Roads', layerIds: ['osm-roads'], visible: true,
    }];
    contributions[0].presentationChanged = (_context, mode) => modes.push(mode);
    const map = new FakeMap();
    const registry = new MapContributionRegistry(contributions);
    registry.attach(map as unknown as maplibregl.Map);
    registry.synchronizeStyle();

    map.calls.length = 0;
    registry.setPresentation('dashboard-trails');
    expect(map.calls.at(-1)).toBe('visibility:osm-roads:none');
    expect(modes.at(-1)).toBe('dashboard-trails');

    registry.toggleVisibility('roads');
    map.calls.length = 0;
    registry.setPresentation(null);
    expect(map.calls.at(-1)).toBe('visibility:osm-roads:none');
    expect(modes.at(-1)).toBeNull();
  });

  it('merges shared descriptors, preserves visibility on restyle, and arbitrates exclusivity', () => {
    const log: string[] = [];
    const contributions = managedContributions(log);
    const analysis = contributions.find((entry) => entry.id === 'analysis')!;
    analysis.visibility = () => [
      { id: 'roads', label: 'Roads', layerIds: ['osm-roads'], visible: true,
        section: 'Master plan' },
      { id: 'slope', label: 'Slope', layerIds: ['slope'], visible: true,
        exclusiveGroup: 'analysis', section: 'Analysis' },
      { id: 'aspect', label: 'Aspect', layerIds: ['aspect'], visible: false,
        exclusiveGroup: 'analysis', section: 'Analysis' },
      { id: 'snow', label: 'Snow', layerIds: ['snow'], visible: false,
        exclusiveGroup: 'analysis', section: 'Analysis' },
    ];
    const road = contributions.find((entry) => entry.id === 'road')!;
    road.visibility = () => [
      { id: 'roads', label: 'Roads', layerIds: ['player-roads'], visible: true,
        section: 'Master plan' },
    ];
    const map = new FakeMap();
    const registry = new MapContributionRegistry(contributions);
    registry.attach(map as unknown as maplibregl.Map);

    expect(registry.synchronizeStyle().find((entry) => entry.id === 'roads')).toEqual({
      id: 'roads', label: 'Roads', layerIds: ['osm-roads', 'player-roads'], visible: true,
      section: 'Master plan',
    });
    registry.toggleVisibility('roads');
    registry.toggleVisibility('snow');
    map.calls.length = 0;
    const afterRestyle = registry.synchronizeStyle();

    expect(afterRestyle.find((entry) => entry.id === 'roads')?.visible).toBe(false);
    expect(afterRestyle.find((entry) => entry.id === 'slope')?.visible).toBe(false);
    expect(afterRestyle.find((entry) => entry.id === 'aspect')?.visible).toBe(false);
    expect(afterRestyle.find((entry) => entry.id === 'snow')?.visible).toBe(true);
    expect(map.calls).toContain('visibility:osm-roads:none');
    expect(map.calls).toContain('visibility:player-roads:none');
  });

  it('preserves a preference when an in-place profile rebuild changes layer IDs', () => {
    const contributions = managedContributions([]);
    let layerIds = ['cover-vector'];
    contributions[0].visibility = () => [{
      id: 'groundcover', label: 'Cover', layerIds, visible: true,
    }];
    const map = new FakeMap();
    const registry = new MapContributionRegistry(contributions);
    registry.attach(map as unknown as maplibregl.Map);
    registry.synchronizeStyle();
    registry.toggleVisibility('groundcover');

    layerIds = ['groundcover-raster'];
    const refreshed = registry.refreshVisibility();

    expect(refreshed.find((entry) => entry.id === 'groundcover')).toMatchObject({
      layerIds: ['groundcover-raster'],
      visible: false,
    });
    expect(map.calls).toContain('visibility:groundcover-raster:none');
  });

  it('rejects conflicting metadata for a shared descriptor', () => {
    const contributions = managedContributions([]);
    contributions[0].visibility = () => [
      { id: 'roads', label: 'Roads', layerIds: ['osm'], visible: true },
    ];
    contributions[2].visibility = () => [
      { id: 'roads', label: 'Access roads', layerIds: ['player'], visible: true },
    ];
    const registry = new MapContributionRegistry(contributions);
    registry.attach(new FakeMap() as unknown as maplibregl.Map);

    expect(() => registry.synchronizeStyle()).toThrow('Conflicting visibility descriptor roads');
  });
});

describe('managed map hit dispatch', () => {
  it('lets a family deterministically resolve multiple rendered candidates', () => {
    const selected: string[] = [];
    const contributions = managedContributions([], selected);
    const trail = contributions.find((entry) => entry.id === 'trail')!;
    const hit = trail.hits![0];
    (hit as { resolve: ManagedMapHitContribution['resolve'] }).resolve = (features, lngLat) => ({
      featureId: String(features.at(-1)?.properties.id), properties: { longitude: lngLat.lng },
    });
    const map = new FakeMap();
    const registry = new MapContributionRegistry(contributions);
    registry.attach(map as unknown as maplibregl.Map);

    map.emitFeatures('click', 'trail-hit', [
      { properties: { id: 'first' } }, { properties: { id: 'nearest' } },
    ]);
    expect(selected).toEqual(['trail:nearest']);
  });

  it('dispatches by priority, owns hover targets, and honors the enabled gate', () => {
    const selected: string[] = [];
    const hovered: string[] = [];
    const map = new FakeMap();
    let enabled = true;
    const registry = new MapContributionRegistry(managedContributions([], selected, hovered));
    registry.attach(map as unknown as maplibregl.Map, () => enabled);

    map.guarded = true;
    map.emit('click', 'trail-hit', 'run');
    expect(selected).toEqual([]);
    map.guarded = false;
    map.emit('click', 'trail-hit', 'run');
    expect(selected).toEqual(['trail:run']);
    map.emit('mouseenter', 'trail-hit');
    map.emit('mousemove', 'trail-hit', 'run');
    expect(map.canvas.style.cursor).toBe('pointer');
    expect(hovered.at(-1)).toBe('trail:run');
    map.emit('mouseleave', 'trail-hit');
    expect(map.canvas.style.cursor).toBe('');
    expect(hovered.at(-1)).toBe('trail:none');

    map.emit('click', 'snowmaking-node-hit', 'pipe-1',
      { segmentId: 'pipe-1:segment:2' });
    expect(selected.at(-1)).toBe('snowmaking:pipe-1:pipe-1:segment:2');

    enabled = false;
    map.emit('click', 'lift-line-hit', 'lift');
    map.emit('mouseenter', 'lift-line-hit');
    map.emit('mousemove', 'lift-line-hit', 'lift');
    expect(selected).toEqual(['trail:run', 'snowmaking:pipe-1:pipe-1:segment:2']);
    expect(map.canvas.style.cursor).toBe('');
    expect(hovered.at(-1)).toBe('lift:none');
  });

  it('clears transient hover on a style reload and explicit interaction cancellation', () => {
    const hovered: string[] = [];
    const map = new FakeMap();
    const registry = new MapContributionRegistry(managedContributions([], [], hovered));
    registry.attach(map as unknown as maplibregl.Map);
    map.emit('mousemove', 'snowmaking-node-hit', 'pipe-1');
    expect(hovered.at(-1)).toBe('snowmaking:pipe-1');

    registry.clearHitHovers();
    expect(hovered.slice(-MAP_HIT_PRIORITY.length)).toEqual(
      MAP_HIT_PRIORITY.map((id) => `${id}:none`));
    registry.synchronizeStyle();
    expect(hovered.at(-1)).toBe('lake:none');
  });
});
