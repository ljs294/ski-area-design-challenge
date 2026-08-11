import { describe, expect, it } from 'vitest';
import { buildSkiNetwork } from '../network';
import type { SavedLift } from '../types';
import { dashboardGeoJSON, snowmakingSegmentMidpoint, type DashboardMapData } from './dashboardMapLayers';

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
      ], createdAt: '2026-01-01' }],
    guns: [], coverDisplay: null, terrainRecord: null, selectedSnowmaking: null,
    snowmakingPresentation: null,
  };
}

describe('dashboard MapLibre projection', () => {
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
    expect(result.features.filter((row) => row.properties?.kind === 'backdrop')).toHaveLength(1);
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
      selectedGunIds: new Set(), gunStatuses: {}, pressureRange: { minPsi: 80, maxPsi: 100 },
      showGunTypes: false, toggleGun: () => {}, setHoveredSegment: () => {},
    };
    const result = dashboardGeoJSON(input);
    const pipe = result.features.find((row) => row.properties?.kind === 'snow-pipe');
    const label = result.features.find((row) => row.properties?.kind === 'snow-pipe-label');
    expect(pipe?.properties).toMatchObject({ name: 'Main', diameterIn: 8, lengthM: 100,
      verticalM: 10, segmentIndex: 0, flowLabel: '58.4 GPM\n90.0 → 84.0 PSI' });
    expect(label?.geometry).toEqual({ type: 'Point', coordinates:
      snowmakingSegmentMidpoint([[-121.5, 46.9], [-121.49, 46.91]]) });
  });
});
