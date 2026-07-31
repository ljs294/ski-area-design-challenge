import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { buildSkiNetwork, type SkiNetwork } from '../network';
import { sanitizeLifts } from '../lifts';
import { sanitizeTrails } from '../trails';
import type { SavedLift, SavedTrail } from '../types';
import { NetworkMap } from './NetworkMap';

// Matches the DOM-free component-test pattern in InfrastructureControl.test.tsx:
// render to a string and assert on markup, so no jsdom is needed.

const LAT0 = 46.93;
const LNG0 = -121.5;
const M_PER_LAT = 111320;
const M_PER_LNG = 111320 * Math.cos((LAT0 * Math.PI) / 180);

function at(eastM: number, northM: number): [number, number] {
  return [LNG0 + eastM / M_PER_LNG, LAT0 + northM / M_PER_LAT];
}

function boxRing(centerline: [number, number][]): [number, number][] {
  const padLng = 15 / M_PER_LNG;
  const padLat = 15 / M_PER_LAT;
  const w = Math.min(...centerline.map((p) => p[0])) - padLng;
  const e = Math.max(...centerline.map((p) => p[0])) + padLng;
  const s = Math.min(...centerline.map((p) => p[1])) - padLat;
  const n = Math.max(...centerline.map((p) => p[1])) + padLat;
  return [
    [w, s],
    [e, s],
    [e, n],
    [w, n],
    [w, s],
  ];
}

function run(
  id: string,
  from: [number, number],
  to: [number, number],
  topElev: number,
  dropM: number,
  extra: Record<string, unknown> = {}
): SavedTrail {
  const centerline: [number, number][] = [];
  const elevs: number[] = [];
  for (let i = 0; i < 5; i++) {
    const t = i / 4;
    centerline.push(at(from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t));
    elevs.push(topElev - dropM * t);
  }
  return sanitizeTrails([
    {
      id,
      name: id,
      parts: [{ polygon: [boxRing(centerline)], centerline, centerlineElevM: elevs }],
      brushWidthM: 30,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

function lift(id: string, a: [number, number], b: [number, number]): SavedLift {
  return sanitizeLifts([
    {
      id,
      name: id,
      liftClass: 'fixed-grip',
      points: [a, b],
      endpointElevM: [200, 500],
      chairSize: 4,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
    },
  ])[0];
}

function render(network: SkiNetwork, props: Partial<Parameters<typeof NetworkMap>[0]> = {}) {
  return renderToStaticMarkup(
    <NetworkMap
      network={network}
      units="metric"
      selectedLiftId={null}
      selectedEdgeId={null}
      onSelectLift={vi.fn()}
      onSelectEdge={vi.fn()}
      onToggleTrailClosed={vi.fn()}
      onToggleLiftClosed={vi.fn()}
      onClose={vi.fn()}
      {...props}
    />
  );
}

/** Lift → `served`, which forks into `west`; `stray` is off on its own. */
function mountain(extra: Record<string, unknown> = {}) {
  const l = lift('L', at(0, 0), at(0, 600));
  const served = run('served', [10, 590], [10, 200], 500, 200, extra);
  const west = run('west', [5, 195], [-400, 0], 300, 100);
  const stray = run('stray', [4000, 400], [4000, 0], 500, 150);
  return buildSkiNetwork([served, west, stray], [l]);
}

describe('NetworkMap', () => {
  it('draws one element per edge, tagged for selection', () => {
    const net = mountain();
    const html = render(net);
    for (const edge of net.edges) {
      expect(html).toContain(`data-edge-id="${edge.id}"`);
    }
    expect(html).toContain('data-edge-id="l:L"');
  });

  it('draws one element per node', () => {
    const net = mountain();
    const html = render(net);
    const drawn = [...html.matchAll(/data-node-id="([^"]+)"/g)].map((m) => m[1]);
    expect(new Set(drawn).size).toBe(net.nodes.length);
  });

  it('marks a closed run so it reads as shut, not merely unselected', () => {
    const net = mountain({ closed: true });
    const html = render(net);
    const closedEdge = net.edges.find((e) => e.condition === 'closed');
    expect(closedEdge).toBeDefined();
    const fragment = html.slice(html.indexOf(`data-edge-id="${closedEdge?.id}"`) - 120);
    expect(fragment.slice(0, 200)).toContain('is-closed');
  });

  it('dims everything a selected lift cannot reach', () => {
    const net = mountain();
    const html = render(net, { selectedLiftId: 'L' });
    const strayEdge = net.edges.find((e) => e.kind === 'trail' && e.trailId === 'stray');
    const servedEdge = net.edges.find((e) => e.kind === 'trail' && e.trailId === 'served');
    const classOf = (id: string) => {
      const i = html.indexOf(`data-edge-id="${id}"`);
      return html.slice(Math.max(0, i - 140), i);
    };
    expect(classOf(strayEdge!.id)).toContain('is-dimmed');
    expect(classOf(servedEdge!.id)).not.toContain('is-dimmed');
  });

  it('lists the runs a selected lift serves, with its placeholder queue', () => {
    const html = render(mountain(), { selectedLiftId: 'L' });
    expect(html).toContain('data-inspector="lift"');
    expect(html).toContain('served');
    expect(html).toContain('People waiting');
    expect(html).toContain('placeholders');
    expect(html).toContain('2,400 p/h'); // quad at 600 pph per seat
  });

  it('shows segment stats when a run segment is selected', () => {
    const net = mountain();
    const edge = net.edges.find((e) => e.kind === 'trail' && e.trailId === 'served');
    const html = render(net, { selectedEdgeId: edge!.id });
    expect(html).toContain('data-inspector="trail"');
    expect(html).toContain('Acreage');
    expect(html).toContain('Max pitch');
  });

  it('warns the designer about runs no lift can reach', () => {
    const html = render(mountain());
    expect(html).toContain('data-inspector="summary"');
    expect(html).toContain('not reachable from any lift');
  });

  it('renders an empty state rather than a blank panel', () => {
    const html = render(buildSkiNetwork([], []));
    expect(html).toContain('Nothing to map yet');
  });
});
