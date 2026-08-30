import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import {
  SMOOTHED_SIMULATION_TUNING,
  adjustedConditionTransitionRow,
  adjustedMacroTransitionRow,
  createJacksonClimateModel,
} from '../../weather-engine/src/index.ts';
import type {
  WeatherConditionDiagnosticsV1,
  WeatherDailySummaryV1,
  WeatherDiagnosticsV2,
  WeatherEventType,
  WeatherEventV1,
} from '../../weather-engine/src/index.ts';
import { DailyComparisonTable } from '../src/DailyComparisonTable.tsx';
import { DailyMetricChart } from '../src/DailyMetricChart.tsx';
import { DailyStateRibbons } from '../src/DailyStateRibbons.tsx';
import { MarkovDiagnosticsPanel } from '../src/MarkovDiagnosticsPanel.tsx';
import { WeatherComparisonScorecard } from '../src/WeatherComparisonScorecard.tsx';
import { WeatherEventTimeline } from '../src/WeatherEventTimeline.tsx';
import { WeatherTuningControls } from '../src/WeatherTuningControls.tsx';

function day(localDate: string): WeatherDailySummaryV1 {
  return {
    localDate, expectedHours: 24, availableHours: 24,
    completeness: { temperatureC: 1, wetBulbC: 1, precipitationMm: 1, snowfallCm: 1, condition: 1 },
    temperatureC: { minimum: -8, mean: -4, maximum: 0 },
    wetBulbC: { minimum: -9, mean: -5, maximum: -1 }, snowmakingHours: 20,
    precipitationMm: 4, precipitationByPhaseMm: { none: 0, rain: 0, mixed: 0, snow: 4, 'freezing-rain': 0 },
    snowfallCm: 5, snowfallSource: 'simulated', conditionHours: { snow: 16, overcast: 8 },
    dominantCondition: 'snow', hazards: [], macroHours: { arctic: 24 }, dominantMacro: 'arctic', eventIds: [],
  };
}

function weatherEvent(type: WeatherEventType, index: number): WeatherEventV1 {
  const startsAt = `2024-01-${String(index + 1).padStart(2, '0')}T00:00:00Z`;
  const endsAt = `2024-01-${String(index + 2).padStart(2, '0')}T00:00:00Z`;
  return {
    version: 1, id: `${type}-${index}`, type, startsAt, endsAt,
    localStartDate: startsAt.slice(0, 10), localEndDate: endsAt.slice(0, 10), durationHours: 24,
    severity: 'minor', intensityPercentile: 50, totalPrecipitationMm: type === 'storm' ? 8 : 0,
    peakPrecipitationMm: type === 'storm' ? 2 : 0,
    precipitationByPhaseMm: { none: 0, rain: 0, mixed: 0, snow: type === 'storm' ? 8 : 0, 'freezing-rain': 0 },
    snowfallCm: type === 'storm' ? 10 : 0, temperatureChangeC: type === 'cold-snap' ? -7 : type === 'warm-up' ? 7 : 0,
    meanWindSpeedKph: 10, peakWindGustKph: 20, pressureChangeHpa: 3,
    stormStyle: type === 'storm' ? 'upslope' : null, styleConfidence: type === 'storm' ? 'moderate' : null,
    styleEvidence: type === 'storm' ? ['terrain and wind signature'] : [],
  };
}

const conditionDiagnostics: WeatherConditionDiagnosticsV1 = {
  conditionOccupancy: { clear: 0.1, 'partly-cloudy': 0.1, overcast: 0.1, flurries: 0.1, snow: 0.2, 'heavy-snow': 0.1, mixed: 0.1, 'freezing-rain': 0.1, rain: 0.1 },
  transitionCounts: { 'clear->snow': 2 },
  spellLengths: { clear: [2], 'partly-cloudy': [2], overcast: [2], flurries: [2], snow: [4], 'heavy-snow': [2], mixed: [2], 'freezing-rain': [2], rain: [2] },
};

const diagnostics: WeatherDiagnosticsV2 = {
  ...conditionDiagnostics,
  macroOccupancy: { arctic: 0.2, 'continental-polar': 0.2, 'maritime-polar': 0.2, 'warm-wet': 0.2, frontal: 0.2 },
  macroTransitionCounts: { 'arctic->frontal': 1 },
  macroSpellLengths: { arctic: [24], 'continental-polar': [12], 'maritime-polar': [18], 'warm-wet': [8], frontal: [6] },
};

describe('Weather Lab comparison components', () => {
  it('renders all curated tuning controls with fitted-value semantics', () => {
    const html = renderToStaticMarkup(<WeatherTuningControls value={SMOOTHED_SIMULATION_TUNING} onChange={() => undefined} onPreset={() => undefined} hasBaseline/>);
    expect(html).toContain('Simulation tuning');
    expect(html).toContain('Temperature persistence');
    expect(html).toContain('Use fitted climate value');
    expect(html).toContain('Match pinned Baseline');
    expect((html.match(/type="range"/g) ?? [])).toHaveLength(13);
    expect(html).not.toContain('Forecast error');
  });

  it('renders aligned daily chart and accessible comparison table', () => {
    const series = { observed: [day('2024-01-01')], baseline: [day('2024-01-01')], candidate: [day('2024-01-01')] };
    const table = renderToStaticMarkup(<DailyComparisonTable series={series} metric="wet-bulb" month={1}/>);
    const chart = renderToStaticMarkup(<DailyMetricChart series={series} metric="temperature" month={1}/>);
    const ribbons = renderToStaticMarkup(<DailyStateRibbons series={series} month={1}/>);
    expect(table).toContain('<caption>Daily wet bulb');
    expect(table).toContain('2024-01-01');
    expect(table).toContain('100% complete');
    expect(chart).toContain('role="img"');
    expect(chart).toContain('Temperature (minimum / mean / maximum)');
    expect(ribbons).toContain('Dominant condition ribbons');
    expect(ribbons).toContain('Dominant macro air-mass ribbons');
  });

  it('renders exactly the revised event taxonomy in a visual timeline and detail table', () => {
    const events = (['storm', 'cold-snap', 'warm-up', 'dry-spell'] as const).map(weatherEvent);
    const html = renderToStaticMarkup(<WeatherEventTimeline series={{ observed: events, baseline: events, candidate: events }} startDate="2024-01-01" endDate="2024-01-31"/>);
    expect(html).toContain('Storm');
    expect(html).toContain('Cold Snap');
    expect(html).toContain('Warm Up');
    expect(html).toContain('Dry Spell');
    expect(html).toContain('Upslope');
    expect(html).toContain('Evidence: terrain and wind signature');
  });

  it('renders condition, macro, transition, and score diagnostics', () => {
    const markov = renderToStaticMarkup(<MarkovDiagnosticsPanel observed={conditionDiagnostics} baseline={diagnostics} candidate={diagnostics}
      monthModel={createJacksonClimateModel().months[0]} tuning={SMOOTHED_SIMULATION_TUNING}
      adjustedRow={adjustedConditionTransitionRow} adjustedMacroRow={adjustedMacroTransitionRow}/>);
    const scores = { temperatureMeanBiasC: 1, temperatureMeanMaeC: 2, wetBulbMeanBiasC: 1, wetBulbMeanMaeC: 2,
      precipitationBiasMm: 3, precipitationMaeMm: 4, dominantConditionAgreement: 0.5, eventCountDifference: 1,
      eventDurationDifferenceHours: 2, eventOverlapScore: 0.75,
      stormSeverityAgreement: 0.5, stormStyleAgreement: null };
    const scorecard = renderToStaticMarkup(<WeatherComparisonScorecard baseline={scores} candidate={{ ...scores, temperatureMeanMaeC: 1 }}/>);
    expect(markov).toContain('Markov-chain diagnostics');
    expect(markov).toContain('Macro air-mass occupancy');
    expect(markov).toContain('clear-&gt;snow');
    expect(markov).toContain('arctic-&gt;frontal');
    expect(markov).toContain('Macro air-mass transition probabilities');
    expect(markov).toContain('Simulation-adjusted');
    expect(scorecard).toContain('Temperature daily MAE');
    expect(scorecard).toContain('-1.00 °C');
  });
});
