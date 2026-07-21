import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { InfrastructureControl, type RoadTool } from './InfrastructureControl';

const callbacks = {
  onArm: vi.fn(), onCancel: vi.fn(), onUndo: vi.fn(), onFinish: vi.fn(),
  onDraftChange: vi.fn(), onConfirm: vi.fn(), onClose: vi.fn(),
};

function render(tool: RoadTool) {
  return renderToStaticMarkup(<InfrastructureControl tool={tool} roads={[]} units="metric" {...callbacks} />);
}

describe('InfrastructureControl', () => {
  it('offers only the two-lane 7 m road type', () => {
    const html = render({ phase: 'idle' });
    expect(html).toContain('Infrastructure · Roads (0)');
    expect(html).toContain('Two-lane road — 7 m');
    expect((html.match(/<option/g) ?? [])).toHaveLength(1);
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
      points: [[-121.5, 46.93], [-121.49, 46.94]] } });
    expect(html).toContain('Paved width');
    expect(html).toContain('Clearing');
    expect(html).toContain('13 m');
    expect(html).toContain('Build road');
  });
});
