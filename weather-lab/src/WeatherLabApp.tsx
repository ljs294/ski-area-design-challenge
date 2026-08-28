import { useEffect, useRef, useState } from 'react';
import {
  HISTORICAL_DIFFICULTY,
  HISTORICAL_SIMULATION_TUNING,
  SMOOTHED_SIMULATION_TUNING,
  V1_COMPATIBILITY_COMPARISON_STREAM_KEY,
  adjustedConditionTransitionRow,
  adjustedMacroTransitionRow,
  sha256Hex,
} from '../../weather-engine/src/index.ts';
import type {
  HistoricalWeatherSeriesV1,
  LocationClimateModelV1,
  WeatherLabLocationContextV1,
  WeatherLabPreparationV1,
  WeatherLabResultV2,
  WeatherLabRunRequestV2,
  WeatherSimulationTuningV1,
} from '../../weather-engine/src/index.ts';
import { DailyComparisonTable } from './DailyComparisonTable.tsx';
import { DailyMetricChart } from './DailyMetricChart.tsx';
import { DailyStateRibbons } from './DailyStateRibbons.tsx';
import { LocationMap } from './LocationMap.tsx';
import { MarkovDiagnosticsPanel } from './MarkovDiagnosticsPanel.tsx';
import type { WeatherWorkerRequest, WeatherWorkerResponse } from './protocol.ts';
import { WeatherChart } from './WeatherChart.tsx';
import { WeatherComparisonScorecard } from './WeatherComparisonScorecard.tsx';
import { WeatherEventTimeline } from './WeatherEventTimeline.tsx';
import { WeatherTuningControls, type WeatherTuningPreset } from './WeatherTuningControls.tsx';
import {
  comparisonExportJson,
  dailyComparisonCsv,
  hourlyComparisonCsv,
  hourlySeriesFromResults,
  isWeatherLabResultV2,
  loadStoredTuning,
  monthDateRange,
  parseTuningJson,
  storeTuning,
  tuningJson,
} from './weatherLabViewModel.ts';
import type { DailyComparisonSeries, DailyMetric, EventComparisonSeries } from './weatherLabViewModel.ts';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const SERVICE_URL = (import.meta.env.VITE_WEATHER_SERVICE_URL as string | undefined)?.replace(/\/$/, '') ?? 'http://127.0.0.1:8787';
const RUN_STEPS = ['coverage', 'daymet', 'merra2', 'compiling', 'artifacts', 'simulation', 'forecasting', 'comparison'] as const;
type RunStep = typeof RUN_STEPS[number];
type SimulationRole = 'baseline' | 'candidate';
type RunLogEntry = Readonly<{ id: number; at: string; stage: string; message: string }>;
type PreparedArtifacts = Readonly<{
  key: string;
  model: LocationClimateModelV1;
  observed: HistoricalWeatherSeriesV1;
}>;

const STEP_LABELS: Record<RunStep, string> = {
  coverage: 'Coverage', daymet: 'Daymet daily', merra2: 'NASA POWER / MERRA-2 hourly',
  compiling: 'Climate compilation', artifacts: 'Artifacts', simulation: 'Simulation', forecasting: 'Forecasting', comparison: 'Comparison',
};

function formatDuration(seconds: number | null) {
  if (seconds == null || !Number.isFinite(seconds)) return 'calculating...';
  if (seconds < 60) return `about ${Math.max(1, Math.ceil(seconds))} sec`;
  const minutes = Math.ceil(seconds / 60);
  return minutes < 60 ? `about ${minutes} min` : `about ${Math.floor(minutes / 60)} hr ${minutes % 60} min`;
}

function download(name: string, text: string, type: string) {
  const anchor = document.createElement('a');
  anchor.href = URL.createObjectURL(new Blob([text], { type }));
  anchor.download = name;
  anchor.click();
  URL.revokeObjectURL(anchor.href);
}

function coordinate(value: string, minimum: number, maximum: number): number | null {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= minimum && parsed <= maximum ? parsed : null;
}

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const payload = await response.json() as T & { error?: { message?: string } };
  if (!response.ok) throw new Error(payload.error?.message ?? `Weather service returned ${response.status}`);
  return payload;
}

const wait = (milliseconds: number, signal: AbortSignal) => new Promise<void>((resolve, reject) => {
  const timeout = window.setTimeout(resolve, milliseconds);
  signal.addEventListener('abort', () => {
    window.clearTimeout(timeout);
    reject(new DOMException('Aborted', 'AbortError'));
  }, { once: true });
});

function initialTuning(): WeatherSimulationTuningV1 {
  try {
    return loadStoredTuning(window.localStorage) ?? { ...SMOOTHED_SIMULATION_TUNING };
  } catch {
    return { ...SMOOTHED_SIMULATION_TUNING };
  }
}

export function WeatherLabApp() {
  const workerRef = useRef<Worker | null>(null);
  const activeId = useRef<string | null>(null);
  const activeRole = useRef<SimulationRole | null>(null);
  const operationRef = useRef<AbortController | null>(null);
  const preparationIdRef = useRef<string | null>(null);
  const contextSequence = useRef(0);
  const candidateTimer = useRef<number | null>(null);
  const baselineTimer = useRef<number | null>(null);
  const artifactsRef = useRef<PreparedArtifacts | null>(null);
  const baselineRef = useRef<WeatherLabResultV2 | null>(null);
  const tuningRef = useRef<WeatherSimulationTuningV1>(initialTuning());
  const seedRef = useRef('Historical');

  const [latitudeText, setLatitudeText] = useState('44.167290');
  const [longitudeText, setLongitudeText] = useState('-71.164239');
  const latitude = coordinate(latitudeText, -90, 90);
  const longitude = coordinate(longitudeText, -180, 180);
  const [context, setContext] = useState<WeatherLabLocationContextV1 | null>(null);
  const [contextMessage, setContextMessage] = useState('Checking location...');
  const [elevationOverride, setElevationOverride] = useState(false);
  const [elevationText, setElevationText] = useState('');
  const [validationYear, setValidationYear] = useState<number | null>(null);
  const [seed, setSeed] = useState('Historical');
  const [tuning, setTuning] = useState<WeatherSimulationTuningV1>(tuningRef.current);
  const [tuningNotice, setTuningNotice] = useState<string | null>(null);
  const [status, setStatus] = useState('Enter coordinates to begin');
  const [progress, setProgress] = useState(0);
  const [running, setRunning] = useState(false);
  const [artifacts, setArtifacts] = useState<PreparedArtifacts | null>(null);
  const [baseline, setBaseline] = useState<WeatherLabResultV2 | null>(null);
  const [candidate, setCandidate] = useState<WeatherLabResultV2 | null>(null);
  const [month, setMonth] = useState(1);
  const [dailyMetric, setDailyMetric] = useState<DailyMetric>('temperature');
  const [runLog, setRunLog] = useState<RunLogEntry[]>([]);
  const logSequence = useRef(0);
  const logElement = useRef<HTMLDivElement | null>(null);
  const seenPreparationEvents = useRef(new Set<string>());
  const [runStartedAt, setRunStartedAt] = useState<number | null>(null);
  const [clockNow, setClockNow] = useState(Date.now());
  const [stepProgress, setStepProgress] = useState<Record<RunStep, number>>(
    () => Object.fromEntries(RUN_STEPS.map((step) => [step, 0])) as Record<RunStep, number>,
  );

  function appendLog(stage: string, message: string, at = new Date().toISOString()) {
    const entry = { id: ++logSequence.current, at, stage, message };
    setRunLog((current) => [...current, entry].slice(-300));
  }

  function clearSimulationTimers() {
    if (candidateTimer.current != null) window.clearTimeout(candidateTimer.current);
    if (baselineTimer.current != null) window.clearTimeout(baselineTimer.current);
    candidateTimer.current = null;
    baselineTimer.current = null;
  }

  function stopWorker() {
    workerRef.current?.terminate();
    workerRef.current = null;
    activeId.current = null;
    activeRole.current = null;
  }

  function cancelServerPreparationById(preparationId: string) {
    void fetch(`${SERVICE_URL}/v1/weather-lab/preparations/${encodeURIComponent(preparationId)}`, {
      method: 'DELETE',
    }).catch(() => undefined);
  }

  function cancelServerPreparation() {
    const preparationId = preparationIdRef.current;
    preparationIdRef.current = null;
    if (preparationId) cancelServerPreparationById(preparationId);
  }

  function makeWorker() {
    stopWorker();
    const worker = new Worker(new URL('./weather.worker.ts', import.meta.url), { type: 'module' });
    workerRef.current = worker;
    worker.onerror = () => {
      if (activeId.current) {
        setStatus('Simulation worker failed; run again to replace it.');
        appendLog('failed', 'Simulation worker crashed and will be replaced.');
        setRunning(false);
      }
      stopWorker();
    };
    worker.onmessage = (event: MessageEvent<WeatherWorkerResponse>) => {
      const message = event.data;
      if (message.requestId !== activeId.current) return;
      const role = activeRole.current;
      if (message.type === 'started') {
        setStatus(`Simulating ${role ?? 'weather'} with paired random draws...`);
        appendLog('simulation', `${role ?? 'Weather'} simulation started for ${message.totalHours.toLocaleString()} hourly samples.`);
      }
      if (message.type === 'progress') {
        const fraction = message.completedHours / message.totalHours;
        setProgress(role === 'baseline' ? .55 + fraction * .2 : .76 + fraction * .19);
        setStepProgress((current) => ({ ...current, simulation: role === 'baseline' ? fraction * 50 : 50 + fraction * 50 }));
        appendLog('simulation', `${role ?? 'Weather'} generated ${message.completedHours.toLocaleString()} of ${message.totalHours.toLocaleString()} hours (${Math.round(fraction * 100)}%).`);
      }
      if (message.type === 'phase') {
        setStatus(`${role === 'baseline' ? 'Baseline' : 'Candidate'}: ${message.message}`);
        appendLog(message.phase, `${role ?? 'Weather'}: ${message.message}`);
        if (role === 'candidate' && message.phase === 'forecasting') {
          setProgress(.96);
          setStepProgress((current) => ({ ...current, simulation: 100, forecasting: 50 }));
        }
        if (role === 'candidate' && message.phase === 'comparison') {
          setProgress(.98);
          setStepProgress((current) => ({ ...current, forecasting: 100, comparison: 50 }));
        }
      }
      if (message.type === 'completed') {
        if (!isWeatherLabResultV2(message.result)) {
          setStatus('Failed: the worker returned a legacy result.');
          appendLog('failed', 'Expected a Weather Lab v2 result from the comparison worker.');
          setRunning(false);
          stopWorker();
          return;
        }
        if (role === 'baseline') {
          baselineRef.current = message.result;
          setBaseline(message.result);
          setCandidate(null);
          setStatus('Historical baseline pinned; scheduling candidate...');
          setProgress(.76);
          appendLog('baseline', `Historical baseline pinned. Truth hash ${message.result.truthHash.slice(0, 16)}...`);
          activeId.current = null;
          activeRole.current = null;
          scheduleCandidate(30);
        } else {
          setCandidate(message.result);
          setStatus('Completed');
          setProgress(1);
          setStepProgress((current) => ({ ...current, simulation: 100, forecasting: 100, comparison: 100 }));
          appendLog('completed', `Run completed for candidate. Truth hash ${message.result.truthHash.slice(0, 16)}...`);
          setRunning(false);
          activeId.current = null;
          activeRole.current = null;
          operationRef.current = null;
        }
      }
      if (message.type === 'cancelled') {
        setStatus('Cancelled');
        appendLog('cancelled', `${role ?? 'Weather'} simulation cancelled.`);
        setRunning(false);
        stopWorker();
      }
      if (message.type === 'failed') {
        setStatus(`Failed: ${message.message}`);
        appendLog('failed', `${role ?? 'Weather'}: ${message.message}`);
        setRunning(false);
        stopWorker();
      }
    };
    return worker;
  }

  function startSimulation(role: SimulationRole, nextArtifacts: PreparedArtifacts, resolvedTuning: WeatherSimulationTuningV1) {
    const requestId = crypto.randomUUID();
    const run: WeatherLabRunRequestV2 = {
      version: 2,
      location: nextArtifacts.model.location,
      stationId: nextArtifacts.model.primaryStation.id,
      stationTimeZone: nextArtifacts.model.primaryStation.timezone,
      validationYear: nextArtifacts.observed.validationYear,
      trainingPolicy: nextArtifacts.model.trainingPeriod.policy,
      worldSeed: seedRef.current,
      difficultyProfile: HISTORICAL_DIFFICULTY,
      generatorVersion: 2,
      climateModelHash: nextArtifacts.model.climateModelHash,
      tuning: resolvedTuning,
      comparisonStreamKey: V1_COMPATIBILITY_COMPARISON_STREAM_KEY,
    };
    const worker = makeWorker();
    activeId.current = requestId;
    activeRole.current = role;
    setRunning(true);
    setStatus(`${role === 'baseline' ? 'Pinning historical baseline' : 'Running tuned candidate'}...`);
    appendLog('worker', `Sending ${role} run ${requestId.slice(0, 8)} to the standalone worker.`);
    worker.postMessage({ type: 'run', requestId, run, model: nextArtifacts.model, observed: nextArtifacts.observed } satisfies WeatherWorkerRequest);
  }

  function scheduleCandidate(delay = 400) {
    if (candidateTimer.current != null) window.clearTimeout(candidateTimer.current);
    stopWorker();
    candidateTimer.current = window.setTimeout(() => {
      candidateTimer.current = null;
      const nextArtifacts = artifactsRef.current;
      if (!nextArtifacts || !baselineRef.current) return;
      startSimulation('candidate', nextArtifacts, tuningRef.current);
    }, delay);
  }

  function scheduleHistoricalBaseline(delay = 300) {
    if (baselineTimer.current != null) window.clearTimeout(baselineTimer.current);
    if (candidateTimer.current != null) window.clearTimeout(candidateTimer.current);
    stopWorker();
    candidateTimer.current = null;
    baselineTimer.current = window.setTimeout(() => {
      baselineTimer.current = null;
      const nextArtifacts = artifactsRef.current;
      if (!nextArtifacts) return;
      baselineRef.current = null;
      setBaseline(null);
      setCandidate(null);
      setStepProgress((current) => ({ ...current, simulation: 0, forecasting: 0, comparison: 0 }));
      startSimulation('baseline', nextArtifacts, HISTORICAL_SIMULATION_TUNING);
    }, delay);
  }

  function syncPreparation(preparation: WeatherLabPreparationV1) {
    const additions = (preparation.events ?? []).filter((event) => {
      const key = `${preparation.id}:${event.sequence}`;
      if (seenPreparationEvents.current.has(key)) return false;
      seenPreparationEvents.current.add(key);
      return true;
    }).map((event) => ({ id: ++logSequence.current, at: event.at, stage: event.stage, message: event.message }));
    if (additions.length) setRunLog((current) => [...current, ...additions].slice(-300));
    const detail = preparation.progress.detailTotal
      ? (preparation.progress.detailCompleted ?? 0) / preparation.progress.detailTotal * 100
      : 0;
    setStepProgress((current) => ({
      ...current,
      daymet: ['merra2', 'compiling', 'persisting', 'ready'].includes(preparation.progress.stage) ? 100 : preparation.progress.stage === 'daymet' ? 10 : current.daymet,
      merra2: ['compiling', 'persisting', 'ready'].includes(preparation.progress.stage) ? 100 : preparation.progress.stage === 'merra2' ? detail : current.merra2,
      compiling: ['persisting', 'ready'].includes(preparation.progress.stage) ? 100 : preparation.progress.stage === 'compiling' ? 10 : current.compiling,
      artifacts: preparation.progress.stage === 'ready' ? 100 : preparation.progress.stage === 'persisting' ? 50 : current.artifacts,
    }));
  }

  useEffect(() => {
    if (logElement.current) logElement.current.scrollTop = logElement.current.scrollHeight;
  }, [runLog]);

  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setClockNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [running]);

  useEffect(() => () => {
    cancelServerPreparation();
    operationRef.current?.abort();
    clearSimulationTimers();
    stopWorker();
  }, []);

  useEffect(() => {
    tuningRef.current = tuning;
    try {
      storeTuning(window.localStorage, tuning);
    } catch (error) {
      setTuningNotice(error instanceof Error ? error.message : String(error));
    }
    if (artifactsRef.current && baselineRef.current) scheduleCandidate();
    return () => {
      if (candidateTimer.current != null) window.clearTimeout(candidateTimer.current);
      candidateTimer.current = null;
    };
  }, [tuning]);

  useEffect(() => {
    seedRef.current = seed;
    if (artifactsRef.current && baselineRef.current) scheduleHistoricalBaseline();
    return () => {
      if (baselineTimer.current != null) window.clearTimeout(baselineTimer.current);
      baselineTimer.current = null;
    };
  }, [seed]);

  const contextElevation = elevationOverride ? elevationText : '';
  useEffect(() => {
    const sequence = ++contextSequence.current;
    const controller = new AbortController();
    cancelServerPreparation();
    operationRef.current?.abort();
    clearSimulationTimers();
    stopWorker();
    artifactsRef.current = null;
    baselineRef.current = null;
    setArtifacts(null);
    setBaseline(null);
    setCandidate(null);
    setRunning(false);
    if (latitude == null || longitude == null) {
      setContext(null);
      setValidationYear(null);
      setContextMessage('Enter latitude -90..90 and longitude -180..180');
      return () => controller.abort();
    }
    setContextMessage('Checking Daymet coverage and resolving the MERRA-2 grid...');
    const timeout = window.setTimeout(() => {
      const parameters = new URLSearchParams({ latitude: String(latitude), longitude: String(longitude) });
      if (elevationOverride && elevationText !== '') parameters.set('elevationM', elevationText);
      void json<{ context: WeatherLabLocationContextV1 }>(`${SERVICE_URL}/v1/weather-lab/location-context?${parameters}`, { signal: controller.signal })
        .then(({ context: next }) => {
          if (sequence !== contextSequence.current) return;
          setContext(next);
          setValidationYear((current) => current != null && next.eligibleValidationYears.includes(current)
            ? current
            : next.eligibleValidationYears[0] ?? null);
          if (!elevationOverride && next.resolvedElevationM != null) setElevationText(next.resolvedElevationM.toFixed(0));
          setContextMessage(next.coverage === 'unsupported'
            ? next.coverageReason ?? 'Outside supported coverage'
            : next.selectedStation ? 'Location ready' : next.warnings[0] ?? 'No qualifying source grid');
          setStatus('Ready');
        })
        .catch((error: unknown) => {
          if (controller.signal.aborted || sequence !== contextSequence.current) return;
          setContext(null);
          setValidationYear(null);
          setContextMessage(error instanceof Error ? error.message : String(error));
        });
    }, 350);
    return () => {
      window.clearTimeout(timeout);
      controller.abort();
    };
  }, [contextElevation, elevationOverride, latitude, longitude]);

  function artifactKey(): string | null {
    if (!context || validationYear == null || latitude == null || longitude == null) return null;
    return sha256Hex({
      latitude,
      longitude,
      elevationM: elevationOverride ? Number(elevationText) : context.resolvedElevationM,
      validationYear,
      stationId: context.selectedStation?.id ?? null,
      trainingPolicy: 'prior-30',
    });
  }

  async function run() {
    if (!context || !context.selectedStation || validationYear == null || latitude == null || longitude == null) return;
    const key = artifactKey();
    if (!key) return;
    cancelServerPreparation();
    operationRef.current?.abort();
    clearSimulationTimers();
    stopWorker();
    baselineRef.current = null;
    setBaseline(null);
    setCandidate(null);
    setRunLog([]);
    setRunStartedAt(Date.now());
    setClockNow(Date.now());
    setRunning(true);
    setProgress(.02);
    setStatus('Creating historical preparation...');
    seenPreparationEvents.current.clear();
    setStepProgress(Object.fromEntries(RUN_STEPS.map((step) => [step, step === 'coverage' ? 100 : 0])) as Record<RunStep, number>);
    appendLog('request', `Starting ${validationYear} comparison at ${latitude.toFixed(6)}, ${longitude.toFixed(6)} using ${context.selectedStation.id}.`);
    appendLog('coverage', 'Daymet coverage and provider eligibility confirmed.');
    appendLog('coverage', `Resolved source grid ${context.selectedStation.id} in ${context.timezone}.`);
    appendLog('request', `Training on up to 30 source-complete prior years; seed "${seedRef.current}"; elevation ${elevationOverride ? `${elevationText} m override` : `${context.resolvedElevationM} m resolved`}.`);

    if (artifactsRef.current?.key === key) {
      appendLog('artifacts', 'Reusing cached climate and observation artifacts; provider preparation skipped.');
      setStepProgress(Object.fromEntries(RUN_STEPS.map((step) => [step, ['simulation', 'forecasting', 'comparison'].includes(step) ? 0 : 100])) as Record<RunStep, number>);
      setProgress(.55);
      startSimulation('baseline', artifactsRef.current, HISTORICAL_SIMULATION_TUNING);
      return;
    }

    const controller = new AbortController();
    operationRef.current = controller;
    const preparationRequestId = crypto.randomUUID();
    activeId.current = preparationRequestId;
    preparationIdRef.current = null;
    try {
      const body = {
        version: 1,
        latitude,
        longitude,
        ...(elevationOverride ? { elevationOverrideM: Number(elevationText) } : {}),
        validationYear,
        trainingPolicy: { kind: 'prior-30' as const },
      };
      const created = await json<{ preparation: WeatherLabPreparationV1 }>(`${SERVICE_URL}/v1/weather-lab/preparations`, {
        method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body),
      });
      if (controller.signal.aborted || preparationRequestId !== activeId.current) {
        cancelServerPreparationById(created.preparation.id);
        return;
      }
      preparationIdRef.current = created.preparation.id;
      let preparation = created.preparation;
      syncPreparation(preparation);
      while (!['succeeded', 'failed', 'cancelled'].includes(preparation.status)) {
        setStatus(preparation.progress.message ?? `Preparing: ${preparation.progress.stage}`);
        const detailFraction = preparation.progress.detailTotal
          ? (preparation.progress.detailCompleted ?? 0) / preparation.progress.detailTotal
          : 0;
        setProgress(preparation.progress.stage === 'daymet'
          ? .05
          : preparation.progress.stage === 'merra2'
            ? .1 + detailFraction * .35
            : preparation.progress.completed / preparation.progress.total * .5);
        await wait(250, controller.signal);
        preparation = (await json<{ preparation: WeatherLabPreparationV1 }>(
          `${SERVICE_URL}/v1/weather-lab/preparations/${encodeURIComponent(preparation.id)}`,
          { signal: controller.signal },
        )).preparation;
        syncPreparation(preparation);
      }
      if (preparation.status !== 'succeeded' || !preparation.result) {
        throw new Error(preparation.error?.message ?? `Preparation ${preparation.status}`);
      }
      preparationIdRef.current = null;
      appendLog('artifacts', `Preparation complete. Model ${preparation.result.modelHash.slice(0, 12)}...; observations ${preparation.result.observationHash.slice(0, 12)}...`);
      setStatus('Loading immutable model and observations...');
      setProgress(.52);
      const [model, observed] = await Promise.all([
        json<LocationClimateModelV1>(`${SERVICE_URL}${preparation.result.modelUrl}`, { signal: controller.signal }),
        json<HistoricalWeatherSeriesV1>(`${SERVICE_URL}${preparation.result.observedSeriesUrl}`, { signal: controller.signal }),
      ]);
      if (controller.signal.aborted || preparationRequestId !== activeId.current) return;
      const nextArtifacts = { key, model, observed } satisfies PreparedArtifacts;
      artifactsRef.current = nextArtifacts;
      setArtifacts(nextArtifacts);
      setStepProgress((current) => ({ ...current, artifacts: 100 }));
      appendLog('artifacts', `Cached 12 monthly climate models, ${observed.hours.length.toLocaleString()} hourly observations, and ${(observed.days?.length ?? 0).toLocaleString()} daily anchors.`);
      startSimulation('baseline', nextArtifacts, HISTORICAL_SIMULATION_TUNING);
    } catch (error) {
      if (controller.signal.aborted) return;
      cancelServerPreparation();
      const message = error instanceof Error ? error.message : String(error);
      setStatus(`Failed: ${message}`);
      appendLog('failed', message);
      setRunning(false);
      activeId.current = null;
      activeRole.current = null;
      operationRef.current = null;
    }
  }

  function cancel() {
    operationRef.current?.abort();
    cancelServerPreparation();
    clearSimulationTimers();
    stopWorker();
    setStatus('Cancelled');
    appendLog('cancelled', 'Cancellation requested; stopped provider retrieval and any active simulation worker.');
    setRunning(false);
  }

  function changeValidationYear(nextYear: number) {
    cancelServerPreparation();
    operationRef.current?.abort();
    clearSimulationTimers();
    stopWorker();
    artifactsRef.current = null;
    baselineRef.current = null;
    setArtifacts(null);
    setBaseline(null);
    setCandidate(null);
    setValidationYear(nextYear);
  }

  function applyPreset(preset: WeatherTuningPreset) {
    const next = preset === 'smoothed'
      ? SMOOTHED_SIMULATION_TUNING
      : preset === 'baseline' && baselineRef.current
        ? baselineRef.current.run.tuning
        : HISTORICAL_SIMULATION_TUNING;
    setTuningNotice(null);
    setTuning({ ...next });
  }

  function changeTuning(next: WeatherSimulationTuningV1) {
    setTuningNotice(null);
    setTuning(next);
  }

  function importTuning(text: string) {
    const imported = parseTuningJson(text);
    setTuning(imported);
    setTuningNotice(`Imported tuning "${imported.id}". Candidate rerun queued.`);
  }

  function pinCandidate() {
    if (!candidate) return;
    baselineRef.current = candidate;
    setBaseline(candidate);
    setCandidate(null);
    setStatus('Candidate pinned as baseline; adjust tuning to compare again.');
    appendLog('baseline', `Candidate ${candidate.truthHash.slice(0, 16)}... pinned as the comparison baseline.`);
  }

  const runnable = context?.coverage === 'supported' && context.selectedStation != null && validationYear != null && !running;
  const displayedStatus = running || baseline || candidate || status.startsWith('Failed') || status.startsWith('Cancel') ? status : contextMessage;
  const completedPercent = Math.round(progress * 100);
  const remainingPercent = Math.max(0, 100 - completedPercent);
  const elapsedSeconds = runStartedAt == null ? 0 : Math.max(0, (clockNow - runStartedAt) / 1_000);
  const etaSeconds = running && progress >= .03 && progress < 1 && elapsedSeconds >= 2
    ? elapsedSeconds * (1 - progress) / progress
    : null;
  const exportResult = candidate ?? baseline;
  const exportBase = exportResult
    ? `${exportResult.run.location.latitude.toFixed(4)}_${exportResult.run.location.longitude.toFixed(4)}_${exportResult.run.stationId}_${exportResult.run.validationYear}`.replace(/[^a-z0-9_.-]/gi, '_')
    : 'weather-lab';
  const dailySeries: DailyComparisonSeries | null = baseline && candidate
    ? { observed: candidate.daily.observed, baseline: baseline.daily.simulated, candidate: candidate.daily.simulated }
    : null;
  const eventSeries: EventComparisonSeries | null = baseline && candidate
    ? { observed: candidate.events.observed, baseline: baseline.events.simulated, candidate: candidate.events.simulated }
    : null;
  const dateRange = validationYear == null ? null : monthDateRange(validationYear, month);

  return <main>
    <header>
      <p className="eyebrow">MOUNTAIN PLANNER · STANDALONE</p>
      <h1>Weather Model Lab</h1>
      <p>Prepare real North American history, pin a deterministic baseline, and tune a paired candidate live.</p>
    </header>

    <section className="location-layout">
      <section className="controls coordinate-controls" aria-label="Run controls">
        <label>Latitude<input aria-label="Latitude" value={latitudeText} onChange={(event) => setLatitudeText(event.target.value)} inputMode="decimal"/></label>
        <label>Longitude<input aria-label="Longitude" value={longitudeText} onChange={(event) => setLongitudeText(event.target.value)} inputMode="decimal"/></label>
        <label>Validation year<select aria-label="Validation year" value={validationYear ?? ''} disabled={!context?.eligibleValidationYears.length}
          onChange={(event) => changeValidationYear(Number(event.target.value))}>
          <option value="" disabled>No eligible year</option>
          {context?.eligibleValidationYears.map((year) => <option key={year} value={year}>{year}</option>)}
        </select></label>
        <label>World seed<input value={seed} onChange={(event) => setSeed(event.target.value)}/></label>
        <label className="elevation-control"><span><input type="checkbox" checked={elevationOverride} onChange={(event) => setElevationOverride(event.target.checked)}/> Override elevation</span>
          <input aria-label="Elevation metres" value={elevationText} disabled={!elevationOverride} onChange={(event) => setElevationText(event.target.value)} inputMode="numeric"/>
        </label>
        <button onClick={() => void run()} disabled={!runnable}>Run weather comparison</button>
        <button className="secondary" onClick={cancel} disabled={!running}>Cancel</button>
      </section>
      {latitude != null && longitude != null && <LocationMap latitude={latitude} longitude={longitude}/>}
    </section>

    <section className={`status ${context?.coverage === 'unsupported' ? 'unsupported' : ''}`}>
      <span>{displayedStatus}</span><progress value={progress} max={1}/>
      <small>{context?.selectedStation
        ? `Station ${context.selectedStation.id} · ${context.selectedStation.name} · ${context.selectedStation.distanceKm.toFixed(1)} km · elevation ${elevationOverride ? elevationText : context.resolvedElevationM} m · ${context.timezone}`
        : context?.warnings.join(' · ')}</small>
      {runLog.length > 0 && <>
        <div className="progress-overview">
          <span><strong>{completedPercent}%</strong> complete</span>
          <span><strong>{remainingPercent}%</strong> remaining</span>
          <span><strong>ETA</strong> {running ? formatDuration(etaSeconds) : progress === 1 ? 'complete' : 'stopped'}</span>
        </div>
        <div className="step-progress" aria-label="Progress by step">{RUN_STEPS.map((step) => {
          const completed = Math.max(0, Math.min(100, Math.round(stepProgress[step])));
          return <div className="step-progress-row" key={step}>
            <span>{STEP_LABELS[step]}</span><progress value={completed} max={100}/>
            <small>{completed}% complete · {100 - completed}% remaining</small>
          </div>;
        })}</div>
        <div className="status-console" role="log" aria-label="Weather preparation activity" aria-live="polite" ref={logElement}>
          <div className="status-console-title"><strong>Activity log</strong><span>{running ? 'Working' : status}</span></div>
          {runLog.map((entry) => <div className={`status-console-entry status-console-${entry.stage}`} key={entry.id}>
            <time dateTime={entry.at}>{new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</time>
            <code>{entry.stage}</code><span>{entry.message}</span>
          </div>)}
        </div>
      </>}
    </section>

    {artifacts && <section className="panel tuning-panel">
      <WeatherTuningControls
        value={tuning}
        onChange={changeTuning}
        onPreset={applyPreset}
        onImportJson={importTuning}
        onExportJson={() => download(`${exportBase}.tuning.json`, tuningJson(tuning), 'application/json')}
      />
      <div className="baseline-actions">
        <button type="button" className="secondary" disabled={running} onClick={() => scheduleHistoricalBaseline(0)}>Re-pin historical baseline</button>
        <button type="button" className="secondary" disabled={running || !candidate} onClick={pinCandidate}>Pin candidate as baseline</button>
        <span>{running ? 'A stale candidate is replaced after the tuning debounce.' : 'Artifacts stay cached while tuning changes.'}</span>
      </div>
      {tuningNotice && <p role="status">{tuningNotice}</p>}
    </section>}

    {(baseline || candidate) && <nav className="months" aria-label="Month">
      {MONTHS.map((label, index) => <button className={month === index + 1 ? 'active' : ''} onClick={() => setMonth(index + 1)} key={label}>{label}</button>)}
    </nav>}

    {baseline && candidate && dailySeries && eventSeries && <>
      <section className="panel export-panel">
        <div className="panel-title"><div><h2>Comparison exports</h2><p>All precipitation values remain liquid-water equivalent; snowfall is reported separately.</p></div>
          <div className="export-actions">
            <button className="secondary" onClick={() => download(`${exportBase}.comparison.json`, comparisonExportJson(baseline, candidate), 'application/json')}>Export JSON</button>
            <button className="secondary" onClick={() => download(`${exportBase}.daily.csv`, dailyComparisonCsv(dailySeries), 'text/csv')}>Daily CSV</button>
            <button className="secondary" onClick={() => download(`${exportBase}.hourly.csv`, hourlyComparisonCsv(hourlySeriesFromResults(candidate.observed, baseline, candidate)), 'text/csv')}>Hourly CSV</button>
          </div>
        </div>
      </section>

      <section className="summary">
        <article><span>Candidate truth hash</span><strong>{candidate.truthHash.slice(0, 16)}</strong></article>
        <article><span>Baseline truth hash</span><strong>{baseline.truthHash.slice(0, 16)}</strong></article>
        <article><span>Forecast issues</span><strong>{candidate.forecasts.length}</strong></article>
        <article><span>Detected events</span><strong>{candidate.events.simulated.length}</strong></article>
      </section>

      <section className="panel"><h2>Baseline vs candidate scorecard</h2><WeatherComparisonScorecard baseline={baseline.scores} candidate={candidate.scores}/></section>

      <section className="panel daily-charts">
        <div className="panel-title"><div><h2>{MONTHS[month - 1]} daily weather</h2><p>Observed, pinned baseline, and tuned candidate.</p></div></div>
        <DailyMetricChart series={dailySeries} metric="temperature" month={month}/>
        <DailyMetricChart series={dailySeries} metric="wet-bulb" month={month}/>
        <DailyMetricChart series={dailySeries} metric="precipitation" month={month}/>
        <DailyMetricChart series={dailySeries} metric="snowfall" month={month}/>
      </section>

      <section className="panel">
        <div className="panel-title"><div><h2>Accessible daily comparison</h2><p>Choose temperatures, wet bulb, precipitation phase, snowfall, conditions, snowmaking, or macro occupancy.</p></div>
          <label className="metric-picker">Metric<select value={dailyMetric} onChange={(event) => setDailyMetric(event.target.value as DailyMetric)}>
            <option value="temperature">Temperature</option><option value="wet-bulb">Wet bulb</option>
            <option value="precipitation">Precipitation</option><option value="snowfall">Snowfall</option>
            <option value="conditions">Conditions</option><option value="snowmaking">Snowmaking</option><option value="macro">Macro states</option>
          </select></label>
        </div>
        <DailyComparisonTable series={dailySeries} metric={dailyMetric} month={month}/>
      </section>

      <section className="panel"><DailyStateRibbons series={dailySeries} month={month}/></section>

      <section className="panel">
        <WeatherEventTimeline series={eventSeries} startDate={dateRange?.startDate} endDate={dateRange?.endDate} title={`${MONTHS[month - 1]} event timeline`}/>
        <p>Events are deterministic summaries of finished hourly weather. They do not drive the generator or consume random draws. Storm styles remain unclassified when evidence is insufficient.</p>
      </section>

      <section className="panel">
        <MarkovDiagnosticsPanel observed={candidate.observedDiagnostics} baseline={baseline.diagnostics} candidate={candidate.diagnostics}
          monthModel={artifacts?.model.months[month - 1]} tuning={tuning} adjustedRow={adjustedConditionTransitionRow}
          adjustedMacroRow={adjustedMacroTransitionRow}/>
      </section>

      <details className="panel raw-hourly"><summary>Raw hourly temperature view</summary>
        <p><i className="observed"/> observed <i className="simulated"/> tuned candidate</p>
        <WeatherChart simulated={candidate.simulated} observed={candidate.observed} month={month}/>
      </details>

      <section className="panel">
        <h2>Monthly comparison</h2>
        <table><thead><tr><th>Metric</th><th>Observed</th><th>Candidate</th><th>Status</th></tr></thead>
          <tbody>{candidate.monthly[month - 1].metrics.map((metric) => <tr key={`${metric.variable}-${metric.metric}`}>
            <td>{metric.variable} · {metric.metric}</td><td>{metric.observed?.toFixed(2) ?? 'Unavailable'}</td>
            <td>{metric.simulated?.toFixed(2) ?? 'Unavailable'}</td><td><span className={`badge ${metric.status}`}>{metric.status}</span></td>
          </tr>)}</tbody>
        </table>
      </section>
    </>}
  </main>;
}
