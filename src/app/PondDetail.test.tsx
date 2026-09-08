import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { SavedPond } from '../types';
import { PondDetail } from './PondDetail';

const pond: SavedPond = {
  id: 'pond-1', name: 'Lower Basin', boundary: [[0, 0], [0, 1], [1, 0], [0, 0]],
  topElevationM: 1200, areaM2: 20_000, averageDepthM: 3, maxDepthM: 5,
  capacityM3: 60_000, isSnowmaking: false, createdAt: '2026-09-07T00:00:00.000Z',
};

describe('PondDetail', () => {
  it('shows standalone pond properties and does not present it as a snowmaking source', () => {
    const html = renderToStaticMarkup(<PondDetail pond={pond} units="metric" onRemove={() => {}} />);
    expect(html).toContain('Standalone pond');
    expect(html).toContain('Not connected to snowmaking');
    expect(html).toContain('Top elevation');
    expect(html).toContain('Pond area');
    expect(html).toContain('20,000 m²');
    expect(html).toContain('Pond volume');
    expect(html).toContain('60.0M L');
    expect(html).toContain('Remove pond');
  });
});
