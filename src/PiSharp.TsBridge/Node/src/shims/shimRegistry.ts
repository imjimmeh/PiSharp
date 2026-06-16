import { materializeModuleShims } from "./materialize.js";
import type { BridgeManifest } from "./manifest.js";

export class ShimRegistry {
	private shimUrls = new Map<string, string>();

	public constructor(private readonly cacheDirectoryProvider: () => string) {}

	public async initialize(manifest: BridgeManifest | null | undefined): Promise<void> {
		this.shimUrls.clear();
		if (!manifest) return;
		this.shimUrls = await materializeModuleShims({
			manifest,
			cacheDirectory: this.cacheDirectoryProvider(),
		});
	}

	public clear(): void {
		this.shimUrls.clear();
	}

	public async refresh(manifest: BridgeManifest | null | undefined): Promise<void> {
		await this.initialize(manifest);
	}

	public resolve(specifier: string): string | null {
		return this.shimUrls.get(specifier) ?? null;
	}

	public require(specifier: string): string {
		const url = this.resolve(specifier);
		if (url) return url;
		throw new Error(`No PiSharp TypeScript bridge compatibility shim is registered for '${specifier}'.`);
	}
}

export type { BridgeManifest } from "./manifest.js";
