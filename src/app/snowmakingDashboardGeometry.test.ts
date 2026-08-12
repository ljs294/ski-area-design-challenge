import { describe, expect, it } from 'vitest';
import { samplePolyline, snowmakingSegmentAnnotationGeometry } from './snowmakingDashboardGeometry';

describe('snowmaking dashboard segment annotations', () => {
  it('places flow above, pressure below, and two arrows along the segment', () => {
    const geometry = snowmakingSegmentAnnotationGeometry([{ x: 0, y: 20 }, { x: 100, y: 20 }], 1);
    expect(geometry.flowLabel).toEqual({ x: 50, y: 7 });
    expect(geometry.pressureLabel).toEqual({ x: 50, y: 33 });
    expect(geometry.arrows.map((arrow) => [arrow.x, arrow.y])).toEqual([[34, 20], [66, 20]]);
    expect(geometry.arrows.every((arrow) => arrow.tangentX === 1 && arrow.tangentY === 0)).toBe(true);
  });

  it('reverses arrow tangents when signed flow reverses the display path', () => {
    const points = [{ x: 0, y: 0 }, { x: 100, y: 0 }];
    expect(samplePolyline(points, 0.5).tangentX).toBe(1);
    expect(samplePolyline([...points].reverse(), 0.5).tangentX).toBe(-1);
  });
});
