/**
 * A small, dependency-neutral binary codec used by the guest simulation
 * sidecar.  It deliberately does not import Node, Electron, WebCrypto, or
 * the browser storage APIs.  The payload is a canonical tagged value tree:
 * object keys are sorted and numbers use compact integer encoding when they
 * can.  That gives the same bytes in a browser, a worker, and a Node test.
 */

export const GUEST_SIMULATION_SIDECAR_MAGIC = 'GSCP';
export const GUEST_SIMULATION_SIDECAR_FORMAT_VERSION = 1 as const;
export const PORTABLE_CONTENT_HASH_ALGORITHM = 'fnv1a64-v1';

const MAGIC_BYTES = new Uint8Array([0x47, 0x53, 0x43, 0x50]);

export interface BinaryContentHasher {
  /** A stable, descriptive algorithm identifier stored in the sidecar. */
  readonly algorithm: string;
  hash(bytes: Uint8Array): string;
}

/**
 * The default checksum is intentionally portable rather than cryptographic.
 * A desktop adapter may supply a SHA-256 implementation through the hasher
 * interface without changing this codec's wire format or importing a runtime
 * dependency into the simulation domain.
 */
export const portableContentHasher: BinaryContentHasher = Object.freeze({
  algorithm: PORTABLE_CONTENT_HASH_ALGORITHM,
  hash: portableContentHash,
});

export interface BinaryCodecOptions {
  readonly hasher?: BinaryContentHasher;
  readonly maxDepth?: number;
}

export interface BinarySidecarHeader {
  readonly magic: typeof GUEST_SIMULATION_SIDECAR_MAGIC;
  readonly formatVersion: typeof GUEST_SIMULATION_SIDECAR_FORMAT_VERSION;
  readonly hashAlgorithm: string;
  readonly payloadLength: number;
  readonly contentHash: string;
}

export class BinaryCodecError extends Error {
  readonly code: 'invalid-value' | 'invalid-header' | 'unsupported-version' | 'checksum-mismatch' | 'truncated';

  constructor(code: BinaryCodecError['code'], message: string) {
    super(message);
    this.name = 'BinaryCodecError';
    this.code = code;
  }
}

/** Compute a deterministic, non-cryptographic checksum over bytes. */
export function portableContentHash(bytes: Uint8Array): string {
  // FNV-1a's 64-bit arithmetic is expressed with BigInt so it is not
  // affected by JavaScript's 53-bit Number mantissa.  BigInt is part of the
  // ES2020 language and is available in both supported runtimes.
  let hash = 0xcbf29ce484222325n;
  const prime = 0x100000001b3n;
  for (const byte of bytes) {
    hash ^= BigInt(byte);
    hash = BigInt.asUintN(64, hash * prime);
  }
  return `${PORTABLE_CONTENT_HASH_ALGORITHM}-${hash.toString(16).padStart(16, '0')}`;
}

function assertHasher(hasher: BinaryContentHasher): void {
  if (!hasher || typeof hasher.algorithm !== 'string' || hasher.algorithm.length === 0
    || typeof hasher.hash !== 'function') {
    throw new TypeError('A content hasher must provide a non-empty algorithm and hash function');
  }
}

function assertSafeNonNegativeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

class ByteWriter {
  private readonly bytes: number[] = [];

  writeByte(value: number): void {
    this.bytes.push(value & 0xff);
  }

  writeBytes(value: Uint8Array): void {
    for (const byte of value) this.bytes.push(byte);
  }

  writeVarUint(value: number): void {
    assertSafeNonNegativeInteger(value, 'varint');
    let remaining = value;
    do {
      const next = remaining % 128;
      remaining = Math.floor(remaining / 128);
      this.writeByte(remaining === 0 ? next : next | 0x80);
    } while (remaining !== 0);
  }

  writeFloat64(value: number): void {
    const bytes = new Uint8Array(8);
    new DataView(bytes.buffer).setFloat64(0, value, true);
    this.writeBytes(bytes);
  }

  toBytes(): Uint8Array {
    return Uint8Array.from(this.bytes);
  }
}

class ByteReader {
  private offset = 0;
  private readonly bytes: Uint8Array;

  constructor(bytes: Uint8Array) {
    this.bytes = bytes;
  }

  get position(): number {
    return this.offset;
  }

  get remaining(): number {
    return this.bytes.length - this.offset;
  }

  readByte(): number {
    if (this.offset >= this.bytes.length) throw new BinaryCodecError('truncated', 'Binary sidecar ended unexpectedly');
    return this.bytes[this.offset++]!;
  }

  readBytes(length: number): Uint8Array {
    assertSafeNonNegativeInteger(length, 'byte length');
    if (this.remaining < length) throw new BinaryCodecError('truncated', 'Binary sidecar payload is truncated');
    const result = this.bytes.slice(this.offset, this.offset + length);
    this.offset += length;
    return result;
  }

  readVarUint(): number {
    let value = 0;
    let multiplier = 1;
    for (let count = 0; count < 8; count += 1) {
      const byte = this.readByte();
      value += (byte & 0x7f) * multiplier;
      if (value > Number.MAX_SAFE_INTEGER) throw new BinaryCodecError('invalid-value', 'Binary varint exceeds safe integer range');
      if ((byte & 0x80) === 0) return value;
      multiplier *= 128;
    }
    throw new BinaryCodecError('invalid-value', 'Binary varint is too long');
  }

  readFloat64(): number {
    const bytes = this.readBytes(8);
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getFloat64(0, true);
  }
}

const VALUE_NULL = 0;
const VALUE_UNDEFINED = 1;
const VALUE_FALSE = 2;
const VALUE_TRUE = 3;
const VALUE_INTEGER = 4;
const VALUE_FLOAT = 5;
const VALUE_STRING = 6;
const VALUE_ARRAY = 7;
const VALUE_OBJECT = 8;
const VALUE_BIGINT = 9;
const VALUE_BYTES = 10;

function encodeText(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

function decodeText(bytes: Uint8Array): string {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw new BinaryCodecError('invalid-value', 'Binary sidecar contains invalid UTF-8');
  }
}

function encodeUnsignedBigInt(writer: ByteWriter, value: bigint): void {
  if (value < 0n) throw new BinaryCodecError('invalid-value', 'Expected an unsigned bigint');
  // BigInt is encoded in base 128.  Unlike a Number varint this remains exact
  // for arbitrary bigint payloads used by deterministic test adapters.
  do {
    const next = value & 0x7fn;
    value >>= 7n;
    writer.writeByte(value === 0n ? Number(next) : Number(next) | 0x80);
  } while (value !== 0n);
}

function decodeUnsignedBigInt(reader: ByteReader): bigint {
  let value = 0n;
  let multiplier = 1n;
  for (let count = 0; count < 11; count += 1) {
    const byte = reader.readByte();
    value += BigInt(byte & 0x7f) * multiplier;
    if ((byte & 0x80) === 0) return value;
    multiplier <<= 7n;
  }
  throw new BinaryCodecError('invalid-value', 'Binary bigint varint is too long');
}

function encodeValue(writer: ByteWriter, value: unknown, depth: number, maxDepth: number): void {
  if (depth > maxDepth) throw new BinaryCodecError('invalid-value', 'Binary value exceeds maximum nesting depth');
  if (value === null) { writer.writeByte(VALUE_NULL); return; }
  if (value === undefined) { writer.writeByte(VALUE_UNDEFINED); return; }
  if (value === false) { writer.writeByte(VALUE_FALSE); return; }
  if (value === true) { writer.writeByte(VALUE_TRUE); return; }
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new BinaryCodecError('invalid-value', 'Binary values must contain finite numbers');
    if (Number.isSafeInteger(value)) {
      writer.writeByte(VALUE_INTEGER);
      writer.writeByte(value < 0 ? 1 : 0);
      writer.writeVarUint(Math.abs(value));
    } else {
      writer.writeByte(VALUE_FLOAT);
      writer.writeFloat64(value);
    }
    return;
  }
  if (typeof value === 'bigint') {
    writer.writeByte(VALUE_BIGINT);
    writer.writeByte(value < 0n ? 1 : 0);
    encodeUnsignedBigInt(writer, value < 0n ? -value : value);
    return;
  }
  if (typeof value === 'string') {
    const bytes = encodeText(value);
    writer.writeByte(VALUE_STRING);
    writer.writeVarUint(bytes.length);
    writer.writeBytes(bytes);
    return;
  }
  if (value instanceof Uint8Array) {
    writer.writeByte(VALUE_BYTES);
    writer.writeVarUint(value.byteLength);
    writer.writeBytes(value);
    return;
  }
  if (Array.isArray(value)) {
    writer.writeByte(VALUE_ARRAY);
    writer.writeVarUint(value.length);
    for (const item of value) encodeValue(writer, item, depth + 1, maxDepth);
    return;
  }
  if (typeof value === 'object') {
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) {
      throw new BinaryCodecError('invalid-value', 'Binary sidecar only supports plain objects, arrays, and Uint8Array values');
    }
    const record = value as Record<string, unknown>;
    const keys = Object.keys(record).sort();
    writer.writeByte(VALUE_OBJECT);
    writer.writeVarUint(keys.length);
    for (const key of keys) {
      encodeValue(writer, key, depth + 1, maxDepth);
      encodeValue(writer, record[key], depth + 1, maxDepth);
    }
    return;
  }
  throw new BinaryCodecError('invalid-value', `Unsupported binary value type: ${typeof value}`);
}

function decodeValue(reader: ByteReader, depth: number, maxDepth: number): unknown {
  if (depth > maxDepth) throw new BinaryCodecError('invalid-value', 'Binary value exceeds maximum nesting depth');
  switch (reader.readByte()) {
    case VALUE_NULL: return null;
    case VALUE_UNDEFINED: return undefined;
    case VALUE_FALSE: return false;
    case VALUE_TRUE: return true;
    case VALUE_INTEGER: {
      const negative = reader.readByte();
      if (negative !== 0 && negative !== 1) throw new BinaryCodecError('invalid-value', 'Invalid integer sign');
      const magnitude = reader.readVarUint();
      return negative === 1 ? -magnitude : magnitude;
    }
    case VALUE_FLOAT: {
      const value = reader.readFloat64();
      if (!Number.isFinite(value)) throw new BinaryCodecError('invalid-value', 'Binary float is not finite');
      return value;
    }
    case VALUE_STRING: return decodeText(reader.readBytes(reader.readVarUint()));
    case VALUE_BYTES: return reader.readBytes(reader.readVarUint());
    case VALUE_ARRAY: {
      const count = reader.readVarUint();
      const values: unknown[] = [];
      for (let index = 0; index < count; index += 1) values.push(decodeValue(reader, depth + 1, maxDepth));
      return values;
    }
    case VALUE_OBJECT: {
      const count = reader.readVarUint();
      const result: Record<string, unknown> = {};
      let previousKey = '';
      for (let index = 0; index < count; index += 1) {
        const key = decodeValue(reader, depth + 1, maxDepth);
        if (typeof key !== 'string' || (index > 0 && key <= previousKey)) {
          throw new BinaryCodecError('invalid-value', 'Binary object keys are not in canonical order');
        }
        previousKey = key;
        // defineProperty keeps the data model exact even for the special
        // object key "__proto__"; direct assignment would invoke the legacy
        // prototype setter instead of creating an own property.
        Object.defineProperty(result, key, {
          value: decodeValue(reader, depth + 1, maxDepth),
          enumerable: true,
          configurable: true,
          writable: true,
        });
      }
      return result;
    }
    case VALUE_BIGINT: {
      const negative = reader.readByte();
      if (negative !== 0 && negative !== 1) throw new BinaryCodecError('invalid-value', 'Invalid bigint sign');
      const magnitude = decodeUnsignedBigInt(reader);
      return negative === 1 ? -magnitude : magnitude;
    }
    default: throw new BinaryCodecError('invalid-value', 'Unknown binary value tag');
  }
}

function concatBytes(...parts: readonly Uint8Array[]): Uint8Array {
  const length = parts.reduce((sum, part) => sum + part.length, 0);
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

function readHeader(bytes: Uint8Array): { header: BinarySidecarHeader; payload: Uint8Array } {
  const reader = new ByteReader(bytes);
  const magic = reader.readBytes(MAGIC_BYTES.length);
  if (!MAGIC_BYTES.every((byte, index) => byte === magic[index])) {
    throw new BinaryCodecError('invalid-header', `Invalid sidecar magic; expected ${GUEST_SIMULATION_SIDECAR_MAGIC}`);
  }
  const formatVersion = reader.readVarUint();
  if (formatVersion !== GUEST_SIMULATION_SIDECAR_FORMAT_VERSION) {
    throw new BinaryCodecError('unsupported-version', `Unsupported sidecar format version ${formatVersion}`);
  }
  const algorithm = decodeText(reader.readBytes(reader.readVarUint()));
  if (algorithm.length === 0) throw new BinaryCodecError('invalid-header', 'Sidecar hash algorithm is empty');
  const payloadLength = reader.readVarUint();
  const contentHash = decodeText(reader.readBytes(reader.readVarUint()));
  if (contentHash.length === 0) throw new BinaryCodecError('invalid-header', 'Sidecar content hash is empty');
  const payload = reader.readBytes(payloadLength);
  if (reader.remaining !== 0) throw new BinaryCodecError('invalid-header', 'Trailing bytes after sidecar payload');
  return {
    header: { magic: GUEST_SIMULATION_SIDECAR_MAGIC, formatVersion: GUEST_SIMULATION_SIDECAR_FORMAT_VERSION,
      hashAlgorithm: algorithm, payloadLength, contentHash }, payload,
  };
}

export function readBinarySidecarHeader(bytes: Uint8Array): BinarySidecarHeader {
  return readHeader(bytes).header;
}

export class BinarySidecarCodec<T = unknown> {
  readonly hasher: BinaryContentHasher;
  readonly maxDepth: number;

  constructor(options: BinaryCodecOptions = {}) {
    this.hasher = options.hasher ?? portableContentHasher;
    assertHasher(this.hasher);
    this.maxDepth = options.maxDepth ?? 128;
    if (!Number.isSafeInteger(this.maxDepth) || this.maxDepth < 1) throw new RangeError('maxDepth must be a positive safe integer');
  }

  encode(value: T): Uint8Array {
    const payloadWriter = new ByteWriter();
    encodeValue(payloadWriter, value, 0, this.maxDepth);
    const payload = payloadWriter.toBytes();
    const contentHash = this.hasher.hash(payload);
    if (typeof contentHash !== 'string' || contentHash.length === 0) throw new TypeError('Content hasher returned an empty hash');
    const algorithmBytes = encodeText(this.hasher.algorithm);
    const hashBytes = encodeText(contentHash);
    const headerWriter = new ByteWriter();
    headerWriter.writeBytes(MAGIC_BYTES);
    headerWriter.writeVarUint(GUEST_SIMULATION_SIDECAR_FORMAT_VERSION);
    headerWriter.writeVarUint(algorithmBytes.length);
    headerWriter.writeBytes(algorithmBytes);
    headerWriter.writeVarUint(payload.length);
    headerWriter.writeVarUint(hashBytes.length);
    headerWriter.writeBytes(hashBytes);
    return concatBytes(headerWriter.toBytes(), payload);
  }

  decode(bytes: Uint8Array): T {
    if (!(bytes instanceof Uint8Array)) throw new TypeError('Binary sidecar must be a Uint8Array');
    const { header, payload } = readHeader(bytes);
    if (header.hashAlgorithm !== this.hasher.algorithm) {
      throw new BinaryCodecError('unsupported-version', `No content hasher is available for ${header.hashAlgorithm}`);
    }
    const actualHash = this.hasher.hash(payload);
    if (actualHash !== header.contentHash) {
      throw new BinaryCodecError('checksum-mismatch', `Sidecar content hash mismatch: expected ${header.contentHash}, got ${actualHash}`);
    }
    const reader = new ByteReader(payload);
    const value = decodeValue(reader, 0, this.maxDepth);
    if (reader.remaining !== 0) throw new BinaryCodecError('invalid-value', 'Trailing bytes in sidecar value');
    return value as T;
  }
}

export function encodeBinarySidecar<T>(value: T, options: BinaryCodecOptions = {}): Uint8Array {
  return new BinarySidecarCodec<T>(options).encode(value);
}

export function decodeBinarySidecar<T>(bytes: Uint8Array, options: BinaryCodecOptions = {}): T {
  return new BinarySidecarCodec<T>(options).decode(bytes);
}

/** Friendly aliases for adapters that call the format a checkpoint codec. */
export const BinaryCheckpointCodec = BinarySidecarCodec;
export const encodeCheckpointBinary = encodeBinarySidecar;
export const decodeCheckpointBinary = decodeBinarySidecar;
