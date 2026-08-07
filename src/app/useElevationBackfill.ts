import { useEffect, type Dispatch, type SetStateAction } from 'react';
import type { SavedLift } from '../types/lifts';
import type { SavedTrail } from '../types/trails';
import { liftStats, orientBottomToTop } from '../lifts';
import {
  difficultyForSlopes,
  orientTopToBottom,
  trailPartsStats,
} from '../trails';
import type { TopologyDocument } from './topologyDocument';

interface TerrainSample {
  elevation: number;
}

interface ElevationBackfillOptions {
  getLifts(): readonly SavedLift[];
  getTrails(): readonly SavedTrail[];
  setLifts: Dispatch<SetStateAction<SavedLift[]>>;
  topology: TopologyDocument;
  samplePoint(lng: number, lat: number, zoom: number): Promise<TerrainSample | null>;
  sampleProfile(line: [number, number][], zoom: number): Promise<number[] | null>;
}

/**
 * One-time compatibility repair for saves created without complete elevation
 * samples. New construction already persists these values; this hook only
 * fills gaps found when the mounted session starts.
 */
export function useElevationBackfill(options: ElevationBackfillOptions): void {
  useEffect(() => {
    const missing = options.getLifts().filter((lift) =>
      lift.endpointElevM.some((elevation) => elevation == null));
    if (missing.length === 0) return;
    let stale = false;
    void Promise.allSettled(missing.map(async (lift) => {
      const samples = await Promise.all(lift.points.map(([lng, lat]) =>
        options.samplePoint(lng, lat, 13)));
      const [a, b] = samples;
      if (!a || !b) throw new Error('No terrain data at this lift.');
      return { id: lift.id, elevations: [a.elevation, b.elevation] as [number, number] };
    })).then((results) => {
      if (stale) return;
      const byId = new Map<string, [number, number]>();
      for (const result of results) {
        if (result.status === 'fulfilled') byId.set(result.value.id, result.value.elevations);
      }
      if (byId.size === 0) return;
      options.setLifts((existing) => existing.map((lift) => {
        const elevations = byId.get(lift.id);
        if (!elevations) return lift;
        const oriented = orientBottomToTop(lift.points, elevations);
        const stats = liftStats(oriented.points, oriented.elevs);
        return {
          ...lift,
          points: oriented.points,
          endpointElevM: oriented.elevs,
          lengthM: stats.lengthM,
          verticalM: stats.verticalM,
        };
      }));
    });
    return () => { stale = true; };
    // Compatibility repair intentionally runs once against the mount snapshot.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const missing = options.getTrails().filter((trail) =>
      trail.parts.some((part) => part.centerlineElevM.length !== part.centerline.length));
    if (missing.length === 0) return;
    let stale = false;
    void Promise.allSettled(missing.map(async (trail) => {
      const parts = await Promise.all(trail.parts.map(async (part) => {
        const elevations = await options.sampleProfile(part.centerline, 13);
        if (!elevations) throw new Error('No terrain data covers this run.');
        const oriented = orientTopToBottom(part.centerline, elevations);
        return { ...part, centerline: oriented.spine, centerlineElevM: oriented.elevM };
      }));
      return { id: trail.id, parts };
    })).then((results) => {
      if (stale) return;
      const byId = new Map<string, SavedTrail['parts']>();
      for (const result of results) {
        if (result.status === 'fulfilled') byId.set(result.value.id, result.value.parts);
      }
      if (byId.size === 0) return;
      const backfill = options.topology.begin();
      backfill.mapTrails((trail) => {
        const parts = byId.get(trail.id);
        if (!parts) return trail;
        const stats = trailPartsStats(parts);
        return {
          ...trail,
          parts,
          lengthM: stats.lengthM,
          verticalM: stats.verticalM,
          avgSlopeDeg: stats.avgSlopeDeg,
          maxSlopeDeg: stats.maxSlopeDeg,
          difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
        };
      });
      backfill.commit();
    });
    return () => { stale = true; };
    // Compatibility repair intentionally runs once against the mount snapshot.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}
