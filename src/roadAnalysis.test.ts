import { describe, expect, it } from 'vitest';
import type { SavedRoad } from './types/roads';
import type { RoadFeature } from './types/vectorFeatures';
import {
  analyzeBuiltRoad,
  analyzeImportedRoad,
  classifyRoadSurface,
  parseRoadLaneCount,
  parseRoadOneWay,
  roadMarkingLines,
} from './roadAnalysis';

const points: [number, number][] = [[-121.5, 46.93], [-121.499, 46.93]];
const feature = (patch: Partial<RoadFeature> = {}): RoadFeature => ({
  id: 'way/1', name: 'Access Road', roadClass: 'minor', highway: 'residential',
  surfaceClass: 'paved', points, ...patch,
});

describe('road analysis', () => {
  it('normalizes pavement, lane counts, and one-way tags', () => {
    expect(classifyRoadSurface(' asphalt ')).toBe('paved');
    expect(classifyRoadSurface('fine_gravel')).toBe('unpaved');
    expect(classifyRoadSurface('chipseal')).toBe('unknown');
    expect(parseRoadLaneCount('4')).toBe(4);
    expect(parseRoadLaneCount('2;3')).toBeUndefined();
    expect(parseRoadLaneCount('13')).toBeUndefined();
    expect(parseRoadOneWay(undefined, 'motorway')).toBe(true);
    expect(parseRoadOneWay('no', 'motorway')).toBe(false);
  });

  it('uses OSM width, lane estimates, and exact kind defaults in order', () => {
    expect(analyzeImportedRoad(feature({ sourceWidthM: 8, lanes: 4 })))
      .toMatchObject({ widthM: 8, widthSource: 'osm', totalLanes: 4 });
    expect(analyzeImportedRoad(feature({ lanes: 3 })))
      .toMatchObject({ widthM: 10.5, widthSource: 'lanes', totalLanes: 3 });
    expect(analyzeImportedRoad(feature({ highway: 'service' })))
      .toMatchObject({ widthM: 5, widthSource: 'default', totalLanes: 1 });
    expect(analyzeImportedRoad(feature({ highway: 'motorway', roadClass: 'major', oneWay: true })))
      .toMatchObject({ widthM: 14, widthSource: 'default', totalLanes: 4 });
    expect(analyzeImportedRoad(feature({ highway: undefined, roadClass: 'major' })))
      .toMatchObject({ widthM: 10.5, widthSource: 'default' });
  });

  it('excludes explicit unpaved surfaces and legacy/path highways', () => {
    expect(analyzeImportedRoad(feature({ surfaceClass: 'unpaved' }))).toBeNull();
    expect(analyzeImportedRoad(feature({ highway: 'track', roadClass: 'path', surfaceClass: 'paved' })))
      .toBeNull();
    expect(analyzeImportedRoad(feature({ highway: undefined, roadClass: 'path', surfaceClass: undefined })))
      .toBeNull();
    expect(analyzeImportedRoad(feature({ surfaceClass: 'unknown' }))).not.toBeNull();
  });

  it('uses stable fallback names and player-built properties', () => {
    expect(analyzeImportedRoad(feature({ name: undefined, highway: 'service' }))?.name)
      .toBe('Unnamed service road');
    const road = { id: 'r1', name: 'Road 1', roadType: 'two-lane', widthM: 7,
      points, lengthM: 75, createdAt: 'now' } as SavedRoad;
    expect(analyzeBuiltRoad(road)).toMatchObject({
      key: 'player:r1', source: 'player', widthM: 7, widthSource: 'player-built',
      totalLanes: 2, forwardLanes: 1, backwardLanes: 1,
    });
  });

  it('creates yellow opposing and white same-direction markings', () => {
    const twoWay = analyzeImportedRoad(feature({ lanes: 4 }))!;
    const markings = roadMarkingLines(twoWay);
    expect(markings.filter((line) => line.kind === 'center')).toHaveLength(1);
    expect(markings.filter((line) => line.kind === 'divider')).toHaveLength(2);

    const oneWay = analyzeImportedRoad(feature({ lanes: 3, oneWay: true }))!;
    const oneWayMarkings = roadMarkingLines(oneWay);
    expect(oneWayMarkings.some((line) => line.kind === 'center')).toBe(false);
    expect(oneWayMarkings.filter((line) => line.kind === 'divider')).toHaveLength(2);
  });

  it('keeps marking geometry bounded by segments and lane count', () => {
    const road = analyzeImportedRoad(feature({ lanes: 4,
      points: [points[0], points[1], [points[1][0], points[1][1] + 0.001]] }))!;
    // The zero-offset center stays one polyline; two offset dividers are
    // emitted per segment so bends never need an unbounded miter join.
    expect(roadMarkingLines(road)).toHaveLength(5);
  });
});
