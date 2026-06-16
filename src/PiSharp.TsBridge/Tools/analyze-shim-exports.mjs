import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve, dirname, relative } from 'node:path';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

function loadTypeScript() {
    try {
        const scriptDir = dirname(fileURLToPath(import.meta.url));
        const bridgeNodeModules = resolve(scriptDir, '..', 'Node', 'node_modules');
        if (existsSync(resolve(bridgeNodeModules, 'typescript'))) {
            const req = createRequire(resolve(bridgeNodeModules, 'typescript', 'package.json'));
            return req('typescript');
        }
    }
    catch {
    }
    try {
        const req = createRequire(import.meta.url);
        return req('typescript');
    }
    catch {
    }
    return null;
}

function resolveModule(fromDir, specifier) {
    if (specifier.endsWith('.js')) {
        const base = specifier.slice(0, -3);
        const tsPath = resolve(fromDir, base + '.ts');
        if (existsSync(tsPath)) return tsPath;
        const tsxPath = resolve(fromDir, base + '.tsx');
        if (existsSync(tsxPath)) return tsxPath;
    }
    const tsPath = resolve(fromDir, specifier + '.ts');
    if (existsSync(tsPath)) return tsPath;
    const tsxPath = resolve(fromDir, specifier + '.tsx');
    if (existsSync(tsxPath)) return tsxPath;
    const indexPath = resolve(fromDir, specifier, 'index.ts');
    if (existsSync(indexPath)) return indexPath;
    return null;
}

function parseSource(ts, filePath) {
    const sourceText = readFileSync(filePath, 'utf8');
    return ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.ES2022, true);
}

const MAX_DEPTH = 20;

function hasExportModifier(ts, node) {
    const modifiers = ts.canHaveModifiers(node) ? ts.getModifiers(node) : undefined;
    if (!modifiers) return false;
    for (const mod of modifiers) {
        if (mod.kind === ts.SyntaxKind.ExportKeyword) return true;
    }
    return false;
}

function variableKind(ts, statement) {
    if (statement.declarationList.flags & ts.NodeFlags.Const) return 'const';
    if (statement.declarationList.flags & ts.NodeFlags.Let) return 'let';
    return 'var';
}

function findDirectDeclaration(ts, sourceFile, name) {
    for (const statement of sourceFile.statements) {
        if (ts.isFunctionDeclaration(statement) && statement.name?.text === name) {
            return 'function';
        }
        if (ts.isClassDeclaration(statement) && statement.name?.text === name) {
            return 'class';
        }
        if (ts.isVariableStatement(statement)) {
            for (const decl of statement.declarationList.declarations) {
                if (ts.isIdentifier(decl.name) && decl.name.text === name) {
                    return variableKind(ts, statement);
                }
            }
        }
    }
    return null;
}

function resolveExport(ts, absPath, name, depth) {
    if (depth > MAX_DEPTH) return null;
    if (!existsSync(absPath)) return null;

    const sourceFile = parseSource(ts, absPath);
    const fileDir = dirname(absPath);

    const directKind = findDirectDeclaration(ts, sourceFile, name);
    if (directKind) {
        return { kind: directKind, sourceFile: absPath };
    }

    for (const statement of sourceFile.statements) {
        if (!ts.isImportDeclaration(statement)) continue;
        if (!statement.moduleSpecifier || !ts.isStringLiteral(statement.moduleSpecifier)) continue;
        const namedBindings = statement.importClause?.namedBindings;
        if (!namedBindings || !ts.isNamedImports(namedBindings)) continue;

        for (const element of namedBindings.elements) {
            if (element.name.text !== name) continue;
            const resolvedPath = resolveModule(fileDir, statement.moduleSpecifier.text);
            if (!resolvedPath) continue;
            const targetName = element.propertyName?.text ?? name;
            return resolveExport(ts, resolvedPath, targetName, depth + 1);
        }
    }

    for (const statement of sourceFile.statements) {
        if (!ts.isExportDeclaration(statement)) continue;
        if (statement.isTypeOnly) continue;

        if (!statement.moduleSpecifier || !ts.isStringLiteral(statement.moduleSpecifier)) continue;
        const resolvedPath = resolveModule(fileDir, statement.moduleSpecifier.text);
        if (!resolvedPath) continue;

        if (statement.exportClause && ts.isNamedExports(statement.exportClause)) {
            for (const element of statement.exportClause.elements) {
                if (element.isTypeOnly) continue;
                const exportName = element.name.text;
                if (exportName !== name) continue;
                const targetName = element.propertyName?.text ?? name;
                return resolveExport(ts, resolvedPath, targetName, depth + 1);
            }
        }

        if (!statement.exportClause) {
            const result = resolveExport(ts, resolvedPath, name, depth + 1);
            if (result) return result;
        }
    }

    return null;
}

function collectAllExportsFromFile(ts, filePath, seen, depth) {
    if (depth > MAX_DEPTH) return [];
    const absPath = resolve(filePath);
    if (seen.has(absPath)) return [];
    seen.add(absPath);

    if (!existsSync(absPath)) return [];

    const sourceFile = parseSource(ts, absPath);
    const fileDir = dirname(absPath);
    const results = [];

    for (const statement of sourceFile.statements) {
        if (ts.isExportDeclaration(statement)) {
            if (statement.isTypeOnly) continue;

            if (statement.exportClause && ts.isNamedExports(statement.exportClause)) {
                if (statement.moduleSpecifier && ts.isStringLiteral(statement.moduleSpecifier)) {
                    const resolvedPath = resolveModule(fileDir, statement.moduleSpecifier.text);
                    if (resolvedPath) {
                        for (const element of statement.exportClause.elements) {
                            if (element.isTypeOnly) continue;
                            const exportName = element.propertyName?.text ?? element.name.text;
                            const resolved = resolveExport(ts, resolvedPath, exportName, depth + 1);
                            if (resolved) {
                                results.push({ name: element.name.text, kind: resolved.kind, sourceFile: resolved.sourceFile });
                            }
                        }
                    }
                }
                else {
                    for (const element of statement.exportClause.elements) {
                        if (element.isTypeOnly) continue;
                        const name = element.name.text;
                        const kind = findDirectDeclaration(ts, sourceFile, name);
                        if (kind) {
                            results.push({ name, kind, sourceFile: absPath });
                        }
                    }
                }
            }
            else if (!statement.exportClause && statement.moduleSpecifier && ts.isStringLiteral(statement.moduleSpecifier)) {
                const resolvedPath = resolveModule(fileDir, statement.moduleSpecifier.text);
                if (resolvedPath) {
                    const subExports = collectAllExportsFromFile(ts, resolvedPath, seen, depth + 1);
                    for (const exp of subExports) {
                        results.push(exp);
                    }
                }
            }
        }
        else if (ts.isFunctionDeclaration(statement)) {
            if (hasExportModifier(ts, statement)) {
                const name = statement.name?.text;
                if (name) results.push({ name, kind: 'function', sourceFile: absPath });
            }
        }
        else if (ts.isClassDeclaration(statement)) {
            if (hasExportModifier(ts, statement)) {
                const name = statement.name?.text;
                if (name) results.push({ name, kind: 'class', sourceFile: absPath });
            }
        }
        else if (ts.isVariableStatement(statement)) {
            if (hasExportModifier(ts, statement)) {
                const kind = variableKind(ts, statement);
                for (const decl of statement.declarationList.declarations) {
                    if (ts.isIdentifier(decl.name)) {
                        results.push({ name: decl.name.text, kind, sourceFile: absPath });
                    }
                }
            }
        }
        else if (ts.isExportAssignment(statement)) {
            if (!statement.isExportEquals) {
                results.push({ name: 'default', kind: 'function', sourceFile: absPath });
            }
        }
    }

    return results;
}

function collectExportsEntry(ts, entryPath) {
    const seen = new Set();
    const rawExports = collectAllExportsFromFile(ts, entryPath, seen, 0);

    const entryDir = dirname(entryPath);
    const deduped = new Map();
    for (const exp of rawExports) {
        if (!deduped.has(exp.name)) {
            const rel = relative(entryDir, exp.sourceFile).replace(/\\/g, '/');
            deduped.set(exp.name, {
                name: exp.name,
                kind: exp.kind,
                sourceModule: rel || '.'
            });
        }
    }

    const results = [...deduped.values()];
    results.sort((a, b) => a.name.localeCompare(b.name));
    return results;
}

function main() {
    const { values } = parseArgs({
        options: {
            entry: { type: 'string' },
            out: { type: 'string' },
        },
        allowPositionals: false,
    });

    if (!values.entry || !values.out) {
        process.stderr.write('Usage: node analyze-shim-exports.mjs --entry <path> --out <path>\n');
        process.exit(1);
    }

    const ts = loadTypeScript();
    if (!ts) {
        process.stderr.write('Error: TypeScript compiler API not found. Ensure typescript is installed in Node/node_modules.\n');
        process.exit(1);
    }

    const entryPath = resolve(values.entry);
    if (!existsSync(entryPath)) {
        process.stderr.write(`Error: Entry file not found: ${entryPath}\n`);
        process.exit(1);
    }

    const exports = collectExportsEntry(ts, entryPath);

    const output = { exports };
    writeFileSync(resolve(values.out), JSON.stringify(output, null, 2) + '\n', 'utf8');

    process.stdout.write(`Analyzed ${exports.length} export(s) from ${entryPath}\n`);
}

main();
