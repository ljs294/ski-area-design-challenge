import type { SavedPond } from '../types';

/** A false value is deliberate; omitted values remain compatible snowmaking ponds. */
export function isStandalonePond(ponds: readonly SavedPond[], id: string): boolean {
  return ponds.find((pond) => pond.id === id)?.isSnowmaking === false;
}
