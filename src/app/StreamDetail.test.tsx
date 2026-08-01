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
  });
});
