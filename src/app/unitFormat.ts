import type { Units } from './SettingsContext';

function decimal(value: number | undefined, digits: number): string {
  return value == null || !Number.isFinite(value) ? '--' : value.toFixed(digits);
}

export function formatTemperature(valueC: number | undefined, units: Units, digits = 1): string {
  const value = units === 'imperial' && valueC != null ? valueC * 9 / 5 + 32 : valueC;
  return `${decimal(value, digits)} \u00b0${units === 'imperial' ? 'F' : 'C'}`;
}

export function formatTemperatureDelta(valueC: number | undefined, units: Units, digits = 1): string {
  const value = units === 'imperial' && valueC != null ? valueC * 9 / 5 : valueC;
  return `${decimal(value, digits)} \u00b0${units === 'imperial' ? 'F' : 'C'}`;
}

export function formatWindSpeed(valueKph: number | undefined, units: Units, digits = 1): string {
  const value = units === 'imperial' && valueKph != null ? valueKph * 0.6213711922 : valueKph;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'mph' : 'km/h'}`;
}

export function formatVelocity(valueMps: number | undefined, units: Units, digits = 1): string {
  const value = units === 'imperial' && valueMps != null ? valueMps * 2.2369362921 : valueMps;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'mph' : 'm/s'}`;
}

export function formatLiquidPrecipitation(valueMm: number | undefined, units: Units, digits = 2): string {
  const value = units === 'imperial' && valueMm != null ? valueMm / 25.4 : valueMm;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'in' : 'mm'}`;
}

export function formatLiquidPrecipitationRate(valueMm: number | undefined, units: Units, digits = 2): string {
  return `${formatLiquidPrecipitation(valueMm, units, digits)}/h`;
}

export function formatSnowfall(valueCm: number | undefined, units: Units, digits = 1): string {
  const value = units === 'imperial' && valueCm != null ? valueCm / 2.54 : valueCm;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'in' : 'cm'}`;
}

export function formatElevation(valueM: number | undefined, units: Units): string {
  if (valueM == null || !Number.isFinite(valueM)) return '--';
  const value = units === 'imperial' ? valueM * 3.280839895 : valueM;
  return `${Math.round(value).toLocaleString()} ${units === 'imperial' ? 'ft' : 'm'}`;
}

export function formatSnowDepth(valueM: number | undefined, units: Units, digits = 0): string {
  const value = units === 'imperial' && valueM != null ? valueM * 39.37007874 : valueM == null ? valueM : valueM * 100;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'in' : 'cm'}`;
}

export function formatPressure(valuePsi: number | undefined, units: Units, digits = 1): string {
  const value = units === 'metric' && valuePsi != null ? valuePsi * 6.894757293 : valuePsi;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'PSI' : 'kPa'}`;
}

export function formatFlow(valueGpm: number | undefined, units: Units, digits = 1): string {
  const value = units === 'metric' && valueGpm != null ? valueGpm * 3.785411784 : valueGpm;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'GPM' : 'L/min'}`;
}

export function formatFeet(valueFt: number | undefined, units: Units, digits = 1): string {
  const value = units === 'metric' && valueFt != null ? valueFt * 0.3048 : valueFt;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'ft' : 'm'}`;
}

export function formatInches(valueIn: number | undefined, units: Units, digits = 1): string {
  const value = units === 'metric' && valueIn != null ? valueIn * 25.4 : valueIn;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'in' : 'mm'}`;
}

export function formatGallons(valueGallons: number | undefined, units: Units, digits = 0): string {
  const value = units === 'metric' && valueGallons != null ? valueGallons * 3.785411784 : valueGallons;
  return `${decimal(value, digits)} ${units === 'imperial' ? 'gal' : 'L'}`;
}
