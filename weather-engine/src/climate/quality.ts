import type { ObservingStationMetadataV1 } from '../contracts.ts';

export interface StationCandidateInputV1 {
  id: string;
  sourceIds: readonly string[];
  name: string;
  latitude: number;
  longitude: number;
  elevationM: number;
  timezone: string;
  temperatureCompleteness: number;
  dewPointCompleteness: number;
  windCompleteness: number;
  trainingOverlap: number;
}

function radians(value: number): number { return value * Math.PI / 180; }

export function distanceKm(latitudeA: number, longitudeA: number, latitudeB: number, longitudeB: number): number {
  const latitudeDelta = radians(latitudeB - latitudeA);
  const longitudeDelta = radians(longitudeB - longitudeA);
  const a = Math.sin(latitudeDelta / 2) ** 2 + Math.cos(radians(latitudeA)) * Math.cos(radians(latitudeB)) * Math.sin(longitudeDelta / 2) ** 2;
  return 6_371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

export function scoreStationCandidates(
  latitude: number,
  longitude: number,
  comparisonElevationM: number,
  candidates: readonly StationCandidateInputV1[],
): readonly ObservingStationMetadataV1[] {
  return candidates.map((candidate) => {
    const distance = distanceKm(latitude, longitude, candidate.latitude, candidate.longitude);
    const coreCompleteness = (candidate.temperatureCompleteness * 0.5 + candidate.dewPointCompleteness * 0.25 + candidate.windCompleteness * 0.25);
    const distanceScore = Math.max(0, 1 - distance / 150);
    const elevationScore = Math.max(0, 1 - Math.abs(candidate.elevationM - comparisonElevationM) / 1_500);
    const score = coreCompleteness * 0.4 + distanceScore * 0.25 + elevationScore * 0.2 + candidate.trainingOverlap * 0.15;
    return { id: candidate.id, sourceIds: candidate.sourceIds, name: candidate.name,
      latitude: candidate.latitude, longitude: candidate.longitude, elevationM: candidate.elevationM,
      timezone: candidate.timezone, distanceKm: distance, score };
  }).filter((candidate) => candidate.distanceKm <= 150)
    .sort((left, right) => right.score - left.score || left.distanceKm - right.distanceKm || left.id.localeCompare(right.id));
}

export function eligiblePrimaryCandidate(candidate: StationCandidateInputV1): boolean {
  return candidate.temperatureCompleteness >= 0.9 && candidate.dewPointCompleteness >= 0.75 &&
    candidate.windCompleteness >= 0.75 && candidate.trainingOverlap >= 0.8;
}
