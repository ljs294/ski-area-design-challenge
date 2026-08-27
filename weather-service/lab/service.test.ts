import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { WeatherLabService } from './service.mjs';

describe('Weather Lab observation service', () => {
  it('ranks the alias-merged Jackson station and persists immutable preparation artifacts', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-lab-service-'));
    try {
      const service = new WeatherLabService({ cacheDirectory: root, mode: 'fixture' });
      const query = new URLSearchParams({ latitude: '44.1672897', longitude: '-71.164239', elevationM: '427' });
      const [station] = service.stations(query);
      expect(station.sourceIds).toEqual(['726130-14755', 'KMWN']); expect(station.score).toBeGreaterThan(.9);
      const created = await service.create({ stationId: 'KMWN', validationYear: 2019, trainingPolicy: { kind: 'prior-30' },
        location: { id: 'jackson-nh', name: 'Jackson', latitude: 44.1672897, longitude: -71.164239, comparisonElevationM: 427 } });
      let job = await service.get(created.id);
      for (let index = 0; index < 20 && job.status !== 'succeeded'; index += 1) { await new Promise((resolve) => setTimeout(resolve, 5)); job = await service.get(created.id); }
      expect(job.status).toBe('succeeded'); expect(job.result.modelHash).toMatch(/^[a-f0-9]{64}$/);
      const model = await service.store.read('models', job.result.modelHash);
      expect(model.trainingPeriod.years).not.toContain(2019); expect(model.excludedValidationYear).toBe(2019); expect(model.months).toHaveLength(12);
      const observed = await service.store.read('observations', job.result.observationHash);
      expect(observed.hours).toHaveLength(8760); expect(observed.observationHash).toMatch(/^[a-f0-9]{64}$/);
      await expect(service.store.read('models', job.result.modelHash)).resolves.toEqual(model);
    } finally { await rm(root, { recursive: true, force: true }); }
  });

  it('refuses silent fixture fallback for live-provider requests', () => {
    const service = new WeatherLabService({ cacheDirectory: tmpdir(), mode: 'live' });
    expect(() => service.stations(new URLSearchParams({ latitude: '44', longitude: '-71' }))).toThrow(/no fixture fallback/i);
  });
});
