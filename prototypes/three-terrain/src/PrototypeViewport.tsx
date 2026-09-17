import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import type { TerrainRecord } from '../../../src/types/terrain';
import { createTerrainMeshes, type TerrainMeshStats } from './terrainMesh';
import { intersectTerrainRay } from './terrainPicking';
import { createLocalTerrainFrame } from './terrainSurface';

type Tool = 'inspect' | 'lift' | 'trail';
type Point = { local: [number, number, number]; lngLat: [number, number] };
export interface PrototypeMetrics extends TerrainMeshStats {
  frameP95Ms: number; pickP95Ms: number; picks: number; strokes: number;
}

function percentile(values: number[], fraction: number): number {
  if (!values.length) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * fraction))]!;
}

function line(points: Point[], color: number, width = 1): THREE.Line {
  const geometry = new THREE.BufferGeometry().setFromPoints(points.map((point) => new THREE.Vector3(...point.local)));
  return new THREE.Line(geometry, new THREE.LineBasicMaterial({ color, linewidth: width,
    depthTest: false, transparent: true, opacity: 0.95 }));
}

function handle(point: Point, color: number): THREE.Mesh {
  const geometry = new THREE.SphereGeometry(10, 16, 10);
  const material = new THREE.MeshBasicMaterial({ color, depthTest: false });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.set(...point.local);
  mesh.renderOrder = 20;
  return mesh;
}

export function PrototypeViewport({ record, onMetrics }: {
  record: TerrainRecord; onMetrics(metrics: PrototypeMetrics): void;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const labelRef = useRef<HTMLDivElement>(null);
  const toolRef = useRef<Tool>('inspect');
  const cancelGestureRef = useRef<() => void>(() => {});
  const [tool, setTool] = useState<Tool>('inspect');
  const [status, setStatus] = useState('Move over the terrain to inspect the analytical surface.');
  useEffect(() => { toolRef.current = tool; }, [tool]);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const frame = createLocalTerrainFrame(record);
    const scene = new THREE.Scene();
    scene.background = new THREE.Color('#9fbcc8');
    scene.fog = new THREE.FogExp2('#b7cbd0', 0.000025);
    const camera = new THREE.PerspectiveCamera(42, 1, 1, Math.max(frame.widthM, frame.depthM) * 8);
    const span = Math.max(frame.widthM, frame.depthM);
    camera.position.set(span * 0.58, span * 0.72, span * 0.72);
    const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5));
    renderer.shadowMap.enabled = true;
    host.append(renderer.domElement);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.set(0, 0, 0); controls.enableDamping = true; controls.maxPolarAngle = Math.PI * 0.495;
    controls.minDistance = span * 0.08; controls.maxDistance = span * 3;
    const terrain = createTerrainMeshes(record, frame);
    scene.add(terrain.group);
    scene.add(new THREE.HemisphereLight(0xe8f3ff, 0x44503d, 1.7));
    const sun = new THREE.DirectionalLight(0xfff2d4, 2.3);
    sun.position.set(-span, span * 1.6, span * 0.5); scene.add(sun);
    const grid = new THREE.GridHelper(span * 1.4, 12, 0xffffff, 0xffffff);
    grid.material.opacity = 0.08; grid.material.transparent = true; scene.add(grid);
    const overlay = new THREE.Group(); scene.add(overlay);
    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    const frameTimes: number[] = [], pickTimes: number[] = [];
    let lastFrame: number | null = null, animation = 0, pointerCaptured = false, capturedPointerId: number | null = null;
    let hover: Point | null = null, liftStart: Point | null = null, trail: Point[] = [];
    const anchors: Point[] = [];
    let preview: THREE.Object3D | null = null, hoverHandle: THREE.Object3D | null = null;
    let picks = 0, strokes = 0, metricsAt = 0;

    const disposeObject = (object: THREE.Object3D | null) => {
      if (!object) return;
      object.removeFromParent();
      object.traverse((child) => {
        if (!(child instanceof THREE.Mesh || child instanceof THREE.Line)) return;
        child.geometry.dispose();
        const materials = Array.isArray(child.material) ? child.material : [child.material];
        for (const material of materials) material.dispose();
      });
    };
    const hitAt = (event: PointerEvent): Point | null => {
      const start = performance.now();
      const rect = renderer.domElement.getBoundingClientRect();
      pointer.set((event.clientX - rect.left) / rect.width * 2 - 1,
        -((event.clientY - rect.top) / rect.height) * 2 + 1);
      raycaster.setFromCamera(pointer, camera);
      const hit = intersectTerrainRay(raycaster.ray, record, frame);
      pickTimes.push(performance.now() - start); if (pickTimes.length > 600) pickTimes.shift(); picks++;
      return hit ? { local: [hit.point[0], hit.point[1] + 3, hit.point[2]], lngLat: hit.lngLat } : null;
    };
    const refreshPreview = () => {
      disposeObject(preview); preview = null;
      if (toolRef.current === 'lift' && liftStart && hover) preview = line([liftStart, hover], 0xf6c453, 3);
      if (toolRef.current === 'trail' && trail.length > 1) preview = line(trail, 0x42a5f5, 5);
      if (preview) { preview.renderOrder = 15; overlay.add(preview); }
    };
    const moveLabel = (point: Point | null) => {
      const label = labelRef.current;
      if (!label || !point) { if (label) label.hidden = true; return; }
      const projected = new THREE.Vector3(...point.local).project(camera);
      const rect = renderer.domElement.getBoundingClientRect();
      label.hidden = projected.z < -1 || projected.z > 1;
      label.style.transform = `translate(${(projected.x * .5 + .5) * rect.width + 12}px, ${(-projected.y * .5 + .5) * rect.height - 12}px)`;
      label.textContent = `${point.lngLat[1].toFixed(5)}, ${point.lngLat[0].toFixed(5)}`;
    };
    const onPointerMove = (event: PointerEvent) => {
      hover = hitAt(event);
      disposeObject(hoverHandle); hoverHandle = hover ? handle(hover, 0xffffff) : null;
      if (hoverHandle) overlay.add(hoverHandle);
      if (pointerCaptured && toolRef.current === 'trail' && hover) {
        const previous = trail.at(-1);
        if (!previous || Math.hypot(previous.local[0] - hover.local[0], previous.local[2] - hover.local[2]) >= 8)
          trail.push(hover);
      }
      refreshPreview(); moveLabel(hover);
    };
    const onPointerDown = (event: PointerEvent) => {
      if (event.button !== 0 || !hover) return;
      if (toolRef.current === 'trail') {
        const snapped = anchors.reduce<Point | null>((best, candidate) => {
          const distance = Math.hypot(candidate.local[0] - hover!.local[0], candidate.local[2] - hover!.local[2]);
          if (distance > 60) return best;
          if (!best) return candidate;
          const bestDistance = Math.hypot(best.local[0] - hover!.local[0], best.local[2] - hover!.local[2]);
          return distance < bestDistance ? candidate : best;
        }, null);
        if (!snapped) { setStatus('Trail heads require an eligible lift/trail anchor within 60 m.'); return; }
        pointerCaptured = true; capturedPointerId = event.pointerId; trail = [snapped];
        renderer.domElement.setPointerCapture(event.pointerId);
        controls.enabled = false; setStatus('Painting trail — release to finish, Escape to cancel.');
      }
    };
    const onPointerUp = (event: PointerEvent) => {
      if (toolRef.current === 'trail' && pointerCaptured) {
        pointerCaptured = false; capturedPointerId = null; controls.enabled = true;
        renderer.domElement.releasePointerCapture(event.pointerId);
        if (trail.length > 1) {
          const end = trail.at(-1)!;
          const snappedTail = anchors.reduce<Point | null>((best, candidate) => {
            if (candidate === trail[0]) return best;
            const distance = Math.hypot(candidate.local[0] - end.local[0], candidate.local[2] - end.local[2]);
            if (distance > 60) return best;
            if (!best) return candidate;
            return distance < Math.hypot(best.local[0] - end.local[0], best.local[2] - end.local[2]) ? candidate : best;
          }, null);
          if (snappedTail) {
            trail[trail.length - 1] = snappedTail;
            const committed = line(trail, 0x1976d2, 5); committed.renderOrder = 12; overlay.add(committed); strokes++;
            setStatus('Anchored trail captured with bounded 8 m resampling.');
          } else setStatus('Trail tail rejected: no eligible anchor within 60 m.');
        }
        trail = []; refreshPreview();
      } else if (toolRef.current === 'lift' && hover) {
        if (!liftStart) { liftStart = hover; overlay.add(handle(hover, 0xf6c453)); setStatus('Move the cursor; click the second terminal or Escape.'); }
        else { const committed = line([liftStart, hover], 0xe19b24, 4); committed.renderOrder = 12; overlay.add(committed);
          overlay.add(handle(hover, 0xf6c453)); anchors.push(liftStart, hover); liftStart = null; refreshPreview();
          setStatus('Lift entered review; both terminals are eligible 60 m trail anchors.'); }
      }
    };
    const cancel = () => { liftStart = null; trail = []; pointerCaptured = false; controls.enabled = true;
      if (capturedPointerId != null && renderer.domElement.hasPointerCapture(capturedPointerId))
        renderer.domElement.releasePointerCapture(capturedPointerId);
      capturedPointerId = null;
      refreshPreview(); setStatus('Active gesture cancelled.'); };
    cancelGestureRef.current = cancel;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancel();
      if (event.key.toLowerCase() === 'o') {
        camera.position.set(controls.target.x, span * 1.25, controls.target.z + 0.001);
        camera.up.set(0, 0, -1); camera.lookAt(controls.target); controls.update();
        setStatus('Overhead view aligned to local vertical.');
      }
    };
    const resize = () => { const width = host.clientWidth, height = host.clientHeight;
      renderer.setSize(width, height, false); camera.aspect = width / Math.max(1, height); camera.updateProjectionMatrix(); };
    const observer = new ResizeObserver(resize); observer.observe(host); resize();
    renderer.domElement.addEventListener('pointermove', onPointerMove);
    renderer.domElement.addEventListener('pointerdown', onPointerDown);
    renderer.domElement.addEventListener('pointerup', onPointerUp);
    window.addEventListener('keydown', onKey);
    const render = (now: number) => {
      if (lastFrame != null) {
        const interval = now - lastFrame;
        if (interval > 0 && interval < 250) { frameTimes.push(interval); if (frameTimes.length > 600) frameTimes.shift(); }
      }
      lastFrame = now;
      controls.update(); if (hover) moveLabel(hover); renderer.render(scene, camera);
      if (now - metricsAt > 750) { metricsAt = now; onMetrics({ ...terrain.stats,
        frameP95Ms: percentile(frameTimes, .95), pickP95Ms: percentile(pickTimes, .95), picks, strokes }); }
      animation = requestAnimationFrame(render);
    };
    animation = requestAnimationFrame(render);
    return () => {
      cancelAnimationFrame(animation); observer.disconnect(); window.removeEventListener('keydown', onKey);
      renderer.domElement.removeEventListener('pointermove', onPointerMove);
      renderer.domElement.removeEventListener('pointerdown', onPointerDown);
      renderer.domElement.removeEventListener('pointerup', onPointerUp);
      cancelGestureRef.current = () => {};
      controls.dispose(); disposeObject(terrain.group); disposeObject(overlay); renderer.dispose(); renderer.domElement.remove();
    };
  }, [record, onMetrics]);

  return <div className="viewport-shell">
    <div className="prototype-tools" role="toolbar" aria-label="Prototype tools">
      {(['inspect', 'lift', 'trail'] as const).map((item) => <button key={item}
        className={tool === item ? 'active' : ''} onClick={() => { cancelGestureRef.current(); toolRef.current = item;
          setTool(item); setStatus(`${item} tool active.`); }}>{item}</button>)}
      <span>O: overhead · Esc: cancel</span>
    </div>
    <div className="viewport" ref={hostRef}><div className="world-label" ref={labelRef} hidden /></div>
    <div className="status">{status}</div>
  </div>;
}
