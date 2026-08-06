/**
 * Pure keybind data + helpers for the in-game camera controls (WASD pan, QE
 * rotate, N/U/1/2 discrete actions). No DOM, no fetch, no `src/app/**`
 * imports — see MEMORY's house convention that pure logic lives here,
 * sibling to skiNodes.ts/lifts.ts, while the React wiring (SettingsContext,
 * MapView's keydown listeners, the Settings rebind UI) lives under src/app.
 *
 * 'Escape' is deliberately never baked in here as a reserved/unassignable
 * key — that is app policy, enforced at the Settings UI input boundary, not
 * something this generic data module should know about.
 */

export type GameAction =
  | 'panForward'
  | 'panBackward'
  | 'panLeft'
  | 'panRight'
  | 'rotateLeft'
  | 'rotateRight'
  | 'tiltUp'
  | 'tiltDown'
  | 'snapNorth'
  | 'toggleView3D'
  | 'openTrailsDashboard'
  | 'openSnowmakingDashboard';

export type Keybinds = Record<GameAction, string>;

/** Display/iteration order: pan, then rotate, then tilt, then the discrete singles. */
export const GAME_ACTION_ORDER: GameAction[] = [
  'panForward',
  'panBackward',
  'panLeft',
  'panRight',
  'rotateLeft',
  'rotateRight',
  'tiltUp',
  'tiltDown',
  'snapNorth',
  'toggleView3D',
  'openTrailsDashboard',
  'openSnowmakingDashboard',
];

export const GAME_ACTION_LABELS: Record<GameAction, string> = {
  panForward: 'Pan forward',
  panBackward: 'Pan backward',
  panLeft: 'Pan left',
  panRight: 'Pan right',
  rotateLeft: 'Rotate left',
  rotateRight: 'Rotate right',
  tiltUp: 'Tilt up',
  tiltDown: 'Tilt down',
  snapNorth: 'Snap to north',
  toggleView3D: 'Toggle 2D / 3D',
  openTrailsDashboard: 'Trails & lifts dashboard',
  openSnowmakingDashboard: 'Snowmaking dashboard',
};

export const DEFAULT_KEYBINDS: Keybinds = {
  panForward: 'w',
  panBackward: 's',
  panLeft: 'a',
  panRight: 'd',
  rotateLeft: 'q',
  rotateRight: 'e',
  tiltUp: 'r',
  tiltDown: 'f',
  snapNorth: 'n',
  toggleView3D: 'u',
  openTrailsDashboard: '1',
  openSnowmakingDashboard: '2',
};

/**
 * Normalize a `KeyboardEvent.key` for comparison. Single characters are
 * lowercased so a held Shift doesn't turn 'w' into a non-match against 'W';
 * multi-character key names (e.g. 'Escape', 'ArrowUp') pass through as-is.
 */
export function normalizeKey(key: string): string {
  return key.length === 1 ? key.toLowerCase() : key;
}

/**
 * Fill in a fully-populated Keybinds object from whatever was in storage.
 * Starts from the defaults, then overwrites only the actions that are
 * actually known (`GAME_ACTION_ORDER`) and present as a non-empty string in
 * `stored` — this is what keeps a persisted settings blob forward-compatible
 * as actions are added, and safe against garbage/partial data.
 */
export function mergeKeybinds(stored: Partial<Record<string, string>> | null | undefined): Keybinds {
  const merged: Keybinds = { ...DEFAULT_KEYBINDS };
  if (!stored) return merged;
  for (const action of GAME_ACTION_ORDER) {
    const value = stored[action];
    if (typeof value === 'string' && value.length > 0) {
      merged[action] = normalizeKey(value);
    }
  }
  return merged;
}

/**
 * Which action (if any) currently owns `key`, for conflict detection while
 * the user is rebinding one action in the Settings UI.
 */
export function actionForKey(keybinds: Keybinds, key: string): GameAction | null {
  const normalized = normalizeKey(key);
  for (const action of GAME_ACTION_ORDER) {
    if (keybinds[action] === normalized) return action;
  }
  return null;
}

/**
 * True while `el` is a text-entry target (input/textarea/contenteditable),
 * so global keyboard shortcuts don't fire while the user is typing a name.
 * Factored out of the old "N toggles the node map" MapView handler so every
 * global keyboard handler shares one guard.
 */
export function isTypingTarget(el: Element | null): boolean {
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) return true;
  if (el instanceof HTMLElement && el.isContentEditable) return true;
  return false;
}
