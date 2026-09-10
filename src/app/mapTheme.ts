import type { Map as MapLibreMap, StyleSpecification } from 'maplibre-gl';

export type MapTheme = 'light' | 'dark';
export type MapColorPreset = 'cupertino' | 'mountain-view' | 'classic' | 'night-vision' | 'wireframe' | 'blueprint' | 'custom';

export interface MapPalette {
  paper: string;
  text: string;
  halo: string;
  contour: string;
  minorContour: string;
  water: string;
  waterLine: string;
  road: string;
  building: string;
  selection: string;
}

/** Five intentionally broad controls keep a custom map readable at a glance. */
export interface CustomMapColors {
  paper: string;
  water: string;
  road: string;
  contour: string;
  text: string;
}

export interface MapColorPresetDefinition {
  id: MapColorPreset;
  label: string;
  swatches: readonly string[];
}

export const DEFAULT_CUSTOM_MAP_COLORS: CustomMapColors = Object.freeze({
  paper: '#e8e5dc', water: '#76a9c4', road: '#625f59', contour: '#deded0', text: '#263c43',
});

/** The original adaptive palette, retained as the default Cupertino treatment. */
export const MAP_PALETTES = {
  light: { paper: '#e8e5dc', text: '#263c43', halo: '#f3f5f2', contour: '#faf7eb', minorContour: '#deded0',
    water: '#76a9c4', waterLine: '#397f9f', road: '#625f59', building: '#a7988c', selection: '#155ab6' },
  dark: { paper: '#16252c', text: '#eaf1f4', halo: '#172730', contour: '#c3d3d7', minorContour: '#819aa3',
    water: '#335d72', waterLine: '#86b9cf', road: '#a5b6b8', building: '#74868b', selection: '#80bef2' },
} as const satisfies Record<MapTheme, MapPalette>;

const PRESET_PALETTES: Record<Exclude<MapColorPreset, 'custom'>, Record<MapTheme, MapPalette>> = {
  cupertino: MAP_PALETTES,
  'mountain-view': {
    light: { paper: '#e4e8e2', text: '#243533', halo: '#f2f3ef', contour: '#f7f8f3', minorContour: '#bcc9bd',
      water: '#6095a6', waterLine: '#2f7085', road: '#7e735d', building: '#b8aa91', selection: '#bd5d34' },
    dark: { paper: '#1d2e31', text: '#e1efea', halo: '#1d2e31', contour: '#d0e0d6', minorContour: '#749587',
      water: '#255d71', waterLine: '#73b8d0', road: '#baa98a', building: '#6b665c', selection: '#e48f59' },
  },
  classic: {
    light: { paper: '#f3f0e6', text: '#292b2d', halo: '#faf8f2', contour: '#ded9ca', minorContour: '#aaa699',
      water: '#4f87a9', waterLine: '#2d6385', road: '#504e4c', building: '#9a948b', selection: '#b8403c' },
    dark: { paper: '#292927', text: '#eeeeea', halo: '#292927', contour: '#d8d3c3', minorContour: '#8d8980',
      water: '#315a78', waterLine: '#83b5d3', road: '#c2bdb2', building: '#77736d', selection: '#e26b63' },
  },
  'night-vision': {
    light: { paper: '#04070c', text: '#d1e1ff', halo: '#04070c', contour: '#3a3d77', minorContour: '#20224b',
      water: '#161c4d', waterLine: '#365da5', road: '#282b62', building: '#171937', selection: '#4e8cff' },
    dark: { paper: '#04070c', text: '#d1e1ff', halo: '#04070c', contour: '#3a3d77', minorContour: '#20224b',
      water: '#161c4d', waterLine: '#365da5', road: '#282b62', building: '#171937', selection: '#4e8cff' },
  },
  wireframe: {
    light: { paper: '#020902', text: '#c5ffbe', halo: '#020902', contour: '#1de920', minorContour: '#0c5c11',
      water: '#071f0b', waterLine: '#1bad18', road: '#2cff35', building: '#0b2a0c', selection: '#d6ff00' },
    dark: { paper: '#020902', text: '#c5ffbe', halo: '#020902', contour: '#1de920', minorContour: '#0c5c11',
      water: '#071f0b', waterLine: '#1bad18', road: '#2cff35', building: '#0b2a0c', selection: '#d6ff00' },
  },
  blueprint: {
    light: { paper: '#08141f', text: '#d9edff', halo: '#08141f', contour: '#3c78ad', minorContour: '#1e4168',
      water: '#163e66', waterLine: '#5e9ed1', road: '#4f91ca', building: '#183757', selection: '#8fc7ff' },
    dark: { paper: '#08141f', text: '#d9edff', halo: '#08141f', contour: '#3c78ad', minorContour: '#1e4168',
      water: '#163e66', waterLine: '#5e9ed1', road: '#4f91ca', building: '#183757', selection: '#8fc7ff' },
  },
};

export const MAP_COLOR_PRESETS: readonly MapColorPresetDefinition[] = Object.freeze([
  { id: 'cupertino', label: 'Cupertino', swatches: ['#24313c', '#617e9e', '#455c7d', '#2a4871', '#0d326c'] },
  { id: 'mountain-view', label: 'Mountain View', swatches: ['#25313d', '#3d5d7c', '#27446d', '#11516f', '#063b68'] },
  { id: 'classic', label: 'Classic', swatches: ['#050607', '#3c3e43', '#4e5156', '#4b5766', '#075278'] },
  { id: 'night-vision', label: 'Night Vision', swatches: ['#030508', '#302b55', '#282349', '#1a2042', '#075578'] },
  { id: 'wireframe', label: 'Wireframe', swatches: ['#020902', '#020d02', '#1de920', '#063206', '#002c00'] },
  { id: 'blueprint', label: 'Blueprint', swatches: ['#060b10', '#3e79ad', '#24517d', '#173a5e', '#0c2540'] },
  { id: 'custom', label: 'Custom', swatches: Object.values(DEFAULT_CUSTOM_MAP_COLORS) },
]);

const HEX_COLOR = /^#[0-9a-f]{6}$/i;

export function isMapColorPreset(value: unknown): value is MapColorPreset {
  return typeof value === 'string' && MAP_COLOR_PRESETS.some((preset) => preset.id === value);
}

/** Reject malformed persisted values before they reach a MapLibre style expression. */
export function normalizeCustomMapColors(value: unknown): CustomMapColors {
  const source = value && typeof value === 'object' ? value as Partial<CustomMapColors> : {};
  const color = (key: keyof CustomMapColors) =>
    typeof source[key] === 'string' && HEX_COLOR.test(source[key]) ? source[key]! : DEFAULT_CUSTOM_MAP_COLORS[key];
  return { paper: color('paper'), water: color('water'), road: color('road'), contour: color('contour'), text: color('text') };
}

function customPalette(colors: CustomMapColors): MapPalette {
  const custom = normalizeCustomMapColors(colors);
  return {
    paper: custom.paper, text: custom.text, halo: custom.paper, contour: custom.contour,
    minorContour: custom.contour, water: custom.water, waterLine: custom.water, road: custom.road,
    building: custom.road, selection: custom.text,
  };
}

export function mapPaletteFor(
  theme: MapTheme,
  preset: MapColorPreset = 'cupertino',
  customColors: CustomMapColors = DEFAULT_CUSTOM_MAP_COLORS,
): MapPalette {
  return preset === 'custom' ? customPalette(customColors) : PRESET_PALETTES[preset][theme];
}

/** Only presentation paint: no geometry, layer order, visibility, or raster changes. */
export function themePaint(
  id: string,
  theme: MapTheme,
  preset: MapColorPreset = 'cupertino',
  customColors: CustomMapColors = DEFAULT_CUSTOM_MAP_COLORS,
): Record<string, unknown> | null {
  const p = mapPaletteFor(theme, preset, customColors);
  if (id === 'dashboard-backdrop') return { 'fill-color': p.paper };
  if (['dashboard-grid', 'dashboard-snow-contours', 'dashboard-trail-ties', 'dashboard-snow-building-outlines'].includes(id)) return { 'line-color': p.text };
  if (id === 'dashboard-snow-water') return { 'fill-color': p.water, 'fill-outline-color': p.waterLine };
  if (id === 'dashboard-snow-buildings') return { 'fill-color': p.building };
  if (id === 'lift-labels') return { 'text-color': '#d42027', 'text-halo-color': '#ffffff' };
  if (id === 'dashboard-trail-nodes') return { 'circle-color': ['case', ['get', 'user'], '#efb84f', ['get', 'terminal'], p.text, p.paper], 'circle-stroke-color': p.text };
  if (id === 'dashboard-guest-label') return { 'text-halo-color': p.halo };
  if (['local-water-selected', 'local-water-line-selected'].includes(id)) return { 'line-color': p.selection };
  if (['dashboard-trail-labels', 'dashboard-snow-building-labels', 'dashboard-snow-flow-arrows', 'dashboard-snow-flow-labels'].includes(id)) return { 'text-color': p.text, 'text-halo-color': p.halo };
  if (id === 'mp-paper') return { 'background-color': p.paper };
  if (id === 'mp-water' || id === 'local-water-fill') return { 'fill-color': p.water };
  if (id === 'mp-waterways' || id === 'local-water-lines') return { 'line-color': p.waterLine };
  if (id === 'mp-roads') return { 'line-color': p.road };
  if (id === 'mp-buildings') return { 'fill-color': p.building };
  if (id === 'contour-lines') return { 'line-color': ['match', ['coalesce', ['get', 'level'], 0], 1, p.contour, p.minorContour] };
  if (['mp-place-labels', 'contour-labels', 'local-water-labels', 'trail-labels'].includes(id)) {
    return { 'text-color': p.text, 'text-halo-color': p.halo };
  }
  return null;
}

export function themedBasemap(
  style: StyleSpecification,
  theme: MapTheme,
  preset: MapColorPreset = 'cupertino',
  customColors: CustomMapColors = DEFAULT_CUSTOM_MAP_COLORS,
): StyleSpecification {
  return { ...style, layers: style.layers.map((layer) => {
    const paint = themePaint(layer.id, theme, preset, customColors);
    return paint ? { ...layer, paint: { ...layer.paint, ...paint } } as typeof layer : layer;
  }) };
}

export function applyMapTheme(
  map: MapLibreMap,
  theme: MapTheme,
  preset: MapColorPreset = 'cupertino',
  customColors: CustomMapColors = DEFAULT_CUSTOM_MAP_COLORS,
): void {
  for (const layer of map.getStyle()?.layers ?? []) {
    const paint = themePaint(layer.id, theme, preset, customColors);
    if (!paint) continue;
    for (const [property, value] of Object.entries(paint)) {
      if (JSON.stringify(map.getPaintProperty(layer.id, property)) !== JSON.stringify(value)) {
        map.setPaintProperty(layer.id, property, value);
      }
    }
  }
}
