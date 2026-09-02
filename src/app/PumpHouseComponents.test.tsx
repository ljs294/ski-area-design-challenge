import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';
import type { SavedBuilding } from '../types/buildings';
import { PumpHouseDetail } from './PumpHouseDetail';
import { PumpHouseOverview } from './PumpHouseOverview';
import { PumpHouseReview } from './PumpHouseReview';
import type { BuildingReviewDraft } from './buildingControllerModel';

const noop = vi.fn();

const building: SavedBuilding = {
  id: 'building-1', name: 'Valley Pump House', buildingTypeId: 'snowmaking-pump-house',
  generatorVersion: 1, center: [-121.495, 46.905], bearingDeg: 90,
  dimensions: { lengthM: 18.288, widthM: 12.192, eaveHeightM: 4.8768 },
  roof: { kind: 'gable', pitchRise: 4, pitchRun: 12 },
  foundation: { kind: 'flattened', finishedFloorElevationM: 1000, terrainGraded: true,
    earthwork: { cutM3: 10, fillM3: 8, balanceM3: 2 } },
  connection: { kind: 'snowmaking-pump', nodeId: 'pump-1' },
  economics: { capitalCostUsd: null, maintenanceCostUsd: null, maintenanceCadence: 'unspecified' },
  createdAt: '2026-01-01T00:00:00.000Z',
};

const pendingDraft: BuildingReviewDraft = {
  buildingTypeId: 'snowmaking-pump-house', name: 'Pump House 1', center: building.center,
  bearingDeg: 45, dimensions: building.dimensions, foundationMode: 'flattened',
  siteStatus: 'pending', siteError: null, siteAnalysis: null, siteIdentity: null,
  hasCollision: false, confirmationError: null,
};

const validAnalysis = {
  geometryKey: 'geometry', terrainRevision: 1, baseElevationChecksum: 'checksum',
  foundationMode: 'flattened', bearingDeg: 45, center: building.center,
  dimensions: building.dimensions, finishedFloorElevationM: 1000, finishedFloorM: 1000,
  perimeterSamples: [], perimeterElevationsM: [], footprintRing: [], padRing: [], apronRing: [],
  patchIndices: new Uint32Array(), patchHeights: new Float32Array(), contourSegments: [],
  editedContourSegments: [], contourGridSize: 2, contourIntervalM: 6.096,
  disturbancePolygons: [], earthwork: { cutM3: 10, fillM3: 8, balanceM3: 2 }, terrainGraded: true,
  pumpNodeElevationM: 1000, terrainPatch: {},
  foundation: { kind: 'flattened', mode: 'flattened', finishedFloorElevationM: 1000,
    perimeterSamples: [], perimeterGroundElevationsM: [], terrainGraded: true,
    earthwork: { cutM3: 10, fillM3: 8, balanceM3: 2 } },
} as unknown as BuildingSiteAnalysisResult;

describe('PumpHouseOverview', () => {
  it('exposes the building subsection and deterministic list selector', () => {
    const html = renderToStaticMarkup(<PumpHouseOverview buildings={[building]} units="imperial"
      onArm={noop} onSelect={noop} />);
    expect(html).toContain('data-testid="pump-house-overview"');
    expect(html).toContain('Buildings (1)');
    expect(html).toContain('Build pump house');
    expect(html).toContain('Valley Pump House');
    expect(html).toContain('60 ft');
    expect(html).toContain('90.0°');
  });

  it('shows center and long-axis instructions during placement', () => {
    const html = renderToStaticMarkup(<PumpHouseOverview buildings={[]} units="imperial"
      onArm={noop} onSelect={noop} onCancel={noop}
      tool={{ phase: 'centered', name: 'Pump House 1', buildingTypeId: 'snowmaking-pump-house',
        dimensions: building.dimensions, foundationMode: 'flattened', center: building.center,
        cursor: [-121.494, 46.905], bearingDeg: 90 }} />);
    expect(html).toContain('pointer controls the long-axis direction');
    expect(html).toContain('data-testid="pump-house-placement-preview"');
    expect(html).toContain('Heading');
    expect(html).toContain('60 ft');
  });
});

describe('PumpHouseReview', () => {
  it('renders editable placement inputs and disables confirmation while pending', () => {
    const html = renderToStaticMarkup(<PumpHouseReview draft={pendingDraft} units="imperial"
      onPatch={noop} onConfirm={noop} onCancel={noop} />);
    expect(html).toContain('data-testid="pump-house-review"');
    expect(html).toContain('aria-label="Pump house name"');
    expect(html).toContain('aria-label="Length in ft"');
    expect(html).toContain('aria-label="Heading in degrees"');
    expect(html).toContain('Flatten site');
    expect(html).toContain('Level structure on slope');
    expect(html).toContain('Analyzing site');
    expect(html).toContain('disabled=""');
  });

  it('shows fixed roof, pump, economics, and earthwork after valid analysis', () => {
    const draft = { ...pendingDraft, siteStatus: 'ok' as const, siteAnalysis: validAnalysis };
    const html = renderToStaticMarkup(<PumpHouseReview draft={draft} units="imperial"
      onPatch={noop} onConfirm={noop} onCancel={noop} />);
    expect(html).toContain('Fixed gable · 4:12 pitch');
    expect(html).toContain('22 ft 8 in');
    expect(html).toContain('1,000 hp / 85% efficiency');
    expect(html).toContain('Capital cost');
    expect(html).toContain('>TBD<');
    expect(html).toContain('data-testid="pump-house-site-valid"');
    expect(html).toContain('data-testid="pump-house-analysis"');
    expect(html).toContain('13.1 yd³');
    expect(html).not.toContain('disabled=""');
  });
});

describe('PumpHouseDetail', () => {
  it('keeps authored geometry locked while exposing rename/remove and pump details', () => {
    const html = renderToStaticMarkup(<PumpHouseDetail building={building}
      pump={{ id: 'pump-1', name: 'Pump P1', kind: 'pump', point: building.center,
        elevM: 1000, ownerBuildingId: building.id,
        pumpRating: { horsepowerHp: 1000, efficiency: 0.85 },
        createdAt: building.createdAt }} connectedPipeCount={2} units="imperial"
      onRename={noop} onRemove={noop} onClose={noop} />);
    expect(html).toContain('data-testid="pump-house-detail"');
    expect(html).toContain('Dimensions, heading, roof, and foundation are locked');
    expect(html).toContain('60 ft');
    expect(html).toContain('22 ft 8 in');
    expect(html).toContain('Pump P1');
    expect(html).toContain('1,000 hp / 85% efficiency');
    expect(html).toContain('data-testid="remove-pump-house-start"');
    expect(html).not.toContain('Edit');
  });
});
