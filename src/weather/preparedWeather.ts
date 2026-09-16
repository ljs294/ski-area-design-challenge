import { historicalAverageAnnualSnowfallCm, loadAnnualWeatherSession } from './annualWeather';
import { resolveWeatherHour } from './weatherSession';
import type { PreparedAnnualWeather, PreparedWeatherIdentity } from './preparedWeatherModel';
import type { WeatherDataPackage } from './weatherModel';

export async function buildPreparedWeather(
  weatherPackage: WeatherDataPackage,
  identity: PreparedWeatherIdentity,
): Promise<PreparedAnnualWeather> {
  const annual = await loadAnnualWeatherSession(weatherPackage, identity.seed, identity.year);
  const session = Object.freeze({
    plan: annual.plan,
    timezone: annual.timezone,
    ...(annual.midpoint ? { midpoint: annual.midpoint } : {}),
  });
  const resolvedHours = Object.freeze(annual.plan.hours.map((hour) => resolveWeatherHour(hour, annual.midpoint)));
  return Object.freeze({
    identity,
    session,
    resolvedHours,
    averageAnnualSnowfallCm: historicalAverageAnnualSnowfallCm(annual.historicalYears, annual.timezone),
  });
}
