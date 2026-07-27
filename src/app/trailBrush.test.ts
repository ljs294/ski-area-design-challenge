import { describe, it, expect } from 'vitest';
import { strokeToPolygon, resampleSpine } from './trailBrush';
import { haversineMeters } from '../geo';

const LAT = 46.93;

function bboxMeters(rings: [number, number][][]) {
  let minLng = Infinity;
  let maxLng = -Infinity;
  let minLat = Infinity;
  let maxLat = -Infinity;
  for (const [lng, lat] of rings[0]) {
    if (lng < minLng) minLng = lng;
    if (lng > maxLng) maxLng = lng;
    if (lat < minLat) minLat = lat;
    if (lat > maxLat) maxLat = lat;
  }
  return {
    widthM: haversineMeters([minLng, LAT], [maxLng, LAT]),
    heightM: haversineMeters([minLng, minLat], [minLng, maxLat]),
  };
}

describe('strokeToPolygon', () => {
  it('buffers a straight stroke into a capsule ~stroke-long and ~brush-wide', () => {
    // An east–west stroke; brush 40 m wide.
    const a: [number, number] = [-121.5, LAT];
    const b: [number, number] = [-121.492, LAT];
    const strokeLen = haversineMeters(a, b);
    const brushWidthM = 40;

    const rings = strokeToPolygon([a, b], brushWidthM);
    expect(rings.length).toBeGreaterThanOrEqual(1);

    const outer = rings[0];
    expect(outer.length).toBeGreaterThanOrEqual(4);
    expect(outer[0]).toEqual(outer[outer.length - 1]); // closed

    const { widthM, heightM } = bboxMeters(rings);
    // Rounded caps add ~one brush radius at each end.
    expect(widthM).toBeGreaterThan(strokeLen);
    expect(widthM).toBeLessThan(strokeLen + brushWidthM + 30);
    expect(heightM).toBeGreaterThan(brushWidthM - 15);
    expect(heightM).toBeLessThan(brushWidthM + 30);
  });

  it('returns nothing for an empty path', () => {
    expect(strokeToPolygon([], 30)).toEqual([]);
  });
});

describe('resampleSpine', () => {
  it('produces evenly spaced stations keeping both endpoints', () => {
    const path: [number, number][] = [
      [-121.5, 46.935],
      [-121.5, 46.93],
      [-121.5, 46.925],
    ];
    const spine = resampleSpine(path);
    expect(spine.length).toBeGreaterThanOrEqual(2);
    expect(spine.length).toBeLessThanOrEqual(80);
    expect(spine[0]).toEqual(path[0]);
    expect(spine[spine.length - 1]).toEqual(path[path.length - 1]);

    // Adjacent stations are near-uniform in spacing.
    const gaps: number[] = [];
    for (let i = 1; i < spine.length; i++) gaps.push(haversineMeters(spine[i - 1], spine[i]));
    const mean = gaps.reduce((s, g) => s + g, 0) / gaps.length;
    for (const g of gaps) expect(Math.abs(g - mean)).toBeLessThan(mean * 0.5 + 1);
  });

  it('passes a degenerate path through', () => {
    expect(resampleSpine([[-121.5, 46.93]])).toEqual([[-121.5, 46.93]]);
  });
});
