import { describe, expect, it } from 'vitest';
import { addGuestLayers, GUEST_HIT_LAYER_ID, GUEST_LAYER_IDS, interpolateGuestPoints, setGuestCompactFrame,
  setGuestPortalData, updateGuestPointData } from './guestLayers';
import type { GuestConnectivity } from './guestConnectivity';

const portal = { version: 1 as const, id: 'entrance', kind: 'guest-entrance' as const,
  type: 'guest-entrance' as const, semantics: 'guest-entrance' as const, direction: 'inbound' as const,
  accepts: 'guests' as const, label: 'Guest Entrance', capacityGuestsPerTick: 12,
  openFromTick: 0, openUntilTick: 86_400, nodeId: 'base', lngLat: [-121.5, 46.9] as const };

function connectivity(reachable: boolean): GuestConnectivity {
  return { state: reachable ? 'reachable' : 'no-open-descent', reachable, portal,
    message: reachable ? 'Summit connects the entrance to 2 open runs.' : 'Resort unreachable.',
    connectedLiftId: 'lift-1', connectedLiftName: 'Summit', reachableRunCount: reachable ? 2 : 0,
    connectionPath: [[-121.5, 46.9], [-121.49, 46.91]], roadAccessLabel: 'Access Road' };
}

describe('guest map connection presentation', () => {
  it('interpolates retained guests while admitting new guests at their authoritative position', () => {
    const previous = [{ id: 'a', lng: -121.5, lat: 46.9, status: 'skiing' }];
    const next = [{ id: 'a', lng: -121.4, lat: 47, status: 'skiing' },
      { id: 'b', lng: -121.3, lat: 47.1, status: 'arriving' }];
    expect(interpolateGuestPoints(previous, next, 0.5)).toEqual([
      { id: 'a', lng: -121.45, lat: 46.95, status: 'skiing' }, next[1],
    ]);
    expect(interpolateGuestPoints(previous, next, 1)).toBe(next);
  });

  it('publishes animation frames as bounded per-feature source diffs', () => {
    const updates: unknown[] = [];
    const map = { getSource: () => ({ updateData: (diff: unknown) => updates.push(diff) }) };
    updateGuestPointData(map as never,
      [{ id: 'moving', lng: 0, lat: 0, status: 'lift-ride' },
        { id: 'departed', lng: 2, lat: 2, status: 'departing' }],
      [{ id: 'moving', lng: 1, lat: 1, status: 'skiing' },
        { id: 'arrived', lng: 3, lat: 3, status: 'arriving' }]);
    expect(updates).toEqual([{ remove: ['departed'], add: [expect.objectContaining({ id: 'arrived' })],
      update: [{ id: 'moving', newGeometry: { type: 'Point', coordinates: [1, 1] },
        addOrUpdateProperties: [{ key: 'status', value: 'skiing' }] }] }]);
  });

  it('installs the connection, halo, marker, and label in declared order', () => {
    const layers: string[] = [], sources = new Set<string>();
    const map = { getSource: (id: string) => sources.has(id) ? {} : undefined,
      addSource: (id: string) => sources.add(id), getLayer: () => undefined,
      addLayer: (layer: { id: string }) => layers.push(layer.id) };
    addGuestLayers(map as never);
    expect(layers).toEqual([...GUEST_LAYER_IDS]);
  });

  it('publishes a status marker and connected-lift line from one reachability result', () => {
    const published: GeoJSON.FeatureCollection[] = [];
    const map = { getSource: () => ({ setData: (next: GeoJSON.FeatureCollection) => { published.push(next); } }) };
    setGuestPortalData(map as never, portal, connectivity(true));
    expect(published[0]?.features.map((feature) => feature.properties?.kind)).toEqual(['connection', 'portal']);
    expect(published[0]?.features.every((feature) => feature.properties?.reachable === true)).toBe(true);
  });

  it('answers delegated guest queries from the exact interpolated GPU position', () => {
    const sources = new Map<string, { setData: (data: unknown) => void; updateData: (data: unknown) => void }>();
    const layers = new Map<string, unknown>();
    const map = {
      getSource: (id: string) => sources.get(id),
      addSource: (id: string) => sources.set(id, { setData: () => {}, updateData: () => {} }),
      getLayer: (id: string) => layers.has(id) ? layers.get(id) : undefined,
      addLayer: (layer: { id: string }) => layers.set(layer.id, layer),
      getLayoutProperty: () => undefined,
      unproject: () => ({ lng: 0, lat: 0 }),
      queryRenderedFeatures: () => [{ id: 'base' }],
    };
    addGuestLayers(map as never);
    const frame = { ids: new Uint32Array([7]), guestIds: new Uint32Array([7]), edgeIndices: new Int32Array([-1]),
      progress: new Float32Array([0]), statusFlags: new Uint32Array([64]), bytesPerGuest: 16 as const, byteLength: 16 };
    setGuestCompactFrame(map as never, frame, [], [0, 0]);
    const custom = layers.get('guest-simulation-dots') as { updateScreenHitIndex: (matrix: Float32Array, width: number, height: number, progress: number) => void };
    custom.updateScreenHitIndex(new Float32Array([
      1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
    ]), 100, 100, 1);
    const hit = (map as never as { queryRenderedFeatures: (point: { x: number; y: number }, options: { layers: string[] }) => readonly { properties?: Record<string, unknown> }[] })
      .queryRenderedFeatures({ x: 75, y: 25 }, { layers: [GUEST_HIT_LAYER_ID] });
    expect(hit[0]?.properties?.id).toBe('guest-000007');
  });
});
