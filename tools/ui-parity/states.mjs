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
  // Task P3-06: a tool armed, its window at step 1.
  { name: 'tool-lift', mockup: 'liftorigin' },
  { name: 'tool-trail', mockup: 'trailpaint' },
  { name: 'tool-lift-review', mockup: 'liftreview' },
  { name: 'tool-trail-review', mockup: 'trailreview' },
  // Task P3-08: Edit trail and Remove trail on a built trail (TF-07), and Analysis › Trails; p3 draws the mockup without
  // its Phase 2 switch, as these are live from task 08.
  { name: 'tool-trail-edit', mockup: 'trailfix', p3: true },
  { name: 'tool-trail-remove', mockup: 'trailgone', p3: true },
  { name: 'analysis-trails', mockup: 'atrails', p3: true },
  // Task P3-15: the Terrain tab's four brushes mid-stroke, and Break ground's review.
  { name: 'tray-terrain', mockup: 'terrainraise', p3: true },
  { name: 'tool-terrain-flatten', mockup: 'terrainflatten', p3: true },
  { name: 'tool-terrain-smooth', mockup: 'terrainsmooth', p3: true },
  { name: 'tool-terrain-slope', mockup: 'terrainslope', p3: true },
  { name: 'tool-terrain-review', mockup: 'terrainreview', p3: true },
  { name: 'analysis', mockup: 'aoverview' },
  { name: 'analysis-lifts', mockup: 'alifts' },
  { name: 'analysis-weather', mockup: 'aweather' },
  { name: 'analysis-finances', mockup: 'afinances' },
  { name: 'rstats', mockup: 'rstats' },
  // Task P2-06: a background download in the bar, running, waiting and stopped.
  { name: 'dl', mockup: 'dl' },
  { name: 'dlwait', mockup: 'dlwait' },
  { name: 'dlfail', mockup: 'dlfail' },
];

// Parts whose words are live in the game (the resort's name, the time, the elevation) or placeholders in the mockup:
// their box, colours and type are compared, not their text.
export const NO_TEXT = new Set([
  'bar-resort-name', 'bar-day', 'bar-date', 'bar-clock', 'bar-elev-value', 'bar-readout', 'menu-head', 'rstats-head',
  'analysis-head', 'legend-head', 'rstats-foot',
]);

export const TOLERANCE = { px: 2, colour: 6, fontSize: 0.5 };
