# PiSharp Model Providers

PiSharp routes model calls through `IModelProvider` from `PiSharp.Ai.Providers`. Providers can be built in, registered by native .NET extensions, or adapted from TypeScript extensions through the bridge.

## Provider contract

```csharp
public interface IModelProvider
{
    string Api { get; }

    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);

    Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);
}
```

- `Api` is the provider API key/name used for selection.
- `StreamAsync()` emits assistant message events for interactive/streaming turns.
- `CompleteAsync()` returns a complete assistant message for non-streaming use cases.

## Built-in providers

Current built-in provider API names include:

- `anthropic`
- `openai`
- `openai-responses`
- `openai-chat-completions`
- `azure-openai-responses`
- `openai-codex-responses`
- `google`
- `google-vertex`
- `amazon-bedrock`
- `mistral`
- `github-copilot-chat` (GitHub Copilot chat completions, OAuth-backed)
- Faux/test provider support

`PiRuntimeBootstrap` calls `PiSharp.Ai.PublicApi.RegisterBuiltInProviders()` during startup.

## Model catalog and selection

PiSharp can load model catalog overrides from:

```text
~/.pi/agent/models.json
```

Model selection can come from:

- `--provider <name>`
- `--model <model>`
- `defaultProvider` setting
- `defaultModel` setting
- extension/runtime model changes

Thinking level can come from `--thinking`, runtime options, or extension APIs.

## Public provider API

`PiSharp.Ai.PublicApi` exposes provider/model operations such as:

- `RegisterBuiltInProviders()`
- `RegisterProvider()`
- `UnregisterProviderSource()`
- `LoadModelsJson()`
- `StreamAsync()`
- `CompleteAsync()`
- `StreamSimpleAsync()`
- `CompleteSimpleAsync()`

Native extensions usually call `api.RegisterProvider(provider)` rather than using `PublicApi` directly.

## Credentials

Runtime credential resolution uses `ProviderCredentialResolver` and `FileOAuthStorage` by default. OAuth/auth data is stored at:

```text
~/.pi/agent/auth.json
```

For OAuth-backed providers such as OpenAI Codex, PiSharp prefers credentials in the nested `providers` object and removes stale legacy root-level provider entries when saving refreshed credentials.
Environment and ambient credentials currently include:

| Provider(s) | Environment / ambient credential source |
| --- | --- |
| `anthropic` | `ANTHROPIC_OAUTH_TOKEN`, then `ANTHROPIC_API_KEY` |
| `openai`, `openai-chat-completions` | `OPENAI_API_KEY` |
| `azure-openai-responses` | `AZURE_OPENAI_API_KEY` |
| `google` | `GEMINI_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `together` | `TOGETHER_API_KEY` |
| `fireworks` | `FIREWORKS_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `deepseek` | `DEEPSEEK_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `moonshot` (legacy), `moonshotai`, `moonshotai-cn` | `MOONSHOT_API_KEY` |
| `kimi` (legacy), `kimi-coding` | `KIMI_API_KEY` |
| `huggingface` | `HUGGINGFACE_API_KEY`, then `HF_TOKEN` |
| `minimax`, `minimax-cn` | `MINIMAX_API_KEY` |
| `zai` | `ZAI_API_KEY` |
| `opencode`, `opencode-go` | `OPENCODE_API_KEY` |
| `xiaomi`, `xiaomi-token-plan-cn`, `xiaomi-token-plan-ams`, `xiaomi-token-plan-sgp` | `XIAOMI_API_KEY` |
| `cloudflare-workers-ai`, `cloudflare-ai-gateway` | `CLOUDFLARE_API_KEY` |
| `perplexity` | `PERPLEXITY_API_KEY` |
| `google-vertex` | ambient: `GOOGLE_APPLICATION_CREDENTIALS` plus `GOOGLE_CLOUD_PROJECT` and `GOOGLE_CLOUD_LOCATION` |
| `amazon-bedrock` | ambient: `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` |
| `github-copilot` | none — OAuth only (see "GitHub Copilot" below) |

Provider/model configuration can also supply headers or an API-key literal/environment-variable name through the model catalog.

## Registering providers from extensions

Native extensions can implement `IModelProvider` and register it:

```csharp
public sealed class ExampleProvider : IModelProvider
{
    public string Api => "example";

    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        // Return or yield assistant streaming events.
        throw new NotImplementedException();
    }

    public Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        // Return one assistant message.
        throw new NotImplementedException();
    }
}

api.RegisterProvider(new ExampleProvider());
```

TypeScript extensions can also register providers. PiSharp adapts those through `TsProviderAdapter`, registering the provider with `PublicApi` and calling back into Node when needed.

## Provider events

Extensions can observe provider/model flow through events including:

- `before_provider_request`
- `before_provider_payload`
- `after_provider_response`
- `model_select`
- `thinking_level_select`
- `thinking_level_changed`

These events are available to native extensions and forwarded to TypeScript extensions through the bridge.

## When to use native vs TypeScript providers

Use a native provider when you need:

- Direct .NET HTTP/auth libraries.
- Efficient streaming without bridge serialization.
- Tight integration with PiSharp runtime abstractions.
- Strong cancellation and disposal behavior.

Use a TypeScript provider when you need:

- Compatibility with existing Pi provider code.
- Node.js packages or TypeScript-only dependencies.


## GitHub Copilot

GitHub Copilot's chat endpoint is the one non-OpenAI-compatible endpoint on the master provider list: it requires spoofed editor headers, an OAuth copilot token (never an API key), and serves `/chat/completions` on the base URL without a `/v1` prefix. PiSharp ships a first-class provider for it:

- API name: `github-copilot-chat` (registered by `BuiltInProviders`).
- Credentials: OAuth only. Run `/login github-copilot` (device flow); the token is stored in `~/.pi/agent/auth.json` under provider id `github-copilot`. A missing token surfaces a friendly `No GitHub Copilot token found — run '/login github-copilot' first.` error instead of a raw 401.
- Default base URL: `https://api.individual.githubcopilot.com` (individual accounts).
- Enterprise: override the base URL per user in `~/.pi/agent/models.json`, using either the bare host or the full endpoint path:

```json
{
  "providers": {
    "github-copilot": {
      "baseUrl": "https://copilot-api.company.ghe.com"
    }
  }
}
```

The provider adds the Copilot editor headers (`User-Agent: GitHubCopilotChat/0.35.0`, `Editor-Version: vscode/1.107.0`, `Editor-Plugin-Version: copilot-chat/0.35.0`, `Copilot-Integration-Id: vscode-chat`) to every request and sends `Authorization: Bearer <copilot token>`.

All 32 catalogued `github-copilot` models route to `github-copilot-chat`.

## Adding an OpenAI-compatible provider (recipe)

Most providers on the breadth list are OpenAI-compatible (or Anthropic-compatible) and need **no new provider class** — only catalog entries (already shipped for 27 providers, or user-defined via `~/.pi/agent/models.json`), a credential (env var or OAuth), and a correct endpoint. Two rules cover every endpoint:

1. **Standard bases** (`https://api.openai.com`, `/v1`-suffixed, or full `https://host/v1`): set `"api": "openai-completions"` + `"baseUrl": "<host>/v1"` and the URL builder appends `chat/completions`.
2. **Embedded-version / non-`/v1` bases** (ZAI `https://api.z.ai/api/paas/v4`, Cloudflare gateway `.../compat`, Copilot enterprise): the URL builder recognizes version-pinned trailing segments (`v<N>`, `compat`, `anthropic`, `v1beta`) and appends the resource directly. You can also set `"baseUrl"` to the **full endpoint path** (`.../paas/v4/chat/completions`, `.../compat/chat/completions`, `https://copilot-api.<domain>/chat/completions`), which the builder honors verbatim.

Canonical recipe (also pinned by `ModelsJsonCatalogLoaderTests`):

```json
{
  "providers": {
    "openrouter": { "apiKey": "OPENROUTER_API_KEY" },
    "perplexity": {
      "api": "anthropic-messages",
      "baseUrl": "https://api.perplexity.ai/anthropic"
    },
    "zai": {
      "baseUrl": "https://api.z.ai/api/paas/v4/chat/completions",
      "headers": { "X-Api-Key": "ZAI_API_KEY" }
    }
  }
}
```

### Recipe reference

| Provider | `api` | `baseUrl` | Env var | Caveat |
| --- | --- | --- | --- | --- |
| OpenRouter | `openai-completions` | `https://openrouter.ai/api/v1` | `OPENROUTER_API_KEY` | catalogued (251 models) |
| Together | `openai-completions` | `https://api.together.xyz/v1` | `TOGETHER_API_KEY` | catalogued |
| Groq | `openai-completions` | `https://api.groq.com/openai/v1` | `GROQ_API_KEY` | catalogued |
| Cerebras | `openai-completions` | `https://api.cerebras.ai/v1` | `CEREBRAS_API_KEY` | catalogued |
| xAI | `openai-completions` | `https://api.x.ai/v1` | `XAI_API_KEY` | catalogued |
| DeepSeek | `openai-completions` | `https://api.deepseek.com` | `DEEPSEEK_API_KEY` | `/v1` also served; `reasoning_content` thinking streaming unverified |
| Fireworks | `anthropic-messages` | `https://api.fireworks.ai/inference` | `FIREWORKS_API_KEY` | Anthropic-compatible endpoint |
| Moonshot / Kimi | `openai-completions` | `https://api.moonshot.ai/v1` (CN: `api.moonshot.cn`) | `MOONSHOT_API_KEY` / `KIMI_API_KEY` | catalogued as `moonshotai` / `kimi-coding` |
| ZAI | `openai-completions` | `https://api.z.ai/api/paas/v4` | `ZAI_API_KEY` | embedded version path; full endpoint path recommended |
| HuggingFace | `openai-completions` | `https://router.huggingface.co/v1` | `HUGGINGFACE_API_KEY` or `HF_TOKEN` | `HF_TOKEN` is the HF-docs convention; both accepted |
| MiniMax | `openai-completions` | `https://api.minimax.io/v1` (CN: `api.minimaxi.com`) | `MINIMAX_API_KEY` | catalogued as `minimax` / `minimax-cn` |
| OpenCode Zen/Go | `openai-completions` | `https://api.opencode.ai/v1` | `OPENCODE_API_KEY` | catalogued as `opencode` (70) / `opencode-go` (17) |
| Xiaomi MiMo | `openai-completions` | `https://api.xiaomimimo.com/v1` (+ `token-plan-cn/ams/sgp` hosts) | `XIAOMI_API_KEY` | catalogued (6 + 12 token-plan models) |
| Cloudflare Workers AI | `openai-completions` | `https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1` | `CLOUDFLARE_API_KEY` | substitute `{account_id}`; base already ends `/v1` |
| Cloudflare AI Gateway | `openai-completions` | `https://gateway.ai.cloudflare.com/v1/{account}/{gateway}/compat` | `CLOUDFLARE_API_KEY` | substitute `{account}`/`{gateway}`; `compat` trailing segment |
| Vercel AI Gateway | `openai-completions` | `https://ai-gateway.vercel.sh/v1` | provider key via Vercel | catalogued as `vercel-ai-gateway` |
| Perplexity | `anthropic-messages` | `https://api.perplexity.ai/anthropic` | `PERPLEXITY_API_KEY` | Anthropic-compatible; recipe only (not catalogued) |
| Prime Inference | `openai-completions` | per your Prime subscription | Prime key | recipe only |
| Ollama (self-hosted) | `openai-completions` | `http://localhost:11434/v1` | none | local |
| LM Studio (self-hosted) | `openai-completions` | `http://localhost:1234/v1` | none | local |
| SambaNova | `openai-completions` | `https://api.sambanova.ai/v1` | SambaNova key | recipe only |

For a full walkthrough of adding a new first-class provider class (not needed for the recipe above), see [pisharp-adding-a-provider.md](pisharp-adding-a-provider.md).