// One station's cross section: a level running surface plus the cut and fill
// faces that tie it back to natural ground — all of it inside a fixed lateral
// budget, because grading never leaves the painted footprint.
//
//        cut face                bench                fill face
//   ~~~~~~\                                                      /~~~~~~
//          \____________________________________________________/
//   |<- budget ->|<-- benchLeft --|-- benchRight -->|<- budget ->|
//
// The bench is *level*, always. That is the whole design: contours are level
// lines, so a surface that does not tilt across the section has contours running
// exactly along the section normal — square to the centreline — and falls only
// along the trail, which is what a skier wants underfoot.
//
// Faces are capped at 45°, which makes the geometry honest: a face only closes
// on the ground when it is steeper than the ground it chases, so a hillside
// steeper than 45° cannot be benched at any width. That rule is not a separate
// check — it falls out of the arithmetic below.
//
// Everything here is pure. The caller supplies a ground sampler in section
// coordinates (signed offset in meters along the section normal).

/** 45°, 1:1. Steeper than this is a rock cut or a wall, neither of which we
 * build — in cut or in fill. */
export const MAX_FACE_SLOPE = 1.0;
/** Narrowest running surface worth calling a trail. */
export const MIN_BENCH_WIDTH_M = 8;

export interface SectionBudget {
  /** Distance from the centreline to the painted edge on the negative-offset
   * side. Grading may not pass either of these. */
  leftHalfWidthM: number;
  rightHalfWidthM: number;
}

export interface SectionPolicy {
  maxFaceSlope?: number;
  minBenchWidthM?: number;
  /** Cap on the level running surface. Trails leave this open so a wide paint
   * benches wide; a road pins it to the pavement width. */
  maxBenchWidthM?: number;
  /** Lateral sampling step. Defaults to 1 m; the engine passes the DEM cell size
   * so the section and the raster agree. */
  stepM?: number;
}

export interface CrossSection {
  /** False when the hillside is too steep to hold a bench inside the budget. */
  gradeable: boolean;
  /** Distance from the centreline to each bench edge (both positive). */
  benchLeftM: number;
  benchRightM: number;
  /** Distance from the centreline to where each face meets natural ground. */
  daylightLeftM: number;
  daylightRightM: number;
  cutDepthM: number;
  fillHeightM: number;
}

/**
 * How far a face may leave the centreline and still tie in at `offsetM`.
 *
 * A face leaving a bench edge `b` closes on the ground at `d` exactly when it
 * has covered the elevation difference over the run available to it:
 *
 *     |g(d) - z| <= s * (d - b)      <=>      b <= d - |g(d) - z| / s
 *
 * so this returns the right-hand side. The widest level bench that ties in is
 * the largest of these over the budget — see `edgeFor`.
 */
function reach(
  benchElevM: number,
  groundElevM: number,
  offsetM: number,
  faceSlope: number
): number {
  return offsetM - Math.abs(groundElevM - benchElevM) / faceSlope;
}

/** Widest bench edge on one side, and the offset where its face daylights. */
function edgeFor(
  benchElevM: number,
  groundAt: (offsetM: number) => number,
  sign: 1 | -1,
  budgetM: number,
  faceSlope: number,
  stepM: number
): { benchM: number; daylightM: number } {
  let benchM = 0;
  let daylightM = 0;
  for (let offset = stepM; offset <= budgetM + 1e-6; offset += stepM) {
    const candidate = reach(benchElevM, groundAt(sign * offset), offset, faceSlope);
    if (candidate > benchM) {
      benchM = candidate;
      daylightM = offset;
    }
  }
  // Ground level with the bench needs no face at all, so the whole budget is
  // running surface; the loop finds that as reach(budget) === budget.
  return { benchM, daylightM: Math.max(daylightM, benchM) };
}

/** Deepest cut and highest fill between the centreline and `limitM`. */
function extremes(
  benchElevM: number,
  groundAt: (offsetM: number) => number,
  leftLimitM: number,
  rightLimitM: number,
  stepM: number
): { cutDepthM: number; fillHeightM: number } {
  let cutDepthM = 0;
  let fillHeightM = 0;
  for (let offset = -leftLimitM; offset <= rightLimitM + 1e-6; offset += stepM) {
    const delta = benchElevM - groundAt(offset);
    if (delta < 0) cutDepthM = Math.max(cutDepthM, -delta);
    else fillHeightM = Math.max(fillHeightM, delta);
  }
  return { cutDepthM, fillHeightM };
}

/**
 * Solve one station: the widest level bench at `benchElevM` whose cut and fill
 * faces both tie into natural ground inside the painted budget.
 *
 * There is nothing to search — each side is one outward scan. On a uniform
 * sidehill of slope `m` the arithmetic reduces to `bench = budget * (1 - m/s)`,
 * so the bench narrows as the hill steepens and reaches zero exactly at
 * `m = s`. That is the 45° rule, arrived at rather than imposed.
 */
export function solveCrossSection(
  benchElevM: number,
  groundAt: (offsetM: number) => number,
  budget: SectionBudget,
  policy: SectionPolicy = {}
): CrossSection {
  const faceSlope = Math.max(0.05, policy.maxFaceSlope ?? MAX_FACE_SLOPE);
  const stepM = Math.max(0.25, policy.stepM ?? 1);
  const leftBudget = Math.max(stepM, budget.leftHalfWidthM);
  const rightBudget = Math.max(stepM, budget.rightHalfWidthM);
  const minBenchWidthM = Math.max(0, policy.minBenchWidthM ?? MIN_BENCH_WIDTH_M);
  const maxBenchHalfM = (policy.maxBenchWidthM ?? Infinity) / 2;

  const left = edgeFor(benchElevM, groundAt, -1, leftBudget, faceSlope, stepM);
  const right = edgeFor(benchElevM, groundAt, 1, rightBudget, faceSlope, stepM);
  const benchLeftM = Math.min(left.benchM, maxBenchHalfM);
  const benchRightM = Math.min(right.benchM, maxBenchHalfM);

  // Capping the bench pulls its edge back in, which only ever gives the face
  // more room, so the daylight offset it found still holds.
  const daylightLeftM = Math.max(benchLeftM, Math.min(left.daylightM, leftBudget));
  const daylightRightM = Math.max(benchRightM, Math.min(right.daylightM, rightBudget));
  const { cutDepthM, fillHeightM } = extremes(benchElevM, groundAt,
    daylightLeftM, daylightRightM, stepM);

  return {
    gradeable: benchLeftM + benchRightM >= minBenchWidthM - 1e-9,
    benchLeftM,
    benchRightM,
    daylightLeftM,
    daylightRightM,
    cutDepthM,
    fillHeightM,
  };
}

/** Surface elevation of a solved section at a signed lateral offset: the level
 * bench, then a face clamped so a cut never fills and a fill never cuts.
 * Returns `null` outside the disturbed width, where natural ground stands. */
export function sectionSurfaceAt(
  section: CrossSection,
  benchElevM: number,
  offsetM: number,
  groundElevM: number,
  policy: SectionPolicy = {}
): number | null {
  if (offsetM >= -section.benchLeftM && offsetM <= section.benchRightM) return benchElevM;
  const daylight = offsetM < 0 ? section.daylightLeftM : section.daylightRightM;
  if (Math.abs(offsetM) > daylight) return null;
  const faceSlope = Math.max(0.05, policy.maxFaceSlope ?? MAX_FACE_SLOPE);
  const edge = offsetM < 0 ? section.benchLeftM : section.benchRightM;
  const distance = Math.abs(offsetM) - edge;
  return benchElevM < groundElevM
    ? Math.min(benchElevM + faceSlope * distance, groundElevM)
    : Math.max(benchElevM - faceSlope * distance, groundElevM);
}
