import type {
	EventDispatchOptions,
	EventDispatchResult,
	PromptDocumentPatch,
	PromptSectionPatch,
} from "../types.js";

interface SectionPatchWithDefaults {
	id?: string;
	kind?: string;
	slot?: string;
	priority?: number;
	contentType?: string;
	content?: string;
	protected?: boolean;
	beforeSectionId?: string;
	afterSectionId?: string;
}

interface PayloadWithSections {
	sections?: Array<{ id?: string }>;
	[key: string]: unknown;
}

interface HandlerResult {
	systemPrompt?: string;
	messages?: unknown[];
	patch?: PromptDocumentPatch;
	promptDocumentPatch?: PromptDocumentPatch;
	action?: string;
	text?: string;
	images?: unknown[];
	cancel?: boolean;
	reason?: string;
	skillPaths?: string[];
	promptPaths?: string[];
	themePaths?: string[];
	result?: unknown;
	operations?: unknown;
}

function mergePromptDocumentPatches(
	left: PromptDocumentPatch | undefined,
	right: PromptDocumentPatch | undefined,
): PromptDocumentPatch {
	if (!left) return right ?? { removeSectionIds: [], replaceSections: [], appendSections: [] };
	if (!right) return left;
	return {
		removeSectionIds: [
			...(left.removeSectionIds ?? []),
			...(right.removeSectionIds ?? []),
		],
		replaceSections: [
			...(left.replaceSections ?? []),
			...(right.replaceSections ?? []),
		],
		appendSections: [
			...(left.appendSections ?? []),
			...(right.appendSections ?? []),
		],
	};
}

function sectionDtoFromPatch(section: SectionPatchWithDefaults): PromptSectionPatch {
	return {
		id: section.id,
		kind: section.kind ?? "extension",
		slot: section.slot ?? "instructions",
		priority: section.priority ?? 0,
		contentType: section.contentType ?? "markdown",
		content: section.content ?? "",
		protected: section.protected ?? false,
		beforeSectionId: section.beforeSectionId,
		afterSectionId: section.afterSectionId,
		sourceId: "extension:ts-bridge",
	} as PromptSectionPatch;
}

function applyPromptDocumentPatchToPayload(
	payload: PayloadWithSections,
	patch: PromptDocumentPatch,
): PayloadWithSections {
	if (!payload || !patch) return payload;
	let sections: Array<{ id?: string; [key: string]: unknown }> = Array.isArray(payload.sections) ? [...payload.sections] : [];
	const remove = new Set(patch.removeSectionIds ?? []);
	sections = sections.filter((section) => section.id !== undefined && !remove.has(section.id));
	for (const replacement of patch.replaceSections ?? []) {
		const dto = sectionDtoFromPatch(replacement);
		const index = sections.findIndex((section) => section.id === dto.id);
		if (index >= 0) sections[index] = dto as unknown as { id?: string; [key: string]: unknown };
		else sections.push(dto as unknown as { id?: string; [key: string]: unknown });
	}
	for (const appended of patch.appendSections ?? []) {
		const dto = sectionDtoFromPatch(appended);
		if (!sections.some((section) => section.id === dto.id))
			sections.push(dto as unknown as { id?: string; [key: string]: unknown });
	}
	return { ...payload, sections };
}

export async function dispatchEventToExtensions({
	name,
	payload,
	extensionStates,
	createExtensionContext,
	flushRegistrationRequests,
}: EventDispatchOptions): Promise<EventDispatchResult> {
	let currentPayload: Record<string, unknown> = payload;
	let systemPrompt;
	let patch;
	let action;
	let text;
	let images;
	let cancel;
	let reason;
	const messages = [];
	const skillPaths = [];
	const promptPaths = [];
	const themePaths = [];
	for (const [extensionId, state] of extensionStates.entries()) {
		const handlers = state.events.get(name) ?? [];
		for (const handler of handlers) {
			let result: HandlerResult | undefined;
			try {
				result = await handler(
					currentPayload,
					createExtensionContext(extensionId),
				) as HandlerResult | undefined;
			} catch (error) {
				if (
					name === "session_shutdown" ||
					name === "resources_discover" ||
					name === "user_bash"
				) {
				console.error(
						`[pisharp-ts-bridge] ${name} for ${extensionId} failed: ${(error as { message?: string })?.message ?? String(error)}`,
					);
					continue;
				}
				throw error;
			}
			if (!result || typeof result !== "object") continue;
			if (name === "before_agent_start") {
				if (typeof result.systemPrompt === "string") {
					systemPrompt = result.systemPrompt;
					currentPayload = { ...(currentPayload ?? {}), systemPrompt };
				}
				if (Array.isArray(result.messages)) messages.push(...result.messages);
			}
			if (name === "before_prompt_render") {
				const nextPatch = result.patch ?? result.promptDocumentPatch;
				if (nextPatch && typeof nextPatch === "object") {
					patch = mergePromptDocumentPatches(patch, nextPatch);
					currentPayload = applyPromptDocumentPatchToPayload(
						currentPayload,
						nextPatch,
					);
				}
			}
			if (name === "input") {
				if (result.action === "handled") {
					action = "handled";
					await flushRegistrationRequests();
					return { action };
				}
				if (result.action === "transform" && typeof result.text === "string") {
					action = "transform";
					text = result.text;
					images = (Array.isArray(result.images)
						? result.images
						: currentPayload?.images) as unknown[] | undefined;
					currentPayload = { ...(currentPayload ?? {}), text, images };
				}
			}
			if (
				(name === "session_before_switch" || name === "session_before_fork") &&
				result.cancel === true
			) {
				cancel = true;
				reason = typeof result.reason === "string" ? result.reason : undefined;
				await flushRegistrationRequests();
				return { cancel, reason };
			}
			if (name === "resources_discover") {
				if (Array.isArray(result.skillPaths))
					skillPaths.push(...result.skillPaths);
				if (Array.isArray(result.promptPaths))
					promptPaths.push(...result.promptPaths);
				if (Array.isArray(result.themePaths))
					themePaths.push(...result.themePaths);
			}
			if (name === "user_bash") {
				if (result.result || result.operations) {
					await flushRegistrationRequests();
					return { bashResult: result.result, operations: result.operations };
				}
			}
		}
	}
	await flushRegistrationRequests();
	return {
		...(systemPrompt === undefined ? {} : { systemPrompt }),
		...(messages.length === 0 ? {} : { messages }),
		...(patch === undefined ? {} : { patch }),
		...(action === undefined ? {} : { action }),
		...(text === undefined ? {} : { text }),
		...(images === undefined ? {} : { images }),
		...(cancel === undefined ? {} : { cancel }),
		...(reason === undefined ? {} : { reason }),
		...(skillPaths.length === 0 ? {} : { skillPaths }),
		...(promptPaths.length === 0 ? {} : { promptPaths }),
		...(themePaths.length === 0 ? {} : { themePaths }),
		bashResult: undefined,
		operations: undefined,
	};
}
