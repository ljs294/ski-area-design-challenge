import { describe, expect, it } from 'vitest';
import { damFromDraft, IDLE_DAM_TOOL, reduceDamTool, type DraftDam } from './damControllerModel';
import { IDLE_POND_TOOL, pondFromDraft, reducePondTool, type DraftPond } from './pondControllerModel';

const damDraft: DraftDam = {
  name: ' ', points: [[0, 0], [1, 1]], crestElevationM: 100, streamId: 'stream',
  streamName: 'Creek', sourceWidthM: 3, inflowM3s: 1, pondRings: [[[0, 0], [1, 0], [0, 0]]],
  areaM2: 10, averageDepthM: 2, capacityM3: 20, maxDamHeightM: 4,
};

const pondDraft: DraftPond = {
  name: ' ', boundary: [[0, 0], [1, 0], [0, 1], [0, 0]], topElevationM: 100,
  areaM2: 10, averageDepthM: 2, maxDepthM: 3, capacityM3: 20, isSnowmaking: true,
};

describe('snowmaking controller models', () => {
  it('moves dam analysis failure back to the anchored retry state', () => {
    const armed = reduceDamTool(IDLE_DAM_TOOL, { type: 'arm' });
    const anchored = reduceDamTool(armed, { type: 'anchor', point: [0, 0], crestElevationM: 100 });
    const analyzing = reduceDamTool(anchored, { type: 'analyze', points: [[0, 0], [1, 1]] });
    expect(reduceDamTool(analyzing, { type: 'analysis-failed', points: [[0, 0], [1, 1]],
      crestElevationM: 100, error: 'No stream' })).toEqual({
      phase: 'anchored', first: [0, 0], crestElevationM: 100, cursor: null, error: 'No stream',
    });
  });

  it('keeps pond design failure in review with the attempted dimensions', () => {
    let state = reducePondTool(IDLE_POND_TOOL, { type: 'arm' });
    state = reducePondTool(state, { type: 'add-point', point: [0, 0] });
    state = reducePondTool(state, { type: 'review', draft: pondDraft });
    expect(reducePondTool(state, { type: 'design-failed', topElevationM: 101,
      excavationDepthM: 4, error: 'Too steep' })).toMatchObject({
      phase: 'review', error: 'Too steep',
      draft: { topElevationM: 101, excavationDepthM: 4 },
    });
  });

  it('constructs saved structures with fallback names and graded earthwork', () => {
    expect(damFromDraft(damDraft, [], 'dam-1', 'now')).toMatchObject({
      id: 'dam-1', name: 'Dam 1', terrainGraded: true, createdAt: 'now',
    });
    expect(pondFromDraft(pondDraft, {
      crestElevationM: 101, floorElevationM: 98, excavationDepthM: 2,
      cutM3: 5, fillM3: 3, balanceM3: 2, maxBermHeightM: 1, maxCutDepthM: 2,
      bermLengthM: 8, disturbedAreaM2: 15, capacityM3: 20, averageDepthM: 2,
      maxDepthM: 3, coveredSamples: 2, validSamples: 2, truncated: false,
      patchIndices: new Uint32Array(), patchHeights: new Float32Array(),
    }, [], 'pond-1', 'now')).toMatchObject({
      id: 'pond-1', name: 'Pond 1', terrainGraded: true,
      earthwork: { cutM3: 5, fillM3: 3, balanceM3: 2 },
    });
  });
});
