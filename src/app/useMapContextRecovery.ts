import { useCallback, useEffect, useRef, useState } from 'react';
import { repairTerrainMapContext } from '../terrainMapContext';
import type { MapContextDecision } from '../terrainIngest';
import type { TerrainRecord } from '../types/terrain';
import type { VectorFeatureSet } from '../types/vectorFeatures';
import type { MapContextProviderError } from '../vectorFeatures';

interface MapContextPublisher {
  publishMapContext(features: VectorFeatureSet, updatedAt: string): unknown;
}

interface MapContextSynchronizer {
  synchronizeData(id: 'analysis'): void;
}

/** Owns both map-context recovery paths: the blocking preparation decision and
 * the later metadata-only repair offered from Settings. */
export function useMapContextRecovery(
  publisher: MapContextPublisher,
  synchronizer: MapContextSynchronizer,
) {
  const [error, setError] = useState<string | null>(null);
  const decisionRef = useRef<((decision: MapContextDecision) => void) | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const repairAbortRef = useRef<AbortController | null>(null);

  const decide = useCallback((decision: MapContextDecision) => {
    const resolve = decisionRef.current;
    if (!resolve) return;
    decisionRef.current = null;
    setError(null);
    resolve(decision);
  }, []);

  const cancelPreparation = useCallback(() => {
    decide('cancel');
    abortRef.current?.abort();
  }, [decide]);
  useEffect(() => () => {
    cancelPreparation();
    repairAbortRef.current?.abort();
  }, [cancelPreparation]);

  const startPreparation = useCallback(() => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    return controller;
  }, []);

  const finishPreparation = useCallback((controller: AbortController) => {
    if (abortRef.current !== controller) return;
    decide('cancel');
    abortRef.current = null;
  }, [decide]);

  const requestDecision = useCallback((failure: MapContextProviderError) =>
    new Promise<MapContextDecision>((resolve) => {
      decisionRef.current = resolve;
      setError(failure.message);
    }), []);

  const repair = useCallback(async (record: TerrainRecord, signal: AbortSignal) => {
    if (record.vectorFeatures) return { ok: true as const };
    repairAbortRef.current?.abort();
    const controller = new AbortController();
    repairAbortRef.current = controller;
    const cancel = () => controller.abort();
    if (signal.aborted) controller.abort();
    else signal.addEventListener('abort', cancel, { once: true });
    try {
      const result = await repairTerrainMapContext(record, undefined, controller.signal);
      if (!result.ok) return result;
      publisher.publishMapContext(result.vectorFeatures, result.updatedAt);
      synchronizer.synchronizeData('analysis');
      return { ok: true as const };
    } finally {
      signal.removeEventListener('abort', cancel);
      if (repairAbortRef.current === controller) repairAbortRef.current = null;
    }
  }, [publisher, synchronizer]);

  return { error, requestDecision, decide, cancelPreparation,
    startPreparation, finishPreparation, repair };
}
