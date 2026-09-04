import maplibregl, { type CustomLayerInterface, type CustomRenderMethodInput } from 'maplibre-gl';
import type { GuestRenderPoint } from './guestLayers';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';

/** A worker edge's display path, kept outside React state. */
export type GuestRenderPath = readonly (readonly [number, number])[];

const FLOATS_PER_GUEST = 5;
export const GUEST_GPU_BYTES_PER_GUEST = FLOATS_PER_GUEST * Float32Array.BYTES_PER_ELEMENT;
const HIT_CELL_SIZE_PX = 24;
const DEFAULT_HIT_RADIUS_PX = 8;

export interface GuestScreenHit {
  readonly id: string;
  readonly x: number;
  readonly y: number;
  readonly statusFlags: number;
  readonly distanceSquared: number;
}

function statusCode(status: string): number {
  if (status === 'incident' || status === 'patrol-response') return 1;
  if (status === 'skiing') return 2;
  if (status === 'lift-ride') return 3;
  if (status === 'lift-queue') return 4;
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

function pathProgressPosition(path: GuestRenderPath, progress: number): readonly [number, number] {
  if (path.length === 0) return [0, 0];
  if (path.length === 1) return path[0]!;
  const lengths: number[] = [];
  let total = 0;
  for (let index = 1; index < path.length; index += 1) {
    const from = path[index - 1]!, to = path[index]!;
    const latitudeScale = Math.cos(((from[1] + to[1]) / 2) * Math.PI / 180);
    const length = Math.hypot((to[0] - from[0]) * latitudeScale, to[1] - from[1]);
    lengths.push(length); total += length;
  }
  if (total <= Number.EPSILON) return path[0]!;
  let remaining = clampUnit(progress) * total;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index]!;
    if (remaining <= length || index === lengths.length - 1) {
      const from = path[index]!, to = path[index + 1]!;
      const fraction = length <= Number.EPSILON ? 0 : clampUnit(remaining / length);
      return [from[0] + (to[0] - from[0]) * fraction, from[1] + (to[1] - from[1]) * fraction];
    }
    remaining -= length;
  }
  return path[path.length - 1]!;
}

export function guestGpuVertexData(previous: readonly GuestRenderPoint[], next: readonly GuestRenderPoint[]): Float32Array<ArrayBuffer> {
  const previousById = new Map(previous.map((point) => [point.id, point]));
  const data = new Float32Array(next.length * FLOATS_PER_GUEST);
  next.forEach((point, index) => {
    const from = previousById.get(point.id) ?? point;
    const fromMercator = maplibregl.MercatorCoordinate.fromLngLat([from.lng, from.lat]);
    const toMercator = maplibregl.MercatorCoordinate.fromLngLat([point.lng, point.lat]);
    const offset = index * FLOATS_PER_GUEST;
    data[offset] = fromMercator.x;
    data[offset + 1] = fromMercator.y;
    data[offset + 2] = toMercator.x;
    data[offset + 3] = toMercator.y;
    data[offset + 4] = statusCode(point.status);
  });
  return data;
}

function framePosition(frame: GuestSimulationRenderFrame, index: number,
  edgePaths: readonly GuestRenderPath[], portalLngLat?: readonly [number, number]): readonly [number, number] {
  const edge = edgePaths[frame.edgeIndices[index] ?? -1];
  if (edge && edge.length > 0) return pathProgressPosition(edge, frame.progress[index] ?? 0);
  return portalLngLat ?? [0, 0];
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
): Float32Array<ArrayBuffer> {
  const previousIndex = previous ? new Map<number, number>(
    Array.from(previous.ids, (id, index) => [id, index] as const),
  ) : undefined;
  const data = new Float32Array(next.ids.length * FLOATS_PER_GUEST);
  for (let index = 0; index < next.ids.length; index += 1) {
    const prior = previousIndex?.get(next.ids[index]!);
    const from = prior === undefined ? framePosition(next, index, edgePaths, portalLngLat)
      : framePosition(previous!, prior, edgePaths, portalLngLat);
    const to = framePosition(next, index, edgePaths, portalLngLat);
    const fromMercator = maplibregl.MercatorCoordinate.fromLngLat([from[0], from[1]]);
    const toMercator = maplibregl.MercatorCoordinate.fromLngLat([to[0], to[1]]);
    const offset = index * FLOATS_PER_GUEST;
    data[offset] = fromMercator.x;
    data[offset + 1] = fromMercator.y;
    data[offset + 2] = toMercator.x;
    data[offset + 3] = toMercator.y;
    data[offset + 4] = statusCodeFromFlags(next.statusFlags[index] ?? 0);
  }
  return data;
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

/** GPU-backed point layer. React supplies authoritative frames; MapLibre owns interpolation. */
export class GuestGpuLayer implements CustomLayerInterface {
  readonly id: string;
  readonly type = 'custom' as const;
  readonly renderingMode = '2d' as const;
  private map: maplibregl.Map | null = null;
  private program: WebGLProgram | null = null;
  private buffer: WebGLBuffer | null = null;
  private gl: WebGLRenderingContext | WebGL2RenderingContext | null = null;
  private count = 0;
  private startedAt = 0;
  private durationMs = 50;
  private pending: Float32Array<ArrayBuffer> = new Float32Array(0);
  private compactMode = false;
  private previousFrame: GuestSimulationRenderFrame | null = null;
  private nextFrame: GuestSimulationRenderFrame | null = null;
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

  constructor(id: string) { this.id = id; }

  setPoints(previous: readonly GuestRenderPoint[], next: readonly GuestRenderPoint[], durationMs = 50): void {
    this.clearHitIndex();
    this.compactMode = false;
    this.previousFrame = null;
    this.nextFrame = null;
    this.legacyGuestIds = next.map((point) => point.id);
    this.legacyStatusCodes = next.map((point) => statusCode(point.status));
    this.pending = guestGpuVertexData(previous, next);
    this.count = next.length;
    this.startedAt = performance.now();
    this.durationMs = Math.max(0, durationMs);
    this.upload();
    this.map?.triggerRepaint();
  }

  /** Retain two authoritative compact frames; MapLibre interpolates them. */
  setRenderFrame(frame: GuestSimulationRenderFrame | null, edgePaths: readonly GuestRenderPath[],
    portalLngLat?: readonly [number, number], durationMs = 50): void {
    if (!frame) {
      this.compactMode = false;
      this.previousFrame = null;
      this.nextFrame = null;
      this.count = 0;
      this.legacyGuestIds = [];
      this.legacyStatusCodes = [];
      this.clearHitIndex();
      this.pending = new Float32Array(0);
      return;
    }
    this.clearHitIndex();
    this.compactMode = true;
    this.legacyGuestIds = [];
    this.legacyStatusCodes = [];
    this.previousFrame = this.nextFrame;
    this.nextFrame = frame;
    this.edgePaths = edgePaths;
    this.portalLngLat = portalLngLat;
    this.pending = guestGpuFrameVertexData(this.previousFrame, frame, edgePaths, portalLngLat);
    this.count = frame.ids.length;
    this.startedAt = performance.now();
    this.durationMs = Math.max(0, durationMs);
    this.upload();
    this.map?.triggerRepaint();
  }

  hasCompactFrame(): boolean { return this.compactMode; }

  /** Return the nearest guest at the interpolated screen position. */
  hitTest(point: { readonly x: number; readonly y: number }, radiusPx = DEFAULT_HIT_RADIUS_PX): GuestScreenHit | null {
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
    this.pending = guestGpuFrameVertexData(this.previousFrame, this.nextFrame,
      this.edgePaths, this.portalLngLat);
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
      uniform mat4 u_matrix;
      uniform float u_progress;
      uniform float u_size;
      varying float v_status;
      void main() {
        gl_Position = u_matrix * vec4(mix(a_from, a_to, u_progress), 0.0, 1.0);
        gl_PointSize = u_size;
        v_status = a_status;
      }
    `);
    const fragment = shader(gl, gl.FRAGMENT_SHADER, `
      precision mediump float;
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
        float edge = smoothstep(0.5, 0.38, radius);
        vec3 color = mix(vec3(1.0), colorFor(v_status), smoothstep(0.48, 0.40, radius));
        gl_FragColor = vec4(color * edge, edge);
      }
    `);
    const program = gl.createProgram();
    if (!program) throw new Error('Unable to create guest map program.');
    gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
    gl.deleteShader(vertex); gl.deleteShader(fragment);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'Unable to link guest map program.');
    this.program = program;
    this.buffer = gl.createBuffer();
    this.upload(gl);
  }

  render(gl: WebGLRenderingContext | WebGL2RenderingContext, options: CustomRenderMethodInput): void {
    if (!this.program || !this.buffer || this.count === 0) return;
    const progress = this.durationMs === 0 ? 1 : Math.min(1, (performance.now() - this.startedAt) / this.durationMs);
    this.rebuildHitIndex(progress, options.modelViewProjectionMatrix as unknown as Float32Array);
    gl.useProgram(this.program); gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
    const stride = GUEST_GPU_BYTES_PER_GUEST;
    const from = gl.getAttribLocation(this.program, 'a_from');
    const to = gl.getAttribLocation(this.program, 'a_to');
    const status = gl.getAttribLocation(this.program, 'a_status');
    gl.enableVertexAttribArray(from); gl.vertexAttribPointer(from, 2, gl.FLOAT, false, stride, 0);
    gl.enableVertexAttribArray(to); gl.vertexAttribPointer(to, 2, gl.FLOAT, false, stride, 8);
    gl.enableVertexAttribArray(status); gl.vertexAttribPointer(status, 1, gl.FLOAT, false, stride, 16);
    gl.uniformMatrix4fv(gl.getUniformLocation(this.program, 'u_matrix'), false,
      options.modelViewProjectionMatrix as unknown as Float32Array);
    gl.uniform1f(gl.getUniformLocation(this.program, 'u_progress'), progress);
    gl.uniform1f(gl.getUniformLocation(this.program, 'u_size'), 7 * Math.min(2, window.devicePixelRatio || 1));
    gl.drawArrays(gl.POINTS, 0, this.count);
    if (progress < 1) this.map?.triggerRepaint();
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
    for (let index = 0; index < count; index += 1) {
      const offset = index * FLOATS_PER_GUEST;
      const worldX = this.pending[offset]! + (this.pending[offset + 2]! - this.pending[offset]!) * fraction;
      const worldY = this.pending[offset + 1]! + (this.pending[offset + 3]! - this.pending[offset + 1]!) * fraction;
      const clipX = matrix[0]! * worldX + matrix[4]! * worldY + matrix[12]!;
      const clipY = matrix[1]! * worldX + matrix[5]! * worldY + matrix[13]!;
      const clipW = matrix[3]! * worldX + matrix[7]! * worldY + matrix[15]!;
      if (!(clipW > 0)) continue;
      const x = (clipX / clipW * 0.5 + 0.5) * width;
      const y = (1 - (clipY / clipW * 0.5 + 0.5)) * height;
      if (x < -HIT_CELL_SIZE_PX || x > width + HIT_CELL_SIZE_PX
        || y < -HIT_CELL_SIZE_PX || y > height + HIT_CELL_SIZE_PX) continue;
      const id = compact?.ids[index] ?? index + 1;
      const flags = compact?.statusFlags[index] ?? this.legacyStatusCodes[index] ?? 0;
      this.hitXs[accepted] = x;
      this.hitYs[accepted] = y;
      this.hitIds[accepted] = id;
      this.hitFlags[accepted] = flags;
      this.hitLegacyIds[accepted] = this.legacyGuestIds[index] ?? '';
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
    if (!context || !this.buffer) return;
    context.bindBuffer(context.ARRAY_BUFFER, this.buffer);
    context.bufferData(context.ARRAY_BUFFER, this.pending, context.DYNAMIC_DRAW);
  }
}
