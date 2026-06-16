# Epic 2: Agent Loop and Session Management

- **Status:** Draft
- **Dependencies:** Epic 1 (Core Abstractions)
- **Depended On By:** Epics 4, 5, 6 (Tools, CLI, Extensions)
- **Target Project:** `PiSharp.Agent`

## Goal

Port the agent loop state machine and session management from TypeScript to C#. This is the heart of the application — it orchestrates LLM calls, tool execution, message streaming, and conversation persistence.

## Key Deliverables

### 1. Agent Loop (`AgentLoop.cs`)

- `IAgentLoop` interface with `RunAsync(prompts, context, config, cancellationToken)` returning `IAsyncEnumerable<AgentEvent>`
- Stream function abstraction: `Func<Model, Context, StreamOptions, IAsyncEnumerable<AssistantMessageEvent>>`
- Main loop logic:
  - **Outer loop:** process follow-up messages after agent would stop
  - **Inner loop:** process tool calls and steering messages
  - **Turn lifecycle:** emit `turn_start` → inject pending messages → stream assistant response → execute tools → emit `turn_end` → check steering → check `shouldStopAfterTurn`
- **Sequential tool execution:** prepare → execute → finalize one at a time
- **Parallel tool execution:** prepare all sequentially, execute concurrently, finalize in completion order, emit tool-result messages in source order
- Hooks: `beforeToolCall`, `afterToolCall`, `shouldStopAfterTurn`, `prepareNextTurn`, `getSteeringMessages`, `getFollowUpMessages`, `transformContext`, `convertToLlm`, `getApiKey`

### 2. Agent Harness (`AgentHarness.cs`)

- `IAgentHarness` wrapping agent loop with session persistence, event hooks, tool management
- Queue management: steering queue, follow-up queue, next-turn queue
- Resource management: skills, prompt templates
- System prompt building with resources and active tools
- Compaction integration
- Branch summarization integration
- Provider request lifecycle (`before_provider_request` → `after_provider_response`)
- Abort handling with signal propagation
- Event hook routing through extension system

### 3. Message Converter (`MessageConverter.cs`)

- `ConvertToLlm`: `AgentMessage[]` → `Message[]` for LLM consumption
- `ConvertFromLlm`: `AssistantMessage` → internal representation
- Handling of custom message types (filtering, conversion)

### 4. Prompt Template Engine (`PromptTemplateEngine.cs`)

- Template storage with name/description/content
- Formatting with argument substitution
- Invocation routing

### 5. Skill Manager (`SkillManager.cs`)

- Skill loading from `SKILL.md` files
- System prompt integration via `agentskills.io` XML format
- Model-visible skill listing

### 6. Session Manager (`SessionManager.cs`)

- `ISession` with tree navigation (fork, navigate, switch)
- Entry appending (messages, commands, metadata)
- Leaf tracking
- Branch summarization
- Label management
- Session metadata (`id`, `createdAt`, `cwd`, `path`)

### 7. JSONL Session Repo (`JsonlSessionRepo.cs`)

- File-based session persistence in JSONL format
- Session tree as linked list of entries
- Reading/writing with proper locking
- Support for custom entry types
- Session listing by directory
- Concurrent access protection

### 8. Memory Session Repo (`MemorySessionRepo.cs`)

- In-memory session store for testing
- Same interface as JSONL repo

### 9. Compaction Service (`CompactionService.cs`)

- Context window management
- Token estimation
- Message summarization using LLM
- `FirstKeptEntryId` tracking
- Compaction settings (enabled, reserveTokens, keepRecentTokens)

### 10. Branch Summarization Service (`BranchSummarizationService.cs`)

- Summarizing old branch entries when navigating the session tree
- Custom instructions support
- LLM-based summary generation

## Implementation Notes

- Use `IAsyncEnumerable<T>` for event streaming (equivalent to TS async generators)
- Port the `EventStream` class directly as `EventStream<TEvent, TResult>` with Push/End pattern
- All tool execution should respect `CancellationToken` for abort
- Sequential vs parallel execution logic must exactly match TS behavior for compatibility
- Session JSONL format must be byte-compatible with TS version for session file interchange
- Use `System.Threading.Channels` for async event streaming
