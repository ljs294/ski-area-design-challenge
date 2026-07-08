import type { ClimateMonth, ClimateProfile } from './types';

/**
 * Generate a procedural climate profile based on latitude and altitude.
 */
export function generateProceduralClimate(latitude: number, avgAltitudeMeters: number): ClimateProfile {
  const monthly: ClimateMonth[] = [];
  const isNorthernHemisphere = latitude >= 0;
  const absLat = Math.abs(latitude);

  // Base temperature based on latitude (polar is colder, equator is hotter)
  // Latitude 0: 80°F, Latitude 45: 50°F, Latitude 90: -10°F
  const latBaseTemp = 85 - absLat * 1.0;

  // Elevation temperature lapse rate: roughly -6.5°C per 1000m (~11.5°F per 1000m)
  const altitudeCooling = (avgAltitudeMeters / 1000) * 11.5;
  const localBaseTemp = latBaseTemp - altitudeCooling;

  for (let month = 0; month < 12; month++) {
    // Northern hemisphere coldest in Jan (month 0), warmest in Jul (month 6)
    let seasonalFactor = Math.sin(((month - 3) / 12) * Math.PI * 2);
    if (!isNorthernHemisphere) {
      seasonalFactor = -seasonalFactor;
    }

    const avgTemp = localBaseTemp + seasonalFactor * 25; // +/- 25 degrees seasonal swing
    const tempHigh = Math.round(avgTemp + 8);
    const tempLow = Math.round(avgTemp - 8);

    // Snow probability: high if low temperature is below freezing (32°F)
    let snowProbability = 0;
    if (tempLow < 32) {
      const coldFactor = (32 - tempLow) / 30; // 0 at 32°F, 1.0 at 2°F
      snowProbability = Math.min(0.9, 0.1 + coldFactor * 0.7);
    }

    // Winter months have higher average wind speeds
    const avgWindSpeed = 12 + Math.abs(seasonalFactor) * 8; // 12km/h to 20km/h

    monthly.push({
      tempHigh,
      tempLow,
      snowProbability,
      avgWindSpeed: Math.round(avgWindSpeed),
    });
  }

  return { monthly };
}
