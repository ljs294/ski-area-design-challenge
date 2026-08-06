import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { buildSkiNetwork } from '../network';
import { MountainDashboards, type DashboardKind } from './MountainDashboards';

// DOM-free component-test pattern, matching NetworkMap.test.tsx /
// SnowmakingDashboard.test.tsx: render to a string and assert on markup.

const networkProps = {
  network: buildSkiNetwork([], []),
  units: 'metric' as const,
  selectedLiftId: null,
  selectedEdgeId: null,
  onSelectLift: vi.fn(),
  onSelectEdge: vi.fn(),
  onToggleTrailClosed: vi.fn(),
  onToggleLiftClosed: vi.fn(),
  onTogglePathClosed: vi.fn(),
};

const snowmakingProps = {
  dams: [],
  ponds: [],
  trails: [],
  lifts: [],
  nodes: [],
  coverDisplay: null,
  terrainRecord: null,
  units: 'metric' as const,
  selectedNodeId: null,
  onSelectNode: vi.fn(),
};

function render(dashboard: DashboardKind = 'trails') {
  return renderToStaticMarkup(
    <MountainDashboards
      dashboard={dashboard}
      onDashboardChange={vi.fn()}
      networkProps={networkProps}
      snowmakingProps={snowmakingProps}
      onClose={vi.fn()}
    />
  );
}

describe('MountainDashboards', () => {
  it('renders the picker with both dashboard options', () => {
    const html = render();
    expect(html).toContain('Trails &amp; Lifts');
    expect(html).toContain('Snowmaking');
  });

  it('marks the trails option pressed when dashboard is "trails"', () => {
    const html = render('trails');
    const trailsBtn = html.slice(0, html.indexOf('Trails &amp; Lifts'));
    const snowmakingBtn = html.slice(html.indexOf('Trails &amp; Lifts'), html.indexOf('Snowmaking'));
    expect(trailsBtn.slice(-60)).toContain('aria-pressed="true"');
    expect(snowmakingBtn.slice(-60)).toContain('aria-pressed="false"');
  });

  it('marks the snowmaking option pressed when dashboard is "snowmaking"', () => {
    const html = render('snowmaking');
    const trailsBtn = html.slice(0, html.indexOf('Trails &amp; Lifts'));
    const snowmakingBtn = html.slice(html.indexOf('Trails &amp; Lifts'), html.indexOf('Snowmaking'));
    expect(trailsBtn.slice(-60)).toContain('aria-pressed="false"');
    expect(snowmakingBtn.slice(-60)).toContain('aria-pressed="true"');
  });

  it('delegates to NetworkMap when dashboard is "trails"', () => {
    const html = render('trails');
    expect(html).toContain('aria-label="Mountain node map"');
    expect(html).not.toContain('aria-label="Snowmaking network map"');
  });

  it('delegates to SnowmakingDashboard when dashboard is "snowmaking"', () => {
    const html = render('snowmaking');
    expect(html).toContain('aria-label="Snowmaking network map"');
    expect(html).not.toContain('aria-label="Mountain node map"');
  });
});
