import { writeFile } from 'node:fs/promises';
import { compareWeatherSeries, createJacksonClimateModel, createJacksonObserved2019, createJacksonRun,
  generateForecastIssues, generateWeatherYear, WEATHER_DIFFICULTY_PRESETS } from '../weather-engine/src/index.ts';

function option(name: string): string | undefined { const index = process.argv.indexOf(name); return index < 0 ? undefined : process.argv[index + 1]; }
async function main() {
  const seed = option('--seed') ?? 'Historical'; const profileName = option('--difficulty') ?? 'historical';
  if (!(profileName in WEATHER_DIFFICULTY_PRESETS)) throw new Error(`Unknown difficulty '${profileName}'`);
  const model = createJacksonClimateModel(); const run = createJacksonRun(seed);
  run.difficultyProfile = WEATHER_DIFFICULTY_PRESETS[profileName as keyof typeof WEATHER_DIFFICULTY_PRESETS];
  const generated = generateWeatherYear(run, model); const forecasts = generateForecastIssues(run, generated.hours).issues;
  const result = compareWeatherSeries(run, generated.hours, createJacksonObserved2019(), forecasts, generated.snapshot);
  const output = JSON.stringify(result);
  const outputFile = option('--output'); if (outputFile) await writeFile(outputFile, `${output}\n`);
  if (process.argv.includes('--json')) process.stdout.write(`${output}\n`);
  else process.stdout.write(`Jackson 2019 · ${generated.hours.length} hours · ${forecasts.length} forecasts\ntruth ${result.truthHash}\ncomparison ${result.comparisonHash}\n`);
}
main().catch((error: unknown) => { process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`); process.exitCode = 1; });
