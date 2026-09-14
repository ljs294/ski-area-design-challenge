import maplibregl, { type CustomLayerInterface, type CustomRenderMethodInput } from 'maplibre-gl';
import type { GuestRenderPoint } from './guestLayers';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';
import { routeLanePosition, type PreparedRoute } from '../dualClock/geometry';

/** A worker edge's display path, kept outside React state. */
export type GuestRenderPath = readonly (readonly [number, number])[];

const FLOATS_PER_GUEST = 6;
export const GUEST_GPU_BYTES_PER_GUEST = FLOATS_PER_GUEST * Float32Array.BYTES_PER_ELEMENT;
export const GUEST_DOT_DIAMETER_CSS_PX = 9;
const HIT_CELL_SIZE_PX = 24;
const DEFAULT_HIT_RADIUS_PX = 8;

function mercatorXFromLng(lng: number): number {
  return (180 + lng) / 360;
}

function mercatorYFromLat(lat: number): number {
  return (180 - (180 / Math.PI * Math.log(Math.tan(Math.PI / 4 + lat * Math.PI / 360)))) / 360;
}

function lngFromMercatorX(x: number): number {
  return x * 360 - 180;
}

function latFromMercatorY(y: number): number {
  const y2 = 180 - y * 360;
  return 360 / Math.PI * Math.atan(Math.exp(y2 * Math.PI / 180)) - 90;
}

export interface GuestScreenHit {
  readonly id: string;
  readonly x: number;
  readonly y: number;
  readonly statusFlags: number;
  readonly distanceSquared: number;
}

function statusCode(status: string): number {
  if (status === 'scheduled' || status === 'departed') return -1;
  if (status === 'incident' || status === 'patrol-response') return 1;
  if (status === 'skiing') return 2;
  if (status === 'lift-ride') return 3;
  if (status === 'lift-queue') return 4;
  if (status === 'trail-queue') return 4;
  if (status === 'facility-queue' || status === 'facility-service') return 5;
  if (status === 'walking') return 6;
  return 0;
}

function statusCodeFromFlags(flags: number): number {
  if ((flags & (1 | 512)) !== 0) return -1;
  if ((flags & (8_192 | 16_384)) !== 0) return 1;
  if ((flags & 64) !== 0) return 2;
  if ((flags & 32) !== 0) return 3;
  if ((flags & 16) !== 0) return 4;
  if ((flags & (1_024 | 2_048)) !== 0) return 5;
  if ((flags & (2 | 4 | 8 | 256 | 4_096 | 32_768 | 65_536)) !== 0) return 6;
  return 0;
}

function clampUnit(value: number): number { return Math.min(1, Math.max(0, value)); }

function usableRoute(route: PreparedRoute | undefined): route is PreparedRoute {
  return !!route && route.length > 0 && route.lanes.length > 0 && route.lanes[0]!.length > 1
    && route.distances.length === route.lanes[0]!.length;
}

function routeEndpointsJoin(previous: PreparedRoute, next: PreparedRoute, previousLane: number, nextLane: number): boolean {
  const from = routeLanePosition(previous, 1, previousLane), to = routeLanePosition(next, 0, nextLane);
  const scale = 111_320 * Math.cos(((from[1] + to[1]) / 2) * Math.PI / 180);
  return Math.hypot((from[0] - to[0]) * scale, (from[1] - to[1]) * 111_320) <= 5;
}

/** Interpolate along the validated lane, never across a trail bend or polygon hole. */
export function interpolatedMotionPosition(previous: GuestRenderPoint | undefined, next: GuestRenderPoint,
  routes: Readonly<Record<string, PreparedRoute>>, fraction: number): readonly [number, number] {
  const motion = next.motion, prior = previous?.motion, progress = clampUnit(fraction);
  const nextRoute = motion ? routes[motion.routeId] : undefined;
  const priorRoute = prior ? routes[prior.routeId] : undefined;
  if (!motion || !usableRoute(nextRoute) || !prior || !usableRoute(priorRoute)) {
    // A publication can skip a topology transition or arrive before its
    // geometry snapshot. The point supplied by the simulation is authoritative;
    // holding it avoids inventing a straight chord across unrelated terrain.
    return [next.lng, next.lat];
  }
  if (prior.routeId === motion.routeId && prior.lane === motion.lane
    && motion.progress >= prior.progress && motion.progress >= 0 && prior.progress >= 0) {
    return routeLanePosition(nextRoute, prior.progress + (motion.progress - prior.progress) * progress, motion.lane);
  }
  // A new route is interpolable only when the previous publication was at its
  // endpoint and the saved route endpoints meet. Otherwise use the next
  // authoritative point for the whole frame rather than drawing a chord.
  if (prior.progress >= 1 - 1e-3 && motion.progress >= 0 && motion.progress <= 1
    && routeEndpointsJoin(priorRoute, nextRoute, prior.lane, motion.lane)) {
    const remaining = Math.max(0, (1 - prior.progress) * Math.max(0, prior.duration));
    const elapsed = Math.max(0, motion.progress * Math.max(0, motion.duration));
    const boundary = remaining / Math.max(0.000001, remaining + elapsed);
    if (progress < boundary) {
      return routeLanePosition(priorRoute, prior.progress + (1 - prior.progress) * progress / Math.max(0.000001, boundary), prior.lane);
    }
    return routeLanePosition(nextRoute, motion.progress * (progress - boundary) / Math.max(0.000001, 1 - boundary), motion.lane);
  }
  return [next.lng, next.lat];
}

const pathLengths = new WeakMap<GuestRenderPath, Float64Array>();
function pathProgressPosition(path: GuestRenderPath, progress: number, output: Float64Array): void {
  if (path.length === 0) { output[0] = 0; output[1] = 0; return; }
  if (path.length === 1) { output[0] = path[0]![0]; output[1] = path[0]![1]; return; }
  let lengths = pathLengths.get(path);
  if (!lengths) {
    lengths = new Float64Array(path.length);
    for (let index = 1; index < path.length; index += 1) {
      const from = path[index - 1]!, to = path[index]!;
      const latitudeScale = Math.cos(((from[1] + to[1]) / 2) * Math.PI / 180);
      lengths[index] = lengths[index - 1] + Math.hypot((to[0] - from[0]) * latitudeScale, to[1] - from[1]);
    }
    pathLengths.set(path, lengths);
  }
  const total = lengths[lengths.length - 1];
  if (total <= Number.EPSILON) {
    output[0] = path[0]![0]; output[1] = path[0]![1]; return;
  }
  const distance = clampUnit(progress) * total;
  let low = 1, high = lengths.length - 1;
  while (low < high) { const mid = (low + high) >>> 1; if (lengths[mid] < distance) low = mid + 1; else high = mid; }
  const from = path[low - 1], to = path[low], length = lengths[low] - lengths[low - 1];
  const fraction = length <= Number.EPSILON ? 0 : clampUnit((distance - lengths[low - 1]) / length);
  output[0] = from[0] + (to[0] - from[0]) * fraction;
  output[1] = from[1] + (to[1] - from[1]) * fraction;
}

export function guestGpuVertexData(previous: readonly GuestRenderPoint[], next: readonly GuestRenderPoint[]): Float32Array<ArrayBuffer> {
  const previousById = new Map<string, GuestRenderPoint>();
  for (const point of previous) previousById.set(point.id, point);
  const data = new Float32Array(next.length * FLOATS_PER_GUEST);
  next.forEach((point, index) => {
    const from = previousById.get(point.id) ?? point;
    const offset = index * FLOATS_PER_GUEST;
    data[offset] = mercatorXFromLng(from.lng);
    data[offset + 1] = mercatorYFromLat(from.lat);
    data[offset + 2] = mercatorXFromLng(point.lng);
    data[offset + 3] = mercatorYFromLat(point.lat);
    data[offset + 4] = statusCode(point.status);
  });
  return data;
}

function framePosition(frame: GuestSimulationRenderFrame, index: number,
  edgePaths: readonly GuestRenderPath[], portalLngLat: readonly [number, number] | undefined,
  output: Float64Array): void {
  const edge = edgePaths[frame.edgeIndices[index] ?? -1];
  if (edge && edge.length > 0) { pathProgressPosition(edge, frame.progress[index] ?? 0, output); return; }
  output[0] = portalLngLat?.[0] ?? 0;
  output[1] = portalLngLat?.[1] ?? 0;
}

function guestIdFromNumericId(id: number): string {
  return `guest-${String(id).padStart(6, '0')}`;
}

/**
 * Convert two compact worker frames directly to the GPU interleaved buffer.
 * No GuestRenderPoint objects or GeoJSON features are created in this path.
 */
export function guestGpuFrameVertexData(
  previous: GuestSimulationRenderFrame | null,
  next: GuestSimulationRenderFrame,
  edgePaths: readonly GuestRenderPath[],
  portalLngLat?: readonly [number, number],
  previousEdgePaths: readonly GuestRenderPath[] = edgePaths,
  previousPortalLngLat: readonly [number, number] | undefined = portalLngLat,
): Float32Array<ArrayBuffer> {
  // Publications normally retain stable roster order. Avoid rebuilding a
  // 10k-entry index in that common case; topology changes and departures use
  // the sparse map fallback without dropping any dots.
  let previousIndex: Map<number, number> | undefined;
  let aligned = !!previous && previous.ids.length === next.ids.length;
  if (aligned) {
    for (let index = 0; index < next.ids.length; index += 1) {
      if (previous!.ids[index] !== next.ids[index]) { aligned = false; break; }
    }
  }
  if (previous && !aligned) {
    previousIndex = new Map<number, number>();
    for (let index = 0; index < previous.ids.length; index += 1) previousIndex.set(previous.ids[index]!, index);
  }
  const data = new Float32Array(next.ids.length * FLOATS_PER_GUEST);
  const from = new Float64Array(2), to = new Float64Array(2);
  for (let index = 0; index < next.ids.length; index += 1) {
    const prior = aligned ? index : previousIndex?.get(next.ids[index]!);
    framePosition(prior === undefined ? next : previous!, prior === undefined ? index : prior,
      prior === undefined ? edgePaths : previousEdgePaths,
      prior === undefined ? portalLngLat : previousPortalLngLat, from);
    framePosition(next, index, edgePaths, portalLngLat, to);
    const offset = index * FLOATS_PER_GUEST;
    data[offset] = mercatorXFromLng(from[0]!);
    data[offset + 1] = mercatorYFromLat(from[1]!);
    data[offset + 2] = mercatorXFromLng(to[0]!);
    data[offset + 3] = mercatorYFromLat(to[1]!);
    data[offset + 4] = statusCodeFromFlags(next.statusFlags[index] ?? 0);
  }
  return data;
}

/** Refresh the terrain-relative Z value for each guest's current interpolated XY position. */
export function updateGuestTerrainElevations(
  data: Float32Array,
  count: number,
  progress: number,
  elevationAt: (lngLat: [number, number]) => number | null,
): boolean {
  const fraction = clampUnit(progress);
  let changed = false;
  for (let index = 0; index < count; index += 1) {
    const offset = index * FLOATS_PER_GUEST;
    const x = data[offset]! + (data[offset + 2]! - data[offset]!) * fraction;
    const y = data[offset + 1]! + (data[offset + 3]! - data[offset + 1]!) * fraction;
    const position: [number, number] = [lngFromMercatorX(x), latFromMercatorY(y)];
    const sampled = elevationAt(position);
    const elevation = sampled !== null && Number.isFinite(sampled) ? sampled : 0;
    const nextZ = elevation / (2 * Math.PI * 6_371_008.8 * Math.cos(position[1] * Math.PI / 180));
    if (data[offset + 5] !== nextZ) {
      data[offset + 5] = nextZ;
      changed = true;
    }
  }
  return changed;
}

function shader(gl: WebGLRenderingContext | WebGL2RenderingContext, type: number, source: string): WebGLShader {
  const value = gl.createShader(type);
  if (!value) throw new Error('Unable to create guest map shader.');
  gl.shaderSource(value, source); gl.compileShader(value);
  if (!gl.getShaderParameter(value, gl.COMPILE_STATUS)) {
    const message = gl.getShaderInfoLog(value) ?? 'Guest map shader compilation failed.';
    gl.deleteShader(value); throw new Error(message);
  }
  return value;
}

/** MapLibre custom layers receive a matrix for normalized Mercator [0, 1] vertices. */
export function guestLayerProjectionMatrix(options: Pick<CustomRenderMethodInput,
  'defaultProjectionData' | 'modelViewProjectionMatrix'>): Float32Array {
  return options.defaultProjectionData.mainMatrix as unknown as Float32Array;
}

/** GPU-backed point layer. React supplies authoritative frames; MapLibre owns interpolation. */
export class GuestGpuLayer implements CustomLayerInterface {
  readonly id: string;
  readonly type = 'custom' as const;
  readonly renderingMode = '2d' as const;
  private map: maplibregl.Map | null = null;
  private program: WebGLProgram | null = null;
  private buffer: WebGLBuffer | null = null;
  private gl: WebGLRenderingContext | WebGL2RenderingContext | null = null;
  private readonly renderedPositions = new Map<string, readonly [number, number]>();
  private bufferDirty = false;
  private terrainSampleAt = Number.NEGATIVE_INFINITY;
  private readonly terrainSampleIntervalMs = 50;
  private fromAttribute = -1;
  private toAttribute = -1;
  private statusAttribute = -1;
  private zAttribute = -1;
  private matrixUniform: WebGLUniformLocation | null = null;
  private progressUniform: WebGLUniformLocation | null = null;
  private sizeUniform: WebGLUniformLocation | null = null;
  private opacityUniform: WebGLUniformLocation | null = null;
  private count = 0;
  private startedAt = 0;
  private durationMs = 50;
  private revealAt = -Infinity;
  private pending: Float32Array<ArrayBuffer> = new Float32Array(0);
  private compactMode = false;
  private previousFrame: GuestSimulationRenderFrame | null = null;
  private nextFrame: GuestSimulationRenderFrame | null = null;
  private previousEdgePaths: readonly GuestRenderPath[] = [];
  private previousPortalLngLat: readonly [number, number] | undefined;
  private edgePaths: readonly GuestRenderPath[] = [];
  private portalLngLat: readonly [number, number] | undefined;
  private legacyGuestIds: readonly string[] = [];
  private legacyStatusCodes: readonly number[] = [];
  private hitHeads = new Int32Array(0);
  private hitNext = new Int32Array(0);
  private hitXs = new Float32Array(0);
  private hitYs = new Float32Array(0);
  private hitIds = new Uint32Array(0);
  private hitFlags = new Uint32Array(0);
  private hitLegacyIds: string[] = [];
  private hitColumns = 0;
  private hitRows = 0;
  private hitCount = 0;
  private lastHitMatrix: ArrayLike<number> | null = null;
  private lastHitWidth = 0;
  private lastHitHeight = 0;
  private motionRoutes: Readonly<Record<string, PreparedRoute>> = {};
  private motionPrevious = new Map<string, GuestRenderPoint>();
  private motionNext: readonly GuestRenderPoint[] = [];

  constructor(id: string) { this.id = id; }
  setMotionRoutes(routes: Readonly<Record<string, PreparedRoute>>): void { this.motionRoutes = routes; }

  /**
   * Return the last position accepted by the same screen index used for
   * picking. A point outside the drawable viewport or discarded by the shader
   * therefore has no camera target.
   */
  renderedPosition(id: string): readonly [number, number] | null {
    return this.renderedPositions.get(id) ?? null;
  }

  setPoints(previous: readonly GuestRenderPoint[], next: readonly GuestRenderPoint[], durationMs = 50): void {
    this.motionNext = next.some(point => point.motion) ? next : [];
    this.motionPrevious = this.motionNext.length ? new Map(previous.map(point => [point.id, point])) : new Map();
    if (this.motionNext.length && durationMs > 0) durationMs = 100;
    if (this.motionNext.length && !previous.length) this.revealAt = performance.now();
    this.terrainSampleAt = Number.NEGATIVE_INFINITY;
    this.compactMode = false;
    this.previousFrame = null;
    this.nextFrame = null;
    this.previousEdgePaths = [];
    this.previousPortalLngLat = undefined;
    this.legacyGuestIds = next.map((point) => point.id);
    this.legacyStatusCodes = next.map((point) => statusCode(point.status));
    this.pending = guestGpuVertexData(previous, next);
    this.bufferDirty = true;
    this.count = next.length;
    this.startedAt = performance.now();
    this.durationMs = Math.max(0, durationMs);
    this.upload();
    this.map?.triggerRepaint();
  }

  /** Retain two authoritative compact frames; MapLibre interpolates them. */
  setRenderFrame(frame: GuestSimulationRenderFrame | null, edgePaths: readonly GuestRenderPath[],
    portalLngLat?: readonly [number, number], durationMs = 50): void {
    this.motionNext = [];
    if (!frame) {
      this.compactMode = false;
      this.previousFrame = null;
      this.nextFrame = null;
      this.previousEdgePaths = [];
      this.previousPortalLngLat = undefined;
      this.count = 0;
      this.legacyGuestIds = [];
      this.legacyStatusCodes = [];
      this.clearHitIndex();
      this.terrainSampleAt = Number.NEGATIVE_INFINITY;
      this.pending = new Float32Array(0);
      this.bufferDirty = false;
      this.map?.triggerRepaint();
      return;
    }
    this.terrainSampleAt = Number.NEGATIVE_INFINITY;
    this.compactMode = true;
    this.legacyGuestIds = [];
    this.legacyStatusCodes = [];
    this.previousFrame = this.nextFrame;
    this.previousEdgePaths = this.edgePaths;
    this.previousPortalLngLat = this.portalLngLat;
    this.nextFrame = frame;
    this.edgePaths = edgePaths;
    this.portalLngLat = portalLngLat;
    this.pending = guestGpuFrameVertexData(this.previousFrame, frame, edgePaths, portalLngLat,
      this.previousEdgePaths, this.previousPortalLngLat);
    this.bufferDirty = true;
    this.count = frame.ids.length;
    this.startedAt = performance.now();
    this.durationMs = Math.max(0, durationMs);
    if (this.hitCount === 0 && this.lastHitMatrix && this.lastHitWidth > 0 && this.lastHitHeight > 0) {
      this.updateScreenHitIndex(this.lastHitMatrix, this.lastHitWidth, this.lastHitHeight, 1);
    }
    this.upload();
    this.map?.triggerRepaint();
  }

  hasCompactFrame(): boolean { return this.compactMode; }

  /** Return the nearest guest at the interpolated screen position. */
  hitTest(point: { readonly x: number; readonly y: number }, radiusPx = DEFAULT_HIT_RADIUS_PX): GuestScreenHit | null {
    // A compact frame can replace the buffer between MapLibre render passes.
    // Refresh lazily so a click in that gap still sees the current frame.
    if (this.hitCount === 0 && this.lastHitMatrix && this.lastHitWidth > 0 && this.lastHitHeight > 0 && this.count > 0) {
      this.updateScreenHitIndex(this.lastHitMatrix, this.lastHitWidth, this.lastHitHeight, 1);
    }
    if (this.hitCount === 0 || !Number.isFinite(point.x) || !Number.isFinite(point.y)
      || !Number.isFinite(radiusPx) || radiusPx < 0) return null;
    const radius = radiusPx;
    const minColumn = Math.max(0, Math.floor((point.x - radius) / HIT_CELL_SIZE_PX));
    const maxColumn = Math.min(this.hitColumns - 1, Math.floor((point.x + radius) / HIT_CELL_SIZE_PX));
    const minRow = Math.max(0, Math.floor((point.y - radius) / HIT_CELL_SIZE_PX));
    const maxRow = Math.min(this.hitRows - 1, Math.floor((point.y + radius) / HIT_CELL_SIZE_PX));
    let bestIndex = -1;
    let bestDistance = radius * radius;
    for (let row = minRow; row <= maxRow; row += 1) {
      for (let column = minColumn; column <= maxColumn; column += 1) {
        let index = this.hitHeads[row * this.hitColumns + column] ?? -1;
        while (index >= 0) {
          const dx = this.hitXs[index]! - point.x;
          const dy = this.hitYs[index]! - point.y;
          const distance = dx * dx + dy * dy;
          if (distance <= bestDistance && (bestIndex < 0 || distance < bestDistance
            || this.hitIds[index]! < this.hitIds[bestIndex]!)) {
            bestIndex = index;
            bestDistance = distance;
          }
          index = this.hitNext[index] ?? -1;
        }
      }
    }
    if (bestIndex < 0) return null;
    const id = this.compactMode ? guestIdFromNumericId(this.hitIds[bestIndex]!)
      : this.hitLegacyIds[bestIndex] ?? guestIdFromNumericId(this.hitIds[bestIndex]!);
    return { id, x: this.hitXs[bestIndex]!, y: this.hitYs[bestIndex]!,
      statusFlags: this.hitFlags[bestIndex]!, distanceSquared: bestDistance };
  }

  /** Snap the currently retained frame after a pause or other discontinuity. */
  snapCompactFrame(): void {
    if (!this.compactMode || !this.nextFrame) return;
    this.clearHitIndex();
    this.previousFrame = this.nextFrame;
    this.previousEdgePaths = this.edgePaths;
    this.previousPortalLngLat = this.portalLngLat;
    this.pending = guestGpuFrameVertexData(this.previousFrame, this.nextFrame,
      this.edgePaths, this.portalLngLat, this.previousEdgePaths, this.previousPortalLngLat);
    this.bufferDirty = true;
    this.durationMs = 0;
    this.startedAt = performance.now();
    this.upload();
    this.map?.triggerRepaint();
  }

  onAdd(map: maplibregl.Map, gl: WebGLRenderingContext | WebGL2RenderingContext): void {
    this.map = map;
    this.gl = gl;
    const vertex = shader(gl, gl.VERTEX_SHADER, `
      precision highp float;
      attribute vec2 a_from;
      attribute vec2 a_to;
      attribute float a_status;
      attribute float a_z;
      uniform mat4 u_matrix;
      uniform float u_progress;
      uniform float u_size;
      varying float v_status;
      void main() {
        gl_Position = u_matrix * vec4(mix(a_from, a_to, u_progress), a_z, 1.0);
        gl_PointSize = u_size;
        v_status = a_status;
      }
    `);
      const fragment = shader(gl, gl.FRAGMENT_SHADER, `
        precision mediump float;
        uniform float u_opacity;
      varying float v_status;
      vec3 colorFor(float value) {
        if (value < 0.5) return vec3(0.145, 0.388, 0.922);
        if (value < 1.5) return vec3(0.863, 0.149, 0.149);
        if (value < 2.5) return vec3(0.055, 0.647, 0.914);
        if (value < 3.5) return vec3(0.980, 0.804, 0.082);
        if (value < 4.5) return vec3(0.976, 0.451, 0.086);
        if (value < 5.5) return vec3(0.659, 0.333, 0.969);
        return vec3(0.133, 0.773, 0.369);
      }
      void main() {
        if (v_status < -0.5) discard;
        vec2 centered = gl_PointCoord - vec2(0.5);
        float radius = length(centered);
        if (radius > 0.5) discard;
        // GLSL smoothstep requires increasing edges. Reverse fades are
        // undefined and some drivers return zero alpha for every point.
        float edge = 1.0 - smoothstep(0.38, 0.5, radius);
        vec3 color = mix(vec3(1.0), colorFor(v_status), 1.0 - smoothstep(0.40, 0.48, radius));
        gl_FragColor = vec4(color * edge, edge) * u_opacity;
      }
    `);
    const program = gl.createProgram();
    if (!program) throw new Error('Unable to create guest map program.');
    gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
    gl.deleteShader(vertex); gl.deleteShader(fragment);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'Unable to link guest map program.');
    this.program = program;
    this.buffer = gl.createBuffer();
    this.fromAttribute = gl.getAttribLocation(program, 'a_from');
    this.toAttribute = gl.getAttribLocation(program, 'a_to');
    this.statusAttribute = gl.getAttribLocation(program, 'a_status');
    this.zAttribute = gl.getAttribLocation(program, 'a_z');
    this.matrixUniform = gl.getUniformLocation(program, 'u_matrix');
    this.progressUniform = gl.getUniformLocation(program, 'u_progress');
    this.sizeUniform = gl.getUniformLocation(program, 'u_size');
    this.opacityUniform = gl.getUniformLocation(program, 'u_opacity');
    this.upload(gl);
  }

  render(gl: WebGLRenderingContext | WebGL2RenderingContext, options: CustomRenderMethodInput): void {
    if (!this.program || !this.buffer || this.count === 0) return;
    // Guest vertices are normalized Mercator coordinates. MapLibre's generic
    // model-view matrix expects world-pixel coordinates; its custom-layer
    // projection is explicitly scaled for the normalized [0, 1] domain.
    const matrix = guestLayerProjectionMatrix(options);
    const progress = this.durationMs === 0 ? 1 : Math.min(1, (performance.now() - this.startedAt) / this.durationMs);
    if (this.motionNext.length) {
      for (let i = 0; i < this.motionNext.length; i++) {
        const point = this.motionNext[i], position = interpolatedMotionPosition(this.motionPrevious.get(point.id), point, this.motionRoutes, progress);
        const offset = i * FLOATS_PER_GUEST;
        this.pending[offset] = this.pending[offset + 2] = mercatorXFromLng(position[0]);
        this.pending[offset + 1] = this.pending[offset + 3] = mercatorYFromLat(position[1]);
      }
      this.bufferDirty = true;
    }
    const now = performance.now();
    const terrain = this.map as (maplibregl.Map & { getTerrain?: () => unknown }) | null;
    const hasTerrain = terrain && typeof terrain.getTerrain === 'function' ? !!terrain.getTerrain() : true;
    if (hasTerrain && now - this.terrainSampleAt >= this.terrainSampleIntervalMs) {
      this.bufferDirty = updateGuestTerrainElevations(this.pending, this.count, progress,
        lngLat => this.map?.queryTerrainElevation(lngLat) ?? null) || this.bufferDirty;
      this.terrainSampleAt = now;
    }
    this.upload(gl);
    this.rebuildHitIndex(progress, matrix);
    gl.useProgram(this.program); gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
    const stride = GUEST_GPU_BYTES_PER_GUEST;
    gl.enableVertexAttribArray(this.fromAttribute); gl.vertexAttribPointer(this.fromAttribute, 2, gl.FLOAT, false, stride, 0);
    gl.enableVertexAttribArray(this.toAttribute); gl.vertexAttribPointer(this.toAttribute, 2, gl.FLOAT, false, stride, 8);
    gl.enableVertexAttribArray(this.statusAttribute); gl.vertexAttribPointer(this.statusAttribute, 1, gl.FLOAT, false, stride, 16);
    gl.enableVertexAttribArray(this.zAttribute); gl.vertexAttribPointer(this.zAttribute, 1, gl.FLOAT, false, stride, 20);
    if (this.matrixUniform) gl.uniformMatrix4fv(this.matrixUniform, false, matrix);
    if (this.progressUniform) gl.uniform1f(this.progressUniform, progress);
    const devicePixelRatio = typeof window === 'undefined' ? 1 : window.devicePixelRatio || 1;
    if (this.sizeUniform) gl.uniform1f(this.sizeUniform, GUEST_DOT_DIAMETER_CSS_PX * Math.min(2, devicePixelRatio));
    const opacity = Math.min(1, (performance.now() - this.revealAt) / 300);
    if (this.opacityUniform) gl.uniform1f(this.opacityUniform, opacity);
    gl.drawArrays(gl.POINTS, 0, this.count);
    if (progress < 1 || opacity < 1) this.map?.triggerRepaint();
  }

  onRemove(_map: maplibregl.Map, gl: WebGLRenderingContext | WebGL2RenderingContext): void {
    if (this.buffer) gl.deleteBuffer(this.buffer);
    if (this.program) gl.deleteProgram(this.program);
    this.buffer = null; this.program = null; this.map = null; this.gl = null;
    this.clearHitIndex();
  }

  private clearHitIndex(): void {
    this.hitHeads = new Int32Array(0);
    this.hitNext = new Int32Array(0);
    this.hitXs = new Float32Array(0);
    this.hitYs = new Float32Array(0);
    this.hitIds = new Uint32Array(0);
    this.hitFlags = new Uint32Array(0);
    this.hitLegacyIds = [];
    this.hitColumns = 0;
    this.hitRows = 0;
    this.hitCount = 0;
    this.renderedPositions.clear();
  }

  private rebuildHitIndex(progress: number, matrix: Float32Array): void {
    const canvas = this.map?.getCanvas();
    if (!canvas || matrix.length < 16) {
      this.clearHitIndex();
      return;
    }
    const bounds = canvas.getBoundingClientRect?.();
    const devicePixelRatio = typeof window === 'undefined' ? 1 : window.devicePixelRatio || 1;
    const width = bounds?.width || canvas.clientWidth || canvas.width / devicePixelRatio;
    const height = bounds?.height || canvas.clientHeight || canvas.height / devicePixelRatio;
    if (!(width > 0) || !(height > 0)) {
      this.clearHitIndex();
      return;
    }
    this.updateScreenHitIndex(matrix, width, height, progress);
  }

  /** Rebuild the screen-space index using the same matrix/interpolation as WebGL. */
  updateScreenHitIndex(matrix: ArrayLike<number>, width: number, height: number, progress: number): void {
    this.lastHitMatrix = matrix;
    this.lastHitWidth = width;
    this.lastHitHeight = height;
    if (matrix.length < 16 || !(width > 0) || !(height > 0)) {
      this.clearHitIndex();
      return;
    }
    const columns = Math.max(1, Math.ceil(width / HIT_CELL_SIZE_PX));
    const rows = Math.max(1, Math.ceil(height / HIT_CELL_SIZE_PX));
    const count = this.count;
    if (this.hitHeads.length !== columns * rows) this.hitHeads = new Int32Array(columns * rows);
    this.hitHeads.fill(-1);
    if (this.hitNext.length < count) this.hitNext = new Int32Array(count);
    if (this.hitXs.length < count) this.hitXs = new Float32Array(count);
    if (this.hitYs.length < count) this.hitYs = new Float32Array(count);
    if (this.hitIds.length < count) this.hitIds = new Uint32Array(count);
    if (this.hitFlags.length < count) this.hitFlags = new Uint32Array(count);
    if (this.hitLegacyIds.length < count) this.hitLegacyIds.length = count;
    const compact = this.nextFrame;
    const fraction = clampUnit(progress);
    let accepted = 0;
    this.renderedPositions.clear();
    for (let index = 0; index < count; index += 1) {
      const offset = index * FLOATS_PER_GUEST;
      const worldX = this.pending[offset]! + (this.pending[offset + 2]! - this.pending[offset]!) * fraction;
      const worldY = this.pending[offset + 1]! + (this.pending[offset + 3]! - this.pending[offset + 1]!) * fraction;
      const worldZ = this.pending[offset + 5]!;
      const flags = compact?.statusFlags[index] ?? 0;
      const visibleStatus = compact ? statusCodeFromFlags(flags) : this.legacyStatusCodes[index] ?? 0;
      if (visibleStatus < 0) continue;
      const clipX = matrix[0]! * worldX + matrix[4]! * worldY + matrix[8]! * worldZ + matrix[12]!;
      const clipY = matrix[1]! * worldX + matrix[5]! * worldY + matrix[9]! * worldZ + matrix[13]!;
      const clipW = matrix[3]! * worldX + matrix[7]! * worldY + matrix[11]! * worldZ + matrix[15]!;
      if (!(clipW > 0)) continue;
      const x = (clipX / clipW * 0.5 + 0.5) * width;
      const y = (1 - (clipY / clipW * 0.5 + 0.5)) * height;
      if (x < -HIT_CELL_SIZE_PX || x > width + HIT_CELL_SIZE_PX
        || y < -HIT_CELL_SIZE_PX || y > height + HIT_CELL_SIZE_PX) continue;
      const id = compact?.ids[index] ?? index + 1;
      const legacyFlags = compact ? 0 : this.legacyStatusCodes[index] ?? 0;
      this.hitXs[accepted] = x;
      this.hitYs[accepted] = y;
      this.hitIds[accepted] = id;
      this.hitFlags[accepted] = compact ? flags : legacyFlags;
      this.hitLegacyIds[accepted] = this.legacyGuestIds[index] ?? '';
      const renderedId = compact ? guestIdFromNumericId(id) : this.legacyGuestIds[index] ?? guestIdFromNumericId(id);
      this.renderedPositions.set(renderedId, Object.freeze([lngFromMercatorX(worldX), latFromMercatorY(worldY)]));
      const column = Math.min(columns - 1, Math.max(0, Math.floor(x / HIT_CELL_SIZE_PX)));
      const row = Math.min(rows - 1, Math.max(0, Math.floor(y / HIT_CELL_SIZE_PX)));
      const cell = row * columns + column;
      this.hitNext[accepted] = this.hitHeads[cell] ?? -1;
      this.hitHeads[cell] = accepted;
      accepted += 1;
    }
    this.hitColumns = columns;
    this.hitRows = rows;
    this.hitCount = accepted;
  }

  private upload(gl?: WebGLRenderingContext | WebGL2RenderingContext): void {
    const context = gl ?? this.gl;
    if (!context || !this.buffer || !this.bufferDirty) return;
    context.bindBuffer(context.ARRAY_BUFFER, this.buffer);
    context.bufferData(context.ARRAY_BUFFER, this.pending, context.DYNAMIC_DRAW);
    this.bufferDirty = false;
  }
}
