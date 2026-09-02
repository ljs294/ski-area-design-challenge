import { SNOW_SURFACES } from '../snow';
import type { Units } from './SettingsContext';

export type SnowDisplayMode = 'depth' | 'conditions';

export const SNOW_DEPTH_BANDS = [
  { maxM: 0.15, label: '0–15 cm', rgb: [219, 239, 255] as const },
  { maxM: 0.30, label: '15–30 cm', rgb: [162, 214, 244] as const },
  { maxM: 0.60, label: '30–60 cm', rgb: [96, 165, 220] as const },
  { maxM: 1.20, label: '60–120 cm', rgb: [45, 111, 181] as const },
  { maxM: Infinity, label: '120+ cm', rgb: [18, 63, 125] as const },
] as const;

const SURFACE_RGB = [
  [239, 248, 255], [183, 218, 238], [101, 166, 196], [94, 112, 132],
  [164, 211, 235], [242, 194, 106], [153, 169, 184], [210, 181, 145],
  [114, 176, 116], [77, 145, 161], [125, 190, 224],
] as const;

export const SNOW_SURFACE_STYLES = SNOW_SURFACES.map((surface, index) => ({
  ...surface,
  rgb: SURFACE_RGB[index],
  color: `rgb(${SURFACE_RGB[index].join(', ')})`,
}));

export const SNOW_DEPTH_LEGEND = SNOW_DEPTH_BANDS.map((band) => ({
  label: band.label,
  color: `rgb(${band.rgb.join(', ')})`,
}));

export function snowDepthLegend(units: Units): Array<{ label: string; color: string }> {
  if (units === 'metric') return [...SNOW_DEPTH_LEGEND];
  return SNOW_DEPTH_BANDS.map((band, index) => {
    const minimum = index === 0 ? 0 : SNOW_DEPTH_BANDS[index - 1].maxM;
    const minimumIn = Math.round(minimum * 39.37007874);
    const label = Number.isFinite(band.maxM)
      ? `${minimumIn}\u2013${Math.round(band.maxM * 39.37007874)} in`
      : `${minimumIn}+ in`;
    return { label, color: `rgb(${band.rgb.join(', ')})` };
  });
}

export const SNOW_CONDITION_LEGEND = SNOW_SURFACE_STYLES.map((surface) => ({
  label: `${surface.code} · ${surface.name}`,
  color: surface.color,
}));

export function snowRgba(depthM: number, surface: number, mode: SnowDisplayMode):
  readonly [number, number, number, number] {
  if (depthM < 0.02 || surface === 0) return [0, 0, 0, 0];
  if (mode === 'conditions') {
    const rgb = SNOW_SURFACE_STYLES[surface - 1]?.rgb;
    return rgb ? [rgb[0], rgb[1], rgb[2], 205] : [0, 0, 0, 0];
  }
  const rgb = SNOW_DEPTH_BANDS.find((band) => depthM < band.maxM)?.rgb ??
    SNOW_DEPTH_BANDS[SNOW_DEPTH_BANDS.length - 1].rgb;
  return [rgb[0], rgb[1], rgb[2], 205];
}
