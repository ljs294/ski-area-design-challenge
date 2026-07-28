import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { TrailControl, type DraftTrail } from './TrailControl';

const part = {
  polygon: [[[0, 1], [1, 1], [1, 0], [0, 0], [0, 1]]] as [number, number][][],
  centerline: [[0.5, 1], [0.5, 0]] as [number, number][],
  centerlineElevM: [100, 80],
};
const draft: DraftTrail = {
  parts: [part], ungradedParts: [part], areaM2: 1000, brushWidthM: 30,
  name: 'Run 1', status: 'complete', difficulty: 'green', elevStatus: 'ok',
  gradingEnabled: false, gradingStatus: 'idle', gradingError: null,
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
});
