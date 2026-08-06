import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { TrailsPanel, type TrailsTool } from './TrailsPanel';
import type { SavedTrail } from '../types';
import type { SavedNode, SavedPath } from '../types/topology';
import type { JunctionSummary } from '../topology';

const trails: SavedTrail[] = [
  {
    id: 'trail-1',
    name: 'Lower Meadow',
    parts: [],
    brushWidthM: 30,
    areaM2: 12000,
    lengthM: 800,
    verticalM: 150,
    avgSlopeDeg: 14,
    maxSlopeDeg: 22,
    difficulty: 'green',
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  },
  {
    id: 'trail-2',
    name: 'Steep Face',
    parts: [],
    brushWidthM: 25,
    areaM2: 9000,
    lengthM: 500,
    verticalM: 300,
    avgSlopeDeg: 30,
    maxSlopeDeg: 40,
    difficulty: 'black',
    status: 'planning',
    createdAt: '2026-01-01T00:00:00.000Z',
  },
];

// One removable mid-run node and one that is load-bearing, which is the whole
// distinction the Nodes list exists to show.
const junctions: JunctionSummary[] = [
  { id: 'j-1', point: [-121.5, 46.93], number: 1, label: 'Lower Meadow', blocked: null },
  { id: 'j-2', point: [-121.51, 46.94], number: 2, label: 'Summit Express top',
    blocked: 'That node is a lift terminal — the lift needs it.' },
];

const legacyNodes: SavedNode[] = [
  {
    id: 'node-1',
    name: 'Node 1',
    point: [-121.5, 46.93],
    elevM: 1500,
    createdAt: '2026-01-01T00:00:00.000Z',
  },
];

const paths: SavedPath[] = [
  {
    id: 'path-1',
    name: 'Path 1',
    points: [
      [-121.5, 46.93],
      [-121.51, 46.94],
    ],
    pointElevM: [],
    widthM: 6,
    from: { kind: 'lift', liftId: 'lift-1', end: 'base', point: [-121.5, 46.93] },
    to: { kind: 'lift', liftId: 'lift-1', end: 'top', point: [-121.51, 46.94] },
    lengthM: 1200,
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  },
];

const callbacks = {
  onPaintRun: vi.fn(),
  onAddNode: vi.fn(),
  onRemoveNodeTool: vi.fn(),
  onDrawPath: vi.fn(),
  onSelectTrail: vi.fn(),
  onSelectNode: vi.fn(),
  onSelectPath: vi.fn(),
  onDeleteNode: vi.fn(),
  onDeleteLegacyNode: vi.fn(),
  onDeletePath: vi.fn(),
  onClose: vi.fn(),
};

function render(opts: {
  trails?: SavedTrail[];
  junctions?: JunctionSummary[];
  legacyNodes?: SavedNode[];
  paths?: SavedPath[];
  activeTool?: TrailsTool;
  warnings?: string[];
}) {
  return renderToStaticMarkup(
    <TrailsPanel
      trails={opts.trails ?? []}
      junctions={opts.junctions ?? []}
      legacyNodes={opts.legacyNodes ?? []}
      paths={opts.paths ?? []}
      units="metric"
      selectedTrailId={null}
      selectedNodeId={null}
      selectedPathId={null}
      activeTool={opts.activeTool ?? 'none'}
      warnings={opts.warnings ?? []}
      {...callbacks}
    />
  );
}

describe('TrailsPanel', () => {
  it('renders a section per entity kind with correct counts', () => {
    const html = render({ trails, junctions, paths });
    expect(html).toContain('Runs (2)');
    expect(html).toContain('Nodes (2)');
    expect(html).toContain('Paths (1)');
  });

  it('renders one data-row-id per run, node, and path', () => {
    const html = render({ trails, junctions, legacyNodes, paths });
    for (const t of trails) expect(html).toContain(`data-row-id="${t.id}"`);
    for (const j of junctions) expect(html).toContain(`data-row-id="${j.id}"`);
    for (const n of legacyNodes) expect(html).toContain(`data-row-id="${n.id}"`);
    for (const p of paths) expect(html).toContain(`data-row-id="${p.id}"`);
    expect((html.match(/data-row-id="/g) ?? [])).toHaveLength(
      trails.length + junctions.length + legacyNodes.length + paths.length
    );
  });

  it('marks only the active tool as pressed', () => {
    const html = render({ activeTool: 'node-add' });
    expect(html).toMatch(/aria-pressed="false"[^>]*>\s*<span aria-hidden="true">◈<\/span> Create Trail/);
    expect(html).toMatch(/aria-pressed="true"[^>]*>\s*<span aria-hidden="true">●<\/span> Add node/);
    expect(html).toMatch(/aria-pressed="false"[^>]*>\s*<span aria-hidden="true">○<\/span> Remove node/);
    expect(html).toMatch(/aria-pressed="false"[^>]*>\s*<span aria-hidden="true">⋯<\/span> Draw path/);
  });

  it('marks the remove-node tool as pressed when active', () => {
    const html = render({ activeTool: 'node-remove' });
    expect(html).toMatch(/aria-pressed="true"[^>]*>\s*<span aria-hidden="true">○<\/span> Remove node/);
    expect(html).toMatch(/aria-pressed="false"[^>]*>\s*<span aria-hidden="true">●<\/span> Add node/);
  });

  it('marks the paint-run tool as pressed when active', () => {
    const html = render({ activeTool: 'trail' });
    expect(html).toMatch(/aria-pressed="true"[^>]*>\s*<span aria-hidden="true">◈<\/span> Create Trail/);
  });

  it('marks the draw-path tool as pressed when active', () => {
    const html = render({ activeTool: 'path' });
    expect(html).toMatch(/aria-pressed="true"[^>]*>\s*<span aria-hidden="true">⋯<\/span> Draw path/);
  });

  it('renders warnings when present and omits the block when empty', () => {
    const withWarnings = render({ warnings: ['2 runs are not reachable from any lift'] });
    expect(withWarnings).toContain('2 runs are not reachable from any lift');
    expect(withWarnings).toContain('trails-warnings');

    const noWarnings = render({ warnings: [] });
    expect(noWarnings).not.toContain('trails-warnings');
  });

  it('renders empty states when a list is empty', () => {
    const html = render({});
    expect(html).toContain('No runs yet — paint your first one.');
    expect(html).toContain('No nodes yet — paint a run, or split one to add a node.');
    expect(html).toContain('No paths yet — connect a lift to a run.');
  });

  // A node the network needs must still be visible and still explain itself —
  // an enabled ✕ that silently does nothing would be the worse failure.
  it('numbers each node and disables removal for a load-bearing one, with the reason', () => {
    const html = render({ junctions });
    expect(html).toContain('Node 1');
    expect(html).toContain('Lower Meadow');
    expect(html).toContain('mid-run');
    expect(html).toContain('Node 2');
    expect(html).toContain('Summit Express top');
    expect(html).toMatch(/disabled=""[^>]*title="That node is a lift terminal — the lift needs it."/);
  });

  it('keeps old free-standing pins deletable and labelled as such', () => {
    const html = render({ legacyNodes });
    expect(html).toContain('Old pin · Unattached');
    expect(html).toContain('aria-label="Delete Node 1"');
  });
});
