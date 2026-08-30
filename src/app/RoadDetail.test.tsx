import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { RoadDetail } from './RoadDetail';

describe('RoadDetail', () => {
  it('shows read-only imported name, width, lanes, and width source', () => {
    const html = renderToStaticMarkup(<RoadDetail units="metric" onClose={() => undefined} road={{
      key: 'osm:way/1', id: 'way/1', source: 'osm', name: 'Pass Road', widthM: 10.5,
      widthSource: 'lanes', points: [[0, 0], [1, 1]], totalLanes: 3,
      forwardLanes: 2, backwardLanes: 1, oneWay: false,
    }} />);
    expect(html).toContain('Pass Road');
    expect(html).toContain('11 m');
    expect(html).toContain('Lane estimate');
    expect(html).toContain('OpenStreetMap');
    expect(html).not.toContain('<input');
  });
});
