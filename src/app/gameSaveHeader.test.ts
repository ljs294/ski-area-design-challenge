import { expect, it } from 'vitest';
import { gameSaveHeader } from './gameSaveHeader';

it('captures camera and identity once and chooses the explicit simulation writer', () => {
  const options = { base: null, name: ' Test ', terrainKey: 'terrain', dual: true, is3D: true,
    createId: () => 'new', now: '2026-01-01T00:00:00Z', map: {
      getCenter: () => ({ lng: -120, lat: 45 }), getZoom: () => 14, getBearing: () => 20, getPitch: () => 30 } };
  expect(gameSaveHeader(options)).toMatchObject({ schemaVersion: 17, key: 'new', name: 'Test', terrainKey: 'terrain',
    center: [-120, 45], zoom: 14, bearing: 20, pitch: 30, createdAt: options.now, updatedAt: options.now });
  expect(gameSaveHeader({ ...options, dual: false }).schemaVersion).toBe(16);
});
