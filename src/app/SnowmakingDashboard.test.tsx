import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import type { LatLonBounds } from '../elevation';
import type { SavedSnowmakingNode } from '../snowmakingNodes';
import type { CoverClassCode, SavedDam, SavedLift, SavedPond, SavedTrail, TerrainRecord } from '../types';
import { SnowmakingDashboard } from './SnowmakingDashboard';

// Matches the DOM-free component-test pattern in NetworkMap.test.tsx /
// SnowmakingControl.test.tsx: render to a string and assert on markup, so no
// jsdom is needed.

const LAT0 = 46.93;
const LNG0 = -121.5;
const M_PER_LAT = 111320;
const M_PER_LNG = 111320 * Math.cos((LAT0 * Math.PI) / 180);

function at(eastM: number, northM: number): [number, number] {
  return [LNG0 + eastM / M_PER_LNG, LAT0 + northM / M_PER_LAT];
}

function squareRing(cx: number, cy: number, halfM: number): [number, number][] {
  return [
    at(cx - halfM, cy - halfM),
    at(cx + halfM, cy - halfM),
    at(cx + halfM, cy + halfM),
    at(cx - halfM, cy + halfM),
    at(cx - halfM, cy - halfM),
  ];
}

function coverFeature(code: CoverClassCode, cx: number, cy: number): CoverDisplayGeoJSON['features'][number] {
  return {
    type: 'Feature',
    properties: { code },
    geometry: { type: 'Polygon', coordinates: [squareRing(cx, cy, 5)] },
  };
}

const testPond: SavedPond = {
  id: 'pond-1', name: 'Upper Pond', boundary: squareRing(300, 300, 20),
  topElevationM: 1001, areaM2: 1600, averageDepthM: 2, maxDepthM: 3, capacityM3: 2160,
  createdAt: '2026-01-01T00:00:00.000Z',
};

const testDam: SavedDam = {
  id: 'dam-1', name: 'Lower Dam', points: [at(-100, -100), at(-60, -100)],
  crestElevationM: 950, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3, inflowM3s: 0.3,
  pondRings: [squareRing(-80, -120, 25)], areaM2: 1600, averageDepthM: 2, capacityM3: 2160,
  maxDamHeightM: 4, createdAt: '2026-01-01T00:00:00.000Z',
};

const nodeA: SavedSnowmakingNode = {
  id: 'node-a', name: 'Upper Pond Intake', kind: 'intake', point: at(300, 300), elevM: 1001,
  source: { kind: 'pond', pondId: 'pond-1' }, createdAt: '2026-01-01T00:00:00.000Z',
};
const nodeB: SavedSnowmakingNode = {
  id: 'node-b', name: 'Pump 1', kind: 'pump', point: at(100, 100), elevM: 1050,
  createdAt: '2026-01-01T00:00:00.000Z',
};

const trails: SavedTrail[] = [];
const lifts: SavedLift[] = [];

const coverDisplay: CoverDisplayGeoJSON = {
  type: 'FeatureCollection',
  features: [
    coverFeature(10, 0, 0), coverFeature(10, 40, 0), coverFeature(10, 0, 40),
    coverFeature(20, 200, 0), coverFeature(20, 240, 0), coverFeature(20, 200, 40),
    coverFeature(80, 300, 250), // water — excluded from the backdrop
    coverFeature(4, -80, -80), // four-class water — excluded from the backdrop
  ],
};

// Bounds surrounding the same LAT0/LNG0 origin as the rest of this file's
// fixtures, so contour geometry projects into the same neighborhood as the
// dam/pond/node fixtures (exact placement isn't asserted, just presence).
const testBounds: LatLonBounds = {
  south: LAT0 - 0.01, north: LAT0 + 0.01, west: LNG0 - 0.01, east: LNG0 + 0.01,
};

// One segment quantized to a level-1 (index/major, every-5th-interval)
// elevation and one to level-0 (minor), per contourGeoJSON's
// `Math.round(ele / 6.096) % 5 === 0` rule for metric units — exercises both
// contourPaths branches without needing realistic terrain.
const testTerrainRecord: TerrainRecord = {
  schemaVersion: 6,
  key: 'test-terrain',
  mountainName: 'Test Mountain',
  latitude: LAT0,
  longitude: LNG0,
  areaSizeMeters: 2000,
  bounds: testBounds,
  sampleGridSize: 2,
  sampleHeights: [1000, 1000, 1000, 1000],
  contourSegments: [
    0.4, 0.4, 0.6, 0.6, 30.48, // 6.096 * 5 -> level 1 (major)
    0.1, 0.1, 0.2, 0.2, 6.096, // 6.096 * 1 -> level 0 (minor)
  ],
  climate: { monthly: [] },
  sourceType: 'preset',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
};

function render(props: Partial<Parameters<typeof SnowmakingDashboard>[0]> = {}) {
  return renderToStaticMarkup(
    <SnowmakingDashboard
      dams={[testDam]}
      ponds={[testPond]}
      trails={trails}
      lifts={lifts}
      nodes={[nodeA, nodeB]}
      coverDisplay={coverDisplay}
      terrainRecord={null}
      units="metric"
      selectedNodeId={null}
      onSelectNode={vi.fn()}
      onClose={vi.fn()}
      {...props}
    />
  );
}

describe('SnowmakingDashboard', () => {
  it('draws one backdrop path per distinct cover class, not per polygon', () => {
    const html = render();
    // 2 classes (10 and 20) present across 6 input polygons — one merged path
    // per class keeps DOM element count fixed regardless of polygon count.
    const matches = [...html.matchAll(/data-cover-code="(\d+)"/g)].map((m) => m[1]);
    expect(matches.sort()).toEqual(['10', '20']);
  });

  it('excludes water cover codes (4 and 80) from the backdrop', () => {
    const html = render();
    expect(html).not.toContain('data-cover-code="4"');
    expect(html).not.toContain('data-cover-code="80"');
  });

  it('does not render contour paths when terrainRecord is null', () => {
    const html = render();
    expect(html).not.toContain('snowmaking-dashboard-contour-major');
    expect(html).not.toContain('snowmaking-dashboard-contour-minor');
  });

  it('renders both major and minor contour paths when terrainRecord has bounds and segments', () => {
    const html = render({ terrainRecord: testTerrainRecord });
    expect(html).toContain('snowmaking-dashboard-contour-major');
    expect(html).toContain('snowmaking-dashboard-contour-minor');
  });

  it('draws one node per saved snowmaking node', () => {
    const html = render();
    expect(html).toContain('data-node-id="node-a"');
    expect(html).toContain('data-node-id="node-b"');
  });

  it('distinguishes the selected node from the rest', () => {
    const html = render({ selectedNodeId: 'node-a' });
    const i = html.indexOf('data-node-id="node-a"');
    const j = html.indexOf('data-node-id="node-b"');
    // The class list sits right before the data-node-id attribute in the
    // rendered <g>, so a short backward slice is enough to isolate it.
    expect(html.slice(Math.max(0, i - 160), i)).toContain('is-selected');
    expect(html.slice(Math.max(0, j - 160), j)).not.toContain('is-selected');
  });

  it('draws water bodies for both dams and standalone ponds', () => {
    const html = render();
    expect(html).toContain('data-pond-id="pond-1"');
    expect(html).toContain('data-dam-id="dam-1"');
  });

  it('renders a helpful empty state when there are no dams, ponds, or nodes', () => {
    const html = render({ dams: [], ponds: [], nodes: [] });
    expect(html).toContain('Nothing to map yet');
    expect(html).toContain('build a dam or pond');
  });

  it('does not crash and still renders chrome when coverDisplay is null', () => {
    const html = render({ coverDisplay: null });
    expect(html).toContain('Close snowmaking map');
    expect(html).not.toContain('data-cover-code');
  });

  describe('inspector', () => {
    it('shows a summary with counts and a clickable directory row per node when nothing is selected', () => {
      const html = render();
      expect(html).toContain('data-inspector="summary"');
      // Counts: 1 dam, 1 pond, 2 nodes.
      const statsSection = html.slice(html.indexOf('network-stats'));
      expect(statsSection).toContain('>1<'); // Dams
      expect(statsSection).toContain('>2<'); // Nodes
      // Directory rows: name + kind + source name.
      expect(html).toContain('Upper Pond Intake');
      expect(html).toContain('Intake'); // human label, not raw 'intake'
      expect(html).toContain('Upper Pond'); // node-a's source pond name
      expect(html).toContain('Pump 1');
      expect(html).toContain('Pump');
    });

    it('shows node detail — name, kind, source, capacity, elevation — when a node is selected', () => {
      const html = render({ selectedNodeId: 'node-a' });
      expect(html).toContain('data-inspector="node"');
      expect(html).toContain('Upper Pond Intake');
      expect(html).toContain('Intake');
      expect(html).toContain('Upper Pond'); // source name
      // formatLakeVolume(2160, 'metric') === '2.2M L' — the same helper/convention SnowmakingControl uses for pond/dam capacity.
      expect(html).toContain('2.2M L');
      // Elevation 1001m formatted via fmtDistance.
      expect(html).toContain('1,001');
    });

    it('shows an em dash for elevation when a node has no elevM', () => {
      const nodeNoElev: SavedSnowmakingNode = {
        id: 'node-c', name: 'No Elev Node', kind: 'hydrant', point: at(50, 50), elevM: null,
        createdAt: '2026-01-01T00:00:00.000Z',
      };
      const html = render({ nodes: [nodeA, nodeB, nodeNoElev], selectedNodeId: 'node-c' });
      expect(html).toContain('No Elev Node');
      expect(html).toContain('—');
    });

    it('omits source/capacity stats without crashing for a hand-placed node with no source', () => {
      const html = render({ selectedNodeId: 'node-b' }); // nodeB has no `source`
      expect(html).toContain('data-inspector="node"');
      expect(html).toContain('Pump 1');
      expect(html).not.toContain('Source');
      expect(html).not.toContain('Capacity');
    });

    it('never renders a rename input in the inspector (read-only, unlike the dock panel)', () => {
      const withSelection = render({ selectedNodeId: 'node-a' });
      const withoutSelection = render();
      expect(withSelection).not.toContain('name-entry-input');
      expect(withoutSelection).not.toContain('name-entry-input');
      // No text inputs at all inside the inspector aside.
      const inspectorHtml = withSelection.slice(withSelection.indexOf('data-inspector'));
      expect(inspectorHtml).not.toContain('<input');
    });
  });
});
