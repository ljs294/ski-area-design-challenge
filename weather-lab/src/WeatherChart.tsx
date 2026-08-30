import { useEffect, useRef } from 'react';
import type { HistoricalWeatherSeriesV1, SimulatedWeatherHourV1 } from '../../weather-engine/src/index.ts';

export function WeatherChart({ simulated, observed, month }: { simulated: readonly SimulatedWeatherHourV1[]; observed: HistoricalWeatherSeriesV1; month: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = ref.current; if (!canvas) return;
    const context = canvas.getContext('2d'); if (!context) return;
    const scale = window.devicePixelRatio || 1; const width = canvas.clientWidth; const height = canvas.clientHeight;
    canvas.width = width * scale; canvas.height = height * scale; context.scale(scale, scale); context.clearRect(0, 0, width, height);
    const sim = simulated.filter((hour) => Number(hour.localDateTime.slice(5, 7)) === month);
    const obs = observed.hours.filter((hour) => Number(hour.localDateTime.slice(5, 7)) === month);
    const values = [...sim.map((hour) => hour.temperatureC), ...obs.flatMap((hour) => hour.temperatureC == null ? [] : [hour.temperatureC])];
    const min = Math.min(...values) - 2; const max = Math.max(...values) + 2;
    const line = (data: readonly (number | null)[], color: string) => { context.beginPath(); context.strokeStyle = color; context.lineWidth = 1.4;
      data.forEach((value, index) => { if (value == null) return; const x = index / Math.max(1, data.length - 1) * width; const y = height - (value - min) / Math.max(1, max - min) * height;
        if (index === 0) context.moveTo(x, y); else context.lineTo(x, y); }); context.stroke(); };
    line(obs.map((hour) => hour.temperatureC), '#8aa4bd'); line(sim.map((hour) => hour.temperatureC), '#ffb45e');
  }, [month, observed, simulated]);
  return <canvas ref={ref} className="weather-chart" aria-label="Observed and simulated temperature chart"/>;
}
