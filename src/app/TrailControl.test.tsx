import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { TrailControl, type DraftTrail } from './TrailControl';
import type { AnchorWorld } from '../topology';
import type { SavedLift } from '../types';

const part = {
  polygon: [[[0, 1], [1, 1], [1, 0], [0, 0], [0, 1]]] as [number, number][][],
  centerline: [[0.5, 1], [0.5, 0]] as [number, number][],
  centerlineElevM: [100, 80],
};
const draft: DraftTrail = {
  parts: [part], ungradedParts: [part], areaM2: 1000, ungradedAreaM2: 1000, brushWidthM: 30,
  name: 'Run 1', status: 'complete', difficulty: 'green', elevStatus: 'ok',
  gradingEnabled: false, gradingStatus: 'idle', gradingError: null,
  earthwork: null, maxGroundCrossSlopePct: 0, maxFaceSlopePct: 0,
  maxDisturbedWidthM: 0, ungradedLengthM: 0,
  infeasibleLines: [],
  anchor: { kind: 'lift', liftId: 'L1', end: 'top', point: [0.5, 1] },
  tailAnchor: { kind: 'lift', liftId: 'L1', end: 'base', point: [0.5, 0] },
};
// Empty by default, so every anchor falls back to its generic label.
const world: AnchorWorld = { trails: [], lifts: [], junctions: [] };
const callbacks = {
  onBrushWidthChange: vi.fn(), onCancel: vi.fn(), onModeChange: vi.fn(),
  onUndo: vi.fn(), onClear: vi.fn(), onFinish: vi.fn(), onDraftChange: vi.fn(),
  onConfirm: vi.fn(), onEditPatch: vi.fn(), onCloseEdit: vi.fn(), onDelete: vi.fn(),
  onRetryElevation: vi.fn(), onGradingChange: vi.fn(), onChangeHead: vi.fn(),
};

describe('TrailControl terrain grading', () => {
  it('prompts for a graph anchor before opening the brush', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'place-head',
      candidate: null, error: null }} trails={[]} selectedId={null} units="metric"
      brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Place Trailhead');
    expect(html).toContain('lift terminal');
    expect(html).toContain('trail centerline');
  });

  it('protects the seed until a user stroke exists', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'paint', mode: 'paint',
      polygons: [], areaM2: 100, activeAreaM2: null, canUndo: false, pending: false,
      error: null, anchor: { kind: 'lift', liftId: 'L1', end: 'top', point: [0.5, 1] },
      hasUserStroke: false }} trails={[]}
      selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Create Trail');
    expect(html).toMatch(/disabled=""[^>]*>Erase/);
    expect(html).toMatch(/disabled=""[^>]*>Finish/);
    expect(html).toContain('Change trailhead');
  });

  it('shows a fixed lift-top connection during review', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft }}
      trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Lift top');
    expect(html).not.toContain('Pick start');
    expect(html).not.toContain('Change');
  });

  // Two runs both reading "On a run" is the failure this replaces: the panel has
  // to say WHICH run, which of its segments, and which graph nodes.
  const lift: SavedLift = {
    id: 'L1', name: 'Summit Express', liftClass: 'fixed-grip', chairSize: 4,
    points: [[0.5, 0], [0.5, 1]], endpointElevM: [80, 100], lengthM: 100, verticalM: 20,
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
  };
  const spine: [number, number][] = [[0.5, 1], [0.5, 0.5], [0.5, 0]];
  const namedWorld: AnchorWorld = {
    lifts: [lift],
    trails: [{
      id: 'T9', name: 'Ridge Run', brushWidthM: 30, areaM2: 0, lengthM: 0, verticalM: null,
      avgSlopeDeg: 0, maxSlopeDeg: 0, difficulty: 'green', status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
      parts: [{ polygon: part.polygon, centerline: spine, centerlineElevM: [],
        segments: [{ id: 'S1', centerline: spine, centerlineElevM: [],
          fromJunctionId: 'J1', toJunctionId: 'J2' }] }],
    }],
    junctions: [
      { id: 'J1', point: [0.5, 1], elevM: null, createdAt: '2026-01-01T00:00:00.000Z' },
      { id: 'J2', point: [0.5, 0], elevM: null, createdAt: '2026-01-01T00:00:00.000Z' },
      { id: 'J3', point: [0.5, 1], elevM: null, liftTerminal: { liftId: 'L1', end: 'top' },
        createdAt: '2026-01-01T00:00:00.000Z' },
    ],
  };

  it('names the lift, the run, the segment and the nodes each end attaches to', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, tailAnchor: { kind: 'trail', trailId: 'T9', point: [0.5, 0.25] },
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30}
      {...callbacks} world={namedWorld} />);
    expect(html).toContain('Summit Express top');
    expect(html).toContain('node 3');
    expect(html).toContain('Ridge Run');
    expect(html).toContain('segment 1 of 1 · between nodes 1 and 2');
    expect(html).not.toContain('On a run');
  });

  it('says why sampling failed rather than a bare "unavailable"', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, elevStatus: 'error',
      elevError: 'The trailhead and trail end are not joined by one painted footprint.',
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('not joined by one painted footprint');
    expect(html).toContain('Retry');
    expect(html).not.toContain('Elevation unavailable');
  });

  it('falls back to the generic line when no reason was recorded', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, elevStatus: 'error',
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Elevation unavailable');
  });

  it('offers an unchecked terrain grading option during review', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft }}
      trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Grade terrain');
    expect(html).not.toContain('checked=""');
    expect(html).toContain('Build run');
  });

  it('blocks construction while a checked grade is calculating', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, gradingEnabled: true, gradingStatus: 'pending',
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('Calculating terrain grade');
    expect(html).toMatch(/disabled=""[^>]*>Build run/);
  });

  const graded: Partial<DraftTrail> = {
    gradingEnabled: true,
    gradingStatus: 'ok',
    earthwork: { cutM3: 1200, fillM3: 300, balanceM3: 900 },
    maxGroundCrossSlopePct: 42,
    maxFaceSlopePct: 100,
    maxDisturbedWidthM: 90,
  };

  it('reports a too-steep stretch without blocking the build', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, ...graded, ungradedLengthM: 140,
      maxGroundCrossSlopePct: 128,
      infeasibleLines: [[[0.4, 0.6], [0.4, 0.5]]],
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('140 m');
    expect(html).toContain('steeper');
    expect(html).toContain('128% cross slope');
    expect(html).toContain('left at natural');
    // Grading is a tool, not a gate.
    expect(html).not.toMatch(/disabled=""[^>]*>Build run/);
  });

  it('says nothing about steepness when the whole run graded', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, ...graded, ungradedLengthM: 0, infeasibleLines: [],
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).not.toContain('left at natural');
  });

  it('shows the earthwork bill for a graded run', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, ...graded, ungradedLengthM: 0, infeasibleLines: [],
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} world={world} />);
    expect(html).toContain('1,200 m³');
    expect(html).toContain('Hillside cross slope');
    expect(html).toContain('42%');
    expect(html).toContain('90 m');
    expect(html).not.toMatch(/disabled=""[^>]*>Build run/);
  });
});
