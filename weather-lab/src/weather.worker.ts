/// <reference lib="webworker" />
import { advanceWeatherTo, compareWeatherSeries, compareWeatherSeriesV2, createWeatherSimulation, createWeatherSnapshot } from '../../weather-engine/src/index.ts';
import type { SimulatedWeatherHourV1 } from '../../weather-engine/src/index.ts';
import type { WeatherWorkerRequest, WeatherWorkerResponse } from './protocol.ts';

const scope = self as DedicatedWorkerGlobalScope;
let activeId: string | null = null;
const send = (message: WeatherWorkerResponse) => scope.postMessage(message);
const yieldToMessages = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

scope.onmessage = (event: MessageEvent<WeatherWorkerRequest>) => {
  const message = event.data;
  if (message.type === 'cancel') { if (activeId === message.requestId) activeId = null; return; }
  activeId = message.requestId;
  void (async () => {
    try {
      let simulation = createWeatherSimulation(message.run, message.model);
      const hours: SimulatedWeatherHourV1[] = [];
      send({ type: 'started', requestId: message.requestId, totalHours: simulation.calendar.length });
      while (simulation.snapshot.nextHourIndex < simulation.calendar.length) {
        if (activeId !== message.requestId) { send({ type: 'cancelled', requestId: message.requestId }); return; }
        const advanced = advanceWeatherTo(simulation, Math.min(simulation.calendar.length, simulation.snapshot.nextHourIndex + 168));
        simulation = advanced.simulation; hours.push(...advanced.hours);
        send({ type: 'progress', requestId: message.requestId, completedHours: hours.length, totalHours: simulation.calendar.length });
        await yieldToMessages();
      }
      if (activeId !== message.requestId) { send({ type: 'cancelled', requestId: message.requestId }); return; }
      send({ type: 'phase', requestId: message.requestId, phase: 'comparison', message: 'Comparing simulated and quality-controlled observed weather.' });
      const snapshot = createWeatherSnapshot(simulation);
      const result = message.run.version === 2
        ? compareWeatherSeriesV2(message.run, hours, message.observed, [], snapshot, message.model)
        : compareWeatherSeries(message.run, hours, message.observed, [], snapshot);
      send({ type: 'completed', requestId: message.requestId, result }); activeId = null;
    } catch (error) {
      send({ type: 'failed', requestId: message.requestId, message: error instanceof Error ? error.message : String(error) }); activeId = null;
    }
  })();
};
