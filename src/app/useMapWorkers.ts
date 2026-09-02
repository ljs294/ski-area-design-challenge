import { useRef, type MutableRefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import { createCoverClearService } from './coverClearService';
import { CoverEditAdapter } from './coverEditClient';
import { DamAnalysisAdapter } from './damAnalysisClient';
import { TerrainGradeAdapter } from './terrainGradeClient';
import type { TerrainDocument } from './terrainDocument';
import { TrailPaintAdapter } from './trailPaintClient';
import { TrailPresentationAdapter } from './trailPresentationClient';

/** Stable worker adapters and the cover service shared by all controllers. */
export function useMapWorkers(
  mapRef: MutableRefObject<maplibregl.Map | null>,
  terrain: TerrainDocument,
) {
  const damAnalysisRef = useRef<DamAnalysisAdapter | null>(null);
  if (!damAnalysisRef.current) damAnalysisRef.current = new DamAnalysisAdapter();
  const coverEditRef = useRef<CoverEditAdapter | null>(null);
  if (!coverEditRef.current) coverEditRef.current = new CoverEditAdapter();
  const terrainGradeRef = useRef<TerrainGradeAdapter | null>(null);
  if (!terrainGradeRef.current) terrainGradeRef.current = new TerrainGradeAdapter();
  const trailPaintRef = useRef<TrailPaintAdapter | null>(null);
  if (!trailPaintRef.current) trailPaintRef.current = new TrailPaintAdapter();
  const trailPresentationRef = useRef<TrailPresentationAdapter | null>(null);
  if (!trailPresentationRef.current) trailPresentationRef.current = new TrailPresentationAdapter();

  const coverEdit = coverEditRef.current;
  return {
    damAnalysis: damAnalysisRef.current,
    coverEdit,
    coverClear: createCoverClearService({
      map: () => mapRef.current,
      terrain,
      adapter: coverEdit,
    }),
    terrainGrade: terrainGradeRef.current,
    trailPaint: trailPaintRef.current,
    trailPresentation: trailPresentationRef.current,
  };
}
