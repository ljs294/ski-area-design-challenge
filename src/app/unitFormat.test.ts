import { describe, expect, it } from 'vitest';
import { formatElevation, formatFeet, formatFlow, formatInches, formatLiquidPrecipitation, formatPressure, formatSnowfall, formatTemperature,
  formatTemperatureDelta, formatVelocity, formatWindSpeed } from './unitFormat';

describe('global unit formatting', () => {
  it('converts canonical metric weather values into US units', () => {
    expect(formatTemperature(0, 'imperial')).toBe('32.0 \u00b0F');
    expect(formatTemperatureDelta(10, 'imperial')).toBe('18.0 \u00b0F');
    expect(formatWindSpeed(16.09344, 'imperial')).toBe('10.0 mph');
    expect(formatVelocity(4.4704, 'imperial')).toBe('10.0 mph');
    expect(formatLiquidPrecipitation(25.4, 'imperial')).toBe('1.00 in');
    expect(formatSnowfall(2.54, 'imperial')).toBe('1.0 in');
    expect(formatElevation(1000, 'imperial')).toBe('3,281 ft');
    expect(formatPressure(100, 'metric')).toBe('689.5 kPa');
    expect(formatFlow(10, 'metric')).toBe('37.9 L/min');
    expect(formatFeet(10, 'metric')).toBe('3.0 m');
    expect(formatInches(4, 'metric')).toBe('101.6 mm');
  });

  it('preserves canonical values and metric labels in Metric mode', () => {
    expect(formatTemperature(0, 'metric')).toBe('0.0 \u00b0C');
    expect(formatWindSpeed(10, 'metric')).toBe('10.0 km/h');
    expect(formatLiquidPrecipitation(12.5, 'metric')).toBe('12.50 mm');
    expect(formatSnowfall(8, 'metric')).toBe('8.0 cm');
    expect(formatElevation(1000, 'metric')).toBe('1,000 m');
  });
});
