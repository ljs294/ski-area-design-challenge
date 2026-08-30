import { describe, expect, it } from 'vitest';
import {
  circularMeanWindDirection,
  displayValue,
  formatWindDirection,
  localHourLabel,
  metricUnit,
  precipitationColor,
} from '../src/forecastViewModel.ts';

describe('weather display view model', () => {
  it('converts only display values and retains distinct precipitation phase colors', () => {
    expect(displayValue(0, 'temperature', 'us')).toBe(32);
    expect(displayValue(25.4, 'precipitation', 'us')).toBeCloseTo(1);
    expect(displayValue(2.54, 'snowfall', 'us')).toBeCloseTo(1);
    expect(displayValue(16.09344, 'wind', 'us')).toBeCloseTo(10, 4);
    expect(displayValue(25.4, 'precipitation', 'metric')).toBe(25.4);
    expect(metricUnit('temperature', 'us')).toBe('°F');
    expect(localHourLabel('2024-03-09T11:00:00.000Z', 'America/New_York')).toMatch(/6:00\s*AM/i);
    expect(new Set(['rain', 'snow', 'mixed', 'freezing-rain'].map((phase) => precipitationColor(phase as never))).size).toBe(4);
  });

  it('reports compass direction and averages directions across north circularly', () => {
    expect(formatWindDirection(0)).toBe('N (0°)');
    expect(formatWindDirection(270)).toBe('W (270°)');
    expect(formatWindDirection(null)).toBe('direction unavailable');
    const acrossNorth = circularMeanWindDirection([350, 10]);
    expect(acrossNorth === 0 || acrossNorth === 360).toBe(true);
    expect(circularMeanWindDirection([null, undefined])).toBeNull();
  });
});
