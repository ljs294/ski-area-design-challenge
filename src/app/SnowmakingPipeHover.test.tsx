import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { SnowmakingPipeHoverState } from './SnowmakingPipeHover';
import { SnowmakingPipeHoverDetails } from './SnowmakingPipeHover';

const hover: SnowmakingPipeHoverState = {
  pipe: { id: 'pipe-1', name: 'Summit Main', diameterIn: 8, lengthM: 285,
    verticalM: 49, vertices: [
      { point: [-121.5, 46.9], elevM: 1000, nodeId: null },
      { point: [-121.49, 46.91], elevM: 1010, nodeId: null },
    ], createdAt: '2026-01-01' },
  segmentId: 'pipe-1:segment:0', segmentIndex: 0, point: { x: 100, y: 120 },
  segmentStats: { lengthM: 142, verticalM: 10 },
  analysis: null, direction: null,
};

describe('Snowmaking pipe hover details', () => {
  it('shows physical properties without changing pipe selection', () => {
    const html = renderToStaticMarkup(<SnowmakingPipeHoverDetails hover={hover} units="metric" />);
    expect(html).toContain('Hovered pipe properties');
    expect(html).toContain('Summit Main · 1');
    expect(html).toContain('8&quot;');
    expect(html).toContain('142 m');
    expect(html).toContain('10 m');
    expect(html).not.toContain('285 m');
    expect(html).not.toContain('49 m');
  });

  it('adds hydraulic properties for an analyzed segment', () => {
    const analysis = { id: hover.segmentId, pipeId: hover.pipe.id, segmentIndex: 0,
      fromNodeKey: 'a', toNodeKey: 'b', flowGpm: 58.4, active: true,
      lengthFt: 328, staticHeadFt: 10, frictionHeadFt: 4.2,
      fromPressurePsi: 90, toPressurePsi: 84, upstreamPressurePsi: 90,
      downstreamPressurePsi: 84 };
    const html = renderToStaticMarkup(<SnowmakingPipeHoverDetails
      hover={{ ...hover, analysis, direction: { from: 'Pond Intake', to: 'Hydrant 1' } }}
      units="imperial" />);
    expect(html).toContain('Pond Intake → Hydrant 1');
    expect(html).toContain('58.4 GPM');
    expect(html).toContain('90 →');
    expect(html).toContain('84 PSI');
    expect(html).toContain('4.2 ft');
    expect(html).toContain('466 ft');
    expect(html).not.toContain('328 ft');
  });
});
