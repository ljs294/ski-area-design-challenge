import { describe, expect, it } from 'vitest';
import { TrailPaintEngine } from './trailPaintEngine';
import { haversineMeters } from '../geo';

const ORIGIN: [number, number] = [-121.5, 46.93];
const north = (meters: number): [number, number] => [ORIGIN[0], ORIGIN[1] + meters / 111_320];

describe('TrailPaintEngine', () => {
  it('unions overlapping strokes and derives a footprint centerline', () => {
    const engine = new TrailPaintEngine(ORIGIN, 30);
    engine.apply([north(0), north(500)], 'paint');
    const firstArea = engine.areaM2();
    engine.apply([north(250), north(750)], 'paint');
    expect(engine.areaM2()).toBeGreaterThan(firstArea);
    expect(engine.areaM2()).toBeLessThan(firstArea * 2);

    const result = engine.analyze();
    expect(result.parts).toHaveLength(1);
    const line = result.parts[0].centerline;
    expect(line.length).toBeGreaterThan(2);
    expect(haversineMeters(line[0], line[line.length - 1])).toBeGreaterThan(650);
  });

  it('keeps disconnected painted islands as separate parts', () => {
    const engine = new TrailPaintEngine(ORIGIN, 20);
    engine.apply([north(0), north(150)], 'paint');
    engine.apply([north(300), north(450)], 'paint');
    const result = engine.analyze();
    expect(result.parts).toHaveLength(2);
  });

  it('supports erase and exact stroke undo', () => {
    const engine = new TrailPaintEngine(ORIGIN, 30);
    engine.apply([north(0), north(500)], 'paint');
    const painted = engine.areaM2();
    engine.apply([north(225), north(275)], 'erase');
    expect(engine.areaM2()).toBeLessThan(painted);
    engine.undo();
    expect(engine.areaM2()).toBe(painted);
    engine.undo();
    expect(engine.areaM2()).toBe(0);
  });

  it('does not lose an eight-kilometer narrow trail to square-grid coarsening', () => {
    const engine = new TrailPaintEngine(ORIGIN, 8);
    const result = engine.apply([north(0), north(8000)], 'paint');
    expect(result.polygons).toHaveLength(1);
    const analysis = engine.analyze();
    expect(analysis.parts).toHaveLength(1);
    expect(haversineMeters(analysis.parts[0].centerline[0], analysis.parts[0].centerline.at(-1)!))
      .toBeGreaterThan(7900);
  }, 2000);

  it('returns rounded vector edges without changing exact raster area', () => {
    const engine = new TrailPaintEngine(ORIGIN, 30);
    const preview = engine.apply([north(0), north(300)], 'paint');
    const exactArea = engine.areaM2();
    const ring = preview.polygons[0][0];
    const diagonalEdges = ring.slice(1).filter((point, i) =>
      Math.abs(point[0] - ring[i][0]) > 1e-10 && Math.abs(point[1] - ring[i][1]) > 1e-10);
    expect(diagonalEdges.length).toBeGreaterThan(0);
    expect(engine.areaM2()).toBe(exactArea);
    expect(engine.analyze().areaM2).toBe(exactArea);
  });

  it('preserves intentional holes in a smoothed footprint', () => {
    const engine = new TrailPaintEngine(ORIGIN, 20);
    const east = (meters: number, northM: number): [number, number] => [
      ORIGIN[0] + meters / (111_320 * Math.cos(ORIGIN[1] * Math.PI / 180)),
      ORIGIN[1] + northM / 111_320,
    ];
    const result = engine.apply([
      east(0, 0), east(100, 0), east(100, 100), east(0, 100), east(0, 0),
    ], 'paint');
    expect(result.polygons).toHaveLength(1);
    expect(result.polygons[0]).toHaveLength(2);
  });
});
