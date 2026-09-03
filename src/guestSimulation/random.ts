/**
 * Stateless, keyed randomness for guest simulation decisions.
 *
 * A decision is identified by all four of these values: the world seed, the
 * entity, a domain tag, and its ordinal within that domain.  There is no
 * mutable stream to accidentally share between systems, so inserting a draw
 * in one system cannot move draws made by another system.
 */

export type RandomKeyPart = string | number | bigint;
export type RandomSeed = RandomKeyPart;
export type RandomEntityId = RandomKeyPart;

export interface KeyedRandomKey {
  readonly worldSeed: RandomSeed;
  readonly entityId: RandomEntityId;
  readonly domainTag: string;
  readonly decisionOrdinal: number;
}

const UINT32_RANGE = 0x1_0000_0000;

function canonicalPart(value: RandomKeyPart, label: string): string {
  if (typeof value === 'number' && !Number.isFinite(value)) {
    throw new RangeError(`${label} must be finite`);
  }
  if (typeof value === 'number' && !Number.isInteger(value)) {
    throw new RangeError(`${label} must be an integer when supplied as a number`);
  }
  return typeof value === 'bigint' ? `${value}n` : String(value);
}

function validateOrdinal(ordinal: number): void {
  if (!Number.isSafeInteger(ordinal) || ordinal < 0) {
    throw new RangeError('decisionOrdinal must be a non-negative safe integer');
  }
}

/** FNV-1a over UTF-16 code units, with a length prefix for each key part. */
function mixString(hash: number, value: string): number {
  let next = hash;
  let length = value.length >>> 0;
  for (let shift = 0; shift < 32; shift += 8) {
    next ^= (length >>> shift) & 0xff;
    next = Math.imul(next, 0x0100_0193) >>> 0;
  }
  for (let index = 0; index < value.length; index += 1) {
    const codeUnit = value.charCodeAt(index);
    next ^= codeUnit & 0xff;
    next = Math.imul(next, 0x0100_0193) >>> 0;
    next ^= codeUnit >>> 8;
    next = Math.imul(next, 0x0100_0193) >>> 0;
  }
  return next >>> 0;
}

function keyHash(key: KeyedRandomKey): number {
  validateOrdinal(key.decisionOrdinal);
  let hash = 0x811c_9dc5;
  hash = mixString(hash, canonicalPart(key.worldSeed, 'worldSeed'));
  hash = mixString(hash, canonicalPart(key.entityId, 'entityId'));
  hash = mixString(hash, key.domainTag);
  hash = mixString(hash, String(key.decisionOrdinal));
  return hash >>> 0;
}

/**
 * PCG's 32-bit output permutation applied to a keyed one-step state.
 * Math.imul is used for every 32-bit multiplication so this is independent
 * of the host's floating-point multiplication precision.
 */
function pcgHash(hash: number): number {
  let state = (hash + 0x9e37_79b9) >>> 0;
  state = (Math.imul(state, 747_796_405) + 2_891_336_453) >>> 0;
  const rotation = (state >>> 28) + 4;
  const word = Math.imul(((state >>> rotation) ^ state) >>> 0, 277_803_737) >>> 0;
  return (word ^ (word >>> 22)) >>> 0;
}

function asKey(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal: number,
): KeyedRandomKey {
  if (typeof domainTag !== 'string' || domainTag.length === 0) {
    throw new TypeError('domainTag must be a non-empty string');
  }
  return { worldSeed, entityId, domainTag, decisionOrdinal };
}

/** Return the deterministic unsigned 32-bit draw for one decision key. */
export function keyedRandomUint32(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal = 0,
): number {
  return pcgHash(keyHash(asKey(worldSeed, entityId, domainTag, decisionOrdinal)));
}

/** Return a deterministic value in the half-open interval [0, 1). */
export function keyedRandomFloat(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal = 0,
): number {
  return uniformOpen01(worldSeed, entityId, domainTag, decisionOrdinal);
}

/**
 * Return a deterministic uniform value in the open interval (0, 1).
 * The half-unit offset gives both endpoints a non-zero margin while retaining
 * all 32 bits of the keyed source draw for CDF sampling.
 */
export function uniformOpen01(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal = 0,
): number {
  return (keyedRandomUint32(worldSeed, entityId, domainTag, decisionOrdinal) + 0.5) / UINT32_RANGE;
}

/** Aliases kept for callers that name the open-interval draw explicitly. */
export const keyedRandomFloat01 = uniformOpen01;
export const randomFloat01 = uniformOpen01;

/**
 * Extract the high 24 bits used by compact arrival CDF tables.  Keeping this
 * as an integer draw avoids a second rounding decision at the CDF boundary.
 */
export function keyedRandomUint24(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal = 0,
): number {
  return keyedRandomUint32(worldSeed, entityId, domainTag, decisionOrdinal) >>> 8;
}

export const randomUint24 = keyedRandomUint24;

function validateBounds(minimum: number, maximum: number): number {
  if (!Number.isSafeInteger(minimum) || !Number.isSafeInteger(maximum)) {
    throw new RangeError('bounded random limits must be safe integers');
  }
  if (maximum < minimum) throw new RangeError('bounded random maximum must be >= minimum');
  const range = maximum - minimum + 1;
  if (range < 1 || range > UINT32_RANGE) {
    throw new RangeError('bounded random range must fit in an unsigned 32-bit draw');
  }
  return range;
}

/**
 * Return an unbiased integer in [minimum, maximum] using rejection sampling.
 * Rejected candidates consume private attempt ordinals, never a shared stream.
 */
export function keyedRandomInt(
  worldSeed: RandomSeed,
  entityId: RandomEntityId,
  domainTag: string,
  decisionOrdinal: number,
  minimum: number,
  maximum: number,
): number {
  const range = validateBounds(minimum, maximum);
  validateOrdinal(decisionOrdinal);
  const limit = UINT32_RANGE - (UINT32_RANGE % range);
  for (let attempt = 0; ; attempt += 1) {
    const attemptOrdinal = decisionOrdinal + attempt;
    if (!Number.isSafeInteger(attemptOrdinal)) {
      throw new RangeError('bounded random decision ordinal overflowed');
    }
    const candidate = keyedRandomUint32(worldSeed, entityId, domainTag, attemptOrdinal);
    if (candidate < limit) return minimum + (candidate % range);
  }
}

/** Alias for callers that prefer the explicit inclusive naming. */
export const keyedRandomIntInclusive = keyedRandomInt;

/**
 * A convenient world-scoped facade. Methods remain keyed and stateless; the
 * facade stores only the immutable world seed.
 */
export class KeyedRandom {
  readonly worldSeed: RandomSeed;

  constructor(worldSeed: RandomSeed) {
    canonicalPart(worldSeed, 'worldSeed');
    this.worldSeed = worldSeed;
  }

  uint32(entityId: RandomEntityId, domainTag: string, decisionOrdinal = 0): number {
    return keyedRandomUint32(this.worldSeed, entityId, domainTag, decisionOrdinal);
  }

  float01(entityId: RandomEntityId, domainTag: string, decisionOrdinal = 0): number {
    return uniformOpen01(this.worldSeed, entityId, domainTag, decisionOrdinal);
  }

  uint24(entityId: RandomEntityId, domainTag: string, decisionOrdinal = 0): number {
    return keyedRandomUint24(this.worldSeed, entityId, domainTag, decisionOrdinal);
  }

  intInclusive(
    entityId: RandomEntityId,
    domainTag: string,
    decisionOrdinal: number,
    minimum: number,
    maximum: number,
  ): number {
    return keyedRandomInt(this.worldSeed, entityId, domainTag, decisionOrdinal, minimum, maximum);
  }

  int(
    entityId: RandomEntityId,
    domainTag: string,
    decisionOrdinal: number,
    minimum: number,
    maximum: number,
  ): number {
    return this.intInclusive(entityId, domainTag, decisionOrdinal, minimum, maximum);
  }
}

export function createKeyedRandom(worldSeed: RandomSeed): KeyedRandom {
  return new KeyedRandom(worldSeed);
}

/** Short factory spelling useful at simulation call sites. */
export const createKeyedRng = createKeyedRandom;

/** Exported for checksum/golden-vector tests and other deterministic domains. */
export function randomKeyHash(key: KeyedRandomKey): number {
  return pcgHash(keyHash(key));
}
