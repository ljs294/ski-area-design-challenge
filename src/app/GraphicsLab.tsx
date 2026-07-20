import { useEffect, useRef, useState } from 'react';
import type { Dispatch, SetStateAction } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { setupAnalysisLayers, type LayerToggle } from './analysisLayers';
import { LayerPanel } from './LayerPanel';
import type { OverlayId } from './Legend';
import { basemapFor, tuneBasemap } from './basemapStyle';
import { mountTerrain, unmountTerrain, tilt3D } from './terrain3d';
import { applyTileLod } from './terrainLod';
import { pixelRatioFor, useSettings, type RenderQuality } from './SettingsContext';

// A developer window for eyeballing graphics settings: two maps side by side
// whose cameras stay locked together, but whose layers and render quality are
// independent — so you can pan/zoom/tilt once and compare, e.g., Standard vs
// Ultra LOD on the same terrain. Bypasses the menu (see App deep-link/shortcut).

const PRESETS = [
  { id: 'crystal', label: 'Crystal', center: [-121.474, 46.928] as [number, number] },
  { id: 'stevens', label: 'Stevens Pass', center: [-121.089, 47.744] as [number, number] },
] as const;
const CENTER = PRESETS[0].center;
const ZOOM = 13;

const QUALITY_OPTS: { value: RenderQuality; label: string }[] = [
  { value: 'standard', label: 'Standard' },
  { value: 'high', label: 'High' },
  { value: 'ultra', label: 'Ultra' },
];

function activeOverlayOf(layers: LayerToggle[]): OverlayId | null {
  const analysis = layers.find((l) => (l.exclusiveGroup === 'overlay' || l.exclusiveGroup === 'analysis') && l.visible);
  const on = analysis ?? layers.find((l) => l.id === 'groundcover' && l.visible);
  return (on?.id as OverlayId) ?? null;
}

/** Compact per-pane render-quality picker (reuses the Settings segmented look). */
function QualityPicker({ value, onChange }: { value: RenderQuality; onChange: (q: RenderQuality) => void }) {
  return (
    <div className="glab-quality segmented">
      {QUALITY_OPTS.map((o) => (
        <button
          key={o.value}
          className={`seg-btn${value === o.value ? ' seg-btn-active' : ''}`}
          aria-pressed={value === o.value}
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function GraphicsLab({ onExit }: { onExit: () => void }) {
  const { resolvedTheme } = useSettings();

  const cont0 = useRef<HTMLDivElement>(null);
  const cont1 = useRef<HTMLDivElement>(null);
  const mapsRef = useRef<[maplibregl.Map | null, maplibregl.Map | null]>([null, null]);
  const guardRef = useRef(false); // prevents the camera-sync echo loop

  const [layers0, setLayers0] = useState<LayerToggle[]>([]);
  const [layers1, setLayers1] = useState<LayerToggle[]>([]);
  const [open0, setOpen0] = useState(true);
  const [open1, setOpen1] = useState(true);
  const [quality0, setQuality0] = useState<RenderQuality>('standard');
  const [quality1, setQuality1] = useState<RenderQuality>('ultra');
  const [is3D, setIs3D] = useState(false);
  const [presetId, setPresetId] = useState<(typeof PRESETS)[number]['id']>('crystal');

  // Per-pane layer toggle with the same exclusive-overlay grouping as MapView.
  function makeToggle(idx: 0 | 1, set: Dispatch<SetStateAction<LayerToggle[]>>) {
    return (id: string) => {
      const map = mapsRef.current[idx];
      if (!map) return;
      set((prev) => {
        const target = prev.find((l) => l.id === id);
        if (!target) return prev;
        const nextVisible = !target.visible;
        return prev.map((l) => {
          if (
            nextVisible &&
            target.exclusiveGroup &&
            l.exclusiveGroup === target.exclusiveGroup &&
            l.id !== id &&
            l.visible
          ) {
            for (const lid of l.layerIds) map.setLayoutProperty(lid, 'visibility', 'none');
            return { ...l, visible: false };
          }
          if (l.id === id) {
            for (const lid of l.layerIds)
              map.setLayoutProperty(lid, 'visibility', nextVisible ? 'visible' : 'none');
            return { ...l, visible: nextVisible };
          }
          return l;
        });
      });
    };
  }
  const toggle0 = makeToggle(0, setLayers0);
  const toggle1 = makeToggle(1, setLayers1);

  // Create both maps once and wire the camera sync.
  useEffect(() => {
    if (!cont0.current || !cont1.current) return;
    const initialQ = [quality0, quality1] as const;
    const setLayers = [setLayers0, setLayers1] as const;
    const conts = [cont0.current, cont1.current];

    const maps = conts.map((container, i) => {
      const m = new maplibregl.Map({
        container,
        style: basemapFor(resolvedTheme),
        center: CENTER,
        zoom: ZOOM,
        pixelRatio: pixelRatioFor(initialQ[i]),
        attributionControl: { compact: true },
      });
      m.addControl(
        new maplibregl.NavigationControl({ visualizePitch: true, showZoom: true, showCompass: true }),
        'bottom-right'
      );
      m.on('style.load', () => {
        tuneBasemap(m);
        setLayers[i](setupAnalysisLayers(m));
        applyTileLod(m, initialQ[i]);
      });
      return m;
    }) as [maplibregl.Map, maplibregl.Map];
    mapsRef.current = maps;

    // Mirror one map's camera onto the other. The guard swallows the echoed
    // 'move' that jumpTo fires on the target so the two don't ping-pong.
    const [a, b] = maps;
    const sync = (from: maplibregl.Map, to: maplibregl.Map) => {
      if (guardRef.current) return;
      guardRef.current = true;
      to.jumpTo({
        center: from.getCenter(),
        zoom: from.getZoom(),
        bearing: from.getBearing(),
        pitch: from.getPitch(),
      });
      guardRef.current = false;
    };
    const onA = () => sync(a, b);
    const onB = () => sync(b, a);
    a.on('move', onA);
    b.on('move', onB);

    return () => {
      a.off('move', onA);
      b.off('move', onB);
      a.remove();
      b.remove();
      mapsRef.current = [null, null];
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Live per-pane quality: re-supersample + re-apply the distance-LOD curve.
  useEffect(() => {
    const m = mapsRef.current[0];
    if (!m) return;
    m.setPixelRatio(pixelRatioFor(quality0));
    applyTileLod(m, quality0);
  }, [quality0]);
  useEffect(() => {
    const m = mapsRef.current[1];
    if (!m) return;
    m.setPixelRatio(pixelRatioFor(quality1));
    applyTileLod(m, quality1);
  }, [quality1]);

  useEffect(() => {
    const preset = PRESETS.find((item) => item.id === presetId)!;
    const first = mapsRef.current[0];
    if (first) first.flyTo({ center: preset.center, zoom: ZOOM, duration: 900 });
  }, [presetId]);

  // Shared 3D toggle — both panes tilt together so the synced camera stays
  // valid. Compare against the last *applied* value (not a first-run flag) so
  // React StrictMode's double-invoked mount effect doesn't fire disable3D on an
  // unloaded style. Guard each map on style-loaded for the same reason.
  const applied3DRef = useRef(false);
  useEffect(() => {
    if (applied3DRef.current === is3D) return;
    applied3DRef.current = is3D;
    const [a, b] = mapsRef.current;
    const q = [quality0, quality1] as const;
    [a, b].forEach((m, i) => {
      if (!m || !m.isStyleLoaded()) return;
      if (is3D) {
        mountTerrain(m);
        tilt3D(m, true);
        applyTileLod(m, q[i]); // terrain-dem source is new; give it the curve
      } else {
        // Tilt flat first, then drop terrain once the ease lands so removing the
        // mesh mid-pitch doesn't snap the camera's elevation.
        tilt3D(m, false);
        m.once('moveend', () => unmountTerrain(m));
      }
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [is3D]);

  // Esc closes the lab.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onExit();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onExit]);

  return (
    <div className="glab">
      <div className="glab-bar">
        <span className="glab-title">Graphics Lab</span>
        <button
          className={`view3d-btn${is3D ? ' view3d-btn-active' : ''}`}
          onClick={() => setIs3D((v) => !v)}
        >
          3D
        </button>
        <div className="segmented glab-presets" aria-label="Graphics Lab location">
          {PRESETS.map((preset) => (
            <button
              key={preset.id}
              className={`seg-btn${presetId === preset.id ? ' seg-btn-active' : ''}`}
              onClick={() => setPresetId(preset.id)}
            >
              {preset.label}
            </button>
          ))}
        </div>
        <span className="glab-hint">Camera synced · layers &amp; quality independent</span>
        <button className="ghost-btn glab-close" onClick={onExit}>
          Close (Esc)
        </button>
      </div>
      <div className="glab-panes">
        <div className="glab-pane">
          <div className="glab-map" ref={cont0} />
          <LayerPanel
            layers={layers0}
            onToggle={toggle0}
            open={open0}
            onToggleOpen={() => setOpen0((o) => !o)}
            activeOverlay={activeOverlayOf(layers0)}
          />
          <QualityPicker value={quality0} onChange={setQuality0} />
          <span className="glab-pane-tag">A · {quality0}</span>
        </div>
        <div className="glab-pane">
          <div className="glab-map" ref={cont1} />
          <LayerPanel
            layers={layers1}
            onToggle={toggle1}
            open={open1}
            onToggleOpen={() => setOpen1((o) => !o)}
            activeOverlay={activeOverlayOf(layers1)}
          />
          <QualityPicker value={quality1} onChange={setQuality1} />
          <span className="glab-pane-tag">B · {quality1}</span>
        </div>
      </div>
    </div>
  );
}
