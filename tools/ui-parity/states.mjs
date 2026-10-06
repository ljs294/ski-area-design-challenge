// The HUD states the parity check compares (task P2-02). Each is the mockup's `#solo&demo=p2,<mockup>` and the game's
// -uicapture shot `s6-<name>`; AppFlow.UiCapture.cs sets the game up the same way for each name. Add a state to both.
export const STATES = [
  { name: 'hud', mockup: '' },
  { name: 'float', mockup: 'float' },
  { name: 'menu', mockup: 'menu' },
  { name: 'layers', mockup: 'layers' },
  { name: 'slope', mockup: 'slope' },
  { name: 'exposure', mockup: 'aspect' },
  { name: 'depth', mockup: 'depth' },
  { name: 'contours', mockup: 'contours' },
  { name: 'slope-contours', mockup: 'layers,slope,contours' },
  { name: 'tray-lifts', mockup: 'tray' },
  { name: 'tray-trails', mockup: 'ttrails' },
  { name: 'tray-snow', mockup: 'snow' },
  { name: 'tray-infra', mockup: 'infra' },
  { name: 'analysis', mockup: 'aoverview' },
  { name: 'analysis-lifts', mockup: 'alifts' },
  { name: 'analysis-weather', mockup: 'aweather' },
  { name: 'analysis-finances', mockup: 'afinances' },
  { name: 'rstats', mockup: 'rstats' },
];

// Parts whose words are live in the game (the resort's name, the time, the elevation) or placeholders in the mockup:
// their box, colours and type are compared, not their text.
export const NO_TEXT = new Set([
  'bar-resort-name', 'bar-day', 'bar-date', 'bar-clock', 'bar-elev-value', 'bar-readout', 'menu-head', 'rstats-head',
  'analysis-head', 'legend-head', 'rstats-foot',
]);

export const TOLERANCE = { px: 2, colour: 6, fontSize: 0.5 };
