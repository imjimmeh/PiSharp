export interface BridgeManifest {
	protocol?: ProtocolManifest | null;
}

export interface ProtocolManifest {
	methods?: Record<string, string> | null;
	runtimeActions?: Record<string, string> | null;
}

export interface BridgeProtocol {
	methods: {
		registerTool: string;
		registerSkill: string;
		registerProvider: string;
		unregisterProvider: string;
		registerCommand: string;
		registerShortcut: string;
		registerFlag: string;
		registerPromptSection: string;
		registerPromptTransform: string;
		registerMessageRenderer: string;
		unregisterMessageRenderer: string;
		registerMessageDecorator: string;
		unregisterMessageDecorator: string;
		runtimeAction: string;
		uiRequest: string;
	};
	runtimeActions: {
		getAllSkills: string;
		getSelectedSkills: string;
		setSelectedSkills: string;
		getFlag: string;
		getFlags: string;
		getActiveTools: string;
		getAllTools: string;
		getCommands: string;
		waitForIdle: string;
		newSession: string;
		forkSession: string;
		navigateTree: string;
		switchSession: string;
		isIdle: string;
		hasPendingMessages: string;
		compact: string;
		getSystemPrompt: string;
		abort: string;
		shutdown: string;
		exec: string;
		getThinkingLevel: string;
		sendMessage: string;
		sendUserMessage: string;
		appendEntry: string;
		setEntryLabel: string;
		getSessionName: string;
		setSessionName: string;
		setActiveTools: string;
		setModel: string;
		setThinkingLevel: string;
		modelRolesResolve: string;
		setModelRole: string;
		reloadExtensions: string;
		installExtension: string;
		updateExtension: string;
		removeExtension: string;
		listInstalledExtensions: string;
		managedSkillCreate: string;
		managedSkillUpdate: string;
		managedSkillDelete: string;
		managedSkillList: string;
		managedSkillPromote: string;
		registerSkillProvider: string;
		discoverSkillProvider: string;
		getSkillProviderPriorities: string;
			emitEvent: string;
			listResources: string;
			readResource: string;
			completeSimple: string;
			promptAndWait: string;
			createAgentSession: string;
			agentSessionPrompt: string;
			agentSessionSteer: string;
			agentSessionFollowUp: string;
			agentSessionAbort: string;
			agentSessionCompact: string;
			agentSessionSetModel: string;
			agentSessionSetThinkingLevel: string;
			agentSessionDispose: string;
			settingsGet: string;
			settingsGetCore: string;
			settingsSet: string;
			settingsRemove: string;
			stateGet: string;
			stateSet: string;
			stateRemove: string;
			stateGetAll: string;
			stateListKeys: string;
			stateClear: string;
			stateGetSchemaVersion: string;
			stateSetSchemaVersion: string;
			stateRegisterMigration: string;
		};
}

const fallbackMethods = {
	RegisterTool: "register_tool",
	RegisterSkill: "register_skill",
	RegisterProvider: "register_provider",
	UnregisterProvider: "unregister_provider",
	RegisterCommand: "register_command",
	RegisterShortcut: "register_shortcut",
	RegisterFlag: "register_flag",
	RegisterPromptSection: "register_prompt_section",
	RegisterPromptTransform: "register_prompt_transform",
	RegisterMessageRenderer: "register_message_renderer",
	UnregisterMessageRenderer: "unregister_message_renderer",
	RegisterMessageDecorator: "register_message_decorator",
	UnregisterMessageDecorator: "unregister_message_decorator",
	RuntimeAction: "runtime_action",
	UiRequest: "ui_request",
};

const fallbackRuntimeActions = {
	GetAllSkills: "get_all_skills",
	GetSelectedSkills: "get_selected_skills",
	SetSelectedSkills: "set_selected_skills",
	GetFlag: "get_flag",
	GetFlags: "get_flags",
	GetActiveTools: "get_active_tools",
	GetAllTools: "get_all_tools",
	GetCommands: "get_commands",
	WaitForIdle: "wait_for_idle",
	NewSession: "new_session",
	ForkSession: "fork_session",
	NavigateTree: "navigate_tree",
	SwitchSession: "switch_session",
	IsIdle: "is_idle",
	HasPendingMessages: "has_pending_messages",
	Compact: "compact",
	GetSystemPrompt: "get_system_prompt",
	Abort: "abort",
	Shutdown: "shutdown",
	Exec: "exec",
	GetThinkingLevel: "get_thinking_level",
	SendMessage: "send_message",
	SendUserMessage: "send_user_message",
	AppendEntry: "append_entry",
	SetEntryLabel: "set_entry_label",
	GetSessionName: "get_session_name",
	SetSessionName: "set_session_name",
	SetActiveTools: "set_active_tools",
	ModelRolesResolve: "model_roles_resolve",
	SetModelRole: "set_model_role",
	SetModel: "set_model",
	SetThinkingLevel: "set_thinking_level",
	ReloadExtensions: "reload_extensions",
	InstallExtension: "install_extension",
	UpdateExtension: "update_extension",
	RemoveExtension: "remove_extension",
	ListInstalledExtensions: "list_installed_extensions",
	ManagedSkillCreate: "managed_skill_create",
	ManagedSkillUpdate: "managed_skill_update",
	ManagedSkillDelete: "managed_skill_delete",
	ManagedSkillList: "managed_skill_list",
	ManagedSkillPromote: "managed_skill_promote",
	RegisterSkillProvider: "register_skill_provider",
	DiscoverSkillProvider: "discover_skill_provider",
	GetSkillProviderPriorities: "get_skill_provider_priorities",
	EmitEvent: "emit_event",
	ListResources: "list_resources",
	ReadResource: "read_resource",
	CompleteSimple: "complete_simple",
	PromptAndWait: "prompt_and_wait",
	CreateAgentSession: "create_agent_session",
	AgentSessionPrompt: "agent_session_prompt",
	AgentSessionSteer: "agent_session_steer",
	AgentSessionFollowUp: "agent_session_follow_up",
	AgentSessionAbort: "agent_session_abort",
	AgentSessionCompact: "agent_session_compact",
	AgentSessionSetModel: "agent_session_set_model",
	AgentSessionSetThinkingLevel: "agent_session_set_thinking_level",
	AgentSessionDispose: "agent_session_dispose",
	SettingsGet: "settings_get",
	SettingsGetCore: "settings_get_core",
	SettingsSet: "settings_set",
	SettingsRemove: "settings_remove",
	StateGet: "state_get",
	StateSet: "state_set",
	StateRemove: "state_remove",
	StateGetAll: "state_get_all",
	StateListKeys: "state_list_keys",
	StateClear: "state_clear",
	StateGetSchemaVersion: "state_get_schema_version",
	StateSetSchemaVersion: "state_set_schema_version",
	StateRegisterMigration: "state_register_migration",
};

export function createBridgeProtocol(manifest?: BridgeManifest | null): BridgeProtocol {
	const methods = manifest?.protocol?.methods ?? fallbackMethods;
	const runtimeActions = manifest?.protocol?.runtimeActions ?? fallbackRuntimeActions;
	return {
		methods: {
			registerTool: required(methods, "RegisterTool"),
			registerSkill: required(methods, "RegisterSkill"),
			registerProvider: required(methods, "RegisterProvider"),
			unregisterProvider: required(methods, "UnregisterProvider"),
			registerCommand: required(methods, "RegisterCommand"),
			registerShortcut: required(methods, "RegisterShortcut"),
			registerFlag: required(methods, "RegisterFlag"),
			registerPromptSection: required(methods, "RegisterPromptSection"),
			registerPromptTransform: required(methods, "RegisterPromptTransform"),
			registerMessageRenderer: required(methods, "RegisterMessageRenderer"),
			unregisterMessageRenderer: required(methods, "UnregisterMessageRenderer"),
			registerMessageDecorator: required(methods, "RegisterMessageDecorator"),
			unregisterMessageDecorator: required(methods, "UnregisterMessageDecorator"),
			runtimeAction: required(methods, "RuntimeAction"),
			uiRequest: required(methods, "UiRequest"),
		},
		runtimeActions: {
			getAllSkills: required(runtimeActions, "GetAllSkills"),
			getSelectedSkills: required(runtimeActions, "GetSelectedSkills"),
			setSelectedSkills: required(runtimeActions, "SetSelectedSkills"),
			getFlag: required(runtimeActions, "GetFlag"),
			getFlags: required(runtimeActions, "GetFlags"),
			getActiveTools: required(runtimeActions, "GetActiveTools"),
			getAllTools: required(runtimeActions, "GetAllTools"),
			getCommands: required(runtimeActions, "GetCommands"),
			waitForIdle: required(runtimeActions, "WaitForIdle"),
			newSession: required(runtimeActions, "NewSession"),
			forkSession: required(runtimeActions, "ForkSession"),
			navigateTree: required(runtimeActions, "NavigateTree"),
			switchSession: required(runtimeActions, "SwitchSession"),
			isIdle: required(runtimeActions, "IsIdle"),
			hasPendingMessages: required(runtimeActions, "HasPendingMessages"),
			compact: required(runtimeActions, "Compact"),
			getSystemPrompt: required(runtimeActions, "GetSystemPrompt"),
			abort: required(runtimeActions, "Abort"),
			shutdown: required(runtimeActions, "Shutdown"),
			exec: required(runtimeActions, "Exec"),
			getThinkingLevel: required(runtimeActions, "GetThinkingLevel"),
			sendMessage: required(runtimeActions, "SendMessage"),
			sendUserMessage: required(runtimeActions, "SendUserMessage"),
			appendEntry: required(runtimeActions, "AppendEntry"),
			setEntryLabel: required(runtimeActions, "SetEntryLabel"),
			getSessionName: required(runtimeActions, "GetSessionName"),
			setSessionName: required(runtimeActions, "SetSessionName"),
			setActiveTools: required(runtimeActions, "SetActiveTools"),
			modelRolesResolve: required(runtimeActions, "ModelRolesResolve"),
			setModelRole: required(runtimeActions, "SetModelRole"),
			setModel: required(runtimeActions, "SetModel"),
			setThinkingLevel: required(runtimeActions, "SetThinkingLevel"),
			reloadExtensions: required(runtimeActions, "ReloadExtensions"),
			installExtension: required(runtimeActions, "InstallExtension"),
			updateExtension: required(runtimeActions, "UpdateExtension"),
			removeExtension: required(runtimeActions, "RemoveExtension"),
			listInstalledExtensions: required(runtimeActions, "ListInstalledExtensions"),
			managedSkillCreate: required(runtimeActions, "ManagedSkillCreate"),
			managedSkillUpdate: required(runtimeActions, "ManagedSkillUpdate"),
			managedSkillDelete: required(runtimeActions, "ManagedSkillDelete"),
			managedSkillList: required(runtimeActions, "ManagedSkillList"),
			managedSkillPromote: required(runtimeActions, "ManagedSkillPromote"),
			registerSkillProvider: required(runtimeActions, "RegisterSkillProvider"),
			discoverSkillProvider: required(runtimeActions, "DiscoverSkillProvider"),
			getSkillProviderPriorities: required(runtimeActions, "GetSkillProviderPriorities"),
				emitEvent: required(runtimeActions, "EmitEvent"),
				listResources: required(runtimeActions, "ListResources"),
				readResource: required(runtimeActions, "ReadResource"),
				completeSimple: required(runtimeActions, "CompleteSimple"),
				promptAndWait: required(runtimeActions, "PromptAndWait"),
				createAgentSession: required(runtimeActions, "CreateAgentSession"),
				agentSessionPrompt: required(runtimeActions, "AgentSessionPrompt"),
				agentSessionSteer: required(runtimeActions, "AgentSessionSteer"),
				agentSessionFollowUp: required(runtimeActions, "AgentSessionFollowUp"),
				agentSessionAbort: required(runtimeActions, "AgentSessionAbort"),
				agentSessionCompact: required(runtimeActions, "AgentSessionCompact"),
				agentSessionSetModel: required(runtimeActions, "AgentSessionSetModel"),
				agentSessionSetThinkingLevel: required(runtimeActions, "AgentSessionSetThinkingLevel"),
				agentSessionDispose: required(runtimeActions, "AgentSessionDispose"),
				settingsGet: required(runtimeActions, "SettingsGet"),
				settingsGetCore: required(runtimeActions, "SettingsGetCore"),
				settingsSet: required(runtimeActions, "SettingsSet"),
				settingsRemove: required(runtimeActions, "SettingsRemove"),
				stateGet: required(runtimeActions, "StateGet"),
				stateSet: required(runtimeActions, "StateSet"),
				stateRemove: required(runtimeActions, "StateRemove"),
				stateGetAll: required(runtimeActions, "StateGetAll"),
				stateListKeys: required(runtimeActions, "StateListKeys"),
				stateClear: required(runtimeActions, "StateClear"),
				stateGetSchemaVersion: required(runtimeActions, "StateGetSchemaVersion"),
				stateSetSchemaVersion: required(runtimeActions, "StateSetSchemaVersion"),
				stateRegisterMigration: required(runtimeActions, "StateRegisterMigration"),
			},
		};
}

function required(values: Record<string, string>, key: string): string {
	const value = values[key];
	if (typeof value !== "string" || value.length === 0) throw new Error(`Bridge manifest protocol value '${key}' is missing.`);
	return value;
}
