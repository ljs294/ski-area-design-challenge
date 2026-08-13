import { describe, expect, it } from 'vitest';
import { buildSkiNetwork } from '../network';
import { snowmakingPipeSegments } from '../snowmakingNetwork';
import type { SavedLift } from '../types';
import { addDashboardMapLayers, dashboardGeoJSON, orientedSnowmakingFlow, snowmakingArrowGlyphRotation, snowmakingPumpArmMarker,
  snowmakingGunColor, snowmakingGunVisualState, snowmakingSegmentMidpoint, type DashboardMapData } from './dashboardMapLayers';

const lift: SavedLift = {
  id: 'lift-1', identifier: 'A', name: 'Summit', liftClass: 'fixed-grip', chairSize: 4,
  points: [[-121.5, 46.9], [-121.49, 46.91]], endpointElevM: [1000, 1200],
  lengthM: 1000, verticalM: 200, status: 'complete', createdAt: '2026-01-01',
};

function data(kind: DashboardMapData['kind']): DashboardMapData {
  return {
    kind, dark: false, units: 'metric', network: buildSkiNetwork([], [lift]),
    selectedLiftId: null, selectedEdgeId: null, dams: [], ponds: [], lakes: [],
    trails: [], lifts: [lift], nodes: [{ id: 'node-1', name: 'Pump', kind: 'pump',
      labelNumber: 1, point: [-121.5, 46.9], elevM: 1000, createdAt: '2026-01-01' }],
    pipes: [{ id: 'pipe-1', name: 'Main', diameterIn: 8, lengthM: 100,
      verticalM: 10, vertices: [
        { point: [-121.5, 46.9], elevM: 1000, nodeId: 'node-1' },
        { point: [-121.49, 46.91], elevM: 1010, nodeId: null },
      ], segments: [{ id: 'pipe-1:segment:0', startVertexIndex: 0, endVertexIndex: 1,
        startPumpPort: 'discharge', endPumpPort: null }], createdAt: '2026-01-01' }],
    guns: [], coverDisplay: null, terrainRecord: null, selectedSnowmaking: null,
    snowmakingPresentation: null,
  };
}

describe('dashboard MapLibre projection', () => {
  it('gives operating status precedence over analyzed selection status', () => {
    expect(snowmakingGunColor(snowmakingGunVisualState({ analysis: true,
      selected: true, status: 'ready', operating: true }))).toBe('#166534');
    expect(snowmakingGunColor(snowmakingGunVisualState({ analysis: true,
      selected: true, status: 'failed', operating: true }))).toBe('#991b1b');
    expect(snowmakingGunColor(snowmakingGunVisualState({ analysis: true,
      selected: true, status: 'ready', operating: false }))).toBe('#86efac');
    expect(snowmakingGunColor(snowmakingGunVisualState({ analysis: true,
      selected: false, status: null, operating: false }))).toBe('#9ca3af');
  });

  it('projects the ski graph with grid, edge identity, and lift hit identity', () => {
    const result = dashboardGeoJSON(data('trails'));
    expect(result.features.some((row) => row.properties?.kind === 'grid')).toBe(true);
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'trail-edge', edgeKind: 'lift', id: 'lift-1' }),
    }));
  });

  it('projects snowmaking entities without mounting a second map surface', () => {
    const result = dashboardGeoJSON(data('snowmaking'));
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'snow-node', id: 'node-1' }),
    }));
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'snow-pipe', id: 'pipe-1' }),
    }));
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'snow-pump-direction', id: 'node-1',
        port: 'discharge', portLabel: 'OUT' }),
    }));
    expect(result.features.filter((row) => row.properties?.kind === 'backdrop')).toHaveLength(1);
  });

  it('projects transient lasso geometry and highlighted gun properties', () => {
    const input = data('snowmaking');
    input.guns = [{ id: 'gun-1', variantId: 'HKD_ImpulseR5_20t', point: [-121.495, 46.905],
      elevM: 1000, hydrantId: 'hydrant-1', createdAt: '2026-01-01' }];
    input.snowmakingLasso = { rect: { minX: 1, minY: 2, maxX: 3, maxY: 4 },
      geoBounds: [-121.5, 46.9, -121.49, 46.91], gunIds: ['gun-1'] };
    input.snowmakingPresentation = {
      mode: 'analysis', segments: [], relevantSegmentColors: new Map(), selectedGunIds: new Set(['gun-1']),
      gunStatuses: { 'gun-1': 'ready' }, invalidPumpIds: new Set(), pressureRange: null,
      showGunTypes: false, toggleGun: () => {}, setGuns: () => {}, setHoveredSegment: () => {},
    };
    const result = dashboardGeoJSON(input);
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'snow-gun-lasso' }),
      geometry: { type: 'Polygon', coordinates: [[[-121.5, 46.9], [-121.49, 46.9],
        [-121.49, 46.91], [-121.5, 46.91], [-121.5, 46.9]]] },
    }));
    expect(result.features).toContainEqual(expect.objectContaining({
      properties: expect.objectContaining({ kind: 'snow-gun', lassoed: true,
        analysis: true, operating: false, status: 'ready' }),
    }));
  });

  it('projects compact hydraulic labels at the physical segment midpoint', () => {
    const input = data('snowmaking');
    input.snowmakingPresentation = {
      mode: 'analysis',
      segments: [{ id: 'pipe-1:segment:0', pipeId: 'pipe-1', segmentIndex: 0,
        fromNodeKey: 'node-1', toNodeKey: 'end', flowGpm: 58.4, active: true,
        lengthFt: 328, staticHeadFt: 10, frictionHeadFt: 4.2,
        fromPressurePsi: 90, toPressurePsi: 84, upstreamPressurePsi: 90,
        downstreamPressurePsi: 84 }],
      relevantSegmentColors: new Map([['pipe-1:segment:0', '#123456']]),
      selectedGunIds: new Set(), gunStatuses: {}, invalidPumpIds: new Set(),
      pressureRange: { minPsi: 80, maxPsi: 100 },
      showGunTypes: false, toggleGun: () => {}, setGuns: () => {}, setHoveredSegment: () => {},
    };
    const result = dashboardGeoJSON(input);
    const pipe = result.features.find((row) => row.properties?.kind === 'snow-pipe');
    const label = result.features.find((row) => row.properties?.kind === 'snow-pipe-label');
    const arrow = result.features.find((row) => row.properties?.kind === 'snow-flow-arrow');
    expect(pipe?.properties).toMatchObject({ name: 'Main', diameterIn: 8,
      verticalM: 10, segmentIndex: 0, flowLabel: '58.4 GPM\n90.0 → 84.0 PSI' });
    expect(pipe?.properties?.lengthM).toBeCloseTo(1346.7, 1);
    expect(arrow?.properties?.rotation).toBe(snowmakingArrowGlyphRotation(
      arrow?.properties?.bearing as number));
    expect(label?.geometry).toEqual({ type: 'Point', coordinates:
      snowmakingSegmentMidpoint([[-121.5, 46.9], [-121.49, 46.91]]) });
    input.snowmakingPresentation = { ...input.snowmakingPresentation,
      segments: [{ ...input.snowmakingPresentation.segments[0], flowGpm: -58.4 }] };
    const reversed = dashboardGeoJSON(input);
    expect(reversed.features.find((row) => row.properties?.kind === 'snow-pipe')?.geometry)
      .toEqual({ type: 'LineString', coordinates: [[-121.49, 46.91], [-121.5, 46.9]] });
    expect(reversed.features.find((row) => row.properties?.kind === 'snow-flow-arrow')?.properties)
      .toMatchObject({ flowFrom: 'Main end', flowTo: 'P1', active: true });
  });

  it('orients arrow geometry from signed flow rather than saved vertex order', () => {
    const points: [number, number][] = [[0, 0], [0, 0.001], [0.001, 0.001]];
    const forward = orientedSnowmakingFlow(points, 50);
    const reverse = orientedSnowmakingFlow(points, -50);
    const stopped = orientedSnowmakingFlow(points, 0);
    expect(forward.coordinates).toEqual(points);
    expect(reverse.coordinates).toEqual([...points].reverse());
    expect(forward.arrow?.bearing).toBeCloseTo(0, 4);
    expect(reverse.arrow?.bearing).toBeCloseTo(180, 3);
    expect(reverse.arrow?.point).toEqual(forward.arrow?.point);
    expect(stopped.arrow).toBeNull();
  });

  it('converts compass bearings to the rotation of a naturally right-facing glyph', () => {
    expect(snowmakingArrowGlyphRotation(0)).toBe(270);
    expect(snowmakingArrowGlyphRotation(90)).toBe(0);
    expect(snowmakingArrowGlyphRotation(180)).toBe(90);
    expect(snowmakingArrowGlyphRotation(270)).toBe(180);
  });

  it('does not let MapLibre flip directional glyphs back to upright', () => {
    const layers: { id: string; layout?: Record<string, unknown> }[] = [];
    const map = { getSource: () => undefined, addSource: () => {},
      addLayer: (layer: { id: string; layout?: Record<string, unknown> }) => layers.push(layer) };
    addDashboardMapLayers(map as never);
    for (const id of ['dashboard-snow-flow-arrows', 'dashboard-snow-pump-arrows']) {
      const layer = layers.find((candidate) => candidate.id === id);
      expect(layer?.layout?.['text-keep-upright']).toBe(false);
    }
    expect(layers.find((layer) => layer.id === 'dashboard-snow-flow-arrows')?.layout?.['text-field'])
      .toBe('▶');
  });

  it('points suction arms toward pumps and discharge arms away from pumps', () => {
    const segment = snowmakingPipeSegments(data('snowmaking').pipes[0])[0];
    const discharge = snowmakingPumpArmMarker(segment, 'node-1', 'discharge');
    const suction = snowmakingPumpArmMarker(segment, 'node-1', 'suction');
    expect(discharge).not.toBeNull();
    expect(suction?.point).toEqual(discharge?.point);
    expect(suction?.bearing).toBeCloseTo(((discharge?.bearing ?? 0) + 180) % 360, 6);
  });
});
