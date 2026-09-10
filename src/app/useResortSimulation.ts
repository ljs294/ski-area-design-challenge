import { useMemo, useState } from 'react';
import type { GameSave } from '../types/gameSave';
import type { DualPublication, ResortSimulationInput } from '../dualClock/model';
import { createDualClock, projectDualClock } from '../dualClock/clock';
import { resolveWeatherHour } from '../weather/weatherSession';
import { generateBareSnowGrid } from '../snow';
import { buildSkiNetwork } from '../network';
import { resortRevision } from '../dualClock/revision';
import { defaultDualAmenities } from '../dualClock/amenities';
import { useGameSimulation, type GameSimulationController } from './useGameSimulation';
import { useDualClockRuntime } from './useDualClockRuntime';

const EMPTY_RESORT: ResortSimulationInput = { revision: 0, edges: [], trails: [], portal: null,
  dailyDemand: 900, ticketPriceCents: 10000, amenities: [] };

/** One orchestration owner for new games; the old hook only supplies weather services. */
export function useResortSimulation(options: Parameters<typeof useGameSimulation>[0] & { initialSave?: GameSave | null }): GameSimulationController {
  const enabled = !options.initialSave || options.initialSave.schemaVersion === 17;
  const [lastPublication, setLastPublication] = useState<DualPublication | null>(null);
  const fallback = useMemo(() => options.initialSave?.dualClock?.clock ? { ...options.initialSave.dualClock.clock, paused: true } : createDualClock(
    options.initialTime?.clock.calendarDate ?? '2026-05-01T07:00:00.000Z', options.initialTime?.clock.timezone ?? 'America/Los_Angeles'),
  [options.initialSave, options.initialTime]);
  const legacy = useGameSimulation({ ...options, playbackEnabled: !enabled,
    externalClock: enabled ? projectDualClock(lastPublication?.clock ?? fallback) : undefined });
  const weather = useMemo(() => legacy.session?.plan.hours.map(hour => resolveWeatherHour(hour, legacy.session!.midpoint)) ?? [], [legacy.session]);
  const initialSnow = useMemo(() => options.terrain ? generateBareSnowGrid(options.terrain) : null, [options.terrain]);
  const initialResort = useMemo(() => {
    const save = options.initialSave; if (!save?.dualClock) return EMPTY_RESORT;
    const network = buildSkiNetwork(save.trails, save.lifts, { nodes: save.nodes ?? [], paths: save.paths ?? [], junctions: save.junctions ?? [] });
    return { ...EMPTY_RESORT, edges: network.edges, trails: save.trails, portal: save.dualClock.portal,
      amenities: defaultDualAmenities(save.dualClock.portal?.nodeId ?? null),
      ticketPriceCents: save.dualClock.nextTicketPriceCents,
      revision: resortRevision(network.edges, save.trails) };
  }, [options.initialSave]);
  const initialization = useMemo(() => options.terrain && options.snow.grid ? {
    seed: `game-${options.terrain.key}`, at: fallback.at,
    timezone: options.initialSave?.dualClock?.clock.timezone ?? legacy.weatherPackage?.manifest.timezone ?? fallback.timezone,
    snow: initialSnow, terrain: options.terrain, weather, resort: initialResort,
    checkpoint: options.initialSave?.dualClock,
  } : null, [options.terrain, options.snow.grid, initialSnow, initialResort, fallback, legacy.weatherPackage, weather, options.initialSave]);
  const invalid = options.initialSave?.schemaVersion === 17 && !options.initialSave.dualClock;
  const runtime = useDualClockRuntime({ enabled: enabled && !invalid, initialization, snow: options.snow,
    prepareWeather: async (from, to) => legacy.prepareHours?.(from, to) ?? null });
  const dual = { ...runtime, weatherReady: !!legacy.weatherPackage, initialPortal: options.initialSave?.dualClock?.portal ?? null,
    initialTicketPriceCents: options.initialSave?.dualClock?.nextTicketPriceCents ?? 10000 };
  // Publish the committed projection to weather presentation without another advancing clock.
  if (dual.publication !== lastPublication) setLastPublication(dual.publication);
  if (!enabled) return legacy;
  const clock = projectDualClock(dual.publication?.clock ?? fallback);
  return { ...legacy, dual, clock,
    status: invalid || dual.error ? 'corrupt' : dual.ready && legacy.weatherPackage ? 'ready' : legacy.status,
    message: invalid ? 'This schema-17 save is missing its simulation checkpoint.' : dual.error ?? legacy.message,
    togglePlayback: dual.togglePlayback, pause: dual.pause,
    addSnow: dual.addSnow,
    advancePlanningPeriod: () => dual.advance('winter'), confirmTransition: () => dual.advance('winter'),
    snapshot: () => ({ ...legacy.snapshot(), time: { schemaVersion: 3, configVersion: 1, clock } }),
  };
}
