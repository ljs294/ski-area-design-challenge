// Generic world-space label drawing, shared by every labeled overlay
// (contour elevations, roads, hydrography, peaks). Labels carry a
// world-space anchor + rotation, but are always drawn in an
// identity-transformed screen-space pass so text stays a fixed, legible
// size regardless of camera zoom, with world->screen projected by hand
// from the camera — same approach GameRenderer already used for contour
// labels, pulled out so other layers don't reimplement it.
import type { Camera } from './renderer';

export interface WorldLabel {
  x: number;
  y: number;
  /** radians; callers should keep this within [-90deg, 90deg] so text reads left-to-right. */
  angle: number;
  text: string;
}

export interface LabelStyle {
  font: string;
  color: string;
  haloColor: string;
  haloWidth: number;
}

/** Minimum distance (world units) enforced between placed labels so a long
 * line feature doesn't get a label at every segment. */
export function thinLabelsBySpacing<T extends WorldLabel>(candidates: T[], minSpacing: number): T[] {
  const kept: T[] = [];
  for (const candidate of candidates) {
    const tooClose = kept.some((k) => Math.hypot(k.x - candidate.x, k.y - candidate.y) < minSpacing);
    if (!tooClose) kept.push(candidate);
  }
  return kept;
}

export function drawWorldLabels(
  ctx: CanvasRenderingContext2D,
  labels: WorldLabel[],
  camera: Camera,
  canvasWidth: number,
  canvasHeight: number,
  weight: number,
  style: LabelStyle
): void {
  if (labels.length === 0) return;

  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalAlpha = weight;
  ctx.font = style.font;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.lineWidth = style.haloWidth;
  ctx.strokeStyle = style.haloColor;
  ctx.fillStyle = style.color;

  for (const label of labels) {
    const sx = camera.x + label.x * camera.zoom;
    const sy = camera.y + label.y * camera.zoom;
    if (sx < -20 || sx > canvasWidth + 20 || sy < -20 || sy > canvasHeight + 20) continue;

    ctx.save();
    ctx.translate(sx, sy);
    ctx.rotate(label.angle);
    ctx.strokeText(label.text, 0, 0);
    ctx.fillText(label.text, 0, 0);
    ctx.restore();
  }

  ctx.restore();
}
