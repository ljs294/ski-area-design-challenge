import { beforeEach, describe, expect, it, vi } from 'vitest';
import { annualWeatherSeed, weatherYearStart } from './weather/annualWeather';
import { preparedWeatherIdentityKey,
  type PreparedAnnualWeather, type PreparedWeatherIdentity } from './weather/preparedWeatherModel';
import { encodePreparedWeather } from './weather/preparedWeatherCodec';

const { desktopRead, desktopWrite } = vi.hoisted(() => ({ desktopRead: vi.fn(), desktopWrite: vi.fn() }));
vi.mock('./desktopBridge', () => ({
  desktop: {
    preparedWeather: { read: desktopRead, write: desktopWrite },
  },
}));

import { readPreparedWeather, writePreparedWeather } from './preparedWeatherStorageClient';

const identity: PreparedWeatherIdentity = {
  packageContentHash: 'package-hash',
  terrainBinding: 'terrain-binding',
  seed: 'seed',
  year: 2023,
  timezone: 'America/New_York',
  generatorVersion: 3,
  configurationVersion: 2,
  cacheFormatVersion: 1,
};

function prepared(): PreparedAnnualWeather {
  const startsAt = Date.parse(weatherYearStart(identity.year, identity.timezone));
  const endsAt = weatherYearStart(identity.year + 1, identity.timezone);
  const hours = Array.from({ length: (Date.parse(endsAt) - startsAt) / 3_600_000 }, (_, index) => ({
    at: new Date(startsAt + index * 3_600_000).toISOString(),
    referenceTemperatureC: 0,
  }));
  const resolvedHours = hours.map((hour) => ({
    ...hour,
    temperatureC: 0,
  }));
  return {
    identity,
    session: {
      timezone: identity.timezone,
      plan: {
        timezone: identity.timezone,
        startsAt: weatherYearStart(identity.year, identity.timezone),
        endsAt: new Date(Date.parse(endsAt) - 3_600_000).toISOString(),
        hours,
        events: [],
        seed: annualWeatherSeed(identity.seed, identity.year),
        packageContentHash: identity.packageContentHash,
        generatorVersion: identity.generatorVersion,
      },
    },
    resolvedHours,
    averageAnnualSnowfallCm: 42,
  } as unknown as PreparedAnnualWeather;
}

describe('prepared weather storage client', () => {
  beforeEach(() => {
    desktopRead.mockReset();
    desktopWrite.mockReset();
  });

  it('includes every identity field in its deterministic key', () => {
    const original = preparedWeatherIdentityKey(identity);
    const changed = preparedWeatherIdentityKey({ ...identity, configurationVersion: 99 });
    expect(original).not.toBe(changed);
    expect(preparedWeatherIdentityKey({ ...identity })).toBe(original);
  });

  it('validates the complete timeline before using a desktop cache', async () => {
    const value = prepared();
    const bytes = await encodePreparedWeather(value);
    desktopRead.mockResolvedValue(bytes);
    expect(await readPreparedWeather(identity)).toEqual(value);
    expect(desktopRead).toHaveBeenCalledWith(identity);

    desktopRead.mockResolvedValue(Uint8Array.from(bytes, (byte, index) => index === 12 ? byte ^ 1 : byte));
    expect(await readPreparedWeather(identity)).toBeNull();
    desktopRead.mockResolvedValue(bytes);
    expect(await readPreparedWeather({ ...identity, packageContentHash: 'wrong-package' })).toBeNull();
  });

  it('writes through the desktop prepared-weather namespace', async () => {
    desktopWrite.mockResolvedValue(true);
    const value = prepared();
    expect(await writePreparedWeather(value)).toBe(true);
    expect(desktopWrite).toHaveBeenCalledWith(identity, expect.any(Uint8Array));
  });

  it('returns safe misses when browser storage is unavailable', async () => {
    vi.resetModules();
    vi.doMock('./desktopBridge', () => ({ desktop: null }));
    vi.stubGlobal('indexedDB', undefined);
    const browserClient = await import('./preparedWeatherStorageClient');
    expect(await browserClient.readPreparedWeather(identity)).toBeNull();
    expect(await browserClient.writePreparedWeather(prepared())).toBe(false);
    vi.unstubAllGlobals();
    vi.doUnmock('./desktopBridge');
  });

  it('rejects an interrupted desktop write', async () => {
    desktopWrite.mockResolvedValue(false);
    expect(await writePreparedWeather(prepared())).toBe(false);
  });

  it('rejects malformed opaque desktop bytes', async () => {
    const value = prepared();
    const bytes = await encodePreparedWeather(value);
    desktopRead.mockResolvedValue(bytes.slice(0, -4));
    expect(await readPreparedWeather(identity)).toBeNull();
  });
});
