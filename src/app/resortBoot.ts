/**
 * Boot progress model for resuming a saved resort (Load / Continue).
 *
 * Loading a resort is a chain of six stages of wildly different cost, and only
 * one of them (the tile preload) can report real progress. Rather than show a
 * bar that jumps or an indeterminate slider that says nothing, each stage owns
 * a fixed slice of 0-100% sized to its typical share of the wait. The bar then
 * eases forward inside the current stage's band, so it is always moving and
 * never lies about which stage it is in.
 *
 * Weights come from the actual shape of the load: `warm` (warmResortTiles,
 * hundreds-to-thousands of main-thread tile rasterizations) dominates and is
 * also the only genuinely determinate stage, so the bar is honest exactly where
 * the player spends most of the wait.
 */

export type BootStage = 'save' | 'package' | 'validate' | 'build' | 'warm' | 'settle';

export interface BootProgress {
  stage: BootStage;
  /** Determinate count, currently only supplied by the `warm` stage. */
  completed?: number;
  total?: number;
  /** Caption override — e.g. the one-time v4→v5 ground-cover upgrade. */
  note?: string;
}

export const BOOT_STAGES: { stage: BootStage; label: string; weight: number }[] = [
  { stage: 'save', label: 'Reading your save', weight: 3 },
  { stage: 'package', label: 'Loading resort package', weight: 20 },
  { stage: 'validate', label: 'Validating terrain', weight: 7 },
  { stage: 'build', label: 'Building the resort', weight: 10 },
  { stage: 'warm', label: 'Preloading terrain', weight: 50 },
  { stage: 'settle', label: 'Final draw', weight: 10 },
];

const TOTAL_WEIGHT = BOOT_STAGES.reduce((sum, s) => sum + s.weight, 0);

/** The [start, end] percentage band a stage occupies on the bar. */
export function bootBand(stage: BootStage): [number, number] {
  let start = 0;
  for (const s of BOOT_STAGES) {
    const end = start + (s.weight / TOTAL_WEIGHT) * 100;
    if (s.stage === stage) return [start, end];
    start = end;
  }
  return [0, 100];
}

export function bootLabel(stage: BootStage): string {
  return BOOT_STAGES.find((s) => s.stage === stage)?.label ?? '';
}

/**
 * The true percentage for a determinate stage, or null when the stage is opaque
 * and the caller should ease within `bootBand(stage)` instead.
 */
export function bootPercent(p: BootProgress): number | null {
  const [start, end] = bootBand(p.stage);
  if (p.total === undefined || p.total <= 0 || p.completed === undefined) return null;
  const fraction = Math.min(1, Math.max(0, p.completed / p.total));
  return start + fraction * (end - start);
}

/** Caption shown under the bar: the note wins, else the stage label. */
export function bootCaption(p: BootProgress): string {
  return p.note ?? bootLabel(p.stage);
}

/**
 * What MapView reports upward while a saved resort boots. The loading screen is
 * owned by App (it must exist before MapView mounts and outlive its first full
 * render), so MapView pushes its state out through this one channel.
 */
export type BootEvent =
  | { type: 'progress'; progress: BootProgress }
  /** The resort's own aerial, once decoded — becomes the loading backdrop. */
  | { type: 'backdrop'; imageryUrl: string | null }
  /** Load failed. `repair` re-runs preparation, matching the old gate button. */
  | { type: 'failed'; message: string; repair: () => void }
  /** Preparation took over; the New Game package gate owns the screen now. */
  | { type: 'handoff' }
  /** The resort is posed and fully drawn — safe to reveal. */
  | { type: 'ready' };

/** Imperative handles MapView hands App so the screen can force or abort a load. */
export interface BootControls {
  reveal(): void;
  abort(): void;
}
