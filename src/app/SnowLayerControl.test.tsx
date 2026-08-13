import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { SnowLayerControl } from './SnowLayerControl';

describe('SnowLayerControl', () => {
  it('shows the mode switch, metric cursor values, and active legend', () => {
    const html = renderToStaticMarkup(<SnowLayerControl mode="depth" onModeChange={vi.fn()} onClose={vi.fn()}
      units="metric" readout={{ elevationM: 1000, overlay: 'snow', slopeDeg: 10,
        aspectCompass: 'N', coverLabel: null, snowDepthM: 0.42, snowSurface: 1 }} />);
    expect(html).toContain('Depth');
    expect(html).toContain('Conditions');
    expect(html).toContain('42 cm');
    expect(html).toContain('P · Powder');
    expect(html).toContain('120+ cm');
    expect(html).toContain('aria-label="Close Snow layer"');
  });

  it('renders all documented surface conditions in conditions mode', () => {
    const html = renderToStaticMarkup(<SnowLayerControl mode="conditions" onModeChange={vi.fn()} onClose={vi.fn()}
      units="imperial" readout={null} />);
    expect(html).toContain('PP · Packed Powder');
    expect(html).toContain('WP · Wet Powder');
    expect(html.match(/class="legend-row"/g)).toHaveLength(11);
  });
});
