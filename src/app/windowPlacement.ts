export interface WindowRect { x: number; y: number; width: number; height: number }
export interface WindowViewport { width: number; height: number }
const GAP = 8;
export function clampWindow(rect: WindowRect, viewport: WindowViewport): WindowRect {
  const width = Math.min(rect.width, Math.max(120, viewport.width - GAP * 2));
  const height = Math.min(rect.height, Math.max(60, viewport.height - GAP * 2));
  return { width, height, x: Math.max(GAP, Math.min(rect.x, viewport.width - width - GAP)),
    y: Math.max(GAP, Math.min(rect.y, viewport.height - height - GAP)) };
}
/** Place a window below an anchor while keeping its right edge aligned to it. */
export function placeWindowBelowAnchor(size: Pick<WindowRect, 'width' | 'height'>,
  anchor: Pick<WindowRect, 'x' | 'y' | 'width' | 'height'>, viewport: WindowViewport, gap = 6): WindowRect {
  return clampWindow({ ...size, x: anchor.x + anchor.width - size.width, y: anchor.y + anchor.height + gap }, viewport);
}
/** Fill each row from the common origin; cascade only when no complete slot fits. */
export function placeWindow(size: Pick<WindowRect, 'width' | 'height'>,
  occupied: WindowRect[], viewport: WindowViewport): WindowRect {
  const fit = clampWindow({ ...size, x: GAP, y: GAP }, viewport);
  const rows = [GAP, ...occupied.map((rect) => rect.y + rect.height + GAP)].sort((a, b) => a - b);
  const columns = [GAP, ...occupied.map((rect) => rect.x + rect.width + GAP)].sort((a, b) => a - b);
  for (const y of rows) for (const x of columns) {
    if (x + fit.width + GAP > viewport.width || y + fit.height + GAP > viewport.height) continue;
    if (occupied.every((other) => x + fit.width + GAP <= other.x || other.x + other.width + GAP <= x
      || y + fit.height + GAP <= other.y || other.y + other.height + GAP <= y)) return { ...fit, x, y };
  }
  return clampWindow({ ...fit, x: GAP + occupied.length * 24, y: GAP + occupied.length * 24 }, viewport);
}
