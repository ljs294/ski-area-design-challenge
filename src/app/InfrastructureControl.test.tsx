import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { InfrastructureControl, type RoadTool } from './InfrastructureControl';

const callbacks = {
  onArm: vi.fn(), onCancel: vi.fn(), onUndo: vi.fn(), onFinish: vi.fn(),
  onDraftChange: vi.fn(), onConfirm: vi.fn(), onClose: vi.fn(),
};

function render(tool: RoadTool, extra: Partial<Parameters<typeof InfrastructureControl>[0]> = {}) {
  return renderToStaticMarkup(<InfrastructureControl tool={tool} roads={[]}
    units="metric" {...callbacks} {...extra} />);
}

describe('InfrastructureControl', () => {
  it('offers only the two-lane 7 m road type', () => {
    const html = render({ phase: 'idle' });
    expect(html).toContain('Infrastructure · 0 roads');
    expect(html).toContain('Two-lane road — 7 m');
    expect((html.match(/<option/g) ?? [])).toHaveLength(1);
  });

  it('leaves water storage to the Snowmaking dock', () => {
    const html = render({ phase: 'idle' });
    expect(html).not.toContain('Build dam');
    expect(html).not.toContain('Build standalone pond');
  });

  it('shows a human-readable unreachable warning and connected lift status', () => {
    const base = { state: 'no-open-descent' as const, reachable: false, portal: null,
      message: 'Resort unreachable: Summit has no reachable open descent.', connectedLiftId: 'lift-1',
      connectedLiftName: 'Summit', reachableRunCount: 0, connectionPath: [],
      roadAccessLabel: 'Virtual edge-of-map access' };
    expect(render({ phase: 'idle' }, { guestConnectivity: base })).toContain('Resort unreachable');
    expect(render({ phase: 'idle' }, { guestConnectivity: { ...base, state: 'reachable', reachable: true,
      message: 'Summit connects the entrance to 2 open runs.', reachableRunCount: 2 } }))
      .toContain('Resort reachable');
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
