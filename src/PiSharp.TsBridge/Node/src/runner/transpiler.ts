import crypto from "node:crypto";
import fs from "node:fs/promises";
import { builtinModules, createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import {
	collectImportSpecifiers,
	findPackageForSpecifier,
	resolveExtensionEntryPath,
	resolvePackageExport,
	resolveTypeScriptImport,
} from "./extensionResolution.js";
import { rewriteImportSpecifiersWithAst } from "./importRewriter.js";
import type {
	DependencyHashRecord,
	DependencyOutput,
	ImportResult,
	LoadTimings,
	ModuleInfo,
	ShimRegistryLike,
	TranspilerApi,
	TypeScriptModule,
} from "../types.js";

const RUNTIME_IMPORT_RESOLVER_VERSION = 6;

export function createLoadTimings(): LoadTimings {
	return {
		cacheLookup: 0,
		compilerLoad: 0,
		transpile: 0,
		dependencyTranspile: 0,
		moduleImport: 0,
		activation: 0,
		registrationFlush: 0,
		registrationApply: 0,
		total: 0,
		cacheHits: 0,
		cacheMisses: 0,
		cacheFallbacks: 0,
	};
}

export function elapsedMilliseconds(startedAt: bigint): number {
	return Number(process.hrtime.bigint() - startedAt) / 1_000_000;
}

export async function measurePhase<T>(timings: LoadTimings, name: string, action: () => T | Promise<T>): Promise<T> {
	const startedAt = process.hrtime.bigint();
	try {
		return await action();
	} finally {
		if (timings && name in timings)
			timings[name] = (timings[name] ?? 0) + elapsedMilliseconds(startedAt);
	}
}

export function hashText(text: string): string {
	return crypto.createHash("sha256").update(text).digest("hex");
}

export function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
	let timeout: NodeJS.Timeout;
	const timer = new Promise<never>((_, reject) => {
		timeout = setTimeout(() => reject(new Error(message)), timeoutMs);
	});
	return Promise.race([promise, timer]).finally(() => clearTimeout(timeout));
}

export function createTranspiler(shimRegistry: ShimRegistryLike, getBridgeManifest: () => unknown): TranspilerApi {
	const transpiledFiles = new Set<string>();
	const transpiledBySource = new Map<string, string>();
	let bridgeCacheDirectory = path.join(os.tmpdir(), "pisharp-ts-bridge-cache");
	let bridgeCacheEnabled = true;
	let importVersion = 0;
	let piManagedTypescriptCompilerPromise: Promise<TypeScriptModule | null> | null = null;

	function cachePathForKey(key: string): string {
		return path.join(bridgeCacheDirectory, key.slice(0, 2), `${key}.mjs`);
	}

	async function readCachedPath(cachePath: string, timings: LoadTimings): Promise<string> {
		return await measurePhase(timings, "cacheLookup", async () => {
			await fs.access(cachePath);
			timings.cacheHits += 1;
			return cachePath;
		});
	}

	async function writeCacheFile(cachePath: string, output: string): Promise<void> {
		await fs.mkdir(path.dirname(cachePath), { recursive: true });
		const tempPath = `${cachePath}.${process.pid}.${Date.now()}.tmp`;
		await fs.writeFile(tempPath, output, "utf8");
		await fs.rename(tempPath, cachePath);
	}

	async function createFallbackTranspilePath(sourcePath: string, timings: LoadTimings): Promise<string> {
		timings.cacheFallbacks += 1;
		const parsed = path.parse(sourcePath);
		const fallbackDir = path.join(os.tmpdir(), "pisharp-ts-bridge-fallback");
		await fs.mkdir(fallbackDir, { recursive: true });
		const fallbackPath = path.join(
			fallbackDir,
			`${parsed.name}.${process.pid}.${Date.now()}.${hashText(sourcePath).slice(0, 12)}.mjs`,
		);
		transpiledFiles.add(fallbackPath);
		return fallbackPath;
	}

	async function writeFallbackTranspile(sourcePath: string, output: string, timings: LoadTimings): Promise<string> {
		const fallbackPath = await createFallbackTranspilePath(sourcePath, timings);
		await fs.writeFile(fallbackPath, output, "utf8");
		return fallbackPath;
	}

	function compilerOptionsFingerprint(ts: TypeScriptModule): string {
		return JSON.stringify({
			module: "ES2022",
			target: "ES2022",
			moduleResolution: "NodeNext",
			verbatimModuleSyntax: false,
			typescript: ts?.version ?? "fallback-stripper",
		});
	}

	function looksJavaScriptCompatibleTypeScript(source: string): boolean {
		return !(
			/^\s*import\s+type\s+/m.test(source) ||
			/\binterface\s+[A-Za-z_$][\w$]*/.test(source) ||
			/^\s*type\s+[A-Za-z_$][\w$]*\s*=/m.test(source) ||
			/\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*:\s*[^=;\n]+[=;]/.test(source) ||
			/function[ \t]*[A-Za-z_$]*[ \t]*\([^\n)]*:[^\n)]*\)/.test(source) ||
			/\)[ \t]*:[ \t]*[^={\n]+[ \t]*\{/.test(source)
		);
	}

	async function transpileTypeScript(source: string, ts: TypeScriptModule, timings: LoadTimings, timingPhase = "transpile"): Promise<string> {
		return await measurePhase(timings, timingPhase, async () => {
			if (!ts?.transpileModule) {
				if (looksJavaScriptCompatibleTypeScript(source)) return source;
				throw new Error(
					"TypeScript compiler is required to load .ts extensions. Install the 'typescript' package next to the extension or in ~/.pi/agent/npm/node_modules.",
				);
			}
			return ts.transpileModule(source, {
				compilerOptions: {
					module: ts.ModuleKind!.ES2022,
					target: ts.ScriptTarget!.ES2022,
					moduleResolution: ts.ModuleResolutionKind!.NodeNext,
					verbatimModuleSyntax: false,
				},
			}).outputText;
		});
	}

	async function descriptorSourceMetadata(extensionPath: string, seen = new Set<string>()): Promise<DependencyHashRecord> {
		const resolved = await resolveExtensionEntryPath(extensionPath);
		if (seen.has(resolved)) return { sourceHash: null, dependencyHashes: [] };
		seen.add(resolved);
		try {
			const source = await fs.readFile(resolved, "utf8");
			const dependencyHashes: Array<{ path: string; hash: string }> = [];
			for (const specifier of collectImportSpecifiers(source)) {
				const tsPath = await resolveTypeScriptImport(specifier, resolved);
				if (!tsPath) continue;
				const dependency = await descriptorSourceMetadata(tsPath, seen);
				if (dependency.sourceHash)
					dependencyHashes.push({
						path: path.resolve(tsPath),
						hash: dependency.sourceHash,
					});
				dependencyHashes.push(...dependency.dependencyHashes);
			}
			return { sourceHash: hashText(source), dependencyHashes };
		} catch {
			return { sourceHash: null, dependencyHashes: [] };
		}
	}

	async function installedPackageMetadata(sourcePath: string): Promise<Pick<DependencyHashRecord, "packageName" | "packageVersion">> {
		const parts = path.resolve(sourcePath).split(path.sep);
		if (!parts.includes("node_modules")) return {};

		let current = path.dirname(path.resolve(sourcePath));
		while (true) {
			const packageJsonPath = path.join(current, "package.json");
			try {
				const packageJson = JSON.parse(await fs.readFile(packageJsonPath, "utf8")) as { name?: unknown; version?: unknown };
				if (typeof packageJson.name === "string" && typeof packageJson.version === "string") {
					return { packageName: packageJson.name, packageVersion: packageJson.version };
				}
			} catch {
			}

			const parent = path.dirname(current);
			if (parent === current) return {};
			current = parent;
		}
	}

	function isPiCodingAgentSpecifier(specifier: string): boolean {
		return (
			specifier === "@earendil-works/pi-coding-agent" ||
			specifier === "@mariozechner/pi-coding-agent" ||
			specifier === "@pi-coding-agent"
		);
	}

	function isPiAiSpecifier(specifier: string): boolean {
		return (
			specifier === "@earendil-works/pi-ai" ||
			specifier === "@mariozechner/pi-ai" ||
			specifier === "@pi-ai"
		);
	}

	function isPiTuiSpecifier(specifier: string): boolean {
		return (
			specifier === "@earendil-works/pi-tui" ||
			specifier === "@mariozechner/pi-tui" ||
			specifier === "@pi-tui"
		);
	}

	function rewriteTypeScriptImportSpecifiers(source: string, dependencyOutputs: DependencyOutput[], transpiledPath: string, ts: TypeScriptModule): string {
		const replacements = new Map<string, string>();
		for (const dependency of dependencyOutputs) {
			let rewritten = path
				.relative(path.dirname(transpiledPath), dependency.path)
				.replaceAll(path.sep, "/");
			if (!rewritten.startsWith("./") && !rewritten.startsWith("../"))
				rewritten = `./${rewritten}`;
			replacements.set(dependency.specifier, rewritten);
		}
		return rewriteImportSpecifiersWithAst(source, replacements, ts);
	}

	function isInsideDirectory(candidatePath: string, directoryPath: string): boolean {
		const relative = path.relative(
			path.resolve(directoryPath),
			path.resolve(candidatePath),
		);
		return (
			Boolean(relative) &&
			!relative.startsWith("..") &&
			!path.isAbsolute(relative)
		);
	}

	async function resolveRuntimePackageAlias(specifier: string, sourcePath: string): Promise<string | null> {
		const packageInfo = await findPackageForSpecifier(specifier, sourcePath);
		if (packageInfo) {
			const { root, subpath } = packageInfo;
			try {
				const packageJson = JSON.parse(
					await fs.readFile(path.join(root, "package.json"), "utf8"),
				) as Record<string, unknown>;
				let entry = resolvePackageExport(packageJson, subpath);
				if (!entry && subpath) entry = `./${subpath}`;
				if (!entry && !subpath && typeof packageJson.module === "string")
					entry = packageJson.module;
				if (!entry && !subpath && typeof packageJson.main === "string")
					entry = packageJson.main;
				if (entry) {
					const candidate = path.resolve(root, entry);
					try {
						await fs.access(candidate);
						return pathToFileURL(candidate).href;
					} catch {}
				}
			} catch {}
		}

		try {
			const resolved = createRequire(pathToFileURL(sourcePath)).resolve(specifier);
			return builtinModules.includes(resolved)
				? `node:${resolved}`
				: pathToFileURL(resolved).href;
		} catch {
			return null;
		}
	}

	async function ensureCompatibilityShim(specifier: string): Promise<string> {
		let shimUrl = shimRegistry.resolve(specifier);
		if (shimUrl) return shimUrl;
		await shimRegistry.refresh(getBridgeManifest());
		shimUrl = shimRegistry.resolve(specifier);
		if (shimUrl) return shimUrl;
		return shimRegistry.require(specifier);
	}

	async function resolveBareImportForRuntime(specifier: string, sourcePath: string): Promise<string | null | undefined> {
		if (isPiCodingAgentSpecifier(specifier))
			return await ensureCompatibilityShim(specifier);
		if (isPiAiSpecifier(specifier))
			return (
				(await resolveRuntimePackageAlias("@earendil-works/pi-ai", sourcePath)) ??
				(await ensureCompatibilityShim(specifier))
			);
		if (isPiTuiSpecifier(specifier))
			return (
				(await resolveRuntimePackageAlias("@earendil-works/pi-tui", sourcePath)) ??
				(await ensureCompatibilityShim(specifier))
			);
		if (builtinModules.includes(specifier)) return `node:${specifier}`;
		return await resolveRuntimePackageAlias(specifier, sourcePath);
	}

	function isCompatibilitySpecifier(specifier: string): boolean {
		return isPiCodingAgentSpecifier(specifier) || isPiAiSpecifier(specifier) || isPiTuiSpecifier(specifier);
	}

	async function cachedModuleUsesCurrentCompatibilityImports(cachePath: string, source: string, sourcePath: string): Promise<boolean> {
		const specifiers = [...new Set(collectImportSpecifiers(source).filter(isCompatibilitySpecifier))];
		if (specifiers.length === 0) return true;

		let cachedSource: string;
		try {
			cachedSource = await fs.readFile(cachePath, "utf8");
		} catch {
			return false;
		}

		for (const specifier of specifiers) {
			const current = await resolveBareImportForRuntime(specifier, sourcePath);
			if (typeof current === "string" && !cachedSource.includes(current)) return false;
		}
		return true;
	}

	async function rewriteBareImportSpecifiers(source: string, specifiers: string[], sourcePath: string, ts: TypeScriptModule): Promise<string> {
		const replacements = new Map<string, string>();
		for (const specifier of specifiers) {
			if (
				specifier.startsWith("node:") ||
				specifier.startsWith("./") ||
				specifier.startsWith("../") ||
				path.isAbsolute(specifier)
			)
				continue;
			const rewritten = await resolveBareImportForRuntime(specifier, sourcePath);
			if (!rewritten) continue;
			replacements.set(specifier, rewritten);
		}
		return rewriteImportSpecifiersWithAst(source, replacements, ts);
	}

	function piManagedNodeModulesDirectory(): string | null {
		const home = process.env.USERPROFILE ?? process.env.HOME;
		return home ? path.join(home, ".pi", "agent", "npm", "node_modules") : null;
	}

	async function importTypeScriptCompilerAt(candidate: string): Promise<TypeScriptModule | null> {
		try {
			await fs.access(candidate);
			return await import(pathToFileURL(candidate).href) as TypeScriptModule;
		} catch {
			return null;
		}
	}

	async function findPiManagedTypeScriptCompiler(): Promise<TypeScriptModule | null> {
		const nodeModules = piManagedNodeModulesDirectory();
		if (!nodeModules) return null;

		const rootCompiler = await importTypeScriptCompilerAt(
			path.join(nodeModules, "typescript", "lib", "typescript.js"),
		);
		if (rootCompiler) return rootCompiler;

		try {
			const entries = await fs.readdir(nodeModules, { withFileTypes: true });
			for (const entry of entries) {
				const packageRoots: string[] = [];
				if (entry.isDirectory() && entry.name.startsWith("@")) {
					try {
						const scoped = await fs.readdir(path.join(nodeModules, entry.name), {
							withFileTypes: true,
						});
						for (const scopedEntry of scoped) {
							if (scopedEntry.isDirectory())
								packageRoots.push(
									path.join(nodeModules, entry.name, scopedEntry.name),
								);
						}
					} catch {}
				} else if (entry.isDirectory()) {
					packageRoots.push(path.join(nodeModules, entry.name));
				}

				for (const packageRoot of packageRoots) {
					const compiler = await importTypeScriptCompilerAt(
						path.join(
							packageRoot,
							"node_modules",
							"typescript",
							"lib",
							"typescript.js",
						),
					);
					if (compiler) return compiler;
				}
			}
		} catch {}
		return null;
	}

	async function loadTypeScriptCompiler(sourcePath: string): Promise<TypeScriptModule | null> {
		try {
			return await import("typescript") as TypeScriptModule;
		} catch {}

		if (sourcePath) {
			try {
				const resolved = createRequire(pathToFileURL(sourcePath)).resolve("typescript");
				return await import(pathToFileURL(resolved).href) as TypeScriptModule;
			} catch {}
		}

		if (!piManagedTypescriptCompilerPromise) {
			piManagedTypescriptCompilerPromise = findPiManagedTypeScriptCompiler();
		}
		return piManagedTypescriptCompilerPromise;
	}

	async function transpileTypeScriptFile(sourcePath: string, timings: LoadTimings, timingPhase = "transpile", options: { forceFresh?: boolean; descriptorMetadata?: DependencyHashRecord } = {}): Promise<string> {
		const resolved = path.resolve(sourcePath);
		const source = await fs.readFile(resolved, "utf8");
		const sourceHash = hashText(source);
		const memoKey = `${resolved}:${sourceHash}`;
		const forceFresh = options.forceFresh === true;
		const memoized = forceFresh
			? undefined
			: await measurePhase(timings, "cacheLookup", async () =>
					transpiledBySource.get(memoKey),
				);
		if (memoized) {
			timings.cacheHits += 1;
			return memoized;
		}

		const tsModule = await measurePhase(timings, "compilerLoad", () =>
			loadTypeScriptCompiler(resolved),
		);
		const ts = (tsModule as TypeScriptModule)?.default ?? tsModule as TypeScriptModule;
		const dependencyMetadata: DependencyHashRecord =
			options.descriptorMetadata ?? (await descriptorSourceMetadata(resolved));
		const cacheKey = hashText(
			JSON.stringify({
				schema: "pisharp-ts-transpile-cache-v5",
				resolverVersion: RUNTIME_IMPORT_RESOLVER_VERSION,
				sourcePath: resolved,
				sourceHash,
				compilerOptions: compilerOptionsFingerprint(ts),
				dependencies: dependencyMetadata.dependencyHashes,
			}),
		);
		let cachePath = cachePathForKey(cacheKey);

		if (!bridgeCacheEnabled)
			cachePath = await createFallbackTranspilePath(resolved, timings);

		if (bridgeCacheEnabled && !forceFresh) {
			try {
				const cached = await readCachedPath(cachePath, timings);
				if (!(await cachedModuleUsesCurrentCompatibilityImports(cached, source, resolved))) {
					timings.cacheHits = Math.max(0, timings.cacheHits - 1);
					timings.cacheMisses += 1;
					console.error(`[pisharp] stale compatibility shim cache; refreshing ${resolved}`);
				} else {
					transpiledBySource.set(memoKey, cached);
					return cached;
				}
			} catch {
				timings.cacheMisses += 1;
			}
		} else if (bridgeCacheEnabled && forceFresh) {
			timings.cacheMisses += 1;
		}

		transpiledBySource.set(memoKey, cachePath);
		const dependencyOutputs: DependencyOutput[] = [];
		for (const specifier of collectImportSpecifiers(source)) {
			const tsPath = await resolveTypeScriptImport(specifier, resolved);
			if (!tsPath) continue;
			dependencyOutputs.push({
				specifier,
				path: await transpileTypeScriptFile(tsPath, timings, "dependencyTranspile", { forceFresh }),
			});
		}

		let output = await transpileTypeScript(source, ts, timings, timingPhase);
		output = output.replaceAll(
			"createRequire(import.meta.url)",
			`createRequire("${pathToFileURL(resolved).href}")`,
		);
		output = rewriteTypeScriptImportSpecifiers(output, dependencyOutputs, cachePath, ts);
		output = await rewriteBareImportSpecifiers(output, collectImportSpecifiers(source), resolved, ts);

		if (bridgeCacheEnabled) {
			try {
				await writeCacheFile(cachePath, output);
				transpiledBySource.set(memoKey, cachePath);
				return cachePath;
			} catch {
				const fallback = await writeFallbackTranspile(resolved, output, timings);
				transpiledBySource.set(memoKey, fallback);
				return fallback;
			}
		}

		await fs.writeFile(cachePath, output, "utf8");
		transpiledBySource.set(memoKey, cachePath);
		return cachePath;
	}

	function moduleHrefFor(modulePath: string): string {
		const url = pathToFileURL(modulePath);
		url.searchParams.set("v", String(importVersion));
		return url.href;
	}

	async function moduleInfoFor(extensionPath: string, timings: LoadTimings, options: { forceFresh?: boolean } = {}): Promise<ModuleInfo> {
		const resolved = await resolveExtensionEntryPath(extensionPath);
		const descriptorMetadata = {
			...(await descriptorSourceMetadata(resolved)),
			...(await installedPackageMetadata(resolved)),
		};
		if (!resolved.endsWith(".ts")) {
			return {
				resolved,
				descriptorMetadata,
				modulePath: resolved,
				href: moduleHrefFor(resolved),
				transpiled: false,
				fromCache: false,
			};
		}

		const cacheHitsBefore = timings.cacheHits;
		const modulePath = await transpileTypeScriptFile(resolved, timings, "transpile", {
			...options,
			descriptorMetadata,
		});
		return {
			resolved,
			descriptorMetadata,
			modulePath,
			href: moduleHrefFor(modulePath),
			transpiled: true,
			fromCache: timings.cacheHits > cacheHitsBefore && !options.forceFresh,
		};
	}

	async function removeCachedModule(moduleInfo: ModuleInfo): Promise<void> {
		if (!moduleInfo?.modulePath || !bridgeCacheEnabled) return;
		if (!isInsideDirectory(moduleInfo.modulePath, bridgeCacheDirectory)) return;
		try {
			await fs.rm(moduleInfo.modulePath, { force: true });
		} catch {}
	}

	async function importExtensionModule(extensionPath: string, timings: LoadTimings): Promise<ImportResult> {
		const moduleInfo = await moduleInfoFor(extensionPath, timings);
		console.error(`[pisharp] transpile done ${extensionPath} cache=${moduleInfo.fromCache ?? false}`);
		try {
			const mod = await measurePhase(timings, "moduleImport", () =>
				import(moduleInfo.href),
			) as Record<string, unknown>;
			console.error(`[pisharp] import done ${extensionPath}`);
			return { module: mod, descriptorMetadata: moduleInfo.descriptorMetadata };
		} catch (error) {
			console.error(`[pisharp] import FAIL ${extensionPath}: ${(error as { message?: string })?.message ?? String(error)}`);
			if (!moduleInfo.transpiled || !moduleInfo.fromCache) throw error;
			console.error(`[pisharp] retry transpile ${extensionPath}`);
			await removeCachedModule(moduleInfo);
			importVersion += 1;
			const refreshed = await moduleInfoFor(extensionPath, timings, {
				forceFresh: true,
			});
			console.error(`[pisharp] retry transpile done ${extensionPath}`);
			try {
				const mod = await measurePhase(timings, "moduleImport", () =>
					import(refreshed.href),
				) as Record<string, unknown>;
				console.error(`[pisharp] retry import done ${extensionPath}`);
				return { module: mod, descriptorMetadata: refreshed.descriptorMetadata };
			} catch (retryError) {
				console.error(`[pisharp] retry import FAIL ${extensionPath}: ${(retryError as { message?: string })?.message ?? String(retryError)}`);
				const original = (error as { message?: string })?.message ?? String(error);
				const retry = (retryError as { message?: string })?.message ?? String(retryError);
				throw new Error(
					`Cached TypeScript extension module failed to import (${original}); fresh transpile retry also failed (${retry}).`,
				);
			}
		}
	}

	function setCacheDirectory(dir: string): void {
		if (typeof dir === "string") bridgeCacheDirectory = path.resolve(dir);
	}

	function setCacheEnabled(enabled: boolean): void {
		if (typeof enabled === "boolean") bridgeCacheEnabled = enabled;
	}

	function clearTranspiledBySource(): void {
		transpiledBySource.clear();
	}

	function bumpImportVersion(): void {
		importVersion += 1;
	}

	function registerExitCleanup(): void {
		process.on("exit", () => {
			for (const file of transpiledFiles) fs.unlink(file).catch(() => {});
		});
	}

	return {
		moduleInfoFor,
		importExtensionModule,
		setCacheDirectory,
		setCacheEnabled,
		clearTranspiledBySource,
		bumpImportVersion,
		registerExitCleanup,
		hashText,
	};
}
