import { useEffect, useRef, type Dispatch, type MutableRefObject,
  type RefObject, type SetStateAction } from 'react';
import maplibregl from 'maplibre-gl';
import type { GameSave } from '../types/gameSave';
import type { TerrainRecord } from '../types/terrain';
import { basemapFor, tuneBasemap } from './basemapStyle';
import { applyAnalysisRenderProfile, setContourUnits, type LayerToggle } from './analysisLayers';
import type { Readout } from './CursorReadout';
import type { MapInteractionLease } from './mapInteractionLease';
import type { MapContributionRegistry } from './mapContribution';
import { resortCameraBounds, getResortRenderStats, setRenderConcurrency,
  setResortRenderQuality, warmResortTiles } from './resortProtocols';
import { resumeCameraOf } from './resumeCheckpoint';
import type { BootControls, BootEvent, BootProgress } from './resortBoot';
import { pixelRatioForElement, type RenderQuality, type Units } from './SettingsContext';
import type { SiteBox } from './sitePicker';
import type { SiteMode } from './SiteControl';
import { applyTileLod } from './terrainLod';
import { mountTerrain, unmountTerrain, PITCH_3D } from './terrain3d';

const TERRAIN_DISABLED =
  typeof window !== 'undefined' && new URLSearchParams(window.location.search).has('flat');

interface MapRuntimeOptions {
  canStart: boolean;
  mode: 'picking' | 'playing';
  initialSave?: GameSave | null;
  initialCenter: [number, number];
  initialZoom: number;
  resolvedTheme: 'light' | 'dark';
  renderQuality: RenderQuality;
  units: Units;
  mapRef: MutableRefObject<maplibregl.Map | null>;
  containerRef: RefObject<HTMLDivElement | null>;
  terrainRecordRef: MutableRefObject<TerrainRecord | null>;
  renderQualityRef: MutableRefObject<RenderQuality>;
  layersRef: MutableRefObject<LayerToggle[]>;
  siteBoxRef: MutableRefObject<SiteBox | null>;
  siteModeRef: MutableRefObject<SiteMode>;
  is3DRef: MutableRefObject<boolean>;
  resortReadyRef: MutableRefObject<boolean>;
  warmAbortRef: MutableRefObject<AbortController | null>;
  bootControls: MutableRefObject<BootControls | null>;
  bootControlsRef?: MutableRefObject<BootControls | null>;
  mapInteractionLeaseRef: MutableRefObject<MapInteractionLease | null>;
  registry: MapContributionRegistry;
  reconfigureAnalysisProfileRef: MutableRefObject<(map: maplibregl.Map) => void>;
  canDispatchHit(): boolean;
  doSampleRef: MutableRefObject<(lngLat: { lng: number; lat: number }) => void>;
  lastLngLatRef: MutableRefObject<{ lng: number; lat: number } | null>;
  rafPendingRef: MutableRefObject<boolean>;
  setLayers: Dispatch<SetStateAction<LayerToggle[]>>;
  setReadout: Dispatch<SetStateAction<Readout | null>>;
  setIsOverhead: Dispatch<SetStateAction<boolean>>;
  setIs3D: Dispatch<SetStateAction<boolean>>;
  reportBoot(event: BootEvent): void;
  reportStage(progress: BootProgress): void;
  showLocalBoot(progress: BootProgress | null): void;
  reportGraphicsFailure(message: string): void;
}

/** Owns MapLibre creation, style restoration, camera warm-up, and live settings. */
export function useMapRuntime(options: MapRuntimeOptions): void {
  const firstModeRun = useRef(true);
  const appliedProfileRef = useRef(options.renderQuality);

  const reinitializeStyle = (map: maplibregl.Map) => {
    tuneBasemap(map);
    const applied = options.registry.synchronizeStyle().map((descriptor) => ({
      ...descriptor,
      layerIds: [...descriptor.layerIds],
    }));
    applyTileLod(map, options.renderQualityRef.current);
    applyAnalysisRenderProfile(map, options.renderQualityRef.current, map.getPitch() < 0.5, {
      hillshade: applied.find((entry) => entry.id === 'hillshade')?.visible,
      contours: applied.find((entry) => entry.id === 'contours')?.visible,
    });
    if (options.terrainRecordRef.current && !TERRAIN_DISABLED) {
      mountTerrain(map, options.renderQualityRef.current);
      if (!options.resortReadyRef.current) {
        options.resortReadyRef.current = true;
        const want3D = options.initialSave?.is3D ?? true;
        if (want3D !== options.is3DRef.current) options.setIs3D(want3D);
        if (options.initialSave) {
          map.jumpTo(resumeCameraOf(options.initialSave, {
            center: options.initialCenter,
            zoom: options.initialZoom,
            bearing: 0,
            pitch: 0,
          }));
        } else {
          map.jumpTo({ pitch: want3D ? PITCH_3D : 0 });
        }
        const record = options.terrainRecordRef.current;
        let revealed = false;
        const reveal = () => {
          if (revealed) return;
          revealed = true;
          setRenderConcurrency(1);
          options.bootControls.current = null;
          if (options.bootControlsRef) options.bootControlsRef.current = null;
          options.reportBoot({ type: 'ready' });
        };
        const controller = new AbortController();
        options.warmAbortRef.current = controller;
        options.bootControls.current = { reveal, abort: () => controller.abort() };
        if (options.bootControlsRef) options.bootControlsRef.current = options.bootControls.current;
        void (async () => {
          options.reportStage({ stage: 'warm' });
          if (record) {
            let lastReport = 0;
            await warmResortTiles(record, (completed, total) => {
              const now = performance.now();
              if (completed < total && now - lastReport < 120) return;
              lastReport = now;
              options.reportStage({ stage: 'warm', completed, total });
            }, controller.signal, options.renderQualityRef.current);
          }
          if (controller.signal.aborted) return;
          options.reportStage({ stage: 'settle' });
          let stable = 0;
          const settle = () => {
            if (revealed || controller.signal.aborted) return;
            const ready = map.areTilesLoaded() &&
              getResortRenderStats().pending === 0 && map.loaded();
            stable = ready ? stable + 1 : 0;
            if (stable >= 2) reveal();
            else requestAnimationFrame(settle);
          };
          requestAnimationFrame(settle);
        })();
      }
    } else {
      unmountTerrain(map);
      if (options.mode === 'playing' && !options.resortReadyRef.current) {
        options.resortReadyRef.current = true;
        options.reportBoot({ type: 'ready' });
      } else {
        options.showLocalBoot(null);
      }
    }
    options.setLayers(applied);
  };

  useEffect(() => {
    if (!options.canStart || options.mapRef.current || !options.containerRef.current) return;
    const start = resumeCameraOf(options.initialSave, {
      center: options.initialCenter,
      zoom: options.initialZoom,
      bearing: 0,
      pitch: 0,
    });
    const map = new maplibregl.Map({
      container: options.containerRef.current,
      style: basemapFor(options.resolvedTheme, { offline: options.mode === 'playing' }),
      center: start.center,
      zoom: start.zoom,
      bearing: start.bearing,
      pitch: start.pitch,
      pixelRatio: pixelRatioForElement(options.renderQuality, options.containerRef.current),
      attributionControl: false,
    });
    options.mapRef.current = map;
    const canvas = map.getCanvas();
    const onContextLost = (event: Event) => {
      event.preventDefault();
      options.reportGraphicsFailure(
        'The graphics context was lost. Select Performance in Settings, then reload the resort.',
      );
    };
    canvas.addEventListener('webglcontextlost', onContextLost);
    options.registry.attach(map, options.canDispatchHit);
    (window as unknown as { appMap: maplibregl.Map }).appMap = map;
    map.dragRotate.enable();
    map.keyboard.enable();
    map.addControl(new maplibregl.AttributionControl({
      compact: true,
      customAttribution: [
        'Elevation: USGS 3DEP',
        'Geocoding © OpenStreetMap contributors (Nominatim)',
      ],
    }), 'bottom-right');
    map.addControl(new maplibregl.NavigationControl({
      visualizePitch: true, showZoom: true, showCompass: true,
    }), 'bottom-right');
    map.on('style.load', () => {
      reinitializeStyle(map);
      if (options.siteModeRef.current === 'locked') {
        const record = options.terrainRecordRef.current;
        const bounds = record ? resortCameraBounds(record) : undefined;
        if (bounds) map.setMaxBounds(bounds);
        else if (options.siteBoxRef.current) map.setMaxBounds(options.siteBoxRef.current.bounds);
      }
    });
    let lastSampleAt = -Infinity;
    const sampleLatest = () => {
      if (!options.lastLngLatRef.current || map.isMoving()) return;
      const now = performance.now();
      if (now - lastSampleAt < 100) return;
      lastSampleAt = now;
      options.doSampleRef.current(options.lastLngLatRef.current);
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      options.lastLngLatRef.current = { lng: event.lngLat.lng, lat: event.lngLat.lat };
      if (options.rafPendingRef.current || map.isMoving()) return;
      options.rafPendingRef.current = true;
      requestAnimationFrame(() => {
        options.rafPendingRef.current = false;
        sampleLatest();
      });
    };
    map.on('mousemove', onMove);
    map.on('moveend', sampleLatest);
    map.on('mouseout', () => {
      options.lastLngLatRef.current = null;
      options.setReadout(null);
    });
    let overhead = map.getPitch() < 0.5;
    const onPitch = () => {
      const next = map.getPitch() < 0.5;
      if (next === overhead) return;
      overhead = next;
      options.setIsOverhead(next);
      const layers = options.layersRef.current;
      applyAnalysisRenderProfile(map, options.renderQualityRef.current, next, {
        hillshade: layers.find((entry) => entry.id === 'hillshade')?.visible,
        contours: layers.find((entry) => entry.id === 'contours')?.visible,
      });
    };
    map.on('pitch', onPitch);
    map.on('pitchend', onPitch);
    return () => {
      options.warmAbortRef.current?.abort();
      map.off('moveend', sampleLatest);
      setRenderConcurrency(1);
      options.mapInteractionLeaseRef.current?.dispose();
      options.registry.dispose();
      canvas.removeEventListener('webglcontextlost', onContextLost);
      delete (window as unknown as { appSetCaptureTransients?: (hidden: boolean) => void })
        .appSetCaptureTransients;
      map.remove();
      options.mapRef.current = null;
      options.setLayers([]);
    };
    // Map creation is intentionally keyed only by package readiness.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [options.canStart]);

  useEffect(() => {
    const map = options.mapRef.current;
    if (!map) return;
    const profileChanged = appliedProfileRef.current !== options.renderQuality;
    appliedProfileRef.current = options.renderQuality;
    if (profileChanged && map.isStyleLoaded() && options.terrainRecordRef.current) {
      options.reconfigureAnalysisProfileRef.current(map);
    }
  }, [options.mapRef, options.reconfigureAnalysisProfileRef, options.renderQuality,
    options.terrainRecordRef]);

  useEffect(() => {
    const map = options.mapRef.current;
    if (!map) return;
    const control = new maplibregl.ScaleControl({
      unit: options.units === 'metric' ? 'metric' : 'imperial',
    });
    map.addControl(control, 'bottom-right');
    return () => { if (options.mapRef.current) options.mapRef.current.removeControl(control); };
  }, [options.mapRef, options.units]);

  useEffect(() => {
    const map = options.mapRef.current;
    if (!map) return;
    const updatePixelRatio = () => {
      const container = options.containerRef.current;
      if (!container) return;
      const next = pixelRatioForElement(options.renderQuality, container);
      if (Math.abs(map.getPixelRatio() - next) > 0.001) map.setPixelRatio(next);
    };
    updatePixelRatio();
    setResortRenderQuality(options.renderQuality);
    applyTileLod(map, options.renderQuality);
    const layers = options.layersRef.current;
    applyAnalysisRenderProfile(map, options.renderQuality, map.getPitch() < 0.5, {
      hillshade: layers.find((entry) => entry.id === 'hillshade')?.visible,
      contours: layers.find((entry) => entry.id === 'contours')?.visible,
    });
    if (options.terrainRecordRef.current && !TERRAIN_DISABLED && map.isStyleLoaded()) {
      mountTerrain(map, options.renderQuality);
      applyTileLod(map, options.renderQuality);
    }
    let raf = 0;
    const schedule = () => {
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(updatePixelRatio);
    };
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(schedule);
    if (options.containerRef.current) observer?.observe(options.containerRef.current);
    window.addEventListener('resize', schedule);
    return () => {
      cancelAnimationFrame(raf);
      observer?.disconnect();
      window.removeEventListener('resize', schedule);
    };
  }, [options.containerRef, options.layersRef, options.mapRef,
    options.reconfigureAnalysisProfileRef, options.renderQuality, options.terrainRecordRef]);

  useEffect(() => {
    if (firstModeRun.current) { firstModeRun.current = false; return; }
    options.mapRef.current?.setStyle(basemapFor('light', {
      offline: options.mode === 'playing',
    }));
  }, [options.mapRef, options.mode]);

  useEffect(() => {
    setContourUnits(options.mapRef.current, options.terrainRecordRef.current, options.units);
  }, [options.mapRef, options.terrainRecordRef, options.units]);
}
