import type { HistoricalWeatherSeriesV1, LocationClimateModelV1, WeatherLabResult, WeatherLabRunRequest } from '../../weather-engine/src/index.ts';

export type WeatherWorkerRequest =
  | { type: 'run'; requestId: string; run: WeatherLabRunRequest; model: LocationClimateModelV1; observed: HistoricalWeatherSeriesV1 }
  | { type: 'cancel'; requestId: string };
export type WeatherWorkerResponse =
  | { type: 'started'; requestId: string; totalHours: number }
  | { type: 'progress'; requestId: string; completedHours: number; totalHours: number }
  | { type: 'phase'; requestId: string; phase: 'forecasting' | 'comparison'; message: string }
  | { type: 'completed'; requestId: string; result: WeatherLabResult }
  | { type: 'cancelled'; requestId: string }
  | { type: 'failed'; requestId: string; message: string };
