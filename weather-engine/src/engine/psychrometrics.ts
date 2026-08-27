import type { PrecipitationPhase } from '../contracts.ts';

export function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

export function quantize(value: number, decimals: number): number {
  const scale = 10 ** decimals;
  return Math.round(value * scale) / scale;
}

export function saturationVaporPressureHpa(temperatureC: number): number {
  return 6.112 * Math.exp((17.67 * temperatureC) / (temperatureC + 243.5));
}

export function relativeHumidityPct(temperatureC: number, dewPointC: number): number {
  return clamp(100 * saturationVaporPressureHpa(dewPointC) / saturationVaporPressureHpa(temperatureC), 0, 100);
}

export function wetBulbTemperatureC(temperatureC: number, relativeHumidity: number, pressureHpa: number): number {
  const humidity = clamp(relativeHumidity, 1, 100);
  const stull = temperatureC * Math.atan(0.151977 * Math.sqrt(humidity + 8.313659)) +
    Math.atan(temperatureC + humidity) - Math.atan(humidity - 1.676331) +
    0.00391838 * humidity ** 1.5 * Math.atan(0.023101 * humidity) - 4.686035;
  return stull + clamp((1013.25 - pressureHpa) * 0.00012 * (temperatureC - stull), -0.6, 0.6);
}

export function precipitationPhase(temperatureC: number, wetBulbC: number, precipitationMm: number): PrecipitationPhase {
  if (precipitationMm < 0.005) return 'none';
  if (temperatureC < 0 && wetBulbC > -0.8 && wetBulbC <= 0.5) return 'freezing-rain';
  if (wetBulbC <= -0.8) return 'snow';
  if (wetBulbC < 1.2) return 'mixed';
  return 'rain';
}

export function pressureAtElevation(referencePressureHpa: number, referenceElevationM: number, elevationM: number): number {
  return referencePressureHpa * Math.exp(-(elevationM - referenceElevationM) / 8_400);
}

export function angularDifference(left: number, right: number): number {
  return ((right - left + 540) % 360) - 180;
}
