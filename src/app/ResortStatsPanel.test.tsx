import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ResortStatsPanel } from './ResortStatsPanel';

function render(units: 'imperial' | 'metric', averageAnnualSnowfallCm: number | null): string {
  return renderToStaticMarkup(<ResortStatsPanel name="Test Peak" onRename={() => undefined}
    lifts={[]} trails={[]} dams={[]} ponds={[]} center={[0, 0]} units={units}
    averageAnnualSnowfallCm={averageAnnualSnowfallCm} onClose={() => undefined} />);
}

describe('ResortStatsPanel annual snowfall', () => {
  it('uses the selected global units', () => {
    expect(render('imperial', 254)).toContain('100.0 in');
    expect(render('metric', 254)).toContain('254.0 cm');
  });

  it('shows no fabricated value before historical weather is loaded', () => {
    const markup = render('metric', null);
    expect(markup).toContain('Avg. annual snowfall');
    expect(markup).not.toContain('TBD');
  });
});
