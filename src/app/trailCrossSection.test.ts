import { describe, expect, it } from 'vitest';
import { MAX_FACE_SLOPE, MIN_BENCH_WIDTH_M, sectionSurfaceAt, solveCrossSection,
  type SectionPolicy } from './trailCrossSection';

/** Natural ground as a straight sidehill through the station, `slope` rise per
 * metre toward positive offsets. */
const sidehill = (slope: number, centerElevM = 1000) =>
  (offsetM: number) => centerElevM + slope * offsetM;

const budget = (halfWidthM: number) =>
  ({ leftHalfWidthM: halfWidthM, rightHalfWidthM: halfWidthM });

const policy: SectionPolicy = { minBenchWidthM: MIN_BENCH_WIDTH_M, stepM: 0.5 };

describe('solveCrossSection', () => {
  it('benches the full painted width on flat ground, with no faces to build', () => {
    const section = solveCrossSection(1000, sidehill(0), budget(20), policy);
    expect(section.gradeable).toBe(true);
    expect(section.benchLeftM).toBeCloseTo(20, 1);
    expect(section.benchRightM).toBeCloseTo(20, 1);
    expect(section.cutDepthM).toBeCloseTo(0, 3);
    expect(section.fillHeightM).toBeCloseTo(0, 3);
  });

  // The identity the whole design rests on: with faces at slope s, a level bench
  // inside budget B on ground of slope m comes out B*(1 - m/s) wide per side.
  it.each([
    [0.00, 20],
    [0.25, 15],
    [0.50, 10],
    [0.75, 5],
  ])('gives bench = budget*(1 - m/s) on a %s sidehill', (slope, expected) => {
    const section = solveCrossSection(1000, sidehill(slope), budget(20), policy);
    expect(section.benchLeftM).toBeCloseTo(expected, 0);
    expect(section.benchRightM).toBeCloseTo(expected, 0);
  });

  it('refuses ground at or beyond the face slope — the 45° rule', () => {
    const hopeless = solveCrossSection(1000, sidehill(1.20), budget(20), policy);
    expect(hopeless.gradeable).toBe(false);
    expect(hopeless.benchLeftM + hopeless.benchRightM).toBeLessThan(MIN_BENCH_WIDTH_M);
    expect(solveCrossSection(1000, sidehill(0.99), budget(20), policy).gradeable).toBe(false);
  });

  it('is gradeable exactly while the bench clears the minimum width', () => {
    // 8 m minimum over a 40 m budget needs 4 m per side, i.e. m <= 0.8.
    expect(solveCrossSection(1000, sidehill(0.75), budget(20), policy).gradeable).toBe(true);
    expect(solveCrossSection(1000, sidehill(0.85), budget(20), policy).gradeable).toBe(false);
  });

  it('lets a wider paint bench ground a narrow one cannot', () => {
    const steep = sidehill(0.75);
    expect(solveCrossSection(1000, steep, budget(8), policy).gradeable).toBe(false);
    expect(solveCrossSection(1000, steep, budget(24), policy).gradeable).toBe(true);
  });

  it('never benches wider than maxBenchWidthM, but still daylights', () => {
    const section = solveCrossSection(1000, sidehill(0.2), budget(20),
      { ...policy, maxBenchWidthM: 7, minBenchWidthM: 7 });
    expect(section.benchLeftM + section.benchRightM).toBeCloseTo(7, 3);
    expect(section.gradeable).toBe(true);
    expect(section.daylightLeftM).toBeGreaterThanOrEqual(section.benchLeftM);
    expect(section.daylightRightM).toBeGreaterThanOrEqual(section.benchRightM);
  });

  it('honours an asymmetric budget side by side', () => {
    const section = solveCrossSection(1000, sidehill(0.5),
      { leftHalfWidthM: 30, rightHalfWidthM: 10 }, policy);
    expect(section.benchLeftM).toBeCloseTo(15, 0);
    expect(section.benchRightM).toBeCloseTo(5, 0);
  });

  it('reports the deepest cut and highest fill across the disturbed width', () => {
    const section = solveCrossSection(1000, sidehill(0.5), budget(20), policy);
    // Uphill (+) side is cut, downhill (-) side is fill, both by slope*daylight.
    expect(section.cutDepthM).toBeCloseTo(0.5 * section.daylightRightM, 1);
    expect(section.fillHeightM).toBeCloseTo(0.5 * section.daylightLeftM, 1);
  });

  it('is deterministic', () => {
    const once = solveCrossSection(1000, sidehill(0.4), budget(18), policy);
    const twice = solveCrossSection(1000, sidehill(0.4), budget(18), policy);
    expect(twice).toEqual(once);
  });
});

describe('sectionSurfaceAt', () => {
  const ground = sidehill(0.5);
  const section = solveCrossSection(1000, ground, budget(20), policy);

  it('holds the bench dead level, which is what squares the contours', () => {
    for (let offset = -section.benchLeftM; offset <= section.benchRightM; offset += 0.5) {
      expect(sectionSurfaceAt(section, 1000, offset, ground(offset), policy))
        .toBeCloseTo(1000, 9);
    }
  });

  it('returns null past daylight, where natural ground stands', () => {
    const beyond = section.daylightRightM + 1;
    expect(sectionSurfaceAt(section, 1000, beyond, ground(beyond), policy)).toBeNull();
  });

  it('never builds a face steeper than the cap', () => {
    const step = 0.25;
    let steepest = 0;
    let previous = sectionSurfaceAt(section, 1000, -section.daylightLeftM,
      ground(-section.daylightLeftM), policy);
    for (let offset = -section.daylightLeftM + step;
      offset <= section.daylightRightM; offset += step) {
      const surface = sectionSurfaceAt(section, 1000, offset, ground(offset), policy);
      if (surface !== null && previous !== null)
        steepest = Math.max(steepest, Math.abs(surface - previous) / step);
      previous = surface;
    }
    expect(steepest).toBeLessThanOrEqual(MAX_FACE_SLOPE + 1e-6);
  });

  it('clamps a cut face to ground rather than filling past it', () => {
    const offset = section.benchRightM + 0.5;
    const surface = sectionSurfaceAt(section, 1000, offset, ground(offset), policy)!;
    expect(surface).toBeLessThanOrEqual(ground(offset) + 1e-9);
    expect(surface).toBeGreaterThanOrEqual(1000 - 1e-9);
  });

  it('clamps a fill face to ground rather than cutting past it', () => {
    const offset = -section.benchLeftM - 0.5;
    const surface = sectionSurfaceAt(section, 1000, offset, ground(offset), policy)!;
    expect(surface).toBeGreaterThanOrEqual(ground(offset) - 1e-9);
    expect(surface).toBeLessThanOrEqual(1000 + 1e-9);
  });
});
