import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { DashboardMenu } from './DashboardMenu';

describe('DashboardMenu', () => {
  it('renders an accessible dashboard-icon pill and exposes its state', () => {
    const html = renderToStaticMarkup(<DashboardMenu active="trails" onChange={vi.fn()} />);
    expect(html).toContain('aria-haspopup="menu"');
    expect(html).toContain('aria-expanded="false"');
    expect(html).toContain('dashboard-bubble-icon');
    expect(html).toContain('Dashboards');
    expect(html).toContain('is-active');
  });

  it('is inactive when no thematic dashboard is selected', () => {
    const html = renderToStaticMarkup(<DashboardMenu active={null} onChange={vi.fn()} />);
    expect(html).not.toContain('dashboard-bubble is-active');
  });
});
