import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { SnowmakingControl, type DamTool, type PondTool } from './SnowmakingControl';
import type { SavedSnowmakingNode } from '../types/snowmaking';
import type { SavedDam, SavedPond } from '../types';

const callbacks = {
  onClose: vi.fn(),
  onArmDam: vi.fn(), onCancelDam: vi.fn(), onDamDraftChange: vi.fn(), onConfirmDam: vi.fn(),
  onSelectDam: vi.fn(), onDeleteDam: vi.fn(), onCloseDam: vi.fn(),
  onArmPond: vi.fn(), onCancelPond: vi.fn(), onUndoPond: vi.fn(), onFinishPond: vi.fn(),
  onPondDraftChange: vi.fn(), onPondElevationChange: vi.fn(), onPondExcavationChange: vi.fn(),
  onConfirmPond: vi.fn(),
  onSelectPond: vi.fn(), onDeletePond: vi.fn(), onClosePond: vi.fn(),
  onPondSnowmakingChange: vi.fn(),
  onArmPipe: vi.fn(), onCancelPipe: vi.fn(), onUndoPipe: vi.fn(), onFinishPipe: vi.fn(),
  onConfirmPipe: vi.fn(), onRenameDraftPipe: vi.fn(), onDiameterChange: vi.fn(),
  onArmNode: vi.fn(), onCancelNode: vi.fn(), onConfirmNode: vi.fn(),
  onSelectNode: vi.fn(), onRenameNode: vi.fn(), onDeleteNode: vi.fn(), onCloseNode: vi.fn(),
  onSelectPipe: vi.fn(), onPatchPipe: vi.fn(), onDeletePipe: vi.fn(), onClosePipe: vi.fn(),
};

function render(damTool: DamTool = { phase: 'idle' }, pondTool: PondTool = { phase: 'idle' },
  units: 'metric' | 'imperial' = 'metric', dams: SavedDam[] = [], ponds: SavedPond[] = [],
  nodes: SavedSnowmakingNode[] = [], selectedNode: SavedSnowmakingNode | null = null) {
  return renderToStaticMarkup(<SnowmakingControl damTool={damTool} pondTool={pondTool}
    dams={dams} ponds={ponds} selectedDam={null} selectedPond={null}
    nodes={nodes} pipes={[]} selectedNode={selectedNode} selectedPipe={null}
    pipeTool={{ phase: 'idle' }} nodeTool={{ phase: 'idle' }} diameterIn={8}
    units={units} {...callbacks} />);
}

describe('SnowmakingControl', () => {
  it('gathers dam, pond, and pipe construction under one dock', () => {
    const html = render();
    expect(html).toContain('Snowmaking · 0 dams · 0 ponds');
    expect(html).toContain('Build dam');
    expect(html).toContain('Build standalone pond');
    expect(html).toContain('Install snowmaking pipe');
  });

  it('enables pipe and device construction', () => {
    expect(render()).not.toMatch(/disabled=""[^>]*>.*Install snowmaking pipe/);
    expect(render()).toContain('Place hydrants');
    expect(render()).toContain('Place pumps');
  });

  it('reviews standalone pond elevation, volume, and lack of natural fill', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160 } });
    expect(html).toContain('Top of pond elevation');
    expect(html).toContain('Snowmaking pond');
    expect(html).toContain('type="checkbox"');
    expect(html).toContain('checked=""');
    expect(html).toContain('Pond volume');
    expect(html).toContain('2.2M L');
    expect(html).toContain('will not fill on its own');
    expect(html).toContain('Build pond');
  });

  it('bills the berm earthwork during standalone pond review', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160, excavationDepthM: 1.5, crestElevationM: 1001.6,
      maxBermHeightM: 4, bermLengthM: 120, maxCutDepthM: 2.5, disturbedAreaM2: 2400,
      earthwork: { cutM3: 1800, fillM3: 1200, balanceM3: 600 } } });
    expect(html).toContain('Excavation below full pool');
    expect(html).toContain('Berm crest elevation');
    expect(html).toContain('Berm length');
    expect(html).toContain('Max berm height');
    expect(html).toContain('Disturbed area');
    expect(html).toContain('1,800 m³'); // cut
    expect(html).toContain('1,200 m³'); // fill
    expect(html).toContain('Surplus cut: 600 m³');
  });

  it('warns when a pond is short of fill', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160, earthwork: { cutM3: 100, fillM3: 900, balanceM3: -800 } } });
    expect(html).toContain('Short of material: 800 m³');
    expect(html).toContain('Deepen the excavation');
  });

  it('shows the full-pool capacity and gameplay fill time during dam review', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
      inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4 } });
    expect(html).toContain('Gameplay inflow');
    expect(html).toContain('300 L/s');
    expect(html).toContain('Estimated fill time');
    expect(html).toContain('2.0 hr');
    expect(html).toContain('Pond volume');
    expect(html).toContain('2.2M L');
    expect(html).toContain('Dam length');
    expect(html).toContain('Average height');
    expect(html).toContain('2.5 m');
    expect(html).toContain('Build dam');
  });

  it('shows dam pond volume in K/M US gallons for imperial games', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
      inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4 } }, { phase: 'idle' }, 'imperial');
    expect(html).toContain('570.6K gal');
  });

  it('bills the dam embankment as borrow fill and offers a ground preview', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, damCrestElevationM: 1000.6, streamId: 'way/1', streamName: 'Creek',
      sourceWidthM: 3, inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4, builtLengthM: 60, disturbedAreaM2: 900,
      earthwork: { cutM3: 0, fillM3: 1200, balanceM3: -1200 } } });
    expect(html).toContain('Dam crest elevation');
    expect(html).toContain('Embankment length');
    expect(html).toContain('Short of material: 1,200 m³');
    expect(html).toContain('Highlighted contours show the ground this dam will reshape.');
  });

  it('surfaces a failed dam build in the review panel', () => {
    const html = render({ phase: 'review', error: 'The terrain changed after this grading preview.',
      draft: { name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2160, maxDamHeightM: 4 } });
    expect(html).toContain('The terrain changed after this grading preview.');
  });

  const testPond: SavedPond = {
    id: 'pond-1', name: 'Upper Pond', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
    topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3, capacityM3: 2160,
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  const testNode: SavedSnowmakingNode = {
    id: 'node-1', name: 'Upper Pond Intake', kind: 'intake', point: [0, 0], elevM: 1001,
    source: { kind: 'pond', pondId: 'pond-1' }, createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('lists snowmaking nodes with their kind and source in the idle overview', () => {
    const html = render(undefined, undefined, 'metric', [], [testPond], [testNode]);
    expect(html).toContain('Upper Pond Intake');
    expect(html).toContain('Intake');
    expect(html).toContain('Upper Pond');
  });

  it('omits the node list section when there are no nodes', () => {
    const html = render(undefined, undefined, 'metric', [], [testPond], []);
    // The dam/pond lists still use .lift-list, so just confirm no swatch for a node row leaks in.
    expect(html).not.toContain('snowmaking-node-swatch');
  });

  it('shows a selected node detail branch with name input, kind, source, and elevation', () => {
    const html = render(undefined, undefined, 'metric', [], [testPond], [testNode], testNode);
    expect(html).toContain('value="Upper Pond Intake"');
    expect(html).toContain('Kind');
    expect(html).toContain('Intake');
    expect(html).toContain('Source');
    expect(html).toContain('Upper Pond');
    expect(html).toContain('Elevation');
  });

  it('does not offer a delete/remove action for a selected node', () => {
    const html = render(undefined, undefined, 'metric', [], [testPond], [testNode], testNode);
    expect(html).not.toContain('lift-delete-btn');
    expect(html).not.toContain('Remove node');
  });

  it('renders a placeholder when a selected node has no sampled elevation', () => {
    const nodeWithoutElevation: SavedSnowmakingNode = { ...testNode, elevM: null };
    const html = render(undefined, undefined, 'metric', [], [testPond], [nodeWithoutElevation], nodeWithoutElevation);
    expect(html).toContain('—');
  });
});
