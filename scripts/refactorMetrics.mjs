import { execFileSync } from 'node:child_process';
import { lstat, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import ts from 'typescript';

const root = process.cwd();
const sourceRoots = ['src', 'electron'];
const testRoots = ['src', 'electron', 'tests'];
const ignoredDirectories = new Set([
  '.git',
  'coverage',
  'dist',
  'dist-electron',
  'dist-web',
  'generated',
  'node_modules',
  'playwright-report',
  'release',
  'scratchpad',
  'test-results',
  '__generated__',
]);
const obsoleteFiles = new Set([
  'menu.html',
  'scripts/downloadPresetTerrain.ts',
  'src/contentManager.ts',
  'src/contours.ts',
  'src/gisSelector.ts',
  'src/hillshade.ts',
  'src/labels.ts',
  'src/main.ts',
  'src/mountainPresets.ts',
  'src/renderer.ts',
  'src/selectionBox.ts',
  'src/style.css',
  'src/tileIndex.ts',
]);

function git(...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
}

function slash(file) {
  return path.relative(root, file).replaceAll(path.sep, '/');
}

async function exists(target) {
  try {
    await lstat(target);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') return false;
    throw error;
  }
}

async function walk(directory) {
  if (!await exists(directory)) return [];
  const files = [];
  const entries = await readdir(directory, { withFileTypes: true });
  entries.sort((left, right) => left.name.localeCompare(right.name, 'en'));
  for (const entry of entries) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name)) continue;
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await walk(target));
    else if (entry.isFile()) files.push(target);
  }
  return files;
}

function isTypeScript(file) {
  return /\.(?:ts|tsx)$/.test(file) && !file.endsWith('.d.ts');
}

function isGenerated(file) {
  const normalized = slash(file);
  return /(?:^|\/)(?:generated|__generated__)(?:\/|$)/.test(normalized)
    || /\.generated\.(?:ts|tsx)$/.test(normalized);
}

function isTest(file) {
  const normalized = slash(file);
  return normalized.startsWith('tests/') || /\.(?:test|spec)\.(?:ts|tsx)$/.test(normalized);
}

function physicalLines(text) {
  if (text.length === 0) return 0;
  const normalized = text.replaceAll('\r\n', '\n');
  return normalized.endsWith('\n')
    ? normalized.slice(0, -1).split('\n').length
    : normalized.split('\n').length;
}

async function lineMetrics(files) {
  let lines = 0;
  for (const file of files) lines += physicalLines(await readFile(file, 'utf8'));
  return { fileCount: files.length, physicalLineCount: lines };
}

function sourceFileOf(file, text) {
  const kind = file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  return ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, kind);
}

function visit(node, callback) {
  callback(node);
  ts.forEachChild(node, (child) => visit(child, callback));
}

async function mapViewMetrics() {
  const file = path.join(root, 'src', 'app', 'MapView.tsx');
  const text = await readFile(file, 'utf8');
  const source = sourceFileOf(file, text);
  let useStateCalls = 0;
  let useEffectCalls = 0;
  let newWorkerExpressions = 0;

  visit(source, (node) => {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
      if (node.expression.text === 'useState') useStateCalls += 1;
      if (node.expression.text === 'useEffect') useEffectCalls += 1;
    }
    if (ts.isNewExpression(node) && ts.isIdentifier(node.expression)
      && node.expression.text === 'Worker') newWorkerExpressions += 1;
  });

  return {
    physicalLineCount: physicalLines(text),
    importDeclarationCount: source.statements.filter(ts.isImportDeclaration).length,
    useStateCallCount: useStateCalls,
    useEffectCallCount: useEffectCalls,
    newWorkerExpressionCount: newWorkerExpressions,
  };
}

function moduleTarget(importer, specifier) {
  if (!specifier.startsWith('.')) return null;
  const base = path.resolve(path.dirname(importer), specifier);
  const candidates = [base, `${base}.ts`, `${base}.tsx`, path.join(base, 'index.ts')];
  return candidates.find((candidate) => path.normalize(candidate) === path.join(root, 'src', 'types.ts')) ?? null;
}

async function importsTypeFacade(file) {
  const text = await readFile(file, 'utf8');
  const source = sourceFileOf(file, text);
  let found = false;
  visit(source, (node) => {
    if (found) return;
    if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node))
      && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)
      && moduleTarget(file, node.moduleSpecifier.text)) found = true;
    if (ts.isImportTypeNode(node) && ts.isLiteralTypeNode(node.argument)
      && ts.isStringLiteral(node.argument.literal)
      && moduleTarget(file, node.argument.literal.text)) found = true;
    if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword
      && node.arguments.length === 1 && ts.isStringLiteral(node.arguments[0])
      && moduleTarget(file, node.arguments[0].text)) found = true;
  });
  return found;
}

async function typeFacadeMetrics(productionFiles) {
  const facade = path.join(root, 'src', 'types.ts');
  const text = await readFile(facade, 'utf8');
  let importerCount = 0;
  for (const file of productionFiles) {
    if (file !== facade && await importsTypeFacade(file)) importerCount += 1;
  }
  return { physicalLineCount: physicalLines(text), productionImporterCount: importerCount };
}

async function fileBytes(files) {
  let bytes = 0;
  for (const file of files) bytes += (await lstat(file)).size;
  return bytes;
}

async function obsoleteMetrics() {
  const tracked = git('ls-files').split(/\r?\n/).filter(Boolean);
  const obsolete = tracked.filter((file) => obsoleteFiles.has(file)
    || file.startsWith('public/presetTerrain/'));
  const present = [];
  for (const file of obsolete) {
    const target = path.join(root, file);
    if (await exists(target)) present.push(target);
  }
  const presets = present.filter((file) => slash(file).startsWith('public/presetTerrain/'));
  return {
    trackedFileCount: present.length,
    trackedBytes: await fileBytes(present),
    presetTerrainBytes: await fileBytes(presets),
  };
}

async function directoryArtifact(directory) {
  const target = path.join(root, directory);
  if (!await exists(target)) return { present: false, bytes: 0 };
  const files = await walk(target);
  return { present: true, bytes: await fileBytes(files) };
}

const sourceCandidates = (await Promise.all(sourceRoots.map((directory) => walk(path.join(root, directory))))).flat();
const productionFiles = sourceCandidates.filter((file) => isTypeScript(file) && !isTest(file) && !isGenerated(file));
const testCandidates = (await Promise.all(testRoots.map((directory) => walk(path.join(root, directory))))).flat();
const testFiles = [...new Set(testCandidates.filter((file) => isTypeScript(file) && isTest(file) && !isGenerated(file)))];
productionFiles.sort((left, right) => slash(left).localeCompare(slash(right), 'en'));
testFiles.sort((left, right) => slash(left).localeCompare(slash(right), 'en'));

const packageJson = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const result = {
  schemaVersion: 1,
  git: {
    head: git('rev-parse', 'HEAD'),
    dirty: git('status', '--porcelain=v1', '--untracked-files=normal').length > 0,
  },
  productionTypeScript: await lineMetrics(productionFiles),
  testTypeScript: await lineMetrics(testFiles),
  mapView: await mapViewMetrics(),
  typeFacade: await typeFacadeMetrics(productionFiles),
  obsoleteVertical: await obsoleteMetrics(),
  dependencies: {
    directDependencyCount: Object.keys(packageJson.dependencies ?? {}).length,
    directDevDependencyCount: Object.keys(packageJson.devDependencies ?? {}).length,
  },
  artifacts: {
    dist: await directoryArtifact('dist'),
    distWeb: await directoryArtifact('dist-web'),
  },
};

process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
