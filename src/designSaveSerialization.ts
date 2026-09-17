const TYPED_ARRAY = '__mountainPlannerTypedArray';

interface EncodedTypedArray {
  [TYPED_ARRAY]: 'Float32Array' | 'Uint8Array';
  values: number[];
}

export function stringifyDesignStorage(value: unknown): string {
  return JSON.stringify(value, (_key, candidate: unknown) => {
    if (candidate instanceof Float32Array) {
      return { [TYPED_ARRAY]: 'Float32Array', values: Array.from(candidate) } satisfies EncodedTypedArray;
    }
    if (candidate instanceof Uint8Array) {
      return { [TYPED_ARRAY]: 'Uint8Array', values: Array.from(candidate) } satisfies EncodedTypedArray;
    }
    return candidate;
  }, 2);
}

export function parseDesignStorage<T>(source: string): T {
  return JSON.parse(source, (_key, candidate: unknown) => {
    if (!candidate || typeof candidate !== 'object') return candidate;
    const encoded = candidate as Partial<EncodedTypedArray>;
    if (!Array.isArray(encoded.values)) return candidate;
    if (encoded[TYPED_ARRAY] === 'Float32Array') return Float32Array.from(encoded.values);
    if (encoded[TYPED_ARRAY] === 'Uint8Array') return Uint8Array.from(encoded.values);
    return candidate;
  }) as T;
}
