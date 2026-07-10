// Generic spatial tile index for world-space line segments — bins segments
// into a tilesPerAxis x tilesPerAxis grid so per-frame rendering only has to
// touch segments actually near the viewport instead of the full (possibly
// millions-long) segment list every frame. Originally built for contour
// lines (contours.ts); shared here since roads and hydrography lines need
// the exact same viewport-culling problem solved the same way.

export interface Segment {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

export interface TileIndex<T extends Segment> {
  tileSize: number;
  tilesPerAxis: number;
  buckets: T[][];
}

export function buildTileIndex<T extends Segment>(
  segments: T[],
  mapSize: number,
  tilesPerAxis: number
): TileIndex<T> {
  const tileSize = mapSize / tilesPerAxis;
  const buckets: T[][] = Array.from({ length: tilesPerAxis * tilesPerAxis }, () => []);
  const clampTile = (t: number) => Math.max(0, Math.min(tilesPerAxis - 1, t));

  for (const seg of segments) {
    const minX = Math.min(seg.x1, seg.x2);
    const maxX = Math.max(seg.x1, seg.x2);
    const minY = Math.min(seg.y1, seg.y2);
    const maxY = Math.max(seg.y1, seg.y2);
    const tx0 = clampTile(Math.floor(minX / tileSize));
    const tx1 = clampTile(Math.floor(maxX / tileSize));
    const ty0 = clampTile(Math.floor(minY / tileSize));
    const ty1 = clampTile(Math.floor(maxY / tileSize));

    for (let ty = ty0; ty <= ty1; ty++) {
      for (let tx = tx0; tx <= tx1; tx++) {
        buckets[ty * tilesPerAxis + tx].push(seg);
      }
    }
  }

  return { tileSize, tilesPerAxis, buckets };
}

/** Segments whose tile(s) overlap the given world-space rectangle. A
 * segment spanning multiple tiles may appear more than once — harmless for
 * stroking (a redundant stroke of the same hairline), and cheaper than
 * deduplicating on every frame. */
export function queryTileIndex<T extends Segment>(
  index: TileIndex<T>,
  minX: number,
  maxX: number,
  minY: number,
  maxY: number
): T[] {
  const clampTile = (t: number) => Math.max(0, Math.min(index.tilesPerAxis - 1, t));
  const tx0 = clampTile(Math.floor(minX / index.tileSize));
  const tx1 = clampTile(Math.floor(maxX / index.tileSize));
  const ty0 = clampTile(Math.floor(minY / index.tileSize));
  const ty1 = clampTile(Math.floor(maxY / index.tileSize));

  const result: T[] = [];
  for (let ty = ty0; ty <= ty1; ty++) {
    for (let tx = tx0; tx <= tx1; tx++) {
      const bucket = index.buckets[ty * index.tilesPerAxis + tx];
      for (let i = 0; i < bucket.length; i++) result.push(bucket[i]);
    }
  }
  return result;
}
