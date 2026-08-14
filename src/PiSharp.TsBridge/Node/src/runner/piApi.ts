import type { BridgeProtocol } from "../protocol.js";
import type { ExtensionState, PiApiDeps, UiApi, UiApiOptions, ToolDescriptor, EventHandler, MessageRendererHandler } from "../types.js";

interface SkillRegistration {
	name: string;
	description: string;
	content?: string;
	filePath?: string;
	path?: string;
	disableModelInvocation?: boolean;
	override?: string;
	globs?: string[];
	alwaysApply?: boolean;
	hide?: boolean;
	source?: string;
	sourcePriority?: number;
	runner?: (context: {
		name: string;
		body: string;
		additionalInstructions?: string;
		args: string[];
	}) => unknown | Promise<unknown>;
}

interface CommandOptions {
	description?: string;
	handler?: (...args: unknown[]) => unknown;
}

interface ShortcutOptions {
	description?: string;
	handler?: (...args: unknown[]) => unknown;
}

interface FlagOptions {
	description?: string;
	type?: string;
	defaultValue?: unknown;
	default?: unknown;
}

interface ProviderConfig {
	name?: string;
	api?: string;
	[key: string]: unknown;
}

/** JavaScript Pi-compatible message renderer callback type. */
export type MessageRenderer = (
	message: {
		role: "custom";
		customType: string;
		content: string | unknown[];
		display: boolean;
		details?: unknown;
		timestamp: number;
	},
	options: { expanded: boolean },
	theme: unknown,
) => unknown | undefined | Promise<unknown | undefined>;

interface MessageRendererOptions {
	name?: string;
	customType?: string;
	handler?: (...args: unknown[]) => unknown;
	rowType?: string | null;
	override?: string;
}

interface MessageDecoratorOptions {
	order?: number;
	rowType?: string | null;
}

interface DeclarationOptions {
	provides?: string[];
	consumes?: string[];
	dependsOn?: string[];
	activation?: string;
	eager?: boolean;
}

interface PromptSection {
	id: string;
	slot?: string;
	priority?: number;
	content?: string;
	markdown?: string;
	title?: string;
	protected?: boolean;
	override?: string;
}

interface PromptTransform {
	name?: string;
	id?: string;
	mode?: string;
	appendMarkdown?: string;
	content?: string;
	removeSectionIds?: string[];
}

type Disposable = { dispose?: () => void; unsubscribe?: () => void };

function normalizeOptions(optionsOrDescription: string | Record<string, unknown>, maybeHandler?: (...args: unknown[]) => unknown): Record<string, unknown> {
	return typeof optionsOrDescription === "object" &&
		optionsOrDescription !== null
		? optionsOrDescription
		: { description: optionsOrDescription, handler: maybeHandler };
}

function serializeTool(tool: {
	name: string;
	label?: string;
	description?: string;
	parameters?: unknown;
	parametersSchema?: unknown;
	executionMode?: string;
	promptSnippet?: string;
	promptGuidelines?: string;
	renderShell?: boolean;
	rendererName?: string;
	renderCall?: unknown;
	renderResult?: unknown;
}): ToolDescriptor {
	return {
		name: tool.name,
		label: tool.label ?? tool.name,
		description: tool.description ?? "",
		parameters: tool.parameters ??
			tool.parametersSchema ?? { type: "object", properties: {} },
		executionMode: tool.executionMode,
		promptSnippet: tool.promptSnippet,
		promptGuidelines: tool.promptGuidelines,
		renderShell: tool.renderShell,
		rendererName: tool.rendererName,
		hasRenderCall: typeof tool.renderCall === "function",
		hasRenderResult: typeof tool.renderResult === "function",
	};
}

function toUnsubscribe(disposable: Disposable | undefined): { dispose: () => void; unsubscribe: () => void } {
	const unsubscribe = () => disposable?.dispose?.();
	const result = unsubscribe as unknown as { dispose: () => void; unsubscribe: () => void };
	result.dispose = unsubscribe;
	result.unsubscribe = unsubscribe;
	return result;
}

export function createPiApi({ extensionId, state, deps }: { extensionId: string; state: ExtensionState; deps: PiApiDeps }): Record<string, unknown> {
	const {
		sendRequest,
		trackRegistrationRequest,
		provideExtensionService,
		getExtensionService,
		waitForExtensionService,
		rememberDescriptorService,
		normalizeServiceKey,
		addEventHandler,
		createUiApi,
		cwd,
		getCommandsSnapshot,
		addCommandToSnapshot,
		getSessionNameSnapshot,
		getSnapshot,
		protocol,
	} = deps;
	const methods = protocol.methods;
	const actions = protocol.runtimeActions;
	const runtime = (action: string, payload: Record<string, unknown>): Promise<unknown> =>
		sendRequest(methods.runtimeAction, { extensionId, action, payload }, { timeoutMs: null });
	const skillApi = {
		register(skill: SkillRegistration): Disposable {
			if (!skill?.name || !skill?.description)
				throw new Error("Skill registration requires name and description");
			const descriptor = {
				extensionId,
				name: skill.name,
				description: skill.description,
				content: skill.content ?? "",
				filePath:
					skill.filePath ?? skill.path ?? `${extensionId}#${skill.name}`,
				disableModelInvocation: skill.disableModelInvocation ?? false,
				override: skill.override ?? "reject",
				globs: skill.globs ?? [],
				alwaysApply: skill.alwaysApply ?? false,
				hide: skill.hide ?? false,
				source: skill.source,
				sourcePriority: skill.sourcePriority ?? 0,
				hasRunner: typeof skill.runner === "function",
			};
			state.descriptor.skills.push(descriptor);
			trackRegistrationRequest(methods.registerSkill, descriptor);
			return { dispose: () => {} };
		},
		list: async (): Promise<unknown> => ((await runtime(actions.getAllSkills, {})) as Record<string, unknown> | undefined)?.value ?? [],
		selected: async (): Promise<unknown> =>
			((await runtime(actions.getSelectedSkills, {})) as Record<string, unknown> | undefined)?.value ?? [],
		select: (skillNames: string[]): Promise<unknown> => runtime(actions.setSelectedSkills, { skillNames }),
		managed: {
			create: (request: { name: string; description: string; content: string; disableModelInvocation?: boolean }): Promise<unknown> =>
				runtime(actions.managedSkillCreate, request as Record<string, unknown>),
			update: (name: string, request: { description?: string; content?: string; disableModelInvocation?: boolean }): Promise<unknown> =>
				runtime(actions.managedSkillUpdate, { name, ...request } as Record<string, unknown>),
			delete: (name: string): Promise<unknown> => runtime(actions.managedSkillDelete, { name }),
			list: async (): Promise<unknown> => ((await runtime(actions.managedSkillList, {})) as Record<string, unknown> | undefined)?.value ?? [],
			promote: (sourceReference: string): Promise<unknown> => runtime(actions.managedSkillPromote, { sourceReference }),
		},
	};
	const packagesApi = {
		install: (reference: string, options: { local?: boolean; force?: boolean; offline?: boolean } = {}): Promise<unknown> =>
			runtime(actions.installExtension, { reference, ...options } as Record<string, unknown>),
		update: (request: { source?: string; extensions?: boolean; extensionSource?: string; force?: boolean; offline?: boolean } = {}): Promise<unknown> =>
			runtime(actions.updateExtension, request as Record<string, unknown>),
		remove: (reference: string, options: { local?: boolean } = {}): Promise<unknown> =>
			runtime(actions.removeExtension, { reference, ...options } as Record<string, unknown>),
		list: async (): Promise<unknown> => ((await runtime(actions.listInstalledExtensions, {})) as Record<string, unknown> | undefined)?.value ?? [],
	};
	const skillProvidersApi = {
		register: (provider: { name: string; priority?: number }): Promise<unknown> =>
			runtime(actions.registerSkillProvider, { name: provider.name, priority: provider.priority ?? 0 }),
	};
	const extensionsApi = {
		provide: (key: string, api: unknown): { dispose: () => void } =>
			provideExtensionService(extensionId, state, key, api),
		get: (key: string): unknown => getExtensionService(state, key),
		waitFor: (key: string, options?: { timeoutMs?: number }): Promise<unknown> => waitForExtensionService(state, key, options),
		declare(options: DeclarationOptions = {}): void {
			for (const key of options.provides ?? [])
				rememberDescriptorService(
					state,
					"providesServices",
					normalizeServiceKey(key),
				);
			for (const key of options.consumes ?? options.dependsOn ?? [])
				rememberDescriptorService(
					state,
					"consumesServices",
					normalizeServiceKey(key),
				);
			if (options.activation === "eager" || options.eager === true)
				state.descriptor.activation = "eager";
		},
	};
	return {
		cwd,
		events: {
			on(eventName: string, handler: EventHandler) {
				return toUnsubscribe(addEventHandler(state, eventName, handler));
			},
			async emit(eventName: string, payload: unknown): Promise<unknown> {
				if (typeof deps.emitEvent === "function") {
					await deps.emitEvent(eventName, payload);
				}
				return runtime(actions.emitEvent, { channel: eventName, payload } as Record<string, unknown>);
			},
		},
		on(eventName: string, handler: EventHandler) {
			return toUnsubscribe(addEventHandler(state, eventName, handler));
		},
		getCommands: () => getCommandsSnapshot?.() ?? [],
		registerCommand(name: string, optionsOrDescription: string | Record<string, unknown>, maybeHandler?: (...args: unknown[]) => unknown): Disposable {
			const options = normalizeOptions(optionsOrDescription, maybeHandler) as CommandOptions;
			if (typeof options.handler !== "function")
				throw new Error(`Command ${name} requires a handler`);
			state.commands.set(name, options.handler as (args: string, context: unknown) => unknown);
			const descriptor = {
				extensionId,
				name,
				description: options.description ?? "",
			};
			state.descriptor.commands.push(descriptor);
			addCommandToSnapshot?.({
				name,
				description: descriptor.description,
				source: "extension",
				sourceInfo: {
					path: extensionId,
					source: extensionId,
					scope: "temporary",
					origin: "top-level",
				},
			});
			trackRegistrationRequest(methods.registerCommand, descriptor);
			return { dispose: () => { state.commands.delete(name); } };
		},
		registerShortcut(keys: string, optionsOrDescription: string | Record<string, unknown>, maybeHandler?: (...args: unknown[]) => unknown): Disposable {
			const options = normalizeOptions(optionsOrDescription, maybeHandler) as ShortcutOptions;
			if (typeof options.handler !== "function")
				throw new Error(`Shortcut ${keys} requires a handler`);
			state.commands.set(`shortcut:${keys}`, options.handler as (args: string, context: unknown) => unknown);
			const descriptor = {
				extensionId,
				keys,
				description: options.description ?? "",
			};
			state.descriptor.shortcuts.push(descriptor);
			trackRegistrationRequest(methods.registerShortcut, descriptor);
			return { dispose: () => { state.commands.delete(`shortcut:${keys}`); } };
		},
		registerFlag(name: string, options: FlagOptions = {}): Disposable {
			const descriptor = {
				extensionId,
				name,
				description: options.description ?? "",
				type: options.type ?? "boolean",
				defaultValue: options.defaultValue ?? options.default,
			};
			state.descriptor.flags.push(descriptor);
			trackRegistrationRequest(methods.registerFlag, descriptor);
			return { dispose: () => {} };
		},
		prompt: {
			registerSection(section: PromptSection): Disposable {
				const descriptor = {
					extensionId,
					id: section.id,
					slot: section.slot ?? "instructions",
					priority: section.priority ?? 0,
					content: section.content ?? section.markdown ?? "",
					title: section.title,
					protected: section.protected ?? false,
					override: section.override ?? "reject",
				};
				state.descriptor.promptSections.push(descriptor);
				trackRegistrationRequest(methods.registerPromptSection, descriptor);
				return { dispose: () => {} };
			},
			registerTransform(transform: PromptTransform): Disposable {
				const descriptor = {
					extensionId,
					name: transform.name ?? transform.id ?? "transform",
					mode: transform.mode ?? "append",
					appendMarkdown: transform.appendMarkdown ?? transform.content,
					removeSectionIds: transform.removeSectionIds,
				};
				state.descriptor.promptTransforms.push(descriptor);
				trackRegistrationRequest(methods.registerPromptTransform, descriptor);
				return { dispose: () => {} };
			},
		},
		registerMessageRenderer(nameOrOptions: string | MessageRendererOptions, maybeHandler?: (...args: unknown[]) => unknown): Disposable {
			const options: MessageRendererOptions =
				typeof nameOrOptions === "object" && nameOrOptions !== null
					? nameOrOptions
					: { name: nameOrOptions, handler: maybeHandler };
			const name = options.name ?? "renderer";
			const customType =
				options.customType ??
				(typeof nameOrOptions === "string" ? nameOrOptions : undefined);
			const handler = options.handler ?? maybeHandler;
			if (typeof handler !== "function")
				throw new Error(`MessageRenderer ${name} requires a handler function`);
			state.renderers.set(name, handler as MessageRendererHandler);
			trackRegistrationRequest(methods.registerMessageRenderer, {
				extensionId,
				name,
				rowType: options.rowType ?? null,
				customType: customType ?? null,
				override: options.override ?? "override",
			});
			let disposed = false;
			const dispose = () => {
				if (disposed) return;
				disposed = true;
				state.renderers.delete(name);
				trackRegistrationRequest(methods.unregisterMessageRenderer, {
					extensionId,
					name,
				});
			};
			return {
				dispose,
				unsubscribe: dispose,
			};
		},
		registerMessageDecorator(nameOrOptions: string | MessageRendererOptions, maybeOptions?: MessageDecoratorOptions | ((...args: unknown[]) => unknown), maybeHandler?: (...args: unknown[]) => unknown): Disposable {
			const isObject =
				typeof nameOrOptions === "object" && nameOrOptions !== null;
			const maybeOptionsObject =
				typeof maybeOptions === "object" && maybeOptions !== null;
			const name = isObject
				? ((nameOrOptions as MessageRendererOptions).name ?? "decorator")
				: nameOrOptions;
			const customType = isObject ? (nameOrOptions as MessageRendererOptions).customType : nameOrOptions;
			let handler: ((...args: unknown[]) => unknown) | undefined = maybeHandler;
			if (isObject) handler = (nameOrOptions as MessageRendererOptions).handler;
			else if (typeof maybeOptions === "function") handler = maybeOptions;
			if (typeof handler !== "function")
				throw new Error(`MessageDecorator ${name} requires a handler function`);
			let order = 0;
			if (isObject) order = (nameOrOptions as MessageDecoratorOptions).order ?? 0;
			else if (maybeOptionsObject) order = (maybeOptions as MessageDecoratorOptions).order ?? 0;
			let rowType: string | null = null;
			if (isObject) rowType = (nameOrOptions as MessageRendererOptions).rowType ?? null;
			else if (maybeOptionsObject) rowType = (maybeOptions as MessageDecoratorOptions).rowType ?? null;
			state.decorators.set(name, handler);
			trackRegistrationRequest(methods.registerMessageDecorator, {
				extensionId,
				name,
				rowType,
				customType: customType ?? null,
				order,
			});
			let disposed = false;
			const dispose = () => {
				if (disposed) return;
				disposed = true;
				state.decorators.delete(name);
				trackRegistrationRequest(methods.unregisterMessageDecorator, {
					extensionId,
					name,
				});
			};
			return {
				dispose,
				unsubscribe: dispose,
			};
		},
		appendEntry: (type: unknown, data: unknown): Promise<unknown> => runtime(actions.appendEntry, { type, data } as Record<string, unknown>),
		getThinkingLevel: async (): Promise<unknown> => {
			const snapshot = getSnapshot();
			if (snapshot?.thinkingLevel) return snapshot.thinkingLevel;
			return ((await runtime(actions.getThinkingLevel, {})) as Record<string, unknown> | undefined)?.value;
		},
		getActiveTools: async (): Promise<unknown> => {
			const snapshot = getSnapshot();
			if (snapshot?.activeTools?.length) return snapshot.activeTools;
			return ((await runtime(actions.getActiveTools, {})) as Record<string, unknown> | undefined)?.value ?? [];
		},
		getAllTools: async (): Promise<unknown> => {
			const snapshot = getSnapshot();
			if (snapshot?.allTools?.length) return snapshot.allTools;
			return ((await runtime(actions.getAllTools, {})) as Record<string, unknown> | undefined)?.value ?? [];
		},
		getAllSkills: () => skillApi.list(),
		getSelectedSkills: () => skillApi.selected(),
		setSelectedSkills: (skillNames: string[]): Promise<unknown> => skillApi.select(skillNames),
		skills: skillApi,
		extensions: extensionsApi,
		packages: packagesApi,
		skillProviders: skillProvidersApi,
		registerSkill: (skill: SkillRegistration): Disposable => skillApi.register(skill),
		registerTool(tool: { name?: string; execute?: unknown } & Record<string, unknown>): Disposable {
			if (!tool?.name || typeof tool.execute !== "function")
				throw new Error("Tool registration requires name and execute");
			state.tools.set(tool.name, tool as { name: string } & Record<string, unknown>);
			const descriptor = {
				extensionId,
				...serializeTool(tool as { name: string }),
			};
			state.descriptor.tools.push(descriptor);
			trackRegistrationRequest(methods.registerTool, descriptor);
			return { dispose: () => { state.tools.delete(tool.name!); } };
		},
		registerProvider(nameOrConfig: string | ProviderConfig, maybeConfig?: ProviderConfig): Disposable {
			const config: ProviderConfig =
				typeof nameOrConfig === "string"
					? { ...(maybeConfig ?? {}), name: nameOrConfig }
					: nameOrConfig;
			const name = config.name ?? config.api ?? "provider";
			state.providers.set(name, config);
			const descriptor = {
				extensionId,
				...config,
				name,
				api: config.api ?? name,
			};
			state.descriptor.providers.push(descriptor);
			trackRegistrationRequest(methods.registerProvider, descriptor);
			return { dispose: () => { state.providers.delete(name); } };
		},
		unregisterProvider(name: string): Promise<unknown> {
			state.providers.delete(name);
			return sendRequest(methods.unregisterProvider, { extensionId, name });
		},
		getFlag: async (name: string): Promise<unknown> => {
			const snapshot = getSnapshot();
			if (snapshot?.flags && name in snapshot.flags) return snapshot.flags[name];
			return ((await runtime(actions.getFlag, { name })) as Record<string, unknown> | undefined)?.value;
		},
		getFlags: async (): Promise<unknown> => {
			const snapshot = getSnapshot();
			if (snapshot?.flags) return snapshot.flags;
			return ((await runtime(actions.getFlags, {})) as Record<string, unknown> | undefined)?.value ?? {};
		},
		setSessionName: (name: string): Promise<unknown> => runtime(actions.setSessionName, { name }),
		getSessionName: () => getSessionNameSnapshot?.(),
		setLabel: (entryId: string, label: string): Promise<unknown> =>
			runtime(actions.setEntryLabel, { entryId, label }),
		sendMessage: (message: string | Record<string, unknown>, options: Record<string, unknown> = {}): Promise<unknown> =>
			runtime(
				actions.sendMessage,
				typeof message === "string"
					? { content: message, options }
					: { message, options },
			),
		sendUserMessage: (content: unknown, options: Record<string, unknown> = {}): Promise<unknown> =>
			runtime(actions.sendUserMessage, { content, options } as Record<string, unknown>),
		setActiveTools: (toolNames: string[]): Promise<unknown> => runtime(actions.setActiveTools, { toolNames }),
		setModel: (model: unknown): Promise<unknown> => runtime(actions.setModel, model as Record<string, unknown>),
		setThinkingLevel: (level: unknown): Promise<unknown> => runtime(actions.setThinkingLevel, { level } as Record<string, unknown>),
		resolveModelRole: (role: string): Promise<{ model: unknown; thinkingLevel: string } | { error: string }> =>
			runtime(actions.modelRolesResolve, { role } as Record<string, unknown>) as Promise<{ model: unknown; thinkingLevel: string } | { error: string }>,
		setModelRole: (role: string): Promise<{ ok: boolean }> =>
			runtime(actions.setModelRole, { role } as Record<string, unknown>) as Promise<{ ok: boolean }>,
		exec: async (command: string, options: Record<string, unknown> = {}): Promise<unknown> =>
			((await runtime(actions.exec, { command, options })) as Record<string, unknown> | undefined)?.value,
		reload: (): Promise<unknown> => runtime(actions.reloadExtensions, {}),
		session: {
			appendEntry: (type: unknown, data: unknown): Promise<unknown> => runtime(actions.appendEntry, { type, data } as Record<string, unknown>),
			setEntryLabel: (entryId: string, label: string): Promise<unknown> =>
				runtime(actions.setEntryLabel, { entryId, label }),
			getName: async (): Promise<unknown> => ((await runtime(actions.getSessionName, {})) as Record<string, unknown> | undefined)?.value,
			getSessionName: async (): Promise<unknown> => ((await runtime(actions.getSessionName, {})) as Record<string, unknown> | undefined)?.value,
			setName: (name: string): Promise<unknown> => runtime(actions.setSessionName, { name }),
			setSessionName: (name: string): Promise<unknown> => runtime(actions.setSessionName, { name }),
		},
		resources: {
			list: (): Promise<unknown> => runtime(actions.listResources, {}),
			read: (uri: string): Promise<unknown> => runtime(actions.readResource, { uri }),
		},
		settings: {
			get: (key: string): Promise<unknown> =>
				((runtime(actions.settingsGet, { key })) as Promise<Record<string, unknown>>).then(
					(result) => (result as Record<string, unknown> | undefined)?.value,
				),
			getCore: (path: string): Promise<unknown> =>
				((runtime(actions.settingsGetCore, { path })) as Promise<Record<string, unknown>>).then(
					(result) => (result as Record<string, unknown> | undefined)?.value,
				),
			set: (key: string, value: unknown, scope: "source" | "global" | "project" = "source"): Promise<unknown> =>
				runtime(actions.settingsSet, { key, value, scope } as Record<string, unknown>),
			remove: (key: string, scope: "source" | "global" | "project" = "source"): Promise<unknown> =>
				runtime(actions.settingsRemove, { key, scope }),
			onChange: (handler: (change: { key: string; value: unknown; layer: string; sourceId: string }) => void): Disposable =>
				toUnsubscribe(addEventHandler(state, "settings_changed", handler as EventHandler)),
		},
		state: {
			get: async (key: string, scope: "user" | "project" = "user"): Promise<unknown> =>
				((await runtime(actions.stateGet, { key, scope })) as Record<string, unknown> | undefined)?.value,
			set: (key: string, value: unknown, scope: "user" | "project" = "user"): Promise<unknown> =>
				runtime(actions.stateSet, { key, value, scope } as Record<string, unknown>),
			remove: (key: string, scope: "user" | "project" = "user"): Promise<unknown> =>
				runtime(actions.stateRemove, { key, scope }),
			getAll: async (scope: "user" | "project" = "user"): Promise<unknown> =>
				((await runtime(actions.stateGetAll, { scope })) as Record<string, unknown> | undefined)?.value ?? {},
			listKeys: async (scope: "user" | "project" = "user"): Promise<unknown> =>
				((await runtime(actions.stateListKeys, { scope })) as Record<string, unknown> | undefined)?.value ?? [],
			clear: (scope: "user" | "project" = "user"): Promise<unknown> =>
				runtime(actions.stateClear, { scope }),
			getSchemaVersion: async (scope: "user" | "project" = "user"): Promise<unknown> =>
				((await runtime(actions.stateGetSchemaVersion, { scope })) as Record<string, unknown> | undefined)?.value,
			setSchemaVersion: async (version: number, scope: "user" | "project" = "user"): Promise<unknown> =>
				((await runtime(actions.stateSetSchemaVersion, { version, scope })) as Record<string, unknown> | undefined)?.value,
		},
		ui: createUiApi({ extensionId, state, sendRequest }) as UiApi,
	};
}
