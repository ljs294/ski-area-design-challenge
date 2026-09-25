// Zero-dependency repository checks for the Unity project on `main`.
// - Agent docs: every AGENTS.md has a same-directory CLAUDE.md containing only `@AGENTS.md`
//   (and vice versa); AGENTS.md stays within 150 lines / 8,000 characters; the root routing
//   table has at most 20 rows; local Markdown links resolve.
// - Unity .meta integrity under Assets/: every asset file and folder has a .meta, and every
//   .meta has its asset.
// Usage: node tools/repo-checks/check.mjs [root]   (root defaults to the current directory)
import { lstat, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

// Generated or third-party folders that are never scanned.
const ignoredDirectories = new Set([
  '.git',
  '.idea',
  '.vs',
  '.vscode',
  'Build',
  'Builds',
  'Library',
  'Logs',
  'MemoryCaptures',
  'Obj',
  'Temp',
  'UserSettings',
  'node_modules',
  'test-results',
]);
const maxAgentLines = 150;
const maxAgentCharacters = 8_000;
const maxRoutingRows = 20;

async function exists(target) {
  try {
    await lstat(target);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') return false;
    throw error;
  }
}

function normalizedText(text) {
  return text.replace(/^﻿/, '').replaceAll('\r\n', '\n');
}

function lineCount(text) {
  const normalized = normalizedText(text);
  if (!normalized) return 0;
  return normalized.endsWith('\n')
    ? normalized.slice(0, -1).split('\n').length
    : normalized.split('\n').length;
}

async function filesBelow(directory) {
  const found = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name)) continue;
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) found.push(...await filesBelow(target));
    else if (entry.isFile()) found.push(target);
  }
  return found;
}

async function checkGuidancePairs(root, files, errors, relative) {
  const agentDirectories = new Set(files.filter((file) => path.basename(file) === 'AGENTS.md').map(path.dirname));
  const claudeDirectories = new Set(files.filter((file) => path.basename(file) === 'CLAUDE.md').map(path.dirname));

  if (!agentDirectories.has(root)) errors.push('Missing root AGENTS.md.');
  if (!claudeDirectories.has(root)) errors.push('Missing root CLAUDE.md.');

  for (const directory of new Set([...agentDirectories, ...claudeDirectories])) {
    const label = relative(directory) || '.';
    if (!agentDirectories.has(directory)) {
      errors.push(`${label}/CLAUDE.md has no paired AGENTS.md.`);
      continue;
    }
    if (!claudeDirectories.has(directory)) {
      errors.push(`${label}/AGENTS.md has no paired CLAUDE.md.`);
      continue;
    }

    const agentFile = path.join(directory, 'AGENTS.md');
    const claudeFile = path.join(directory, 'CLAUDE.md');
    const agentText = normalizedText(await readFile(agentFile, 'utf8'));
    const claudeText = normalizedText(await readFile(claudeFile, 'utf8'));
    const lines = lineCount(agentText);

    if (claudeText !== '@AGENTS.md\n' && claudeText !== '@AGENTS.md') {
      errors.push(`${relative(claudeFile)} must contain exactly @AGENTS.md and an optional final newline.`);
    }
    if (lines > maxAgentLines) {
      errors.push(`${relative(agentFile)} has ${lines} lines; maximum is ${maxAgentLines}.`);
    }
    if (agentText.length > maxAgentCharacters) {
      errors.push(`${relative(agentFile)} has ${agentText.length} characters; maximum is ${maxAgentCharacters}.`);
    }
  }
}

async function checkRootRoutingTable(root, errors) {
  const agentFile = path.join(root, 'AGENTS.md');
  if (!await exists(agentFile)) return;

  const lines = normalizedText(await readFile(agentFile, 'utf8')).split('\n');
  const heading = lines.findIndex((line) => line.trim() === '## Routing');
  if (heading === -1) {
    errors.push('AGENTS.md must contain a ## Routing section.');
    return;
  }

  const nextHeading = lines.findIndex((line, index) => index > heading && /^##\s+/.test(line));
  const section = lines.slice(heading + 1, nextHeading === -1 ? undefined : nextHeading);
  const tableLines = section.map((line) => line.trim()).filter((line) => line.startsWith('|'));
  if (tableLines.length < 2) {
    errors.push('AGENTS.md ## Routing must contain a Markdown table.');
    return;
  }

  const routingRows = tableLines.length - 2;
  if (routingRows > maxRoutingRows) {
    errors.push(`AGENTS.md has ${routingRows} routing-table data rows; maximum is ${maxRoutingRows}.`);
  }
}

function markdownTargets(text) {
  const targets = [];
  const linkPattern = /!?\[[^\]]*\]\((<[^>]+>|[^\s)]+)(?:\s+["'][^"']*["'])?\)/g;
  for (const match of text.matchAll(linkPattern)) {
    targets.push(match[1].replace(/^<|>$/g, ''));
  }
  return targets;
}

function isExternal(target) {
  return /^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(target);
}

async function checkLocalLinks(files, errors, relative) {
  for (const file of files.filter((candidate) => candidate.toLowerCase().endsWith('.md'))) {
    const text = await readFile(file, 'utf8');
    for (const target of markdownTargets(text)) {
      if (!target || target.startsWith('#') || isExternal(target)) continue;
      const pathPart = target.split('#', 1)[0].split('?', 1)[0];
      if (!pathPart) continue;

      let decoded;
      try {
        decoded = decodeURIComponent(pathPart);
      } catch {
        errors.push(`${relative(file)} contains a malformed encoded link: ${target}`);
        continue;
      }

      if (!await exists(path.resolve(path.dirname(file), decoded))) {
        errors.push(`${relative(file)} links to missing local path: ${target}`);
      }
    }
  }
}

// Unity skips these names when importing, so they neither need nor may have a .meta.
function isUnityHidden(name) {
  return name.startsWith('.') || name.endsWith('~') || name.toLowerCase() === 'cvs' || name.toLowerCase().endsWith('.tmp');
}

async function checkMetaIntegrity(directory, errors, relative) {
  const entries = await readdir(directory, { withFileTypes: true });
  const names = new Set(entries.map((entry) => entry.name));
  for (const entry of entries) {
    if (isUnityHidden(entry.name)) continue;
    const target = path.join(directory, entry.name);
    if (entry.name.endsWith('.meta')) {
      const assetName = entry.name.slice(0, -'.meta'.length);
      if (!names.has(assetName)) errors.push(`${relative(target)} is an orphaned .meta (no ${assetName}).`);
      continue;
    }
    if (!names.has(`${entry.name}.meta`)) errors.push(`${relative(target)} has no .meta file.`);
    if (entry.isDirectory()) await checkMetaIntegrity(target, errors, relative);
  }
}

export async function runChecks(root) {
  const errors = [];
  const relative = (file) => path.relative(root, file).replaceAll(path.sep, '/');
  const files = await filesBelow(root);
  await checkGuidancePairs(root, files, errors, relative);
  await checkRootRoutingTable(root, errors);
  await checkLocalLinks(files, errors, relative);

  const assets = path.join(root, 'Assets');
  const hasAssets = await exists(assets);
  if (hasAssets) await checkMetaIntegrity(assets, errors, relative);

  return {
    errors,
    guidancePairs: files.filter((file) => path.basename(file) === 'AGENTS.md').length,
    markdownFiles: files.filter((file) => file.toLowerCase().endsWith('.md')).length,
    checkedAssets: hasAssets,
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = path.resolve(process.argv[2] ?? process.cwd());
  const result = await runChecks(root);
  if (result.errors.length > 0) {
    console.error('Repository checks failed:');
    for (const error of result.errors) console.error(`- ${error}`);
    process.exitCode = 1;
  } else {
    const meta = result.checkedAssets ? '.meta integrity OK' : 'no Assets/ folder yet';
    console.log(`Repository checks passed (${result.guidancePairs} guidance pair, ${result.markdownFiles} Markdown files, ${meta}).`);
  }
}
