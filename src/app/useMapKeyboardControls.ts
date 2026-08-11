import { useEffect, useRef, type Dispatch, type RefObject, type SetStateAction } from 'react';
import type maplibregl from 'maplibre-gl';
import type { Keybinds } from '../keybinds';
import { isTypingTarget, normalizeKey } from '../keybinds';
import type { DashboardKind } from './MountainDashboards';

const PAN_SPEED_PX_S = 900;
const ROTATE_SPEED_DEG_S = 90;
const PITCH_SPEED_DEG_S = 60;

interface MapKeyboardControlsOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  suspended: boolean;
  keybinds: Keybinds;
  activeDashboard: DashboardKind | null;
  toggle3D(): void;
  setActiveDashboard: Dispatch<SetStateAction<DashboardKind | null>>;
}

/** Global map-camera and dashboard shortcuts, backed by commit-timed live refs. */
export function useMapKeyboardControls(options: MapKeyboardControlsOptions): void {
  const optionsRef = useRef(options);
  useEffect(() => { optionsRef.current = options; }, [options]);
  const heldRef = useRef<Set<string>>(new Set());
  const rafIdRef = useRef<number | null>(null);
  const lastFrameRef = useRef<number | null>(null);

  useEffect(() => {
    const heldKeys = heldRef.current;
    function stepFrame(timestamp: number) {
      rafIdRef.current = null;
      if (!heldKeys.size) { lastFrameRef.current = null; return; }
      const last = lastFrameRef.current ?? timestamp;
      const elapsed = Math.min(0.1, Math.max(0, (timestamp - last) / 1000));
      lastFrameRef.current = timestamp;
      const map = optionsRef.current.mapRef.current;
      if (map) {
        const keys = optionsRef.current.keybinds;
        let dx = 0, dy = 0;
        if (heldKeys.has(keys.panForward)) dy -= 1;
        if (heldKeys.has(keys.panBackward)) dy += 1;
        if (heldKeys.has(keys.panLeft)) dx -= 1;
        if (heldKeys.has(keys.panRight)) dx += 1;
        if (dx || dy) { const length = Math.hypot(dx, dy);
          map.panBy([(dx / length) * PAN_SPEED_PX_S * elapsed,
            (dy / length) * PAN_SPEED_PX_S * elapsed], { animate: false }); }
        let bearing = 0;
        if (heldKeys.has(keys.rotateLeft)) bearing -= ROTATE_SPEED_DEG_S * elapsed;
        if (heldKeys.has(keys.rotateRight)) bearing += ROTATE_SPEED_DEG_S * elapsed;
        if (bearing) map.setBearing(map.getBearing() + bearing);
        let pitch = 0;
        if (heldKeys.has(keys.tiltUp)) pitch += PITCH_SPEED_DEG_S * elapsed;
        if (heldKeys.has(keys.tiltDown)) pitch -= PITCH_SPEED_DEG_S * elapsed;
        if (pitch) { const next = Math.min(map.getMaxPitch(), Math.max(0, map.getPitch() + pitch));
          if (next !== map.getPitch()) map.setPitch(next); }
      }
      if (heldKeys.size) rafIdRef.current = requestAnimationFrame(stepFrame);
    }
    const continuousKeys = () => { const keys = optionsRef.current.keybinds; return [
      keys.panForward, keys.panBackward, keys.panLeft, keys.panRight,
      keys.rotateLeft, keys.rotateRight, keys.tiltUp, keys.tiltDown,
    ]; };
    const onKeyDown = (event: KeyboardEvent) => {
      const current = optionsRef.current;
      if (current.suspended || isTypingTarget(document.activeElement)) return;
      const key = normalizeKey(event.key);
      if (!continuousKeys().includes(key)) return;
      const wasEmpty = !heldKeys.size; heldKeys.add(key);
      if (wasEmpty && rafIdRef.current === null) { lastFrameRef.current = null;
        rafIdRef.current = requestAnimationFrame(stepFrame); }
    };
    const onKeyUp = (event: KeyboardEvent) => { heldKeys.delete(normalizeKey(event.key)); };
    window.addEventListener('keydown', onKeyDown); window.addEventListener('keyup', onKeyUp);
    return () => { window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      if (rafIdRef.current !== null) cancelAnimationFrame(rafIdRef.current); heldKeys.clear(); };
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const current = optionsRef.current;
      if (current.suspended || isTypingTarget(document.activeElement)) return;
      const key = normalizeKey(event.key), keys = current.keybinds;
      if (key === keys.snapNorth) {
        current.mapRef.current?.easeTo({ bearing: 0, duration: 300 });
      } else if (key === keys.toggleView3D) {
        current.toggle3D();
      } else if (key === keys.openTrailsDashboard) {
        current.setActiveDashboard((value) => value === 'trails' ? null : 'trails');
      } else if (key === keys.openSnowmakingDashboard) {
        current.setActiveDashboard((value) => value === 'snowmaking' ? null : 'snowmaking');
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);
}
