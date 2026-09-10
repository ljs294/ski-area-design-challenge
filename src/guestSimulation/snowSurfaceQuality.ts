/** Shared snow-grid surface interpretation for detailed and aggregate guest conditions. */
export const SNOW_SURFACE_QUALITY: Readonly<Record<number, number>> = Object.freeze({
  1: 0.96, 2: 0.9, 3: 0.94, 4: 0.68, 5: 0.16, 6: 0.84,
  7: 0.42, 8: 0.64, 9: 0.58, 10: 0.5, 11: 0.78,
});
