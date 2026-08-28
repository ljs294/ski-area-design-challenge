import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { WeatherLabService, historicalSeries, trainingYears } from './service.mjs';

async function terminal(service: WeatherLabService, id: string) {
  let job = await service.get(id);
  for (let index = 0; index < 300 && ['queued', 'running'].includes(job.status); index += 1) {
    await new Promise((resolve) => setTimeout(resolve, 20)); job = await service.get(id);
  }
  return job;
}
function dayRows(year: number) {
  const count = (Date.UTC(year + 1, 0, 1) - Date.UTC(year, 0, 1)) / 86_400_000;
  return Array.from({ length: count }, (_, index) => ({ date: new Date(Date.UTC(year, 0, index + 1)).toISOString().slice(0, 10),
    tminC: -5, tmaxC: 5, precipitationMm: index === 0 ? 8 : 0, vaporPressurePa: 500,
    shortwaveWm2: 140, snowWaterEquivalentMm: 0, daylightSeconds: 36_000 }));
}
function merraHours(year: number) {
  const count = (Date.UTC(year + 1, 0, 1) - Date.UTC(year, 0, 1)) / 3_600_000;
  return Array.from({ length: count }, (_, index) => ({ at: new Date(Date.UTC(year, 0, 1, index)).toISOString(),
    temperatureC: -2 + Math.sin(index / 24), relativeHumidityPct: 78, pressureHpa: 1012,
    uWindMps: -4, vWindMps: 1, precipitationMm: index % 200 === 0 ? .5 : 0,
    shortwaveWm2: Math.max(0, 300 * Math.sin(index % 24 / 24 * Math.PI)), cloudCoverPct: 55 }));
}

describe('Weather Lab MERRA-2 observation service', () => {
  it('uses MERRA-2 and derived snowfall provenance without station data', async () => {
    const source = { id: 'MERRA2-44.000--71.250', sourceIds: ['MERRA2-44.000--71.250'], name: 'MERRA-2 grid',
      latitude: 44, longitude: -71.25, elevationM: 427, timezone: 'America/New_York', distanceKm: 0, score: 1 };
    const series = await historicalSeries(source, 2019, source.timezone, merraHours(2019), dayRows(2019));
    expect(series.provenance.providers).toEqual(['MERRA-2', 'Daymet V4']);
    expect(series.days?.[0].snowfallKind).toBe('derived');
    expect(series.days?.[0].sources.snowfall).toContain('MERRA-2');
    expect(series.hours.filter((hour) => hour.localDateTime.startsWith('2019-01-01'))
      .reduce((sum, hour) => sum + (hour.precipitationMm ?? 0), 0)).toBeCloseTo(8, 8);
  });

  it('persists immutable fixture artifacts', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-lab-service-'));
    try {
      const service = new WeatherLabService({ cacheDirectory: root, mode: 'fixture' });
      const job = await terminal(service, (await service.create({ stationId: 'KMWN', validationYear: 2019,
        trainingPolicy: { kind: 'prior-30' } })).id);
      expect(job.status).toBe('succeeded');
      expect((await service.store.read('models', job.result.modelHash)).months).toHaveLength(12);
      expect((await service.store.read('observations', job.result.observationHash)).hours).toHaveLength(8760);
    } finally { await rm(root, { recursive: true, force: true }); }
  });

  it('resolves one MERRA-2 grid and the available prior-30 years', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-lab-context-'));
    try {
      const service = new WeatherLabService({ cacheDirectory: root, mode: 'live',
        daymet: { async request() { return { elevationM: 1655, days: dayRows(2025) }; } },
        merra2: { availableStartYear: 2001, async getHourly() { throw new Error('context must not download hourly data'); } },
        now: () => new Date('2026-08-27T00:00:00Z') });
      const context = await service.locationContext({ latitude: 39.74, longitude: -104.99 });
      expect(context.selectedStation?.id).toMatch(/^MERRA2-/); expect(context.stations).toHaveLength(1);
      expect(context.eligibleValidationYears[0]).toBe(2025); expect(context.timezone).toBe('America/Denver');
      expect(context.selectedStation?.availableYears?.[0]).toBe(2001);
      expect(context.warnings[0]).toContain('begin in 2001');
      expect(trainingYears({ kind: 'prior-30' }, 2025, context.selectedStation?.availableYears)).toEqual(
        Array.from({ length: 24 }, (_, index) => 2001 + index));
    } finally { await rm(root, { recursive: true, force: true }); }
  });

  it('prepares Daymet/MERRA-2 history with source progress and no validation leakage', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-lab-live-')); const requestedYears: number[] = [];
    try {
      const service = new WeatherLabService({ cacheDirectory: root, mode: 'live',
        daymet: { async request(_lat: number, _lon: number, years: number[]) { return { elevationM: 1655, days: years.flatMap(dayRows) }; } },
        merra2: { async getHourly(_request: unknown, year: number) { requestedYears.push(year); return { hours: merraHours(year) }; } },
        now: () => new Date('2026-08-27T00:00:00Z') });
      const job = await terminal(service, (await service.create({ version: 1, latitude: 39.74, longitude: -104.99,
        elevationOverrideM: 1700, validationYear: 2019, trainingPolicy: { kind: 'fixed', startYear: 2018, endYear: 2018 } })).id);
      expect(job.status).toBe('succeeded');
      const model = await service.store.read('models', job.result.modelHash);
      const observed = await service.store.read('observations', job.result.observationHash);
      expect(model.trainingPeriod.years).toEqual([2018]); expect(model.provenance.providers).toEqual(['Daymet V4', 'MERRA-2']);
      expect(observed.provenance.providers).toEqual(['MERRA-2', 'Daymet V4']); expect(observed.days[0].snowfallKind).toBe('derived');
      expect(requestedYears).toEqual([2018, 2019, 2020]);
      const progress = job.events.filter((event: { stage: string }) => event.stage === 'merra2');
      expect(progress.at(-1).message).toContain('3/3');
    } finally { await rm(root, { recursive: true, force: true }); }
  }, 20_000);
});
