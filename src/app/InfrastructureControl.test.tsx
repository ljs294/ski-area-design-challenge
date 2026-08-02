import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { InfrastructureControl, type RoadTool } from './InfrastructureControl';

const callbacks = {
  onArm: vi.fn(), onCancel: vi.fn(), onUndo: vi.fn(), onFinish: vi.fn(),
  onDraftChange: vi.fn(), onConfirm: vi.fn(), onClose: vi.fn(),
  onArmDam: vi.fn(), onCancelDam: vi.fn(), onDamDraftChange: vi.fn(), onConfirmDam: vi.fn(),
  onSelectDam: vi.fn(), onDeleteDam: vi.fn(), onCloseDam: vi.fn(),
  onArmPond: vi.fn(), onCancelPond: vi.fn(), onUndoPond: vi.fn(), onFinishPond: vi.fn(),
  onPondDraftChange: vi.fn(), onPondElevationChange: vi.fn(), onConfirmPond: vi.fn(),
  onSelectPond: vi.fn(), onDeletePond: vi.fn(), onClosePond: vi.fn(),
};

function render(tool: RoadTool) {
  return renderToStaticMarkup(<InfrastructureControl tool={tool} damTool={{ phase: 'idle' }}
    pondTool={{ phase: 'idle' }} roads={[]} dams={[]} ponds={[]} selectedDam={null}
    selectedPond={null} units="metric" {...callbacks} />);
}

describe('InfrastructureControl', () => {
  it('offers only the two-lane 7 m road type', () => {
    const html = render({ phase: 'idle' });
    expect(html).toContain('Infrastructure · 0 roads · 0 dams · 0 ponds');
    expect(html).toContain('Two-lane road — 7 m');
    expect((html.match(/<option/g) ?? [])).toHaveLength(1);
  });

  it('offers dam construction from Infrastructure', () => {
    expect(render({ phase: 'idle' })).toContain('Build dam');
  });

  it('offers standalone pond construction from Infrastructure', () => {
    expect(render({ phase: 'idle' })).toContain('Build standalone pond');
  });

  it('reviews standalone pond elevation, volume, and lack of natural fill', () => {
    const html = renderToStaticMarkup(<InfrastructureControl tool={{ phase: 'idle' }}
      damTool={{ phase: 'idle' }} pondTool={{ phase: 'review', error: null, draft: {
        name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
        topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
        capacityM3: 2160 } }} roads={[]} dams={[]} ponds={[]} selectedDam={null}
      selectedPond={null} units="metric" {...callbacks} />);
    expect(html).toContain('Top of pond elevation');
    expect(html).toContain('Pond volume');
    expect(html).toContain('2.2M L');
    expect(html).toContain('will not fill on its own');
    expect(html).toContain('Build pond');
  });

  it('shows the full-pool capacity and gameplay fill time during dam review', () => {
    const html = renderToStaticMarkup(<InfrastructureControl tool={{ phase: 'idle' }}
      damTool={{ phase: 'review', draft: { name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
        maxDamHeightM: 4 } }}
      pondTool={{ phase: 'idle' }} roads={[]} dams={[]} ponds={[]} selectedDam={null}
      selectedPond={null} units="metric" {...callbacks} />);
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
    const html = renderToStaticMarkup(<InfrastructureControl tool={{ phase: 'idle' }}
      damTool={{ phase: 'review', draft: { name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
        maxDamHeightM: 4 } }}
      pondTool={{ phase: 'idle' }} roads={[]} dams={[]} ponds={[]} selectedDam={null}
      selectedPond={null} units="imperial" {...callbacks} />);
    expect(html).toContain('570.6K gal');
  });

  it('requires two placed points before route review', () => {
    const one = render({ phase: 'drawing', roadType: 'two-lane',
      points: [[-121.5, 46.93]], cursor: null });
    expect(one).toMatch(/disabled=""[^>]*>Finish route/);
    const two = render({ phase: 'drawing', roadType: 'two-lane',
      points: [[-121.5, 46.93], [-121.49, 46.94]], cursor: null });
    expect(two).not.toMatch(/disabled=""[^>]*>Finish route/);
  });

  it('shows paved and total clearing widths during review', () => {
    const html = render({ phase: 'review', draft: { name: 'Road 1', roadType: 'two-lane',
      points: [[-121.5, 46.93], [-121.49, 46.94]], gradingStatus: 'ok',
      gradingError: null, gradingPolygons: [],
      earthwork: { cutM3: 100, fillM3: 40, balanceM3: 60 },
      maxFaceSlopePct: 100, maxGroundCrossSlopePct: 25,
      maxDisturbedWidthM: 20, ungradedLengthM: 0,
      gradingInfeasibleLines: [] } });
    expect(html).toContain('Paved width');
    expect(html).toContain('Clearing');
    expect(html).toContain('13 m');
    expect(html).toContain('100 m³');
    expect(html).toContain('Build road');
  });
});
