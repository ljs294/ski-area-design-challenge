import type { HistoricalWeatherSeriesV1, LocationClimateModelV1, WeatherLabResultV1, WeatherLabRunRequestV1 } from '../../weather-engine/src/index.ts';

export type WeatherWorkerRequest =
  | { type: 'run'; requestId: string; run: WeatherLabRunRequestV1; model: LocationClimateModelV1; observed: HistoricalWeatherSeriesV1 }
  | { type: 'cancel'; requestId: string };
export type WeatherWorkerResponse =
  | { type: 'started'; requestId: string; totalHours: number }
  | { type: 'progress'; requestId: string; completedHours: number; totalHours: number }
  | { type: 'completed'; requestId: string; result: WeatherLabResultV1 }
  | { type: 'cancelled'; requestId: string }
  | { type: 'failed'; requestId: string; message: string };
