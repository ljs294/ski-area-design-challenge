import type { XY } from '../network';

export interface SnowmakingPathSample extends XY {
  tangentX: number;
  tangentY: number;
}

export interface SnowmakingSegmentAnnotationGeometry {
  flowLabel: XY;
  pressureLabel: XY;
  labelAngleDeg: number;
  arrows: [SnowmakingPathSample, SnowmakingPathSample];
}

export function samplePolyline(points: readonly XY[], fraction: number): SnowmakingPathSample {
  if (points.length < 2) return { ...(points[0] ?? { x: 0, y: 0 }), tangentX: 1, tangentY: 0 };
  const lengths: number[] = [];
  let total = 0;
  for (let index = 1; index < points.length; index += 1) {
    const length = Math.hypot(points[index].x - points[index - 1].x,
      points[index].y - points[index - 1].y);
    lengths.push(length); total += length;
  }
  let target = Math.max(0, Math.min(1, fraction)) * total;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index];
    if (target > length && index < lengths.length - 1) { target -= length; continue; }
    const before = points[index], after = points[index + 1];
    const ratio = length > 0 ? target / length : 0;
    return { x: before.x + (after.x - before.x) * ratio,
      y: before.y + (after.y - before.y) * ratio,
      tangentX: length > 0 ? (after.x - before.x) / length : 1,
      tangentY: length > 0 ? (after.y - before.y) / length : 0 };
  }
  const before = points.at(-2)!, after = points.at(-1)!;
  const length = Math.hypot(after.x - before.x, after.y - before.y) || 1;
  return { ...after, tangentX: (after.x - before.x) / length,
    tangentY: (after.y - before.y) / length };
}

export function snowmakingSegmentAnnotationGeometry(points: readonly XY[],
  screenUnit: number): SnowmakingSegmentAnnotationGeometry {
  const midpoint = samplePolyline(points, 0.5);
  let normalX = -midpoint.tangentY, normalY = midpoint.tangentX;
  if (normalY > 0 || (Math.abs(normalY) < 1e-9 && normalX < 0)) {
    normalX *= -1; normalY *= -1;
  }
  const offset = 13 * screenUnit;
  let labelAngleDeg = Math.atan2(midpoint.tangentY, midpoint.tangentX) * 180 / Math.PI;
  if (labelAngleDeg > 90) labelAngleDeg -= 180;
  if (labelAngleDeg < -90) labelAngleDeg += 180;
  return {
    flowLabel: { x: midpoint.x + normalX * offset, y: midpoint.y + normalY * offset },
    pressureLabel: { x: midpoint.x - normalX * offset, y: midpoint.y - normalY * offset },
    labelAngleDeg,
    arrows: [samplePolyline(points, 0.34), samplePolyline(points, 0.66)],
  };
}
