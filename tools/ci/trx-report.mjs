#!/usr/bin/env node
// Task 0.4 (CI speed): reads one or more .trx files from `dotnet test --logger trx` and writes where the time went:
// every test's duration, fixture and categories, totals by fixture and category, and the slowest tests.
//   node tools/ci/trx-report.mjs <label>=<file.trx> [<label>=<file.trx> ...] > timings.json
// Plain Node, no packages: the trx is regular enough for a few regular expressions.
import { readFileSync } from 'node:fs';

const attr = (tag, name) => {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1].replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&') : '';
};
const seconds = (d) => {
  const m = /^(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(d || '');
  return m ? +m[1] * 3600 + +m[2] * 60 + +m[3] : 0;
};

function read(file) {
  const xml = readFileSync(file, 'utf8');
  const defs = new Map();
  for (const m of xml.matchAll(/<UnitTest\b[^>]*>[\s\S]*?<\/UnitTest>/g)) {
    const block = m[0];
    const id = attr(block.match(/<UnitTest\b[^>]*>/)[0], 'id');
    const method = block.match(/<TestMethod\b[^>]*>/);
    const className = method ? attr(method[0], 'className') : '';
    const categories = [...block.matchAll(/<TestCategoryItem\b[^>]*>/g)].map((c) => attr(c[0], 'TestCategory'));
    defs.set(id, { fixture: className.split('.').pop(), categories });
  }
  const tests = [];
  for (const m of xml.matchAll(/<UnitTestResult\b[^>]*>/g)) {
    const tag = m[0];
    const def = defs.get(attr(tag, 'testId')) || { fixture: '?', categories: [] };
    tests.push({
      name: attr(tag, 'testName'),
      fixture: def.fixture,
      categories: def.categories,
      outcome: attr(tag, 'outcome'),
      seconds: seconds(attr(tag, 'duration')),
    });
  }
  const times = xml.match(/<Times\b[^>]*>/);
  const wall = times ? (Date.parse(attr(times[0], 'finish')) - Date.parse(attr(times[0], 'start'))) / 1000 : null;
  return { tests, wall };
}

const sum = (rows, key) => {
  const out = {};
  for (const t of rows) for (const k of [].concat(key(t))) out[k] = (out[k] || 0) + t.seconds;
  return Object.fromEntries(Object.entries(out).sort((a, b) => b[1] - a[1]));
};

const runs = {};
for (const arg of process.argv.slice(2)) {
  const [label, file] = arg.includes('=') ? arg.split(/=(.*)/s) : [arg, arg];
  const { tests, wall } = read(file);
  runs[label] = {
    wallSeconds: wall,
    testSeconds: tests.reduce((a, t) => a + t.seconds, 0),
    counts: tests.reduce((a, t) => ((a[t.outcome] = (a[t.outcome] || 0) + 1), a), {}),
    byFixture: sum(tests, (t) => t.fixture),
    byCategory: sum(tests, (t) => (t.categories.length ? t.categories : ['(none)'])),
    slowest: [...tests].sort((a, b) => b.seconds - a.seconds).slice(0, 40),
    tests,
  };
}
process.stdout.write(JSON.stringify(runs, null, 1));
