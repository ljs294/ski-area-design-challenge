import { useEffect, useRef, useState, type MutableRefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedSnowGrid, SnowGrid } from '../types/snow';
import type { TerrainRecord } from '../types/terrain';
import { encodeSnowGrid, generateSnowBaseline, hydrateSnowGrid } from '../snow';
import { refreshSnowSource, setActiveSnowGrid } from './snowProtocol';
import type { SnowDisplayMode } from './snowStyle';

export interface SnowLayerState {
  grid: SnowGrid | null;
  gridRef: MutableRefObject<SnowGrid | null>;
  mode: SnowDisplayMode;
  modeRef: MutableRefObject<SnowDisplayMode>;
  load: (terrain: TerrainRecord, saved?: SavedSnowGrid) => void;
  regenerate: (terrain: TerrainRecord) => void;
  replace: (grid: SnowGrid, refresh?: boolean) => void;
  refresh: () => void;
  changeMode: (mode: SnowDisplayMode) => void;
  snapshot: (fallback?: SavedSnowGrid) => SavedSnowGrid | undefined;
}

/** Owns the persisted snow grid and its session-only map presentation. */
export function useSnowLayer(mapRef: MutableRefObject<maplibregl.Map | null>): SnowLayerState {
  const [grid, setGrid] = useState<SnowGrid | null>(null);
  const gridRef = useRef<SnowGrid | null>(null);
  const [mode, setMode] = useState<SnowDisplayMode>('depth');
  const modeRef = useRef<SnowDisplayMode>('depth');

  function publish(next: SnowGrid): void {
    gridRef.current = next;
    setActiveSnowGrid(next);
    setGrid(next);
  }

  useEffect(() => () => setActiveSnowGrid(null), []);

  return {
    grid,
    gridRef,
    mode,
    modeRef,
    load: (terrain, saved) => publish(hydrateSnowGrid(saved, terrain)),
    regenerate: (terrain) => publish(generateSnowBaseline(terrain)),
    replace: (next, shouldRefresh = true) => {
      publish(next);
      if (shouldRefresh) refreshSnowSource(mapRef.current, modeRef.current);
    },
    refresh: () => refreshSnowSource(mapRef.current, modeRef.current),
    changeMode: (nextMode) => {
      modeRef.current = nextMode;
      setMode(nextMode);
      refreshSnowSource(mapRef.current, nextMode);
    },
    snapshot: (fallback) => gridRef.current ? encodeSnowGrid(gridRef.current) : fallback,
  };
}
