/**
 * Phase 7 publication primitives.
 *
 * The simulation keeps authoritative guest state in its existing sharding
 * model.  This module is only a presentation boundary: a publication is a
 * compact, immutable-after-build slab that can be transferred to a renderer
 * without allocating one object per guest.  It deliberately uses ordinary
 * ArrayBuffer (never SharedArrayBuffer) so the same contract works in a
 * browser, Electron, and a deterministic test runner.
 */

export const GUEST_PUBLICATION_FORMAT_VERSION = 1 as const;

/** Four bytes for id, twelve for xyz, and three one-byte presentation fields. */
export const GUEST_PUBLICATION_BYTES_PER_GUEST = 19 as const;

export interface GuestPublicationRow {
  readonly guestId: number;
  readonly x: number;
  readonly y: number;
  readonly elevation: number;
  /** Stable numeric code owned by the consumer (0 is allowed). */
  readonly statusCode: number;
  /** Presentation value in the inclusive [0, 255] range. */
  readonly satisfaction: number;
  /** Bit flags reserved for selected/highlighted/route-visible state. */
  readonly flags?: number;
}

export interface GuestPublicationMetadata {
  readonly tick: number;
  readonly sequence: number;
  readonly environmentRevision: number;
  readonly topologyRevision: number;
}

/** Views into a compact publication. All columns share one transferable slab. */
export interface GuestPublicationSlab extends GuestPublicationMetadata {
  readonly formatVersion: typeof GUEST_PUBLICATION_FORMAT_VERSION;
  readonly length: number;
  readonly byteLength: number;
  readonly buffer: ArrayBuffer;
  readonly guestIds: Uint32Array;
  /** Three floats per guest, in x/y/elevation order. */
  readonly positions: Float32Array;
  readonly statusCodes: Uint8Array;
  readonly satisfaction: Uint8Array;
  readonly flags: Uint8Array;
}

/** Object-shaped envelope used when crossing a worker/IPC boundary. */
export interface GuestPublicationEnvelope extends GuestPublicationMetadata {
  readonly formatVersion: typeof GUEST_PUBLICATION_FORMAT_VERSION;
  readonly length: number;
  readonly buffer: ArrayBuffer;
}

function nonNegativeInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative safe integer`);
  }
  return value;
}

function finiteNumber(value: number, name: string): number {
  if (!Number.isFinite(value)) throw new RangeError(`${name} must be finite`);
  return value;
}

function byte(value: number, name: string): number {
  nonNegativeInteger(value, name);
  if (value > 255) throw new RangeError(`${name} must fit in one byte`);
  return value;
}

function slabByteLength(length: number): number {
  return length * GUEST_PUBLICATION_BYTES_PER_GUEST;
}

function viewOffsets(length: number): {
  readonly ids: number;
  readonly positions: number;
  readonly statusCodes: number;
  readonly satisfaction: number;
  readonly flags: number;
} {
  const ids = 0;
  const positions = length * Uint32Array.BYTES_PER_ELEMENT;
  const statusCodes = positions + length * 3 * Float32Array.BYTES_PER_ELEMENT;
  const satisfaction = statusCodes + length;
  const flags = satisfaction + length;
  return { ids, positions, statusCodes, satisfaction, flags };
}

function createViews(buffer: ArrayBuffer, length: number): Omit<GuestPublicationSlab, keyof GuestPublicationMetadata | 'formatVersion' | 'length' | 'byteLength' | 'buffer'> {
  const offsets = viewOffsets(length);
  return {
    guestIds: new Uint32Array(buffer, offsets.ids, length),
    positions: new Float32Array(buffer, offsets.positions, length * 3),
    statusCodes: new Uint8Array(buffer, offsets.statusCodes, length),
    satisfaction: new Uint8Array(buffer, offsets.satisfaction, length),
    flags: new Uint8Array(buffer, offsets.flags, length),
  };
}

function validateMetadata(metadata: GuestPublicationMetadata): void {
  nonNegativeInteger(metadata.tick, 'tick');
  nonNegativeInteger(metadata.sequence, 'sequence');
  nonNegativeInteger(metadata.environmentRevision, 'environmentRevision');
  nonNegativeInteger(metadata.topologyRevision, 'topologyRevision');
}

/** Build one compact slab in input order; input is never retained. */
export function packGuestPublication(
  rows: readonly GuestPublicationRow[],
  metadata: GuestPublicationMetadata,
): GuestPublicationSlab {
  validateMetadata(metadata);
  const length = rows.length;
  const buffer = new ArrayBuffer(slabByteLength(length));
  const views = createViews(buffer, length);
  const ids = new Set<number>();
  for (let index = 0; index < length; index += 1) {
    const row = rows[index];
    nonNegativeInteger(row.guestId, 'guestId');
    if (ids.has(row.guestId)) throw new RangeError(`duplicate guestId ${row.guestId}`);
    ids.add(row.guestId);
    views.guestIds[index] = row.guestId;
    views.positions[index * 3] = finiteNumber(row.x, 'x');
    views.positions[index * 3 + 1] = finiteNumber(row.y, 'y');
    views.positions[index * 3 + 2] = finiteNumber(row.elevation, 'elevation');
    views.statusCodes[index] = byte(row.statusCode, 'statusCode');
    views.satisfaction[index] = byte(row.satisfaction, 'satisfaction');
    views.flags[index] = byte(row.flags ?? 0, 'flags');
  }
  return {
    ...metadata,
    formatVersion: GUEST_PUBLICATION_FORMAT_VERSION,
    length,
    byteLength: buffer.byteLength,
    buffer,
    ...views,
  };
}

/** Convert a slab to a transferable envelope without copying its ArrayBuffer. */
export function toGuestPublicationEnvelope(slab: GuestPublicationSlab): GuestPublicationEnvelope {
  validateGuestPublicationSlab(slab);
  return {
    formatVersion: slab.formatVersion,
    length: slab.length,
    tick: slab.tick,
    sequence: slab.sequence,
    environmentRevision: slab.environmentRevision,
    topologyRevision: slab.topologyRevision,
    buffer: slab.buffer,
  };
}

/** The exact transfer list for postMessage/MessagePort integrations. */
export function guestPublicationTransferables(slab: GuestPublicationSlab): readonly [ArrayBuffer] {
  validateGuestPublicationSlab(slab);
  return [slab.buffer];
}

/** Recreate typed views after an envelope's ArrayBuffer has been transferred. */
export function fromGuestPublicationEnvelope(envelope: GuestPublicationEnvelope): GuestPublicationSlab {
  if (!envelope || envelope.formatVersion !== GUEST_PUBLICATION_FORMAT_VERSION) {
    throw new RangeError('unsupported guest publication format version');
  }
  nonNegativeInteger(envelope.length, 'length');
  validateMetadata(envelope);
  if (!(envelope.buffer instanceof ArrayBuffer)) throw new TypeError('guest publication buffer must be an ArrayBuffer');
  const expectedBytes = slabByteLength(envelope.length);
  if (envelope.buffer.byteLength !== expectedBytes) {
    throw new RangeError(`guest publication buffer has ${envelope.buffer.byteLength} bytes; expected ${expectedBytes}`);
  }
  const views = createViews(envelope.buffer, envelope.length);
  return {
    ...envelope,
    byteLength: envelope.buffer.byteLength,
    ...views,
  };
}

export function validateGuestPublicationSlab(slab: GuestPublicationSlab): void {
  if (!slab || slab.formatVersion !== GUEST_PUBLICATION_FORMAT_VERSION) {
    throw new RangeError('unsupported guest publication format version');
  }
  nonNegativeInteger(slab.length, 'length');
  validateMetadata(slab);
  if (!(slab.buffer instanceof ArrayBuffer)) throw new TypeError('guest publication buffer must be an ArrayBuffer');
  if (slab.byteLength !== slab.buffer.byteLength || slab.byteLength !== slabByteLength(slab.length)) {
    throw new RangeError('guest publication byte length is inconsistent');
  }
  if (slab.guestIds.buffer !== slab.buffer || slab.positions.buffer !== slab.buffer
    || slab.statusCodes.buffer !== slab.buffer || slab.satisfaction.buffer !== slab.buffer
    || slab.flags.buffer !== slab.buffer) {
    throw new RangeError('guest publication columns must share one ArrayBuffer');
  }
  const offsets = viewOffsets(slab.length);
  if (slab.guestIds.byteOffset !== offsets.ids || slab.positions.byteOffset !== offsets.positions
    || slab.statusCodes.byteOffset !== offsets.statusCodes || slab.satisfaction.byteOffset !== offsets.satisfaction
    || slab.flags.byteOffset !== offsets.flags) {
    throw new RangeError('guest publication columns have unexpected byte offsets');
  }
  if (slab.guestIds.length !== slab.length || slab.positions.length !== slab.length * 3
    || slab.statusCodes.length !== slab.length || slab.satisfaction.length !== slab.length
    || slab.flags.length !== slab.length) {
    throw new RangeError('guest publication columns have inconsistent lengths');
  }
}

/** Stable non-cryptographic checksum for deterministic publication tests. */
export function checksumGuestPublication(slab: GuestPublicationSlab): number {
  validateGuestPublicationSlab(slab);
  let checksum = 2_166_136_261;
  for (let index = 0; index < slab.length; index += 1) {
    checksum = Math.imul((checksum ^ slab.guestIds[index]) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ Math.fround(slab.positions[index * 3]) * 1_000_000) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ Math.fround(slab.positions[index * 3 + 1]) * 1_000_000) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ Math.fround(slab.positions[index * 3 + 2]) * 1_000_000) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ slab.statusCodes[index]) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ slab.satisfaction[index]) >>> 0, 16_777_619) >>> 0;
    checksum = Math.imul((checksum ^ slab.flags[index]) >>> 0, 16_777_619) >>> 0;
  }
  return checksum;
}

/** Read a row for diagnostics/tests; hot paths should consume the typed views. */
export function readGuestPublicationRow(slab: GuestPublicationSlab, index: number): GuestPublicationRow {
  validateGuestPublicationSlab(slab);
  nonNegativeInteger(index, 'index');
  if (index >= slab.length) throw new RangeError('publication row index is out of bounds');
  return {
    guestId: slab.guestIds[index],
    x: slab.positions[index * 3],
    y: slab.positions[index * 3 + 1],
    elevation: slab.positions[index * 3 + 2],
    statusCode: slab.statusCodes[index],
    satisfaction: slab.satisfaction[index],
    flags: slab.flags[index],
  };
}
