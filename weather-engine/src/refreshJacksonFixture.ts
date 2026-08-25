import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { JACKSON_NH_TEST_LOCATION } from './climateBaseline.ts';
import { fetchDaymetBaseline } from './climateProviders.ts';

const moduleDirectory = dirname(fileURLToPath(import.meta.url));
const fixturePath = resolve(moduleDirectory, '../fixtures/jackson-nh-2010-2019.json');

async function main(): Promise<void> {
  process.stdout.write('Fetching Jackson, NH Daymet observations for 2010-2019...\n');
  const baseline = await fetchDaymetBaseline(JACKSON_NH_TEST_LOCATION);
  await mkdir(dirname(fixturePath), { recursive: true });
  await writeFile(fixturePath, `${JSON.stringify(baseline, null, 2)}\n`, 'utf8');
  process.stdout.write(`Wrote ${fixturePath}\n`);
}

main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
