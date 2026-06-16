declare const process: {
	env: Record<string, string | undefined>;
};

const SERVICE_KEY = "pisharp.embeddings";
const DEFAULT_TIMEOUT_MS = 30_000;

type EmbeddingVector = number[];

type EmbedManyRequest = {
	providerId?: string;
	model?: string;
	inputs: unknown[];
	dimensions?: number;
	providerOptions?: Record<string, unknown>;
	headers?: Record<string, string>;
	timeoutMs?: number;
};

type EmbedRequest = Omit<EmbedManyRequest, "inputs"> & {
	input: unknown;
};

type EmbeddingResponse = {
	embeddings: EmbeddingVector[];
	providerId: string;
	model?: string;
	dimensions: number;
	usage?: unknown;
	metadata?: Record<string, unknown>;
};

type SingleEmbeddingResponse = EmbeddingResponse & {
	embedding: EmbeddingVector;
};

type EmbeddingProvider = {
	id: string;
	label?: string;
	kind?: string;
	defaultModel?: string;
	supportsBatching?: boolean;
	supportsDimensions?: boolean;
	embedMany(request: EmbedManyRequest): Promise<EmbeddingResponse>;
};

type ProviderInfo = {
	id: string;
	label: string;
	kind: string;
	defaultModel?: string;
	supportsBatching: boolean;
	supportsDimensions: boolean;
};

type EmbeddingsApi = {
	version: 1;
	registerProvider(provider: EmbeddingProvider): { dispose(): void };
	listProviders(): Promise<ProviderInfo[]>;
	getDefaultProvider(): Promise<ProviderInfo>;
	embed(request: EmbedRequest): Promise<SingleEmbeddingResponse>;
	embedMany(request: EmbedManyRequest): Promise<EmbeddingResponse>;
};

type PiApi = {
	extensions: {
		declare(options: { provides?: string[]; activation?: "eager" }): void;
		provide(key: string, api: EmbeddingsApi): { dispose(): void };
	};
};

type OpenAIEmbeddingData = {
	index?: number;
	embedding: EmbeddingVector;
};

type OpenAIEmbeddingResponse = {
	object?: string;
	model?: string;
	data?: OpenAIEmbeddingData[];
	usage?: unknown;
};

export default function activate(pi: PiApi): EmbeddingsApi {
	pi.extensions.declare({ provides: [SERVICE_KEY], activation: "eager" });
	const providers = new Map<string, EmbeddingProvider>();
	const defaultProviderId = process.env.PISHARP_EMBEDDINGS_PROVIDER ?? "openai";

	function registerProvider(provider: EmbeddingProvider): { dispose(): void } {
		if (!provider.id || typeof provider.embedMany !== "function") {
			throw new Error(
				"Embedding provider registration requires id and embedMany(request)",
			);
		}
		const registeredProvider = provider;
		providers.set(provider.id, registeredProvider);
		return {
			dispose() {
				if (providers.get(provider.id) === registeredProvider)
					providers.delete(provider.id);
			},
		};
	}

	function resolveProvider(providerId: string): EmbeddingProvider {
		const provider = providers.get(providerId);
		if (!provider)
			throw new Error(`Embedding provider '${providerId}' is not registered`);
		return provider;
	}

	registerProvider(
		createOpenAICompatibleProvider({
			id: "openai",
			label: "OpenAI-compatible embeddings",
			baseUrl:
				process.env.PISHARP_EMBEDDINGS_BASE_URL ?? "https://api.openai.com/v1",
			apiKey:
				process.env.PISHARP_EMBEDDINGS_API_KEY ?? process.env.OPENAI_API_KEY,
			defaultModel:
				process.env.PISHARP_EMBEDDINGS_MODEL ?? "text-embedding-3-small",
		}),
	);

	const api: EmbeddingsApi = {
		version: 1,
		registerProvider,
		listProviders: async () => [...providers.values()].map(toProviderInfo),
		getDefaultProvider: async () =>
			toProviderInfo(resolveProvider(defaultProviderId)),
		embed: async (request) => {
			const response = await api.embedMany({
				...request,
				inputs: [request.input],
			});
			return { ...response, embedding: response.embeddings[0] ?? [] };
		},
		embedMany: async (request) => {
			const provider = resolveProvider(request.providerId ?? defaultProviderId);
			return provider.embedMany(normalizeEmbedManyRequest(request, provider));
		},
	};

	pi.extensions.provide(SERVICE_KEY, api);
	return api;
}

function createOpenAICompatibleProvider(config: {
	id: string;
	label: string;
	baseUrl: string;
	apiKey?: string;
	defaultModel: string;
}): EmbeddingProvider {
	return {
		id: config.id,
		label: config.label,
		kind: "openai-compatible",
		defaultModel: config.defaultModel,
		supportsBatching: true,
		supportsDimensions: true,
		async embedMany(request) {
			if (!config.apiKey && !config.baseUrl.startsWith("http://localhost")) {
				throw new Error(
					`Embedding provider '${config.id}' requires an API key`,
				);
			}
			const body = {
				model: request.model ?? config.defaultModel,
				input: request.inputs,
				...(request.dimensions ? { dimensions: request.dimensions } : {}),
				...(request.providerOptions ?? {}),
			};
			const response = await fetchWithTimeout(
				`${trimTrailingSlash(config.baseUrl)}/embeddings`,
				{
					method: "POST",
					headers: {
						"content-type": "application/json",
						...(config.apiKey
							? { authorization: `Bearer ${config.apiKey}` }
							: {}),
						...(request.headers ?? {}),
					},
					body: JSON.stringify(body),
				},
				request.timeoutMs ?? DEFAULT_TIMEOUT_MS,
			);
			if (!response.ok) {
				const text = await response.text();
				throw new Error(
					`Embedding provider '${config.id}' failed (${response.status}): ${text}`,
				);
			}
			const json = (await response.json()) as OpenAIEmbeddingResponse;
			const data = [...(json.data ?? [])].sort(
				(left, right) => (left.index ?? 0) - (right.index ?? 0),
			);
			const embeddings = data.map((item) => item.embedding);
			return {
				embeddings,
				providerId: config.id,
				model: json.model ?? body.model,
				dimensions: embeddings[0]?.length ?? request.dimensions ?? 0,
				usage: json.usage,
				metadata: { object: json.object },
			};
		},
	};
}

function normalizeEmbedManyRequest(
	request: EmbedManyRequest,
	provider: EmbeddingProvider,
): EmbedManyRequest {
	if (!Array.isArray(request.inputs) || request.inputs.length === 0) {
		throw new Error("embedMany requires a non-empty inputs array");
	}
	return {
		...request,
		model: request.model ?? provider.defaultModel,
		inputs: request.inputs.map((input) => String(input ?? "")),
	};
}

function toProviderInfo(provider: EmbeddingProvider): ProviderInfo {
	return {
		id: provider.id,
		label: provider.label ?? provider.id,
		kind: provider.kind ?? "custom",
		defaultModel: provider.defaultModel,
		supportsBatching: provider.supportsBatching !== false,
		supportsDimensions: provider.supportsDimensions === true,
	};
}

async function fetchWithTimeout(
	url: string,
	init: RequestInit,
	timeoutMs: number,
): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), timeoutMs);
	try {
		return await fetch(url, { ...init, signal: controller.signal });
	} finally {
		clearTimeout(timeout);
	}
}

function trimTrailingSlash(value: string): string {
	return value.replace(/\/+$/u, "");
}
