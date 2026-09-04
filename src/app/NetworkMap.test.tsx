import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { buildSkiNetwork, type SkiNetwork } from '../network';
import { sanitizeLifts } from '../lifts';
import { sanitizeNodes, sanitizePaths } from '../skiNodes';
import type { AnchorRef } from '../types/anchors';
import type { SavedNode, SavedPath } from '../types/topology';
import { sanitizeTrails } from '../trails';
import type { SavedLift, SavedTrail } from '../types';
import { NetworkMap } from './NetworkMap';
import { analyzeGuestConnectivity } from './guestConnectivity';

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

function node(id: string, point: [number, number], extra: Record<string, unknown> = {}): SavedNode {
  return sanitizeNodes([
    {
      id,
      name: id,
      point,
      elevM: null,
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

function path(
  id: string,
  points: [number, number][],
  from: AnchorRef,
  to: AnchorRef,
  extra: Record<string, unknown> = {}
): SavedPath {
  return sanitizePaths([
    {
      id,
      name: id,
      points,
      pointElevM: [],
      widthM: 6,
      from,
      to,
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
      liftTypeId: 'fixed-grip-quad',
      points: [a, b],
      endpointElevM: [200, 500],
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
      onTogglePathClosed={vi.fn()}
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
  it('shows the Guest Entrance connection and warns when its lift has no open descent', () => {
    const reachableNetwork = mountain();
    const base = reachableNetwork.nodes.find((candidate) => candidate.liftBases.includes('L'))!;
    const portal = { version: 1 as const, id: 'entrance', kind: 'guest-entrance' as const,
      type: 'guest-entrance' as const, semantics: 'guest-entrance' as const, direction: 'inbound' as const,
      accepts: 'guests' as const, label: 'Guest Entrance', capacityGuestsPerTick: 12,
      openFromTick: 0, openUntilTick: 86_400, nodeId: base.id, lngLat: base.lngLat };
    const connected = analyzeGuestConnectivity(reachableNetwork, portal);
    expect(connected.reachable).toBe(true);
    expect(connected.connectedLiftName).toBe('L');
    expect(render(reachableNetwork, { guestConnectivity: connected })).toContain('Connected Guest Entrance');

    const closedNetwork = mountain({ closed: true });
    const closedBase = closedNetwork.nodes.find((candidate) => candidate.liftBases.includes('L'))!;
    const unreachable = analyzeGuestConnectivity(closedNetwork, { ...portal, nodeId: closedBase.id, lngLat: closedBase.lngLat });
    expect(unreachable.state).toBe('no-open-descent');
    expect(render(closedNetwork, { guestConnectivity: unreachable, panelOnly: true })).toContain('Resort unreachable');
  });

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
    expect(html).toContain('Fixed-Grip Quad Chairlift');
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

  it('draws a path edge with an x:-prefixed id and the network-path class', () => {
    const n1 = node('n1', at(0, 300));
    const n2 = node('n2', at(0, 0));
    const p = path(
      'p1',
      [at(0, 300), at(0, 0)],
      { kind: 'node', nodeId: 'n1', point: at(0, 300) },
      { kind: 'node', nodeId: 'n2', point: at(0, 0) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1, n2], paths: [p] });
    const html = render(net);
    const pathEdgeIds = [...(net.pathEdgeIds.get('p1') ?? [])];
    expect(pathEdgeIds.length).toBeGreaterThan(0);
    for (const id of pathEdgeIds) {
      expect(id.startsWith('x:')).toBe(true);
      const i = html.indexOf(`data-edge-id="${id}"`);
      expect(i).toBeGreaterThan(-1);
      expect(html.slice(Math.max(0, i - 140), i)).toContain('network-path');
    }
  });

  it('selecting a path edge shows the path inspector with rating and length', () => {
    const n1 = node('n1', at(0, 300));
    const n2 = node('n2', at(0, 0));
    const p = path(
      'p1',
      [at(0, 300), at(0, 0)],
      { kind: 'node', nodeId: 'n1', point: at(0, 300) },
      { kind: 'node', nodeId: 'n2', point: at(0, 0) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1, n2], paths: [p] });
    const edgeId = (net.pathEdgeIds.get('p1') ?? [])[0];
    expect(edgeId).toBeDefined();
    const html = render(net, { selectedEdgeId: edgeId });
    expect(html).toContain('data-inspector="path"');
    expect(html).toContain('Rating');
    expect(html).toContain('Length');
    expect(html).toContain('Segment 1 of');
  });

  it('draws a user-placed node with its own class', () => {
    const n = node('solo', at(5000, 5000));
    const net = buildSkiNetwork([], [], { nodes: [n] });
    const html = render(net);
    expect(html).toContain('network-node--user-node');
  });

  it('shows a Paths count in the summary panel', () => {
    const n1 = node('n1', at(0, 300));
    const n2 = node('n2', at(0, 0));
    const p = path(
      'p1',
      [at(0, 300), at(0, 0)],
      { kind: 'node', nodeId: 'n1', point: at(0, 300) },
      { kind: 'node', nodeId: 'n2', point: at(0, 0) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1, n2], paths: [p] });
    const html = render(net);
    expect(html).toContain('data-inspector="summary"');
    const match = html.match(/Paths<\/span><span class="network-stat-value">(\d+)</);
    expect(match).not.toBeNull();
    expect(Number(match?.[1])).toBe(net.pathEdgeIds.size);
    expect(net.pathEdgeIds.size).toBe(1);
  });

  it('leaves unanchoredTrailIds informational only — no warning block on an otherwise-clean network', () => {
    const net = mountain();
    // Legacy trails carry no `anchor` field, so every run in the fixture is
    // unanchored by construction — this is the "every existing save" case the
    // spec calls out as informational, not a warning.
    expect(net.diagnostics.unanchoredTrailIds.length).toBeGreaterThan(0);
    expect(net.diagnostics.unresolvedAnchorTrailIds).toEqual([]);
    expect(net.diagnostics.unresolvedAnchorPathIds).toEqual([]);
    expect(net.diagnostics.overreachingAnchorIds).toEqual([]);
    expect(net.diagnostics.degeneratePathIds).toEqual([]);
    const html = render(net);
    expect(html).toContain('Unanchored runs');
    expect(html).not.toContain('no longer resolve');
    expect(html).not.toContain('unusually long gap');
    expect(html).not.toContain('same junction');
  });

  it('warns when a run or path anchor no longer resolves', () => {
    const n1 = node('n1', at(0, 300));
    const p = path(
      'p1',
      [at(0, 300), at(0, 0)],
      { kind: 'node', nodeId: 'n1', point: at(0, 300) },
      { kind: 'node', nodeId: 'ghost', point: at(0, 0) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1], paths: [p] });
    expect(net.diagnostics.unresolvedAnchorPathIds).toEqual(['p1']);
    const html = render(net);
    expect(html).toContain('no longer resolve');
  });

  it('warns when an anchor unions across an unusually long gap', () => {
    const l = lift('L', at(0, 0), at(0, 600));
    const b = run('B', [0, 800], [400, 800], 500, 100, {
      anchor: { kind: 'lift', liftId: 'L', end: 'top', point: at(0, 600) },
    });
    const net = buildSkiNetwork([b], [l]);
    expect(net.diagnostics.overreachingAnchorIds).toEqual(['B']);
    const html = render(net);
    expect(html).toContain('unusually long gap');
  });

  it('warns when a path starts and ends at the same junction', () => {
    const n1 = node('n1', at(0, 50));
    const p = path(
      'loop',
      [at(0, 50), at(200, 50), at(400, 50)],
      { kind: 'node', nodeId: 'n1', point: at(0, 50) },
      { kind: 'node', nodeId: 'n1', point: at(400, 50) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1], paths: [p] });
    expect(net.diagnostics.degeneratePathIds).toEqual(['loop']);
    const html = render(net);
    expect(html).toContain('same junction');
  });
});
