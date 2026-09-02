import { describe, expect, it } from 'vitest';
import {
  buildingBearingBetween,
  buildingDraftMapData,
  buildingFromDraft,
  canConfirmBuilding,
  IDLE_BUILDING_TOOL,
  reduceBuildingTool,
  type BuildingReviewDraft,
} from './buildingControllerModel';
import { defaultBuildingDraft } from '../buildings';
import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';

const CENTER: [number, number] = [-121.5, 46.9];
const SITE: BuildingSiteAnalysisResult = {
  geometryKey: 'site-key', terrainRevision: 3, baseElevationChecksum: 'elevation-a',
  foundationMode: 'slope', bearingDeg: 90, center: CENTER,
  dimensions: { lengthM: 18.288, widthM: 12.192, eaveHeightM: 4.8768 },
  finishedFloorElevationM: 100.1524, finishedFloorM: 100.1524,
  perimeterSamples: [], perimeterElevationsM: [90, 91, 92, 93, 94, 95, 96, 97],
  footprintRing: [], padRing: [], apronRing: [], patchIndices: new Uint32Array(),
  patchHeights: new Float32Array(), contourSegments: [], editedContourSegments: [],
  contourGridSize: 2, contourIntervalM: 6, disturbancePolygons: [],
  earthwork: { cutM3: 0, fillM3: 0, balanceM3: 0 }, terrainGraded: false,
  pumpNodeElevationM: 100.1524, terrainPatch: {} as never,
  foundation: { kind: 'slope', mode: 'slope', finishedFloorElevationM: 100.1524,
    perimeterSamples: [], perimeterGroundElevationsM: [90, 91, 92, 93, 94, 95, 96, 97],
    terrainGraded: false, earthwork: { cutM3: 0, fillM3: 0, balanceM3: 0 } },
};

function reviewed(): BuildingReviewDraft {
  const values = defaultBuildingDraft(CENTER);
  return {
    ...values, siteStatus: 'ok', siteError: null, siteAnalysis: SITE,
    siteIdentity: { geometryKey: 'site-key', terrainRevision: 3, elevationChecksum: 'elevation-a' },
    hasCollision: false, confirmationError: null,
  };
}

describe('building controller model', () => {
  it('places the full preview, fixes center, and locks a heading only after one metre', () => {
    const armed = reduceBuildingTool(IDLE_BUILDING_TOOL, { type: 'arm' });
    const hover = reduceBuildingTool(armed, { type: 'move', point: CENTER });
    expect(buildingDraftMapData(hover)).toMatchObject({ center: CENTER, lengthM: 18.288, widthM: 12.192 });
    const centered = reduceBuildingTool(hover, { type: 'center', point: CENTER });
    const tooClose = reduceBuildingTool(centered, { type: 'lock', point: CENTER });
    expect(tooClose).toBe(centered);
    const direction: [number, number] = [CENTER[0] + 0.001, CENTER[1]];
    const review = reduceBuildingTool(centered, { type: 'lock', point: direction });
    expect(review).toMatchObject({ phase: 'review', draft: {
      center: CENTER, siteStatus: 'pending',
    } });
    if (review.phase !== 'review') throw new Error('review expected');
    expect(review.draft.bearingDeg).toBeCloseTo(90, 3);
  });

  it('normalizes headings and invalidates site work for geometry edits', () => {
    const state = { phase: 'review' as const, draft: reviewed() };
    const patched = reduceBuildingTool(state, { type: 'patch', patch: {
      bearingDeg: -90, dimensions: { ...state.draft.dimensions, widthM: 10 },
    } });
    expect(patched).toMatchObject({ phase: 'review', draft: {
      bearingDeg: 270, siteStatus: 'pending', siteAnalysis: null, siteIdentity: null,
    } });
    expect(buildingBearingBetween(CENTER, [CENTER[0], CENTER[1] + 0.001])).toBeCloseTo(0, 5);
  });

  it('retains review after analysis failures and resolves a slope building', () => {
    const review = reduceBuildingTool(
      reduceBuildingTool(IDLE_BUILDING_TOOL, { type: 'arm' }),
      { type: 'center', point: CENTER },
    );
    const locked = reduceBuildingTool(review, { type: 'lock', point: [CENTER[0] + 0.001, CENTER[1]] });
    const failed = reduceBuildingTool(locked, { type: 'site-failed', error: 'out of bounds' });
    expect(failed).toMatchObject({ phase: 'review', draft: { siteStatus: 'error', siteError: 'out of bounds' } });
    const succeeded = reduceBuildingTool(failed, { type: 'site-succeeded', result: SITE,
      identity: { geometryKey: 'site-key', terrainRevision: 3, elevationChecksum: 'elevation-a' } });
    if (succeeded.phase !== 'review') throw new Error('review expected');
    expect(canConfirmBuilding(succeeded.draft)).toBe(true);
    const building = buildingFromDraft(succeeded.draft, [], 'building-1', 'pump-1', '2026-01-01T00:00:00.000Z');
    expect(building).toMatchObject({ id: 'building-1', connection: { nodeId: 'pump-1' }, foundation: {
      kind: 'slope', finishedFloorElevationM: 100.1524,
    } });
  });

  it('cancels from every phase to the stable idle singleton', () => {
    const armed = reduceBuildingTool(IDLE_BUILDING_TOOL, { type: 'open' });
    const centered = reduceBuildingTool(armed, { type: 'anchor', point: CENTER });
    expect(reduceBuildingTool(centered, { type: 'cancel' })).toBe(IDLE_BUILDING_TOOL);
  });
});
