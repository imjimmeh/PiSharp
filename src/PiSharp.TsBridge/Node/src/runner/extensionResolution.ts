import fs from "node:fs/promises";
import path from "node:path";

export function collectImportSpecifiers(source: string): string[] {
	const specs = new Set<string>();
	for (const match of source.matchAll(
		/(?:from\s+["']|import\s*["']|import\s*\(\s*["'])([^"']+)["']/g,
	)) {
		if (match[1]) specs.add(match[1]);
	}
	return [...specs];
}

export async function resolveExtensionEntryPath(extensionPath: string): Promise<string> {
	const resolved = path.resolve(extensionPath);
	try {
		const stats = await fs.stat(resolved);
		if (!stats.isDirectory()) return resolved;
	} catch {
		return resolved;
	}

	for (const candidate of ["index.ts", "index.mjs", "index.js"]) {
		const candidatePath = path.join(resolved, candidate);
		try {
			await fs.access(candidatePath);
			return candidatePath;
		} catch {}
	}

	try {
		const packageJson: { module?: string; main?: string } = JSON.parse(
			await fs.readFile(path.join(resolved, "package.json"), "utf8"),
		);
		for (const entry of [packageJson.module, packageJson.main]) {
			if (typeof entry !== "string") continue;
			const candidatePath = path.resolve(resolved, entry);
			try {
				await fs.access(candidatePath);
				return candidatePath;
			} catch {}
		}
	} catch {}

	try {
		const entries = await fs.readdir(resolved, { withFileTypes: true });
		const childIndexes: string[] = [];
		for (const entry of entries) {
			if (!entry.isDirectory()) continue;
			for (const indexName of ["index.ts", "index.mjs", "index.js"]) {
				const candidatePath = path.join(resolved, entry.name, indexName);
				try {
					await fs.access(candidatePath);
					childIndexes.push(candidatePath);
					break;
				} catch {}
			}
		}
		if (childIndexes.length === 1) return childIndexes[0]!;
	} catch {}

	return resolved;
}

export async function resolveTypeScriptImport(specifier: string, sourcePath: string): Promise<string | null> {
	if (specifier.startsWith("node:") || path.isAbsolute(specifier)) return null;
	if (specifier.startsWith("./") || specifier.startsWith("../")) {
		const withoutExt = specifier.replace(/\.(js|ts)$/u, "");
		const candidate = path.resolve(
			path.dirname(sourcePath),
			`${withoutExt}.ts`,
		);
		try {
			await fs.access(candidate);
			return candidate;
		} catch {
			return null;
		}
	}

	const packageInfo = await findPackageForSpecifier(specifier, sourcePath);
	if (!packageInfo) return null;
	const { root, subpath } = packageInfo;
	let packageJson: { exports?: unknown; main?: string; module?: string };
	try {
		packageJson = JSON.parse(
			await fs.readFile(path.join(root, "package.json"), "utf8"),
		);
	} catch {
		return null;
	}
	let entry = resolvePackageExport(packageJson, subpath);
	if (!entry && subpath) entry = `./${subpath}.ts`;
	if (!entry && !subpath && typeof packageJson.main === "string")
		entry = packageJson.main;
	if (!entry) return null;
	const candidate = path.resolve(root, entry);
	if (!candidate.endsWith(".ts")) return null;
	try {
		await fs.access(candidate);
		return candidate;
	} catch {
		return null;
	}
}

export async function findPackageForSpecifier(specifier: string, sourcePath: string): Promise<{ root: string; subpath: string } | null> {
	const parts = specifier.split("/");
	const packageName = specifier.startsWith("@")
		? `${parts[0]}/${parts[1]}`
		: parts[0]!;
	const subpath = parts.slice(specifier.startsWith("@") ? 2 : 1).join("/");
	let current = path.dirname(sourcePath);
	while (current && current !== path.dirname(current)) {
		const root = path.join(current, "node_modules", packageName);
		try {
			await fs.access(path.join(root, "package.json"));
			return { root, subpath };
		} catch {
			/* continue */
		}
		current = path.dirname(current);
	}
	return null;
}

export function resolvePackageExport(packageJson: { exports?: unknown }, subpath: string): string | null {
	const key = subpath ? `./${subpath}` : ".";
	const exportsField = packageJson.exports as Record<string, unknown> | undefined;
	const value = exportsField?.[key] ?? (!subpath ? exportsField : null);
	return resolveExportValue(value as string | Record<string, unknown> | null | undefined);
}

function resolveExportValue(value: string | Record<string, unknown> | null | undefined): string | null {
	if (typeof value === "string") return value;
	if (!value || typeof value !== "object") return null;
	const obj = value as Record<string, unknown>;
	return (
		resolveExportValue(obj.import as string | Record<string, unknown> | null | undefined) ??
		resolveExportValue(obj.default as string | Record<string, unknown> | null | undefined) ??
		resolveExportValue(obj.node as string | Record<string, unknown> | null | undefined)
	);
}
