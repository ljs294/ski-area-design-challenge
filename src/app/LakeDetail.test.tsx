import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { LakeAnalysis } from '../lakeAnalysis';
import { LakeDetail } from './LakeDetail';

const lake: LakeAnalysis = {
  id: 'way/42', name: 'Mirror Lake', sourceName: null, nameSource: 'player',
  areaM2: 20_000, surfaceElevationM: 1200,
  estimatedAverageDepthM: 3, averageDepthM: 4, depthSource: 'override', volumeM3: 80_000,
};

describe('LakeDetail', () => {
  it('shows derived properties, provenance, editable units, and reset affordance', () => {
    const html = renderToStaticMarkup(<LakeDetail lake={lake} units="metric"
      onNameOverride={() => {}} onDepthOverride={() => {}} onClose={() => {}} />);
    expect(html).toContain('Mirror Lake');
    expect(html).toContain('2.0 ha');
    expect(html).toContain('Custom');
    expect(html).toContain('80.0M L');
    expect(html).toContain('Reset to terrain estimate');
    expect(html).toContain('not measured bathymetry');
    expect(html).toContain('Pond name');
    expect(html).toContain('Remove name');
  });

  it('allows manual entry when the terrain estimate is unavailable', () => {
    const unavailable = { ...lake, averageDepthM: null, estimatedAverageDepthM: null,
      depthSource: 'unavailable' as const, volumeM3: null };
    const html = renderToStaticMarkup(<LakeDetail lake={unavailable} units="imperial"
      onNameOverride={() => {}} onDepthOverride={() => {}} onClose={() => {}} />);
    expect(html).toContain('Unavailable');
    expect(html).toContain('Average depth in feet');
  });
});
