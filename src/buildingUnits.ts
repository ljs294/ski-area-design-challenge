/** Canonical conversion constants. All persisted building dimensions are metres. */
export const METERS_PER_FOOT = 0.3048;
export const METERS_PER_INCH = METERS_PER_FOOT / 12;
export const FEET_PER_METER = 1 / METERS_PER_FOOT;

export function feetToMeters(feet: number): number {
  return feet * METERS_PER_FOOT;
}

export function inchesToMeters(inches: number): number {
  return inches * METERS_PER_INCH;
}

export function metersToFeet(meters: number): number {
  return meters * FEET_PER_METER;
}

export function metersToInches(meters: number): number {
  return meters / METERS_PER_INCH;
}

/** The rise of a gable roof measured from the eave to the ridge. */
export function gableRoofRiseM(widthM: number, pitchRise = 4, pitchRun = 12): number {
  return (widthM / 2) * (pitchRise / pitchRun);
}

export function gableRidgeHeightM(
  widthM: number,
  eaveHeightM: number,
  pitchRise = 4,
  pitchRun = 12,
): number {
  return eaveHeightM + gableRoofRiseM(widthM, pitchRise, pitchRun);
}

export const feetToM = feetToMeters;
export const inchesToM = inchesToMeters;
export const mToFeet = metersToFeet;
export const mToInches = metersToInches;
export const roofRiseM = gableRoofRiseM;
export const ridgeHeightM = gableRidgeHeightM;

export function normalizeBearingDeg(bearingDeg: number): number {
  if (!Number.isFinite(bearingDeg)) return 0;
  const normalized = bearingDeg % 360;
  return normalized < 0 ? normalized + 360 : normalized;
}

export const PUMP_HOUSE_DEFAULTS = Object.freeze({
  lengthFt: 60,
  widthFt: 40,
  eaveHeightFt: 16,
  roofPitchRise: 4,
  roofPitchRun: 12,
  roofRiseFt: 6 + 8 / 12,
  ridgeHeightFt: 22 + 8 / 12,
  lengthM: feetToMeters(60),
  widthM: feetToMeters(40),
  eaveHeightM: feetToMeters(16),
  roofRiseM: feetToMeters(6 + 8 / 12),
  ridgeHeightM: feetToMeters(22 + 8 / 12),
});

/** Display a canonical metric length in the current unit system. */
export function formatBuildingLength(meters: number, units: 'imperial' | 'metric'): string {
  if (units === 'imperial') return `${Math.round(metersToFeet(meters)).toLocaleString()} ft`;
  return `${Math.round(meters).toLocaleString()} m`;
}

/** Display a canonical metric height in the current unit system. */
export function formatBuildingHeight(meters: number, units: 'imperial' | 'metric'): string {
  if (units === 'imperial') {
    const totalInches = Math.round(metersToInches(meters));
    const feet = Math.floor(totalInches / 12);
    const inches = totalInches % 12;
    return inches ? `${feet} ft ${inches} in` : `${feet} ft`;
  }
  return `${meters.toFixed(2)} m`;
}
