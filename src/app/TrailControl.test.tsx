import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { TrailControl, type DraftTrail } from './TrailControl';

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
};
const callbacks = {
  onBrushWidthChange: vi.fn(), onCancel: vi.fn(), onModeChange: vi.fn(),
  onUndo: vi.fn(), onClear: vi.fn(), onFinish: vi.fn(), onDraftChange: vi.fn(),
  onConfirm: vi.fn(), onEditPatch: vi.fn(), onCloseEdit: vi.fn(), onDelete: vi.fn(),
  onRetryElevation: vi.fn(), onGradingChange: vi.fn(),
};

describe('TrailControl terrain grading', () => {
  it('offers an unchecked terrain grading option during review', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft }}
      trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} />);
    expect(html).toContain('Grade terrain');
    expect(html).not.toContain('checked=""');
    expect(html).toContain('Build run');
  });

  it('blocks construction while a checked grade is calculating', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, gradingEnabled: true, gradingStatus: 'pending',
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} />);
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
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} />);
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
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} />);
    expect(html).not.toContain('left at natural');
  });

  it('shows the earthwork bill for a graded run', () => {
    const html = renderToStaticMarkup(<TrailControl tool={{ phase: 'review', draft: {
      ...draft, ...graded, ungradedLengthM: 0, infeasibleLines: [],
    } }} trails={[]} selectedId={null} units="metric" brushWidthM={30} {...callbacks} />);
    expect(html).toContain('1,200 m³');
    expect(html).toContain('Hillside cross slope');
    expect(html).toContain('42%');
    expect(html).toContain('90 m');
    expect(html).not.toMatch(/disabled=""[^>]*>Build run/);
  });
});
