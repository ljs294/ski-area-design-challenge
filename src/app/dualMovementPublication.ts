import type { DualPublication } from '../dualClock/model';

type Point = DualPublication['points'][number];
// Append movement statuses so the existing wire values remain stable for
// checkpoints and older renderers.
const STATUSES = ['walking', 'lift-queue', 'lift-ride', 'skiing', 'resting', 'departed', 'trail-queue'];
const BYTES_PER_SLOT = 33;
export interface DualMovementFrame { count: number; capacity: number; buffer: ArrayBuffer; ids: string[]; routes: string[] }
function columns(frame: Pick<DualMovementFrame, 'buffer' | 'capacity'>) {
  const { buffer, capacity } = frame;
  return { coordinates: new Float64Array(buffer, 0, capacity * 2), motion: new Float32Array(buffer, capacity * 16, capacity * 3),
    routes: new Uint32Array(buffer, capacity * 28, capacity), statuses: new Uint8Array(buffer, capacity * 32, capacity) };
}

/** Worker-owned pool. Only presentation buffers transfer; domain state never detaches. */
export class DualMovementPublisher {
  private available: ArrayBuffer[] = [];
  recycle(buffer: ArrayBuffer): void {
    if (buffer.byteLength && buffer.byteLength % BYTES_PER_SLOT === 0 && this.available.length < 2) this.available.push(buffer);
  }
  frame(points: Point[]): DualMovementFrame | undefined {
    if (!points.length) return undefined;
    let capacity = 1; while (capacity < points.length) capacity *= 2;
    const index = this.available.findIndex(buffer => buffer.byteLength >= capacity * BYTES_PER_SLOT);
    const buffer = index >= 0 ? this.available.splice(index, 1)[0] : new ArrayBuffer(capacity * BYTES_PER_SLOT);
    capacity = buffer.byteLength / BYTES_PER_SLOT;
    const frame: DualMovementFrame = { count: points.length, capacity, buffer, ids: [], routes: [] };
    const data = columns(frame), routeIds = new Map<string, number>();
    for (let i = 0; i < points.length; i++) {
      const point = points[i]; frame.ids.push(point.id);
      data.coordinates[i * 2] = point.lng; data.coordinates[i * 2 + 1] = point.lat;
      data.statuses[i] = Math.max(0, STATUSES.indexOf(point.status));
      if (point.motion) {
        let route = routeIds.get(point.motion.routeId);
        if (route === undefined) { route = frame.routes.length; frame.routes.push(point.motion.routeId); routeIds.set(point.motion.routeId, route); }
        data.routes[i] = route + 1;
        data.motion[i * 3] = point.motion.lane; data.motion[i * 3 + 1] = point.motion.progress; data.motion[i * 3 + 2] = point.motion.duration;
      } else data.routes[i] = 0;
    }
    return frame;
  }
}

/** Own the decoded points before returning the transferred buffer to the worker. */
export function decodeDualMovement(frame: DualMovementFrame): Point[] {
  if (frame.count > frame.capacity || frame.ids.length !== frame.count || frame.buffer.byteLength !== frame.capacity * BYTES_PER_SLOT) throw new Error('Invalid guest movement publication.');
  const data = columns(frame), points: Point[] = [];
  for (let i = 0; i < frame.count; i++) points.push({ id: frame.ids[i], lng: data.coordinates[i * 2], lat: data.coordinates[i * 2 + 1],
    status: STATUSES[data.statuses[i]] ?? 'walking', ...(data.routes[i] ? { motion: { routeId: frame.routes[data.routes[i] - 1],
      lane: data.motion[i * 3], progress: data.motion[i * 3 + 1], duration: data.motion[i * 3 + 2] } } : {}) });
  return points;
}
