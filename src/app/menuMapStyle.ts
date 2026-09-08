import { createMasterPlanStyle } from './masterPlanStyle';

/** Natural menu colors are independent of gameplay presets and UI theme. */
export function createMenuMapStyle() {
  const style = createMasterPlanStyle();
  for (const layer of style.layers) {
    if (layer.type === 'background') layer.paint = { 'background-color': '#aca99a' };
    if (layer.id === 'mp-context-landcover' && layer.type === 'fill') layer.paint = {
      'fill-opacity': 0.9,
      'fill-color': ['match', ['get', 'class'],
        'wood', '#527259', 'grass', '#91a579', 'farmland', '#a8ad80',
        'ice', '#e6eee9', 'rock', '#aaa69b', 'sand', '#b8b09a', '#8c987c'],
    };
    if (layer.id === 'mp-water' && layer.type === 'fill') layer.paint = { 'fill-color': '#528ca4', 'fill-opacity': 0.9 };
    if (layer.id === 'mp-waterways' && layer.type === 'line') layer.paint = { ...layer.paint, 'line-color': '#528ca4' };
  }
  return style;
}
