import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import ts from 'typescript';

const repoRoot = process.cwd();
const sourceRoot = path.join(repoRoot, 'src');
const appRoot = path.join(sourceRoot, 'app');
const typeModelRoot = path.join(sourceRoot, 'types');
const sourceExtensions = new Set(['.ts', '.tsx']);

// Remove this exact exception in benchmark B1 with the obsolete terrain-ingest
// vertical. It is deliberately keyed by both importer and specifier so it
// cannot mask any other core-to-app dependency inversion.
const TEMPORARY_CORE_TO_APP_EXCEPTIONS = new Map([
  ['src/terrainIngest.ts::./app/worldcoverProtocol', 'remove in B1 obsolete-vertical deletion'],
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

for (const filePath of files) {
  const sourceFile = sourceFileFor(filePath);
  const isAppFile = isWithin(filePath, appRoot);
  const isTypeFile = isWithin(filePath, typeModelRoot);

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
