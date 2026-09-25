import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { runChecks } from './check.mjs';

const validAgents = '# Guide\n\n## Routing\n| Area | Start here |\n| --- | --- |\n| Docs | `docs/` |\n';

async function fixture(files) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'repo-checks-'));
  for (const [name, content] of Object.entries(files)) {
    const target = path.join(root, name);
    await mkdir(path.dirname(target), { recursive: true });
    if (content !== null) await writeFile(target, content);
  }
  return root;
}

async function errorsFor(files) {
  const root = await fixture(files);
  try {
    return (await runChecks(root)).errors;
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

test('a paired guide with no Assets folder passes', async () => {
  assert.deepEqual(await errorsFor({ 'AGENTS.md': validAgents, 'CLAUDE.md': '@AGENTS.md\n' }), []);
});

test('CLAUDE.md must contain only the import', async () => {
  const errors = await errorsFor({ 'AGENTS.md': validAgents, 'CLAUDE.md': '@AGENTS.md\nextra\n' });
  assert.match(errors.join('\n'), /must contain exactly @AGENTS.md/);
});

test('nested AGENTS.md needs a paired CLAUDE.md', async () => {
  const errors = await errorsFor({
    'AGENTS.md': validAgents,
    'CLAUDE.md': '@AGENTS.md\n',
    'docs/AGENTS.md': '# Nested\n',
  });
  assert.match(errors.join('\n'), /docs\/AGENTS.md has no paired CLAUDE.md/);
});

test('AGENTS.md over the character limit fails', async () => {
  const errors = await errorsFor({ 'AGENTS.md': validAgents + 'x'.repeat(8_000), 'CLAUDE.md': '@AGENTS.md' });
  assert.match(errors.join('\n'), /characters; maximum is 8000/);
});

test('broken local Markdown links fail and external links are ignored', async () => {
  const errors = await errorsFor({
    'AGENTS.md': validAgents,
    'CLAUDE.md': '@AGENTS.md\n',
    'README.md': '[ok](AGENTS.md) [web](https://example.com/x.md) [bad](docs/missing.md)',
  });
  assert.deepEqual(errors, ['README.md links to missing local path: docs/missing.md']);
});

test('generated Unity folders are not scanned', async () => {
  const errors = await errorsFor({
    'AGENTS.md': validAgents,
    'CLAUDE.md': '@AGENTS.md\n',
    'Library/PackageCache/pkg/AGENTS.md': '# Third party\n',
  });
  assert.deepEqual(errors, []);
});

test('.meta integrity: missing and orphaned .meta files fail', async () => {
  const errors = await errorsFor({
    'AGENTS.md': validAgents,
    'CLAUDE.md': '@AGENTS.md\n',
    'Assets/Scripts.meta': 'guid',
    'Assets/Scripts/Good.cs': '',
    'Assets/Scripts/Good.cs.meta': 'guid',
    'Assets/Scripts/NoMeta.cs': '',
    'Assets/Scripts/Gone.cs.meta': 'guid',
    'Assets/Hidden~/Anything.txt': '',
    'Assets/.hidden': '',
  });
  assert.deepEqual(errors.sort(), [
    'Assets/Scripts/Gone.cs.meta is an orphaned .meta (no Gone.cs).',
    'Assets/Scripts/NoMeta.cs has no .meta file.',
  ]);
});

test('.meta integrity: folders need a .meta too', async () => {
  const errors = await errorsFor({
    'AGENTS.md': validAgents,
    'CLAUDE.md': '@AGENTS.md\n',
    'Assets/Folder/File.cs': '',
    'Assets/Folder/File.cs.meta': 'guid',
  });
  assert.deepEqual(errors, ['Assets/Folder has no .meta file.']);
});
