import { describe, expect, it } from 'vitest';
import { coreCompleteness, parseDaymet, resolveNorthAmericanTimezone } from './providers.mjs';

describe('Weather Lab provider normalization', () => {
  it('parses Daymet elevation and required daily fields', () => {
    const parsed = parseDaymet(['Daymet Software Version 4.0', 'Elevation: 427 meters',
      'year,yday,dayl (s),prcp (mm/day),srad (W/m^2),swe (kg/m^2),tmax (deg c),tmin (deg c),vp (Pa)',
      '2019,1,32000,4.2,120,15,-2,-12,410'].join('\n'));
    expect(parsed.elevationM).toBe(427); expect(parsed.days).toEqual([{ date: '2019-01-01', tminC: -12, tmaxC: -2,
      precipitationMm: 4.2, vaporPressurePa: 410, shortwaveWm2: 120, snowWaterEquivalentMm: 15, daylightSeconds: 32000 }]);
  });

  it('preserves missing Daymet values instead of inventing zeroes', () => {
    const parsed = parseDaymet(['Elevation: 427 meters',
      'year,yday,dayl (s),prcp (mm/day),srad (W/m^2),swe (kg/m^2),tmax (deg c),tmin (deg c),vp (Pa)',
      '2019,1,32000,,120,,,-12,410'].join('\n'));
    expect(parsed.days[0]).toMatchObject({ precipitationMm: null, snowWaterEquivalentMm: null, tmaxC: null, tminC: -12 });
  });

  it('resolves deterministic IANA timezones for supported coordinates', () => {
    expect(resolveNorthAmericanTimezone(39.7, -105)).toBe('America/Denver');
    expect(resolveNorthAmericanTimezone(44, -71)).toBe('America/New_York');
  });

  it('measures only the core MERRA-2 fields used by compilation', () => {
    expect(coreCompleteness([{ temperatureC: 1, dewPointC: 0, pressureHpa: 1010, windSpeedKph: 10,
      cloudCoverPct: 50, precipitationMm: 0 }])).toBe(1);
  });
});
