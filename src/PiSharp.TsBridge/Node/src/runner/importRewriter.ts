import type { TypeScriptModule } from "../types.js";

interface Edit {
	start: number;
	end: number;
	text: string;
}

function applyEdits(source: string, edits: Edit[]): string {
	if (!edits || edits.length === 0) return source;
	let output = source;
	for (const edit of [...edits].sort((left, right) => right.start - left.start)) {
		output = `${output.slice(0, edit.start)}${edit.text}${output.slice(edit.end)}`;
	}
	return output;
}

function replacementText(source: string, start: number, replacement: string): string {
	const quote = source[start] === "'" ? "'" : '"';
	return `${quote}${replacement}${quote}`;
}

function escapeRegExp(text: string): string {
	return String(text).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function rewriteImportSpecifiersWithPatterns(source: string, replacements: Map<string, string>): string {
	let output = source;
	for (const [from, to] of replacements.entries()) {
		const specifierPattern = escapeRegExp(from);
		const patterns = [
			new RegExp(`(\\bfrom\\s+)(["'])${specifierPattern}\\2`, "g"),
			new RegExp(`(\\bimport\\s+)(["'])${specifierPattern}\\2`, "g"),
			new RegExp(`(\\bimport\\s*\\(\\s*)(["'])${specifierPattern}\\2`, "g"),
		];
		for (const pattern of patterns) {
			output = output.replace(pattern, (_match: string, prefix: string, quote: string) =>
				`${prefix}${quote}${to}${quote}`,
			);
		}
	}
	return output;
}

export function rewriteImportSpecifiersWithAst(source: string, replacements: Map<string, string>, ts?: TypeScriptModule): string {
	if (!source || !replacements || replacements.size === 0) return source;
	if (!ts?.createSourceFile)
		return rewriteImportSpecifiersWithPatterns(source, replacements);
	const sourceFile = ts.createSourceFile(
		"module.mjs",
		source,
		ts.ScriptTarget!.ESNext,
		true,
		ts.ScriptKind!.JS,
	);
	const edits: Edit[] = [];
	const visit = (node: unknown) => {
		if (ts.isImportDeclaration?.(node) && ts.isStringLiteral?.((node as { moduleSpecifier?: unknown }).moduleSpecifier)) {
			const specifierNode = (node as { moduleSpecifier: { text: string; getStart: (source: unknown) => number; end: number } }).moduleSpecifier;
			const replacement = replacements.get(specifierNode.text);
			if (replacement) {
				const start = specifierNode.getStart(sourceFile);
				edits.push({
					start,
					end: specifierNode.end,
					text: replacementText(source, start, replacement),
				});
			}
		}
		if (
			ts.isExportDeclaration?.(node) &&
			(node as { moduleSpecifier?: unknown }).moduleSpecifier &&
			ts.isStringLiteral?.((node as { moduleSpecifier: unknown }).moduleSpecifier)
		) {
			const specifierNode = (node as { moduleSpecifier: { text: string; getStart: (source: unknown) => number; end: number } }).moduleSpecifier;
			const replacement = replacements.get(specifierNode.text);
			if (replacement) {
				const start = specifierNode.getStart(sourceFile);
				edits.push({
					start,
					end: specifierNode.end,
					text: replacementText(source, start, replacement),
				});
			}
		}
		if (
			ts.isCallExpression?.(node) &&
			(node as { expression: { kind: number }; arguments: unknown[] }).expression.kind === ts.SyntaxKind!.ImportKeyword &&
			(node as { arguments: unknown[] }).arguments.length > 0 &&
			ts.isStringLiteral?.((node as { arguments: unknown[] }).arguments[0])
		) {
			const specifier = (node as { arguments: { text: string; getStart: (source: unknown) => number; end: number }[] }).arguments[0]!;
			const replacement = replacements.get(specifier.text);
			if (replacement) {
				const start = specifier.getStart(sourceFile);
				edits.push({
					start,
					end: specifier.end,
					text: replacementText(source, start, replacement),
				});
			}
		}
		ts.forEachChild?.(node, visit);
	};
	visit(sourceFile);
	return applyEdits(source, edits);
}
