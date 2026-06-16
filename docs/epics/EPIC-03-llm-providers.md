# Epic 3: LLM Provider Abstraction and Implementations

**Dependencies:** Epic 1 (Core Abstractions)

**Depended On By:** Epic 4 (Agent Loop needs providers)

## Goal

Port the unified LLM provider abstraction from TypeScript (`packages/ai`) to C#, supporting all 25+ existing providers with streaming responses, model discovery, and OAuth.

**Target Project:** `PiSharp.Ai`

## Key Deliverables

### 1. Provider Abstraction

- `IModelProvider` interface with streaming response:

  ```csharp
  public interface IModelProvider
  {
      string Name { get; }
      IAsyncEnumerable<AssistantMessageEvent> StreamAsync(Model model, Context context, StreamOptions options);
  }
  ```

- `Model` class with `Provider`, `Id`, `Name`, `Api` type, `BaseUrl`, `Cost`, `ContextWindow`, `MaxTokens`, `Reasoning` support, `Input` types, `Thinking` budgets, etc.
- `Context` with `SystemPrompt`, `Messages`, `Tools`
- `StreamOptions` with `Temperature`, `MaxTokens`, `ApiKey`, `Signal`, `Transport`, `CacheRetention`, `Headers`, `Metadata`
- `AssistantMessageEvent` discriminated union: `Start`, `TextStart`, `TextDelta`, `TextEnd`, `ThinkingStart`, `ThinkingDelta`, `ThinkingEnd`, `ToolCallStart`, `ToolCallDelta`, `ToolCallEnd`, `Done`, `Error`
- `AssistantMessage` with `Content` (text, image, toolCall), `StopReason`, `ErrorMessage`, `Usage`

### 2. API Type Registry (`api-registry.ts` → `ApiRegistry.cs`)

- Maps API type strings (e.g., `"anthropic-messages"`, `"openai-responses"`) to provider implementations
- Support for extension-provided custom APIs
- Lazy loading of provider implementations

### 3. Built-in Provider Implementations

- **Anthropic** (anthropic-messages API)
- **OpenAI** (openai-completions, openai-responses, openai-codex-responses)
- **Azure OpenAI** (azure-openai-responses)
- **Google** (google-generative-ai API)
- **Google Vertex AI**
- **AWS Bedrock** (bedrock-converse-stream)
- **Mistral** (mistral-conversations)
- **DeepSeek**, **GitHub Copilot**, **xAI**, **Groq**, **Cerebras**, **OpenRouter**, etc.

Each provider implements:

- Message conversion (internal → provider format)
- Tool conversion (internal → provider format)
- Response parsing (provider stream → `AssistantMessageEvent` stream)
- Error handling and rate limiting
- Authentication (API key, OAuth, session tokens)

### 4. Model Management

- `ModelRegistry` with built-in models, provider model registration, model discovery
- Model generation scripts (port `generate-models.ts` logic)
- Model filtering by capability (reasoning, image support, context window)
- `KnownProvider` enum with all provider names

### 5. Streaming Utilities

- `EventStream<TEvent, TResult>` — matching TS implementation
- `AssistantMessageEventStream` — specialized stream for LLM responses
- Response parsing helpers (SSE parsing, JSON streaming)
- Partial JSON handling for streaming responses

### 6. Authentication

- API key resolution (env vars, `auth.json`, dynamic providers)
- OAuth flow support (login, token refresh, credential storage)
- Transport header injection (proxy, custom headers)

### 7. Configuration and Discovery

- Environment variable API key detection (`env-api-keys.ts` → `ApiKeyDetector.cs`)
- OAuth credential storage (`auth-storage.ts` → `OAuthStorage.cs`)
- Provider display names for login UI

## Provider Implementation Priority (ordered)

1. Anthropic (most commonly used)
2. OpenAI (openai-responses)
3. Google (generative-ai)
4. AWS Bedrock
5. OpenRouter (aggregates many providers)
6. GitHub Copilot
7. DeepSeek, xAI, Groq, Cerebras (OpenAI-compatible)
8. Mistral
9. Remaining providers

## Implementation Notes

- Use `HttpClientFactory` for HTTP client management
- SSE parsing via `System.Text.Json` `Utf8JsonReader` for streaming
- Support `CancellationToken` throughout for abort
- Rate limiting with Polly or similar library
- Proxy support via `HttpClientHandler`
- All provider implementations should be in separate files following the TS pattern
- Support both SDK-based and HTTP-based provider implementations
- OAuth support should be extensible for custom flows

## C# Port Architecture Notes

- `PiSharp.Ai` implements provider dispatch through `IModelProvider` and `ApiRegistry`, keyed by `ModelDescriptor.Api`.
- Built-in provider registration is centralized in `BuiltInProviders` with source id `built-in`, so built-ins can be cleared without removing extension providers.
- Provider streams normalize raw provider responses into `AssistantMessageEvent` contracts with one `Start`, ordered text/tool events, and exactly one terminal `Done` or `Error`.
- The C# port intentionally uses raw `HttpClient`, `System.Text.Json`, and local SSE/JSON helpers for Anthropic, OpenAI-compatible, Google/Vertex, Bedrock, and Mistral providers.
- No official paid-provider SDK dependency is required by the current C# port; fixture-only tests cover request shape, auth, stream parsing, registry dispatch, and provider matrix semantics without live calls.
