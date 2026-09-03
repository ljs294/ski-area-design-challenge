import { describe, expect, it } from 'vitest';
import { normalizeDaymetDaily } from './providers.mjs';

describe('Daymet response normalization', () => {
  it('skips the live API metadata preamble and removes units from headings', () => {
    const csv = `Latitude: 44.1142  Longitude: -71.1167
X & Y on Lambert Conformal Conic: 2160917.68 548899.61
Tile: 12115
Elevation: 405 meters
All years; all variables; Daymet Software Version 4.0
How to cite: Thornton et al.
year,yday,dayl (s),prcp (mm/day),srad (W/m^2),swe (kg/m^2),tmax (deg c),tmin (deg c),vp (Pa)
2025,1,31541.06,8.12,90.72,85.60,2.65,-2.84,495.48
2025,2,31589.84,0.00,66.35,84.07,0.43,-2.35,513.79`;

    expect(normalizeDaymetDaily(csv, 2025)).toEqual([
      { date: '2025-01-01', tminC: -2.84, tmaxC: 2.65, precipitationMm: 8.12,
        vaporPressurePa: 495.48, snowWaterEquivalentMm: 85.6, shortwaveWm2: 90.72, daylightSeconds: 31541.06 },
      { date: '2025-01-02', tminC: -2.35, tmaxC: 0.43, precipitationMm: 0,
        vaporPressurePa: 513.79, snowWaterEquivalentMm: 84.07, shortwaveWm2: 66.35, daylightSeconds: 31589.84 },
    ]);
  });
});
