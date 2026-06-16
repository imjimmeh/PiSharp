declare const process: {
	env: Record<string, string | undefined>;
};

type EmbeddingVector = number[];

type EmbeddingResponse = {
	embeddings: EmbeddingVector[];
};

type EmbeddingsApi = {
	embed(request: {
		input: string;
		purpose?: string;
		timeoutMs?: number;
	}): Promise<{ embedding: EmbeddingVector }>;
	embedMany(request: {
		inputs: string[];
		purpose?: string;
		timeoutMs?: number;
	}): Promise<EmbeddingResponse>;
};

type ExtensionSkill = {
	name?: string;
	Name?: string;
	description?: string;
	Description?: string;
	filePath?: string;
	FilePath?: string;
	disableModelInvocation?: boolean;
	DisableModelInvocation?: boolean;
};

type NormalizedSkill = {
	name: string;
	description: string;
	location: string;
	disableModelInvocation: boolean;
};

type PromptDocumentSection = {
	id?: string;
	kind?: string;
	slot?: string;
	priority?: number;
	contentType?: string;
	content?: string;
	protected?: boolean;
};

type PromptDocumentSectionPatch = {
	id: string;
	kind?: string;
	slot?: string;
	priority?: number;
	contentType?: "raw" | "markdown";
	content: string;
	protected?: boolean;
};

type BeforePromptRenderEvent = {
	prompt?: string;
	sections?: PromptDocumentSection[];
};

type PiApi = {
	extensions: {
		declare(options: { consumes?: string[]; activation?: "eager" }): void;
		waitFor(
			key: string,
			options?: { timeoutMs?: number },
		): Promise<EmbeddingsApi>;
	};
	skills: {
		list(): Promise<ExtensionSkill[]>;
	};
	on(
		eventName: "before_prompt_render",
		handler: (
			event: BeforePromptRenderEvent,
		) => Promise<
			{ patch: { replaceSections: PromptDocumentSectionPatch[] } } | undefined
		>,
	): { dispose(): void };
};

const EMBEDDINGS_SERVICE_KEY = "pisharp.embeddings";
const DEFAULT_MAX_SKILLS = 8;
const DEFAULT_TIMEOUT_MS = 5_000;

export default function activate(pi: PiApi): void {
	pi.extensions.declare({
		consumes: [EMBEDDINGS_SERVICE_KEY],
		activation: "eager",
	});
	const maxSkills = readPositiveInteger(
		process.env.PISHARP_SKILL_RELEVANCE_MAX_SKILLS,
		DEFAULT_MAX_SKILLS,
	);
	const timeoutMs = readPositiveInteger(
		process.env.PISHARP_SKILL_RELEVANCE_TIMEOUT_MS,
		DEFAULT_TIMEOUT_MS,
	);
	const minScore = Number.parseFloat(
		process.env.PISHARP_SKILL_RELEVANCE_MIN_SCORE ?? "-1",
	);
	let embeddingsPromise: Promise<EmbeddingsApi> | undefined;

	pi.on("before_prompt_render", async (event) => {
		try {
			const skillsSection = (event.sections ?? []).find(
				(section) => section.id === "skills.available",
			);
			if (!skillsSection?.content) return undefined;
			const availableNames = availableSkillNames(skillsSection.content);
			if (availableNames.size === 0) return undefined;
			const skills = await visibleSkills(pi, availableNames);
			if (skills.length === 0) return undefined;
			const embeddings = await embeddingsService();
			const ranked = await rankSkills(
				embeddings,
				skills,
				String(event.prompt ?? ""),
				timeoutMs,
			);
			const selected = ranked
				.filter((item) => item.score >= minScore)
				.slice(0, maxSkills)
				.map((item) => item.skill);
			if (selected.length === 0 || selected.length === skills.length)
				return undefined;
			return {
				patch: {
					replaceSections: [
						{
							id: "skills.available",
							kind: "skills",
							slot: skillsSection.slot ?? "skills",
							priority: skillsSection.priority ?? 0,
							contentType: "raw",
							content: formatAvailableSkillsSection(selected),
							protected: skillsSection.protected ?? false,
						},
					],
				},
			};
		} catch (error) {
			console.error(
				`[relevance-filtered-skills] keeping original skills: ${error instanceof Error ? error.message : String(error)}`,
			);
			return undefined;
		}
	});

	function embeddingsService(): Promise<EmbeddingsApi> {
		embeddingsPromise ??= pi.extensions
			.waitFor(EMBEDDINGS_SERVICE_KEY, { timeoutMs })
			.catch((error: unknown) => {
				embeddingsPromise = undefined;
				throw error;
			});
		return embeddingsPromise;
	}
}

async function rankSkills(
	embeddings: EmbeddingsApi,
	skills: NormalizedSkill[],
	prompt: string,
	timeoutMs: number,
): Promise<Array<{ skill: NormalizedSkill; score: number }>> {
	const response = await embeddings.embedMany({
		inputs: skills.map((skill) => `${skill.name}\n${skill.description}`),
		purpose: "document",
		timeoutMs,
	});
	if (
		!Array.isArray(response.embeddings) ||
		response.embeddings.length !== skills.length
	) {
		throw new Error(
			`Embedding provider returned ${response.embeddings?.length ?? 0} embeddings for ${skills.length} skills`,
		);
	}
	const promptEmbedding = (
		await embeddings.embed({
			input: prompt || "current turn",
			purpose: "query",
			timeoutMs,
		})
	).embedding;
	return skills
		.map((skill, index) => ({
			skill,
			score: cosineSimilarity(
				promptEmbedding,
				response.embeddings[index] ?? [],
			),
		}))
		.sort((left, right) => right.score - left.score);
}

async function visibleSkills(
	pi: PiApi,
	availableNames: Set<string>,
): Promise<NormalizedSkill[]> {
	const skills = await pi.skills.list();
	return skills
		.map(normalizeSkill)
		.filter((skill): skill is NormalizedSkill =>
			Boolean(
				skill?.name &&
					availableNames.has(skill.name) &&
					skill.description &&
					!skill.disableModelInvocation,
			),
		);
}

function normalizeSkill(skill: ExtensionSkill): NormalizedSkill | undefined {
	const name = skill.name ?? skill.Name;
	const description = skill.description ?? skill.Description;
	if (!name || !description) return undefined;
	return {
		name,
		description,
		location: skill.filePath ?? skill.FilePath ?? "",
		disableModelInvocation:
			skill.disableModelInvocation ?? skill.DisableModelInvocation ?? false,
	};
}

function availableSkillNames(skillsSectionContent: string): Set<string> {
	return new Set(
		[...skillsSectionContent.matchAll(/<name>([\s\S]*?)<\/name>/gu)].map(
			(match) => unescapeXml(match[1] ?? ""),
		),
	);
}

function formatAvailableSkillsSection(skills: NormalizedSkill[]): string {
	return [
		"The following skills provide specialized instructions for specific tasks.",
		"Read the full skill file when the task matches its description.",
		"When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
		"",
		formatAvailableSkills(skills),
	].join("\n");
}

function formatAvailableSkills(skills: NormalizedSkill[]): string {
	const lines = ["<available_skills>"];
	for (const skill of skills) {
		lines.push("  <skill>");
		lines.push(`    <name>${escapeXml(skill.name)}</name>`);
		lines.push(
			`    <description>${escapeXml(skill.description)}</description>`,
		);
		lines.push(`    <location>${escapeXml(skill.location)}</location>`);
		lines.push("  </skill>");
	}
	lines.push("</available_skills>");
	return lines.join("\n");
}

function cosineSimilarity(
	left: EmbeddingVector,
	right: EmbeddingVector,
): number {
	let dot = 0;
	let leftMagnitude = 0;
	let rightMagnitude = 0;
	const length = Math.min(left.length, right.length);
	for (let index = 0; index < length; index += 1) {
		dot += (left[index] ?? 0) * (right[index] ?? 0);
		leftMagnitude += (left[index] ?? 0) * (left[index] ?? 0);
		rightMagnitude += (right[index] ?? 0) * (right[index] ?? 0);
	}
	if (leftMagnitude === 0 || rightMagnitude === 0) return 0;
	return dot / (Math.sqrt(leftMagnitude) * Math.sqrt(rightMagnitude));
}

function escapeXml(value: string): string {
	return value
		.replaceAll("&", "&amp;")
		.replaceAll("<", "&lt;")
		.replaceAll(">", "&gt;")
		.replaceAll('"', "&quot;")
		.replaceAll("'", "&apos;");
}

function unescapeXml(value: string): string {
	return value
		.replaceAll("&quot;", '"')
		.replaceAll("&apos;", "'")
		.replaceAll("&gt;", ">")
		.replaceAll("&lt;", "<")
		.replaceAll("&amp;", "&");
}

function readPositiveInteger(
	value: string | undefined,
	fallback: number,
): number {
	const parsed = Number.parseInt(value ?? "", 10);
	return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}
