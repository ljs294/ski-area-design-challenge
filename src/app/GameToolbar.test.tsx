import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { GameForecastDay, GameForecastHour } from '../weather/gameForecast';
import { GameToolbar } from './GameToolbar';
import type { GameSimulationController } from './useGameSimulation';

function forecastHour(index: number): GameForecastHour {
  return {
    at: new Date(Date.UTC(2026, 8, 1, index)).toISOString(), temperatureC: 2 + index / 10,
    wetBulbC: 1, humidityPct: 80, precipitationMm: 0, precipitationType: 'none', snowfallCm: 0,
    windSpeedKph: 12, windGustKph: 20, windDirectionDeg: 270, windUms: -3, windVms: 0,
    cloudCoverPct: 30, visibilityKm: 20,
    pressureHpa: 1010, radiationWm2: 100, globalRadiationWm2: 100, directRadiationWm2: 70,
    diffuseRadiationWm2: 30, cloudTransmissionPct: 70, snowWaterEquivalentMm: 0,
    solarElevationDeg: 20, solarAzimuthDeg: 180, provenance: { fields: {}, fieldFlags: 0 },
    leadHour: index, confidencePct: 99,
  };
}

describe('in-game weather presentation', () => {
  it('renders seven fluid forecast tabs and a compact 24-hour grid', () => {
    const hours = Array.from({ length: 24 }, (_, index) => forecastHour(index));
    const days: GameForecastDay[] = Array.from({ length: 7 }, (_, index) => ({
      date: `2026-09-0${index + 1}`, confidencePct: 99 - index, condition: 'none', lowC: -2,
      highC: 5, precipitationMm: 1, snowfallCm: 2, windSpeedKph: 12, windGustKph: 20, hours,
    }));
    const simulation = {
      status: 'ready', message: 'Ready', analysisOpen: true,
      weatherPackage: { manifest: { quality: 'estimated', sourceSummary: 'test package' } },
      session: { timezone: 'America/New_York' }, current: hours[0],
      forecast: { schemaVersion: 1, issuedAt: hours[0].at, endsAt: hours.at(-1)!.at, annualRunIdentity: 'test', hours, days },
      clock: { season: 'winter', winterWeek: 1, calendarDate: hours[0].at, timezone: 'America/New_York', speed: 1, runState: 'paused' },
      togglePlayback() {}, setSpeed() {}, toggleAnalysis() {},
    } as unknown as GameSimulationController;

    const terrain = {
      key: 'test-terrain', sampleGridSize: 2, sampleHeights: new Float32Array([1000, 1000, 1000, 1000]),
      bounds: { west: -71.2, south: 44, east: -71, north: 44.2 },
    } as never;
    const markup = renderToStaticMarkup(<GameToolbar resortName="Test Peak" onOpenStats={() => undefined}
      readout={null} units="metric" terrain={terrain} simulation={simulation} />);

    expect(markup.match(/role="tab"/g)).toHaveLength(7);
    expect(markup).toContain('aria-selected="true"');
    expect(markup).toContain('class="game-forecast-hour-grid"');
    expect(markup.match(/<time>/g)).toHaveLength(24);
    expect(markup).toContain('title="Inspect this game weather package"');
    expect(markup).not.toContain('Local weather');
    expect(markup).toContain('2.0 \u00b0C');
    expect(markup).toContain('12.0 km/h');

    const usMarkup = renderToStaticMarkup(<GameToolbar resortName="Test Peak" onOpenStats={() => undefined}
      readout={null} units="imperial" terrain={terrain} simulation={simulation} />);
    expect(usMarkup).toContain('35.6 \u00b0F');
    expect(usMarkup).toContain('7.5 mph');
    expect(usMarkup).toContain('0.04 in liquid');
  });
});
