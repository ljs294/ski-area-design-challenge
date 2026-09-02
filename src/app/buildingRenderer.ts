import { MercatorCoordinate } from 'maplibre-gl';
import type { CustomLayerInterface, CustomRenderMethodInput, Map as MapLibreMap } from 'maplibre-gl';
import {
  generateRectangularGableMesh,
  type BuildingMaterialId,
  type RectangularGableMesh,
} from '../buildingMesh';
import {
  addBuildingLayers,
  BUILDING_BUILT_LAYER_IDS,
  BUILDING_HIT_LAYERS,
  buildingGeoJSON,
  buildingDraftGeoJSON,
  clearBuildingLayers,
  setBuildingCaptureTransient,
  setBuildingData,
  setBuildingDraftData,
  setSelectedBuilding,
  type BuildingDraftMapData,
  type BuildingRenderRecord,
} from './buildingLayers';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution, type MapVisibilityDescriptor } from './mapContribution';

export const BUILDING_CUSTOM_LAYER_ID = 'building-3d';

export interface BuildingDraftRenderData extends BuildingRenderRecord {
  readonly draft?: true;
}

interface GpuBatch {
  readonly data: Float32Array;
  readonly vertexCount: number;
}

const VERTEX_SHADER = `
attribute vec3 a_position;
attribute vec3 a_normal;
attribute vec3 a_origin;
attribute float a_meterScale;
attribute vec4 a_color;
uniform mat4 u_matrix;
varying vec3 v_normal;
varying vec4 v_color;
void main() {
  // Mesh y is north-positive while Mercator y is south-positive.
  vec3 p = vec3(a_origin.x + a_position.x * a_meterScale,
    a_origin.y - a_position.y * a_meterScale,
    a_origin.z + a_position.z * a_meterScale);
  gl_Position = u_matrix * vec4(p, 1.0);
  v_normal = normalize(vec3(a_normal.x, -a_normal.y, a_normal.z));
  v_color = a_color;
}`;

const FRAGMENT_SHADER = `
precision mediump float;
varying vec3 v_normal;
varying vec4 v_color;
void main() {
  vec3 light = normalize(vec3(-0.35, -0.45, 0.82));
  float diffuse = 0.56 + 0.44 * max(dot(normalize(v_normal), light), 0.0);
  gl_FragColor = vec4(v_color.rgb * diffuse, v_color.a);
}`;

function floorElevation(building: BuildingRenderRecord): number {
  const direct = building.finishedFloorElevationM;
  if (direct != null && Number.isFinite(direct)) return direct;
  const foundation = building.foundation as (BuildingRenderRecord['foundation'] & Record<string, unknown>) | undefined;
  const value = foundation?.finishedFloorElevationM ?? foundation?.['finishedFloorM'];
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function meshInput(building: BuildingRenderRecord): Parameters<typeof generateRectangularGableMesh>[0] {
  const foundation = building.foundation;
  const perimeter = foundation?.perimeterGroundElevationsM ?? foundation?.perimeterElevationsM ??
    foundation?.groundElevationsM;
  return {
    lengthM: building.dimensions.lengthM,
    widthM: building.dimensions.widthM,
    eaveHeightM: building.dimensions.eaveHeightM ?? 4.8768,
    bearingDeg: building.bearingDeg ?? 0,
    finishedFloorElevationM: floorElevation(building),
    perimeterGroundElevationsM: perimeter,
  };
}

function geometryKey(building: BuildingRenderRecord): string {
  const foundation = building.foundation;
  return [
    building.id, building.center[0], building.center[1], building.bearingDeg ?? 0,
    building.dimensions.lengthM, building.dimensions.widthM, building.dimensions.eaveHeightM ?? 4.8768,
    floorElevation(building),
    ...(foundation?.perimeterGroundElevationsM ?? foundation?.perimeterElevationsM ?? foundation?.groundElevationsM ?? []),
  ].join('|');
}

function colorFor(mesh: RectangularGableMesh, material: BuildingMaterialId): readonly [number, number, number, number] {
  return mesh.materials[material].color;
}

function createBatch(
  buildings: readonly BuildingRenderRecord[],
): GpuBatch {
  const values: number[] = [];
  let vertexCount = 0;
  for (const building of buildings) {
    const mesh = generateRectangularGableMesh(meshInput(building));
    const anchor = MercatorCoordinate.fromLngLat(
      { lng: building.center[0], lat: building.center[1] }, floorElevation(building),
    );
    const meterScale = anchor.meterInMercatorCoordinateUnits();
    const materialByVertex = new Array<BuildingMaterialId>(mesh.vertices.length / 3).fill('wall');
    for (const group of mesh.groups) {
      for (let index = group.start; index < group.start + group.count; index += 1) {
        materialByVertex[index] = group.material;
      }
    }
    for (let index = 0; index < mesh.vertices.length / 3; index += 1) {
      const offset = index * 3;
      const normalOffset = offset;
      const color = colorFor(mesh, materialByVertex[index]);
      values.push(
        mesh.vertices[offset], mesh.vertices[offset + 1], mesh.vertices[offset + 2],
        mesh.normals[normalOffset], mesh.normals[normalOffset + 1], mesh.normals[normalOffset + 2],
        anchor.x, anchor.y, anchor.z, meterScale,
        color[0], color[1], color[2], color[3],
      );
    }
    vertexCount += mesh.vertices.length / 3;
  }
  return { data: Float32Array.from(values), vertexCount };
}

function compileShader(
  gl: WebGLRenderingContext | WebGL2RenderingContext,
  kind: number,
  source: string,
): WebGLShader {
  const shader = gl.createShader(kind);
  if (!shader) throw new Error('Unable to allocate building shader.');
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(shader) ?? 'unknown shader error';
    gl.deleteShader(shader);
    throw new Error(`Unable to compile building shader: ${log}`);
  }
  return shader;
}

function linkProgram(
  gl: WebGLRenderingContext | WebGL2RenderingContext,
  vertex: WebGLShader,
  fragment: WebGLShader,
): WebGLProgram {
  const program = gl.createProgram();
  if (!program) throw new Error('Unable to allocate building program.');
  gl.attachShader(program, vertex);
  gl.attachShader(program, fragment);
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const log = gl.getProgramInfoLog(program) ?? 'unknown link error';
    gl.deleteProgram(program);
    throw new Error(`Unable to link building program: ${log}`);
  }
  return program;
}

type VertexArrayApi = {
  createVertexArray(): WebGLVertexArrayObject | null;
  bindVertexArray(array: WebGLVertexArrayObject | null): void;
  deleteVertexArray(array: WebGLVertexArrayObject | null): void;
};

function vertexArrayApi(gl: WebGLRenderingContext | WebGL2RenderingContext): VertexArrayApi | null {
  if ('createVertexArray' in gl) return gl as unknown as VertexArrayApi;
  const extension = gl.getExtension('OES_vertex_array_object') as {
    createVertexArrayOES(): WebGLVertexArrayObject | null;
    bindVertexArrayOES(array: WebGLVertexArrayObject | null): void;
    deleteVertexArrayOES(array: WebGLVertexArrayObject | null): void;
  } | null;
  if (!extension) return null;
  return {
    createVertexArray: () => extension.createVertexArrayOES(),
    bindVertexArray: (array) => extension.bindVertexArrayOES(array),
    deleteVertexArray: (array) => extension.deleteVertexArrayOES(array),
  };
}

/**
 * One native WebGL layer for every player building. Meshes are flattened into
 * one interleaved VBO and rendered with one draw call; camera frames never
 * regenerate the CPU mesh or GPU buffer.
 */
export class BuildingBatchRenderer implements CustomLayerInterface {
  readonly id = BUILDING_CUSTOM_LAYER_ID;
  readonly type = 'custom' as const;
  readonly renderingMode = '3d' as const;

  private buildings: readonly BuildingRenderRecord[] = [];
  private buildingsKey = '';
  private draft: BuildingDraftRenderData | null = null;
  private draftKey = '';
  private dirty = true;
  private visible = true;
  private captureHidden = false;
  private map: MapLibreMap | null = null;
  private program: WebGLProgram | null = null;
  private vertexShader: WebGLShader | null = null;
  private fragmentShader: WebGLShader | null = null;
  private buffer: WebGLBuffer | null = null;
  private vao: WebGLVertexArrayObject | null = null;
  private vaoApi: VertexArrayApi | null = null;
  private vertexCount = 0;
  private locations: {
    position: number; normal: number; origin: number; meterScale: number; color: number;
    matrix: WebGLUniformLocation | null;
  } | null = null;

  setBuildings(buildings: readonly BuildingRenderRecord[]): void {
    this.buildings = buildings;
    const key = buildings.map(geometryKey).join('||');
    if (key === this.buildingsKey) return;
    this.buildingsKey = key;
    this.dirty = true;
    this.map?.triggerRepaint();
  }

  setDraft(draft: BuildingDraftRenderData | null): void {
    this.draft = draft;
    const key = draft ? geometryKey(draft) : '';
    if (key === this.draftKey) return;
    this.draftKey = key;
    this.dirty = true;
    this.map?.triggerRepaint();
  }

  setVisible(visible: boolean): void {
    this.visible = visible;
    this.map?.triggerRepaint();
  }

  setCaptureTransient(hidden: boolean): void {
    if (hidden === this.captureHidden) return;
    this.captureHidden = hidden;
    this.dirty = true;
    this.map?.triggerRepaint();
  }

  getMeshCount(): number { return this.buildings.length; }
  getVertexCount(): number { return this.vertexCount; }

  onAdd(map: MapLibreMap, gl: WebGLRenderingContext | WebGL2RenderingContext): void {
    this.map = map;
    this.dirty = true;
    this.vertexShader = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER);
    this.fragmentShader = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER);
    this.program = linkProgram(gl, this.vertexShader, this.fragmentShader);
    this.buffer = gl.createBuffer();
    this.vaoApi = vertexArrayApi(gl);
    this.vao = this.vaoApi?.createVertexArray() ?? null;
    this.locations = {
      position: gl.getAttribLocation(this.program, 'a_position'),
      normal: gl.getAttribLocation(this.program, 'a_normal'),
      origin: gl.getAttribLocation(this.program, 'a_origin'),
      meterScale: gl.getAttribLocation(this.program, 'a_meterScale'),
      color: gl.getAttribLocation(this.program, 'a_color'),
      matrix: gl.getUniformLocation(this.program, 'u_matrix'),
    };
    if (this.buffer) this.uploadBatch(gl);
  }

  private uploadBatch(gl: WebGLRenderingContext | WebGL2RenderingContext): void {
    if (!this.buffer || !this.program || !this.locations) return;
    const records = this.captureHidden ? this.buildings : [...this.buildings, ...(this.draft ? [this.draft] : [])];
    const batch = createBatch(records);
    this.vertexCount = batch.vertexCount;
    const previousArrayBuffer = typeof gl.getParameter === 'function'
      ? gl.getParameter(gl.ARRAY_BUFFER_BINDING) as WebGLBuffer | null : null;
    const vaoBindingEnum = (gl as unknown as { VERTEX_ARRAY_BINDING?: number }).VERTEX_ARRAY_BINDING;
    const previousVao = vaoBindingEnum != null && typeof gl.getParameter === 'function'
      ? gl.getParameter(vaoBindingEnum) as WebGLVertexArrayObject | null : null;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
    gl.bufferData(gl.ARRAY_BUFFER, batch.data, gl.STATIC_DRAW);
    const stride = 14 * Float32Array.BYTES_PER_ELEMENT;
    this.vaoApi?.bindVertexArray(this.vao);
    gl.enableVertexAttribArray(this.locations.position);
    gl.vertexAttribPointer(this.locations.position, 3, gl.FLOAT, false, stride, 0);
    gl.enableVertexAttribArray(this.locations.normal);
    gl.vertexAttribPointer(this.locations.normal, 3, gl.FLOAT, false, stride, 3 * 4);
    gl.enableVertexAttribArray(this.locations.origin);
    gl.vertexAttribPointer(this.locations.origin, 3, gl.FLOAT, false, stride, 6 * 4);
    gl.enableVertexAttribArray(this.locations.meterScale);
    gl.vertexAttribPointer(this.locations.meterScale, 1, gl.FLOAT, false, stride, 9 * 4);
    gl.enableVertexAttribArray(this.locations.color);
    gl.vertexAttribPointer(this.locations.color, 4, gl.FLOAT, false, stride, 10 * 4);
    this.vaoApi?.bindVertexArray(previousVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, previousArrayBuffer);
    this.dirty = false;
  }

  render(gl: WebGLRenderingContext | WebGL2RenderingContext, options: CustomRenderMethodInput): void {
    if (!this.visible || !this.program || !this.buffer || !this.locations || this.vertexCount === 0) return;
    if (this.dirty) this.uploadBatch(gl);
    if (this.vertexCount === 0) return;

    const previousProgram = typeof gl.getParameter === 'function' ? gl.getParameter(gl.CURRENT_PROGRAM) as WebGLProgram | null : null;
    const previousArrayBuffer = typeof gl.getParameter === 'function' ? gl.getParameter(gl.ARRAY_BUFFER_BINDING) as WebGLBuffer | null : null;
    const vaoBindingEnum = (gl as unknown as { VERTEX_ARRAY_BINDING?: number }).VERTEX_ARRAY_BINDING;
    const previousVao = vaoBindingEnum != null && typeof gl.getParameter === 'function'
      ? gl.getParameter(vaoBindingEnum) as WebGLVertexArrayObject | null : null;
    const previousDepth = typeof gl.isEnabled === 'function' ? gl.isEnabled(gl.DEPTH_TEST) : false;
    const previousBlend = typeof gl.isEnabled === 'function' ? gl.isEnabled(gl.BLEND) : false;
    const previousCull = typeof gl.isEnabled === 'function' ? gl.isEnabled(gl.CULL_FACE) : false;
    gl.useProgram(this.program);
    gl.uniformMatrix4fv(this.locations.matrix, false, options.modelViewProjectionMatrix);
    gl.enable(gl.DEPTH_TEST);
    gl.enable(gl.CULL_FACE);
    gl.disable(gl.BLEND);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
    this.vaoApi?.bindVertexArray(this.vao);
    gl.drawArrays(gl.TRIANGLES, 0, this.vertexCount);
    this.vaoApi?.bindVertexArray(previousVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, previousArrayBuffer);
    gl.useProgram(previousProgram);
    if (previousDepth) gl.enable(gl.DEPTH_TEST); else gl.disable(gl.DEPTH_TEST);
    if (previousBlend) gl.enable(gl.BLEND); else gl.disable(gl.BLEND);
    if (previousCull) gl.enable(gl.CULL_FACE); else gl.disable(gl.CULL_FACE);
  }

  onRemove(_map: MapLibreMap, gl: WebGLRenderingContext | WebGL2RenderingContext): void {
    this.vaoApi?.bindVertexArray(null);
    if (this.vao) this.vaoApi?.deleteVertexArray(this.vao);
    if (this.buffer) gl.deleteBuffer(this.buffer);
    if (this.program) gl.deleteProgram(this.program);
    if (this.vertexShader) gl.deleteShader(this.vertexShader);
    if (this.fragmentShader) gl.deleteShader(this.fragmentShader);
    this.map = null;
    this.vao = null;
    this.vaoApi = null;
    this.buffer = null;
    this.program = null;
    this.vertexShader = null;
    this.fragmentShader = null;
    this.locations = null;
    this.vertexCount = 0;
    this.dirty = true;
  }
}

export interface BuildingContributionOptions {
  readonly getBuildings: () => readonly BuildingRenderRecord[];
  readonly getSelectedId?: () => string | null;
  readonly getDraft?: () => BuildingDraftMapData | null;
  readonly structuresVisible?: () => boolean;
  readonly setSelected?: (id: string) => void;
  readonly synchronizeMap?: () => void;
}

function draftToRenderData(draft: BuildingDraftMapData): BuildingDraftRenderData {
  return {
    id: 'building:draft',
    draft: true,
    center: draft.center,
    bearingDeg: draft.bearingDeg,
    dimensions: {
      lengthM: draft.lengthM,
      widthM: draft.widthM,
      eaveHeightM: draft.eaveHeightM ?? 4.8768,
    },
    finishedFloorElevationM: draft.finishedFloorElevationM,
    foundation: draft.perimeterGroundElevationsM ? {
      perimeterElevationsM: draft.perimeterGroundElevationsM,
    } : undefined,
  };
}

/** Managed-map adapter kept separate so controllers can own the data source. */
export function createBuildingContribution(options: BuildingContributionOptions): ManagedMapContribution {
  const renderer = new BuildingBatchRenderer();
  let captureDraft: BuildingDraftMapData | null = null;
  let captureActive = false;
  let descriptorVisible = true;
  return {
    id: 'building',
    zOrder: MAP_Z_ORDER.building,
    hits: [{ id: 'building', priority: MAP_HIT_RANK.building, layerIds: [...BUILDING_HIT_LAYERS],
      select: (id) => options.setSelected?.(id) }],
    install: ({ map }) => {
      descriptorVisible = options.structuresVisible?.() !== false;
      renderer.setVisible(descriptorVisible);
      addBuildingLayers(map);
      if (!map.getLayer(BUILDING_CUSTOM_LAYER_ID)) map.addLayer(renderer);
    },
    synchronizeData: ({ map }) => {
      const buildings = options.getBuildings();
      const selected = options.getSelectedId?.() ?? null;
      const draft = options.getDraft?.() ?? null;
      renderer.setBuildings(buildings);
      renderer.setDraft(draft ? draftToRenderData(draft) : null);
      setBuildingData(map, buildings, selected);
      setBuildingDraftData(map, draft);
      setSelectedBuilding(map, selected);
    },
    visibility: (): MapVisibilityDescriptor[] => options.structuresVisible?.() !== false ? [{
      id: 'buildings', label: 'Player buildings', layerIds: [...BUILDING_BUILT_LAYER_IDS],
      visible: true, section: 'Structures',
    }] : [],
    visibilityChanged: ({ map }, descriptorId, visible) => {
      if (descriptorId !== 'buildings') return;
      descriptorVisible = visible;
      renderer.setVisible(visible);
      for (const id of BUILDING_BUILT_LAYER_IDS) {
        if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', visible ? 'visible' : 'none');
      }
    },
    presentationChanged: (_context, mode) => renderer.setVisible(descriptorVisible && mode === null),
    setCaptureTransient: ({ map }, hidden) => {
      const draft = options.getDraft?.() ?? null;
      if (hidden && !captureActive) {
        captureDraft = draft;
        captureActive = true;
      }
      setBuildingCaptureTransient(map, hidden, draft);
      renderer.setCaptureTransient(hidden);
      renderer.setDraft(hidden ? null : (captureDraft ? draftToRenderData(captureDraft) : null));
      if (!hidden) {
        captureDraft = null;
        captureActive = false;
      }
    },
    cleanup: ({ map }) => {
      if (map.getLayer(BUILDING_CUSTOM_LAYER_ID)) map.removeLayer(BUILDING_CUSTOM_LAYER_ID);
      clearBuildingLayers(map);
    },
  };
}

export function buildingRenderSnapshot(
  buildings: readonly BuildingRenderRecord[],
  selectedId: string | null = null,
): { meshCount: number; footprint: GeoJSON.FeatureCollection; draft: GeoJSON.FeatureCollection } {
  return { meshCount: buildings.length, footprint: buildingGeoJSON(buildings, selectedId), draft: buildingDraftGeoJSON(null) };
}
