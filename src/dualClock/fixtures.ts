import type { TerrainRecord } from '../types/terrain';
import type { SavedTrail } from '../types/trails';
import type { LiftEdge, TrailEdge } from '../network';
import type { ResolvedWeatherHour } from '../weather/weatherModel';
import type { DualInitialization } from './model';

export function dualFixture(demand = 1000, dimension = 8): DualInitialization {
  const y = 1000 / 111320, x = 30 / (111320 * Math.cos((45 + y / 2) * Math.PI / 180));
  const bounds = { west: -120, east: -120 + x, south: 45, north: 45 + y };
  const base: [number, number] = [-120 + x / 2, 45 + y * 0.001], top: [number, number] = [-120 + x / 2, 45 + y * 0.999];
  const trail: SavedTrail = { id: 'run', name: 'Test Run', brushWidthM: 30, areaM2: 30000, lengthM: 1000,
    verticalM: 300, avgSlopeDeg: 20, maxSlopeDeg: 20, difficulty: 'blue', status: 'complete', createdAt: '',
    parts: [{ polygon: [[[bounds.west, bounds.south], [bounds.east, bounds.south], [bounds.east, bounds.north], [bounds.west, bounds.north], [bounds.west, bounds.south]]],
      centerline: [top, base], centerlineElevM: [1300, 1000] }] };
  const lift: LiftEdge = { id: 'lift-edge', liftId: 'lift', liftName: 'Test Lift', kind: 'lift', from: 'base', to: 'top',
    path: [base, top], liftTypeId: 'fixed-grip-quad', lengthM: 1000, travelTimeS: 300, rideTimeS: 300,
    condition: 'open', open: true, verticalM: 300, capacityPph: 2400, peopleWaiting: 0, waitTimeS: 0,
    status: 'complete', planned: false, orientationResolved: true, servesTrailIds: ['run'] };
  const edge: TrailEdge = { id: 'trail-edge', kind: 'trail', from: 'top', to: 'base', trailId: 'run', trailName: 'Test Run',
    partIndex: 0, segmentIndex: 0, path: [top, base], elevM: [1300, 1000], difficulty: 'blue', avgSlopeDeg: 20, maxSlopeDeg: 20,
    verticalM: 300, netVerticalM: 300, areaM2: 30000, brushWidthM: 30, lengthM: 1000, travelTimeS: 300,
    condition: 'open', open: true, status: 'complete', planned: false, elevationResolved: true, traverse: false };
  return { seed: 'dual-test', at: '2026-11-02T08:00:00.000Z', timezone: 'UTC', terrain: null, weather: [],
    snow: { bounds, width: dimension, height: dimension, depthM: new Float32Array(dimension * dimension).fill(0.5), surface: new Uint8Array(dimension * dimension).fill(1) },
    resort: { revision: 1, edges: [lift, edge], trails: [trail], portal: { id: 'portal', nodeId: 'base', lngLat: base, capacityPerMinute: 1000 },
      dailyDemand: demand, ticketPriceCents: 10000, amenities: [] } };
}
export function fixtureTerrain(input: DualInitialization): TerrainRecord {
  return { schemaVersion: 6, key: 'dual-terrain', mountainName: 'Test', latitude: 45, longitude: -120, areaSizeMeters: 1000,
    bounds: input.snow!.bounds, sampleGridSize: 2, sampleHeights: [1000, 1000, 1300, 1300], climate: { monthly: [] },
    sourceType: 'live', createdAt: '', updatedAt: '' };
}
export function fixtureHour(at: string, temperatureC = -5, precipitationMm = 0): ResolvedWeatherHour {
  return { at, temperatureC, precipitationMm, wetBulbC: temperatureC - 1, humidityPct: 90,
    precipitationType: precipitationMm ? 'snow' : 'none', snowfallCm: precipitationMm, windSpeedKph: 10, windGustKph: 15,
    windDirectionDeg: 0, cloudCoverPct: 50, visibilityKm: 10, pressureHpa: 900, radiationWm2: 0,
    windUms: 0, windVms: 0, snowWaterEquivalentMm: precipitationMm, globalRadiationWm2: 0,
    directRadiationWm2: 0, diffuseRadiationWm2: 0, cloudTransmissionPct: 60, solarElevationDeg: 0, solarAzimuthDeg: 0,
    provenance: { fieldFlags: 0, fields: {} } };
}
