import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import ts from 'typescript';

const repoRoot = process.cwd();
const sourceRoot = path.join(repoRoot, 'src');
const appRoot = path.join(sourceRoot, 'app');
const typeModelRoot = path.join(sourceRoot, 'types');
const typeFacadePath = path.join(sourceRoot, 'types.ts');
const sourceExtensions = new Set(['.ts', '.tsx']);

const TEMPORARY_CORE_TO_APP_EXCEPTIONS = new Map();

// Exact public compatibility surface. Additions in C2 intentionally expose
// the canonical geo, construction, topology, and snowmaking-node models.
// TerrainDB is intentionally absent: its obsolete runtime consumer was removed
// in B3. Any future manifest change requires an explicit compatibility review.
const TYPE_FACADE_MANIFEST = new Map([
  ['./types/anchors', ['AnchorRef']],
  ['./types/construction', ['ConstructionStatus']],
  ['./types/cover', [
    'CoverClassCode', 'CoverDisplayMetadata', 'CoverGeometryMetadata', 'CoverGrid',
    'CoverGridData', 'CoverMetadata', 'LandCoverClass', 'OriginalCoverMetadata',
    'SiteCoverGrid', 'TerrainCoverClass', 'TerrainCoverCode', 'TerrainCoverGrid',
    'TerrainCoverProvenance', 'WorldCoverClassCode',
  ]],
  ['./types/earthwork', ['EarthworkEstimate']],
  ['./types/gameSave', ['GameSave', 'GameSaveSummary', 'SavedSiteBox']],
  ['./types/geo', ['LatLonBounds']],
  ['./types/lifts', [
    'ChairSize', 'LiftClass', 'LiftStatus', 'SavedFixedGripLift', 'SavedLift', 'SavedLiftBase',
  ]],
  ['./types/roads', ['RoadType', 'SavedRoad']],
  ['./types/snowmaking', [
    'SavedDam', 'SavedPond', 'SavedSnowmakingNode', 'SnowmakingNodeKind', 'SnowmakingSourceRef',
  ]],
  ['./types/terrain', [
    'AreaSizeMeters', 'ClimateMonth', 'ClimateProfile', 'ContourMetadata',
    'LocalImageryMetadata', 'SurroundElevation', 'TerrainPackageManifest',
    'TerrainPackagePhase', 'TerrainPackageProgress', 'TerrainPackageValidation',
    'TerrainRecord', 'TerrainSummary',
  ]],
  ['./types/topology', ['SavedJunction', 'SavedNode', 'SavedPath', 'SavedTrailSegment']],
  ['./types/trails', ['SavedTrail', 'SavedTrailPart', 'TrailDifficulty', 'TrailStatus']],
  ['./types/vectorFeatures', [
    'LandCoverFeature', 'OsmLandCoverClass', 'PeakFeature', 'RoadClass', 'RoadFeature',
    'VectorFeatureSet', 'WaterLineClass', 'WaterLineFeature', 'WaterPolygonFeature',
  ]],
]);

const ROOT_FACADE_ALLOWLIST = new Set([
  'src/desktopBridge.ts',
  'src/ipcContract.ts',
]);

// These two predate the refactor and remain explicitly documented follow-up
// candidates. New production files do not inherit their historical waiver.
const HISTORIC_LARGE_FILE_ALLOWLIST = new Set([
  'src/network.ts',
  'src/coverEdit.ts',
]);

function normalized(filePath) {
  return path.resolve(filePath).replaceAll('\\', '/');
}

function isWithin(filePath, directory) {
  const relative = path.relative(directory, filePath);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function walk(directory) {
  if (!fs.existsSync(directory)) return [];

  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walk(entryPath));
    else if (sourceExtensions.has(path.extname(entry.name))) files.push(entryPath);
  }
  return files;
}

function moduleSpecifiers(sourceFile) {
  const imports = [];

  function record(node, kind, literal) {
    if (literal && ts.isStringLiteralLike(literal)) {
      const position = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile));
      imports.push({
        kind,
        specifier: literal.text,
        line: position.line + 1,
      });
    }
  }

  function visit(node) {
    if (ts.isImportDeclaration(node)) {
      record(node, node.importClause?.isTypeOnly ? 'type import' : 'import', node.moduleSpecifier);
    } else if (ts.isExportDeclaration(node)) {
      record(node, node.isTypeOnly ? 'type re-export' : 're-export', node.moduleSpecifier);
    } else if (
      ts.isCallExpression(node)
      && node.expression.kind === ts.SyntaxKind.ImportKeyword
    ) {
      record(node, 'dynamic import', node.arguments[0]);
    } else if (ts.isImportTypeNode(node)) {
      record(node, 'TypeScript import type', node.argument.literal);
    } else if (
      ts.isImportEqualsDeclaration(node)
      && ts.isExternalModuleReference(node.moduleReference)
    ) {
      record(node, 'import equals', node.moduleReference.expression);
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return imports;
}

function sourceFileFor(filePath) {
  const sourceText = fs.readFileSync(filePath, 'utf8');
  const kind = filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  return ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.Latest, true, kind);
}

function physicalLines(text) {
  const normalizedText = text.replaceAll('\r\n', '\n');
  if (!normalizedText) return 0;
  return normalizedText.endsWith('\n')
    ? normalizedText.slice(0, -1).split('\n').length
    : normalizedText.split('\n').length;
}

function projectTarget(importer, specifier) {
  if (specifier.startsWith('.')) return path.resolve(path.dirname(importer), specifier);
  if (specifier === 'src') return sourceRoot;
  if (specifier.startsWith('src/')) return path.resolve(repoRoot, specifier);
  if (specifier.startsWith('@/')) return path.resolve(sourceRoot, specifier.slice(2));
  return null;
}

function resolveSourceModule(target, sourceFiles) {
  const candidates = [
    target,
    `${target}.ts`,
    `${target}.tsx`,
    path.join(target, 'index.ts'),
    path.join(target, 'index.tsx'),
  ];
  return candidates.find((candidate) => sourceFiles.has(normalized(candidate))) ?? null;
}

const files = walk(sourceRoot);
const sourceFiles = new Set(files.map(normalized));
const typeFiles = files.filter((filePath) => isWithin(filePath, typeModelRoot));
const typeFileSet = new Set(typeFiles.map(normalized));
const errors = [];
const usedTemporaryExceptions = [];
const typeGraph = new Map(typeFiles.map((filePath) => [normalized(filePath), []]));

const mapViewPath = path.join(appRoot, 'MapView.tsx');
const mapViewText = fs.readFileSync(mapViewPath, 'utf8');
const mapViewSource = sourceFileFor(mapViewPath);
const mapViewLines = physicalLines(mapViewText);
const mapViewImports = mapViewSource.statements.filter(ts.isImportDeclaration).length;
let mapViewEffects = 0;
let mapViewWorkers = 0;
function inspectMapView(node) {
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)
      && node.expression.text === 'useEffect') mapViewEffects += 1;
  if (ts.isNewExpression(node) && ts.isIdentifier(node.expression)
      && node.expression.text === 'Worker') mapViewWorkers += 1;
  ts.forEachChild(node, inspectMapView);
}
inspectMapView(mapViewSource);
if (mapViewLines > 1800) errors.push(`src/app/MapView.tsx exceeds 1,800 lines (${mapViewLines})`);
if (mapViewImports > 40) errors.push(`src/app/MapView.tsx exceeds 40 imports (${mapViewImports})`);
if (mapViewEffects > 15) errors.push(`src/app/MapView.tsx exceeds 15 local effects (${mapViewEffects})`);
if (mapViewWorkers > 0) errors.push(`src/app/MapView.tsx constructs ${mapViewWorkers} workers`);

for (const filePath of files) {
  const repositoryPath = path.relative(repoRoot, filePath).replaceAll('\\', '/');
  if (/\.(?:test|spec)\.tsx?$/.test(repositoryPath)) continue;
  const lines = physicalLines(fs.readFileSync(filePath, 'utf8'));
  if (filePath !== mapViewPath && lines > 800 && !HISTORIC_LARGE_FILE_ALLOWLIST.has(repositoryPath)) {
    errors.push(`${repositoryPath} exceeds the 800-line production-file budget (${lines})`);
  }
  if (/^src\/app\/use.*Controller\.tsx?$/.test(repositoryPath) && lines > 600) {
    errors.push(`${repositoryPath} exceeds the 600-line controller budget (${lines})`);
  }
}

const facadeSource = sourceFileFor(typeFacadePath);
const facadeLines = fs.readFileSync(typeFacadePath, 'utf8').split(/\r?\n/).length;
if (facadeLines > 100) errors.push(`src/types.ts exceeds the 100-line facade budget (${facadeLines})`);

const actualFacadeManifest = new Map();
for (const statement of facadeSource.statements) {
  const position = facadeSource.getLineAndCharacterOfPosition(statement.getStart(facadeSource));
  const line = position.line + 1;
  if (!ts.isExportDeclaration(statement)) {
    errors.push(`src/types.ts:${line} contains a non-export statement; the facade must be type-only re-exports`);
    continue;
  }
  if (!statement.isTypeOnly) {
    errors.push(`src/types.ts:${line} contains a runtime-capable export`);
  }
  if (
    !statement.moduleSpecifier
    || !ts.isStringLiteralLike(statement.moduleSpecifier)
    || !statement.exportClause
    || !ts.isNamedExports(statement.exportClause)
  ) {
    errors.push(`src/types.ts:${line} must use an explicit named type re-export`);
    continue;
  }

  const specifier = statement.moduleSpecifier.text;
  if (actualFacadeManifest.has(specifier)) {
    errors.push(`src/types.ts:${line} repeats facade module ${specifier}`);
    continue;
  }
  actualFacadeManifest.set(
    specifier,
    statement.exportClause.elements.map((element) => element.name.text).sort(),
  );
}

for (const [specifier, expectedNames] of TYPE_FACADE_MANIFEST) {
  const actualNames = actualFacadeManifest.get(specifier);
  if (!actualNames) {
    errors.push(`src/types.ts is missing facade module ${specifier}`);
    continue;
  }
  if (actualNames.join('\0') !== [...expectedNames].sort().join('\0')) {
    errors.push(
      `src/types.ts exports the wrong manifest for ${specifier}: expected [${[...expectedNames].sort().join(', ')}], got [${actualNames.join(', ')}]`,
    );
  }
}
for (const specifier of actualFacadeManifest.keys()) {
  if (!TYPE_FACADE_MANIFEST.has(specifier)) {
    errors.push(`src/types.ts contains undocumented facade module ${specifier}`);
  }
}

const authoritativeModelFile = new Map();
for (const [specifier, names] of TYPE_FACADE_MANIFEST) {
  const modelPath = normalized(path.resolve(path.dirname(typeFacadePath), `${specifier}.ts`));
  for (const name of names) authoritativeModelFile.set(name, modelPath);
}

for (const filePath of files) {
  const sourceFile = sourceFileFor(filePath);
  const isAppFile = isWithin(filePath, appRoot);
  const isTypeFile = isWithin(filePath, typeModelRoot);
  const repositoryPath = path.relative(repoRoot, filePath).replaceAll('\\', '/');

  if (normalized(filePath) !== normalized(typeFacadePath)) {
    for (const statement of sourceFile.statements) {
      if (
        (ts.isInterfaceDeclaration(statement) || ts.isTypeAliasDeclaration(statement))
        && statement.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword)
      ) {
        const expectedFile = authoritativeModelFile.get(statement.name.text);
        if (expectedFile && expectedFile !== normalized(filePath)) {
          errors.push(
            `${repositoryPath} re-declares authoritative model ${statement.name.text}; use ${path.relative(repoRoot, expectedFile)}`,
          );
        }
      }
      if (ts.isExportDeclaration(statement) && statement.exportClause && ts.isNamedExports(statement.exportClause)) {
        for (const element of statement.exportClause.elements) {
          const expectedFile = authoritativeModelFile.get(element.name.text);
          if (expectedFile) {
            errors.push(
              `${repositoryPath} re-exports authoritative model ${element.name.text}; import it from ${path.relative(repoRoot, expectedFile)}`,
            );
          }
        }
      }
    }
  }

  for (const imported of moduleSpecifiers(sourceFile)) {
    const target = projectTarget(filePath, imported.specifier);
    if (!target) continue;

    if (!isAppFile && isWithin(target, appRoot)) {
      const importer = path.relative(repoRoot, filePath).replaceAll('\\', '/');
      const exceptionKey = `${importer}::${imported.specifier}`;
      const removalMilestone = TEMPORARY_CORE_TO_APP_EXCEPTIONS.get(exceptionKey);
      if (removalMilestone) {
        usedTemporaryExceptions.push(`${exceptionKey} (${removalMilestone})`);
      } else {
        errors.push(
          `${path.relative(repoRoot, filePath)}:${imported.line} ${imported.kind} crosses from core/model code into src/app: ${imported.specifier}`,
        );
      }
    }

    const resolvedTarget = resolveSourceModule(target, sourceFiles);
    if (
      resolvedTarget && normalized(resolvedTarget) === normalized(typeFacadePath)
      && path.dirname(filePath) === sourceRoot
      && !/\.(test|spec)\.tsx?$/.test(filePath)
      && !ROOT_FACADE_ALLOWLIST.has(repositoryPath)
    ) {
      errors.push(`${repositoryPath}:${imported.line} must import its authoritative domain model instead of src/types.ts`);
    }
    if (isTypeFile && resolvedTarget && !isWithin(resolvedTarget, typeModelRoot)) {
      errors.push(
        `${path.relative(repoRoot, filePath)}:${imported.line} ${imported.kind} crosses from src/types into implementation code: ${imported.specifier}`,
      );
    }

    if (isTypeFile && resolvedTarget && typeFileSet.has(normalized(resolvedTarget))) {
      typeGraph.get(normalized(filePath)).push(normalized(resolvedTarget));
    }
  }
}

for (const exceptionKey of TEMPORARY_CORE_TO_APP_EXCEPTIONS.keys()) {
  if (!usedTemporaryExceptions.some((exception) => exception.startsWith(`${exceptionKey} (`))) {
    errors.push(`stale temporary architecture exception must be removed: ${exceptionKey}`);
  }
}

const visited = new Set();
const visiting = new Set();
const stack = [];

function findCycles(filePath) {
  if (visiting.has(filePath)) {
    const cycleStart = stack.indexOf(filePath);
    const cycle = [...stack.slice(cycleStart), filePath]
      .map((item) => path.relative(repoRoot, item))
      .join(' -> ');
    errors.push(`src/types dependency cycle: ${cycle}`);
    return;
  }
  if (visited.has(filePath)) return;

  visiting.add(filePath);
  stack.push(filePath);
  for (const dependency of typeGraph.get(filePath) ?? []) findCycles(dependency);
  stack.pop();
  visiting.delete(filePath);
  visited.add(filePath);
}

for (const filePath of typeGraph.keys()) findCycles(filePath);

if (errors.length > 0) {
  console.error('Architecture checks failed:\n');
  for (const error of [...new Set(errors)].sort()) console.error(`- ${error}`);
  process.exitCode = 1;
} else {
  console.log(`Architecture checks passed (${files.length} source files, ${typeFiles.length} type-model files).`);
  for (const exception of usedTemporaryExceptions) {
    console.warn(`TEMPORARY architecture exception: ${exception}`);
  }
}
