import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import type { DesignCameraState, DesignSaveBundle } from '../../src/types/designSave';
import { useSettings } from '../../src/app/SettingsContext';
import { intersectTerrainRay } from './terrainPicking';
import { createTerrainScene, type TerrainSceneResult, type ThreeFeatureSelection } from './terrainScene';
import { createLocalTerrainFrame } from './terrainSurface';

export type ThreeAnalysisOverlay = 'none' | 'contours' | 'imagery';
export interface ThreeViewportHandle { toggleOverhead(): void; resetView(): void }

export const ThreeViewport = forwardRef<ThreeViewportHandle, {
  bundle: DesignSaveBundle;
  overlay: ThreeAnalysisOverlay;
  selected: ThreeFeatureSelection | null;
  onSelect(selection: ThreeFeatureSelection | null): void;
  onCameraChange(camera: DesignCameraState): void;
  onReady?(): void;
}>(function ThreeViewport({ bundle, overlay, selected, onSelect, onCameraChange, onReady }, forwardedRef) {
  const hostRef = useRef<HTMLDivElement>(null);
  const labelRef = useRef<HTMLDivElement>(null);
  const controlRef = useRef<{ toggleOverhead(): void; resetView(): void } | null>(null);
  const worldRef = useRef<TerrainSceneResult | null>(null);
  const { resolvedTheme, settings } = useSettings();
  useImperativeHandle(forwardedRef, () => ({
    toggleOverhead: () => controlRef.current?.toggleOverhead(),
    resetView: () => controlRef.current?.resetView(),
  }), []);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const { save, terrain: record } = bundle;
    const frame = createLocalTerrainFrame(record), span = Math.max(frame.widthM, frame.depthM);
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(resolvedTheme === 'dark' ? '#536b77' : '#9fbcc8');
    scene.fog = new THREE.FogExp2(scene.background, 0.000025);
    const camera = new THREE.PerspectiveCamera(42, 1, 1, span * 8);
    const renderer = new THREE.WebGLRenderer({ antialias: settings.renderQuality !== 'performance',
      powerPreference: 'high-performance' });
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.setPixelRatio(Math.min(devicePixelRatio,
      settings.renderQuality === 'high' || settings.renderQuality === 'ultra' ? 1.75 : 1.35));
    host.append(renderer.domElement);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = !settings.reducedMotion; controls.maxPolarAngle = Math.PI * .495;
    controls.minDistance = span * .06; controls.maxDistance = span * 3;
    const initialCenter = frame.toLocal(save.camera.center[0], save.camera.center[1]);
    controls.target.set(initialCenter[0], initialCenter[1], initialCenter[2]);
    const restoreDistance = THREE.MathUtils.clamp(span * Math.pow(2, (14 - save.camera.zoom) / 2),
      controls.minDistance, controls.maxDistance);
    const restoredPitch = save.camera.is3D ? THREE.MathUtils.degToRad(save.camera.pitch || 45) : 0;
    const restoredBearing = THREE.MathUtils.degToRad(save.camera.bearing);
    const horizontal = Math.sin(restoredPitch) * restoreDistance;
    camera.position.set(controls.target.x + Math.sin(restoredBearing) * horizontal,
      controls.target.y + Math.cos(restoredPitch) * restoreDistance,
      controls.target.z + Math.cos(restoredBearing) * horizontal + (save.camera.is3D ? 0 : .001));
    controls.update();
    const world = createTerrainScene(save, record, frame); worldRef.current = world; scene.add(world.root);
    scene.add(new THREE.HemisphereLight(0xe8f3ff, 0x44503d, resolvedTheme === 'dark' ? 1.35 : 1.7));
    const sun = new THREE.DirectionalLight(0xfff2d4, 2.2);
    sun.position.set(-span, span * 1.6, span * .5); scene.add(sun);
    const raycaster = new THREE.Raycaster(); raycaster.params.Line = { threshold: 18 };
    const pointer = new THREE.Vector2();
    let animation = 0, overhead = !save.camera.is3D, hoverPoint: THREE.Vector3 | null = null;
    const emitCamera = () => {
      const center = frame.toLngLat(controls.target.x, controls.target.z);
      const distance = camera.position.distanceTo(controls.target);
      const offset = camera.position.clone().sub(controls.target);
      onCameraChange({ center, zoom: 14 - 2 * Math.log2(Math.max(.001, distance / span)),
        bearing: THREE.MathUtils.radToDeg(Math.atan2(offset.x, offset.z)),
        pitch: overhead ? 0 : THREE.MathUtils.radToDeg(Math.acos(THREE.MathUtils.clamp(offset.y / distance, -1, 1))),
        is3D: !overhead });
    };
    const resetView = () => {
      overhead = false; controls.target.set(0, 0, 0);
      camera.position.set(span * .58, span * .72, span * .72); camera.up.set(0, 1, 0);
      controls.update(); emitCamera();
    };
    const toggleOverhead = () => {
      overhead = !overhead;
      if (overhead) {
        const distance = camera.position.distanceTo(controls.target);
        camera.position.set(controls.target.x, controls.target.y + distance, controls.target.z + .001);
        camera.up.set(0, 0, -1);
      } else {
        const distance = camera.position.distanceTo(controls.target);
        camera.position.set(controls.target.x + distance * .55, controls.target.y + distance * .68,
          controls.target.z + distance * .48); camera.up.set(0, 1, 0);
      }
      camera.lookAt(controls.target); controls.update(); emitCamera();
    };
    controlRef.current = { toggleOverhead, resetView };
    controls.addEventListener('end', emitCamera);
    const updatePointer = (event: PointerEvent) => {
      const rect = renderer.domElement.getBoundingClientRect();
      pointer.set((event.clientX - rect.left) / rect.width * 2 - 1,
        -((event.clientY - rect.top) / rect.height) * 2 + 1);
      raycaster.setFromCamera(pointer, camera);
    };
    const moveLabel = (point: THREE.Vector3 | null) => {
      const label = labelRef.current;
      if (!label || !point) { if (label) label.hidden = true; return; }
      const projected = point.clone().project(camera), rect = renderer.domElement.getBoundingClientRect();
      label.hidden = projected.z < -1 || projected.z > 1;
      label.style.transform = `translate(${(projected.x * .5 + .5) * rect.width + 12}px, ${(-projected.y * .5 + .5) * rect.height - 12}px)`;
      const [lng, lat] = frame.toLngLat(point.x, point.z);
      label.textContent = `${lat.toFixed(5)}, ${lng.toFixed(5)}`;
    };
    const pointerMove = (event: PointerEvent) => {
      updatePointer(event);
      const hit = intersectTerrainRay(raycaster.ray, record, frame);
      hoverPoint = hit ? new THREE.Vector3(...hit.point) : null; moveLabel(hoverPoint);
    };
    const click = (event: PointerEvent) => {
      updatePointer(event);
      const priority = { node: 0, junction: 0, path: 1, lift: 2, trail: 3 } as const;
      const hit = raycaster.intersectObjects(world.pickables, false).sort((left, right) => {
        const leftFeature = left.object.userData.selection as ThreeFeatureSelection;
        const rightFeature = right.object.userData.selection as ThreeFeatureSelection;
        return priority[leftFeature.kind] - priority[rightFeature.kind] || left.distance - right.distance;
      })[0];
      onSelect((hit?.object.userData.selection as ThreeFeatureSelection | undefined) ?? null);
    };
    renderer.domElement.addEventListener('pointermove', pointerMove);
    renderer.domElement.addEventListener('click', click);
    const resize = () => {
      const width = host.clientWidth, height = host.clientHeight;
      renderer.setSize(width, height, false); camera.aspect = width / Math.max(1, height); camera.updateProjectionMatrix();
    };
    const observer = new ResizeObserver(resize); observer.observe(host); resize();
    const render = () => { controls.update(); if (hoverPoint) moveLabel(hoverPoint); renderer.render(scene, camera);
      animation = requestAnimationFrame(render); };
    animation = requestAnimationFrame(render); onReady?.();
    return () => {
      cancelAnimationFrame(animation); observer.disconnect(); controls.removeEventListener('end', emitCamera);
      controls.dispose(); renderer.domElement.removeEventListener('pointermove', pointerMove);
      renderer.domElement.removeEventListener('click', click); world.dispose(); renderer.dispose(); renderer.domElement.remove();
      controlRef.current = null; if (worldRef.current === world) worldRef.current = null;
    };
  }, [bundle, onCameraChange, onReady, onSelect, resolvedTheme, settings.reducedMotion, settings.renderQuality]);

  useEffect(() => {
    const world = worldRef.current;
    if (!world) return;
    world.contours.visible = overlay === 'contours';
    world.imagery.visible = overlay === 'imagery';
    for (const object of world.pickables) {
      const feature = object.userData.selection as ThreeFeatureSelection | undefined;
      const active = !!feature && feature.kind === selected?.kind && feature.id === selected.id;
      const material = object instanceof THREE.Line || object instanceof THREE.Mesh ? object.material : null;
      const first = Array.isArray(material) ? material[0] : material;
      if (first && 'color' in first && first.color instanceof THREE.Color) {
        first.color.setHex(active ? 0xfff176 : Number(object.userData.baseColor ?? 0xffffff));
      }
    }
  }, [overlay, selected]);

  return <div className="three-world" ref={hostRef} data-overlay={overlay}
    data-selected={selected ? `${selected.kind}:${selected.id}` : ''}>
    <div className="three-world-label" ref={labelRef} hidden />
  </div>;
});
