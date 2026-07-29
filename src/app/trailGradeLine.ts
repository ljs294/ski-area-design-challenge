// The designed longitudinal profile of a painted run — its grade line.
//
// A fall-line run should keep the mountain's own shape: rolls, flats and local
// rises ARE the run, so all it gets is a light zero-phase smoothing pass. A
// traverse is the opposite problem — painted along a contour it comes out
// near-level, which strands skiers, and its bench elevation is the single
// biggest lever on how much earthwork the cross section needs. So traverse
// stations get a designed grade line: descending, inside a skiable band, and
// sitting as close to natural ground as that band allows.
//
// The two regimes are not blended elevation-wise — that would bend a steep
// pitch toward a traverse grade. Instead the grade band applies only where the
// run actually traverses; elsewhere the natural station-to-station drop passes
// through untouched. Where they meet you get a real grade break, which is what
// a traverse running out onto a pitch looks like.
//
// Pure and deterministic: no DOM, no terrain sampling. The caller supplies
// chainage and ground elevation per station, oriented top-first.

/** Skiable grade band for a designed traverse. Below ~4% a cat track stops
 * carrying skiers; above ~15% it stops being a traverse and becomes a pitch. */
export const TRAVERSE_MIN_GRADE = 0.04;
export const TRAVERSE_MAX_GRADE = 0.15;
/** Default corridor the grade line must stay inside, in meters either side of
 * natural ground. The bench sits on this line, so it also bounds how far the
 * running surface can float off the hillside. */
export const DEFAULT_MAX_DEVIATION_M = 4;

/** Alternating projection passes. The feasibility sweep is a contraction, so
 * this converges well before the cap; the fixed count keeps it deterministic. */
const PROJECTION_PASSES = 40;

export type StationMode = 'fall-line' | 'traverse';

export interface GradeLinePolicy {
  minGrade?: number;
  maxGrade?: number;
  /** How far the grade line may sit off natural ground. */
  maxDeviationM?: number;
}

export interface GradeLineDesign {
  elevations: number[];
  mode: StationMode[];
}

/** One light, zero-phase smoothing pass. It deliberately imposes no downhill
 * invariant: real rolls, flats, and local rises remain part of the run.
 *
 * The window shrinks symmetrically near the ends rather than being truncated on
 * one side — a lopsided kernel is no longer zero phase, and on a constant grade
 * it would leave a visible kink at the second-to-last station of every run. */
export function smoothTrailProfile(elevations: number[]): number[] {
  if (elevations.length < 3) return elevations.slice();
  const weights = [1, 2, 3, 2, 1];
  return elevations.map((value, i) => {
    const radius = Math.min(2, i, elevations.length - 1 - i);
    if (radius === 0) return value;
    let sum = 0;
    let weight = 0;
    for (let offset = -radius; offset <= radius; offset++) {
      const w = weights[offset + 2];
      sum += elevations[i + offset] * w;
      weight += w;
    }
    return sum / weight;
  });
}

/**
 * Per-station "how much of this run is a traverse" weight in [0,1]. 1 where the
 * natural along-trail grade is gentler than the band's max — a traverse, which
 * needs a designed descent — and 0 where the ground already falls steeply, a
 * fall-line pitch that should be left alone. The ramp between them is smoothed
 * so the regimes meet over several stations rather than at one vertex.
 */
export function traverseWeights(
  chainageM: number[],
  groundElevM: number[],
  maxGrade = TRAVERSE_MAX_GRADE
): number[] {
  const n = groundElevM.length;
  const weights = new Array<number>(n).fill(0);
  for (let i = 0; i < n; i++) {
    const a = Math.max(0, i - 1);
    const b = Math.min(n - 1, i + 1);
    const run = chainageM[b] - chainageM[a];
    const grade = run > 1e-6 ? Math.abs(groundElevM[b] - groundElevM[a]) / run : 0;
    // Full weight at or below the band max, tapering out by twice the band max.
    if (grade <= maxGrade) weights[i] = 1;
    else if (grade >= maxGrade * 2) weights[i] = 0;
    else weights[i] = 1 - (grade - maxGrade) / maxGrade;
  }
  const spread = weights.slice();
  for (let pass = 0; pass < 2; pass++) {
    const source = spread.slice();
    for (let i = 0; i < n; i++) {
      let sum = 0;
      let count = 0;
      for (let offset = -2; offset <= 2; offset++) {
        const station = i + offset;
        if (station < 0 || station >= n) continue;
        sum += source[station];
        count++;
      }
      spread[i] = sum / count;
    }
  }
  return spread;
}

/**
 * Sweep the run top to bottom making it feasible, in strict priority order:
 *
 *  1. a traversed interval never ascends — a counter-slope strands skiers;
 *  2. no station sits further than `deviationM` from natural ground — the grade
 *     line may not trench its way down the mountain;
 *  3. the drop stays inside [minGrade*run, maxGrade*run].
 *
 * Each yields to the one above it, so a hillside that climbs back up under a
 * run already at its corridor floor will push the line past `deviationM` rather
 * than send skiers uphill, and a hillside falling faster than `maxGrade` will
 * drag the line down with it rather than strand it on a growing fill.
 *
 * The third is a goal rather than a constraint because it can genuinely be
 * unreachable: a run painted along a contour cannot be given a skiable descent
 * without digging, and following the mountain with a flat spot is the honest
 * answer. Intervals the run does not traverse keep their drop exactly, so
 * corrections made inside a traverse carry through the pitches around it
 * without bending them.
 */
function sweepFeasible(
  elevations: number[],
  chainageM: number[],
  groundElevM: number[],
  constrained: boolean[],
  minGrade: number,
  maxGrade: number,
  deviationM: number
): number[] {
  const out = elevations.slice();
  const corridor = (value: number, i: number) => Math.max(
    groundElevM[i] - deviationM, Math.min(groundElevM[i] + deviationM, value));
  out[0] = corridor(out[0], 0);
  for (let i = 1; i < out.length; i++) {
    if (!constrained[i - 1]) {
      out[i] = corridor(out[i - 1] - (elevations[i - 1] - elevations[i]), i);
      continue;
    }
    const run = Math.max(1e-6, chainageM[i] - chainageM[i - 1]);
    // Feasible window: never above the previous station, never outside the
    // corridor. Never empty — the corridor floor is always reachable by
    // descending, which is the direction we are going anyway.
    const ceiling = Math.min(out[i - 1], groundElevM[i] + deviationM);
    const floor = Math.min(ceiling, groundElevM[i] - deviationM);
    const wanted = out[i - 1] - Math.max(minGrade * run,
      Math.min(maxGrade * run, out[i - 1] - out[i]));
    out[i] = Math.max(floor, Math.min(ceiling, wanted));
  }
  return out;
}

/**
 * Design the run's grade line.
 *
 * Traverse stations pull toward natural ground under a curvature term, then get
 * swept back into the feasible set; iterating the two settles on the shortest
 * move off the mountain that still gives a descending, skiable run. A
 * re-centring step keeps the sweep from dragging the whole line downhill.
 *
 * `chainageM` must be non-decreasing and the run oriented top-first (see
 * `orientTopToBottom` in ../trails).
 */
export function designGradeLine(
  chainageM: number[],
  groundElevM: number[],
  policy: GradeLinePolicy = {}
): GradeLineDesign {
  const n = groundElevM.length;
  const minGrade = Math.max(0, policy.minGrade ?? TRAVERSE_MIN_GRADE);
  const maxGrade = Math.max(minGrade + 1e-6, policy.maxGrade ?? TRAVERSE_MAX_GRADE);
  const deviationM = Math.max(0, policy.maxDeviationM ?? DEFAULT_MAX_DEVIATION_M);
  if (n < 3) {
    return {
      elevations: groundElevM.slice(),
      mode: groundElevM.map(() => 'fall-line' as StationMode),
    };
  }

  const weights = traverseWeights(chainageM, groundElevM, maxGrade);
  const mode = weights.map((w): StationMode => (w >= 0.5 ? 'traverse' : 'fall-line'));
  // An interval is band-constrained when both of its stations traverse.
  const constrained = weights.map((w, i) =>
    i >= n - 1 ? false : w >= 0.5 && weights[i + 1] >= 0.5);
  if (!constrained.some(Boolean)) {
    return { elevations: smoothTrailProfile(groundElevM), mode };
  }

  const sweep = (values: number[]) => sweepFeasible(values, chainageM, groundElevM,
    constrained, minGrade, maxGrade, deviationM);
  let designed = sweep(smoothTrailProfile(groundElevM));
  for (let pass = 0; pass < PROJECTION_PASSES; pass++) {
    // Attraction toward natural ground plus a curvature term, so the designed
    // line reads as an alignment rather than a staircase of clamped segments.
    // Fall-line stations are pinned — they are not ours to redesign.
    const relaxed = designed.map((value, i) => {
      if (weights[i] <= 0) return value;
      const neighbourMean = i === 0 || i === n - 1
        ? value : (designed[i - 1] + designed[i + 1]) / 2;
      const pulled = value * 0.40 + neighbourMean * 0.35 + groundElevM[i] * 0.25;
      return value * (1 - weights[i]) + pulled * weights[i];
    });
    // Re-centre the traversed stations on natural ground so the sweep cannot
    // drag the whole alignment off the mountain.
    let residual = 0;
    let residualWeight = 0;
    for (let i = 0; i < n; i++) {
      residual += (groundElevM[i] - relaxed[i]) * weights[i];
      residualWeight += weights[i];
    }
    if (residualWeight > 1e-9) {
      const centring = residual / residualWeight;
      for (let i = 0; i < n; i++) relaxed[i] += centring * weights[i];
    }
    designed = sweep(relaxed);
  }

  return { elevations: designed, mode };
}
