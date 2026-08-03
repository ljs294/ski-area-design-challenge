import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { StreamDetail } from './StreamDetail';

describe('StreamDetail', () => {
  it('shows editable width and guaranteed gameplay flow', () => {
    const html = renderToStaticMarkup(<StreamDetail units="metric" onWidthOverride={() => {}} onClose={() => {}}
      stream={{ id: 'way/1', name: 'Mill Creek', waterClass: 'stream', sourceWidthM: null,
        widthM: 3, widthSource: 'default', lengthM: 1200, flowM3s: 0.3 }} />);
    expect(html).toContain('Mill Creek');
    expect(html).toContain('Channel width');
    expect(html).toContain('Gameplay default');
    expect(html).toContain('300 L/s');
    expect(html).toContain('Fixed gameplay capacity');
import { describe, expect, it, vi } from 'vitest';
import type { StreamAnalysis } from '../streamAnalysis';
import { StreamDetail } from './StreamDetail';

const stream: StreamAnalysis = {
  id: 'way/7', name: 'Cold Creek', waterClass: 'stream', lengthM: 1500,
  sourceWidthM: 5, defaultWidthM: 3, widthM: 7, widthSource: 'override', dischargeM3s: 1,
};

describe('StreamDetail', () => {
  it('shows editable width and the guaranteed gameplay flow', () => {
    const html = renderToStaticMarkup(<StreamDetail stream={stream} units="metric"
      onWidthOverride={vi.fn()} onClose={vi.fn()} />);
    expect(html).toContain('Cold Creek');
    expect(html).toContain('Channel width');
    expect(html).toContain('Reset to OpenStreetMap width');
    expect(html).toContain('Gameplay flow estimate');
    expect(html).toContain('1,000 L/s');
    expect(html).toContain('Used for snowmaking simulation');
    expect(html).toContain('not measured streamflow');
  });

  it('always shows flow when using a class-default width', () => {
    const html = renderToStaticMarkup(<StreamDetail stream={{ ...stream,
      widthM: 3, dischargeM3s: 0.3, widthSource: 'default', sourceWidthM: null }} units="imperial"
      onWidthOverride={vi.fn()} onClose={vi.fn()} />);
    expect(html).toContain('US gal/min');
    expect(html).not.toContain('unavailable');
    expect(html).not.toContain('Reset to class default');
  });
});
