using Microsoft.Extensions.Logging;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai;
using PiSharp.Ai.Models;
using PiSharp.Runtime;
using System.Collections.Immutable;
using System.Linq;

namespace PiSharp.Cli.Commands;

public sealed class ModelSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["model", "models"];

    public string Description { get; } = "Built-in /model command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var logger = context.Runtime.LoggerFactory?.CreateLogger<ModelSlashCommand>();
        var current = context.Runtime.CurrentModelSelection;
        var isScoped = current.IsScoped && current.ScopedModels.Count > 0;
        IReadOnlyList<ModelDescriptor> candidates;
        if (isScoped)
        {
            candidates = current.ScopedModels;
        }
        else
        {
            var allModels = PublicApi.Models.Select(model => model.Descriptor).ToArray();
            var storedProviders = context.OAuthStorage is not null
                ? await context.OAuthStorage.ListStoredProvidersAsync(cancellationToken)
                : [];
            var customProviders = ModelRegistry.GetCustomProviders();
            var storedSet = new HashSet<string>(storedProviders, StringComparer.OrdinalIgnoreCase);
            var filtered = allModels
                .Where(m => ModelRegistry.IsProviderAccessible(m.Provider, storedSet, customProviders))
                .ToArray();
            candidates = filtered.Length > 0 ? filtered : allModels;
        }

        if (candidates.Count == 0) candidates = [current.Model];
        logger?.LogDebug("Model command candidates loaded scoped={Scoped} scopedModelCount={ScopedModelCount} candidateCount={CandidateCount} queryPresent={HasQuery}",
            isScoped, current.ScopedModels.Count, candidates.Count, !string.IsNullOrWhiteSpace(args));

        logger?.LogDebug("Model command selecting model candidateCount={CandidateCount} queryPresent={HasQuery}",
            candidates.Count, !string.IsNullOrWhiteSpace(args));
        var selectedText = string.IsNullOrWhiteSpace(args)
            ? await context.SelectAsync("Select model", [.. candidates.Select(ModelOptionText)], cancellationToken)
            : args.Trim();
        logger?.LogDebug("Model command selection returned selected={HasSelection}", !string.IsNullOrWhiteSpace(selectedText));

        if (string.IsNullOrWhiteSpace(selectedText)) return new SlashCommandResult(true, "Model selection cancelled.");

        // @role branch — resolve through RuntimeModelSelector instead of FindModel.
        if (selectedText.StartsWith('@'))
        {
            try
            {
                var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, selectedText, null));
                await context.Runtime.SetModelAsync(selection, "slash", cancellationToken);
                await context.Runtime.PersistCurrentModelSelectionAsync(cancellationToken);
                logger?.LogDebug("Model command applied role role={Role} provider={Provider} model={ModelId}", selectedText, selection.Model.Provider, selection.Model.Id);
                return new SlashCommandResult(true, $"Model set to {selection.Model.Provider}/{selection.Model.Id} (role {selectedText})");
            }
            catch (InvalidOperationException ex)
            {
                return new SlashCommandResult(true, ex.Message, IsError: true);
            }
        }

        var selected = FindModel(candidates, selectedText);
        if (selected is null)
        {
            return new SlashCommandResult(true, $"Model '{selectedText}' was not found.", IsError: true);
        }

        var selection2 = current with { Model = selected, ThinkingLevel = ModelRegistry.ClampThinkingLevel(selected, current.ThinkingLevel) };
        logger?.LogDebug("Model command applying selection provider={Provider} model={ModelId}", selection2.Model.Provider, selection2.Model.Id);
        await context.Runtime.SetModelAsync(selection2, "slash", cancellationToken);
        await context.Runtime.PersistCurrentModelSelectionAsync(cancellationToken);
        logger?.LogDebug("Model command applied selection provider={Provider} model={ModelId}", selection2.Model.Provider, selection2.Model.Id);
        return new SlashCommandResult(true, $"Model set to {selection2.Model.Provider}/{selection2.Model.Id}");
    }

    private static string ModelOptionText(ModelDescriptor model)
        => string.IsNullOrWhiteSpace(model.Name) ? $"{model.Provider}/{model.Id}" : $"{model.Provider}/{model.Id} — {model.Name}";

    private static ModelDescriptor? FindModel(IReadOnlyList<ModelDescriptor> candidates, string query)
    {
        var normalized = query.Split('—', 2)[0].Trim();
        if (normalized.Contains('/', StringComparison.Ordinal))
        {
            var parts = normalized.Split('/', 2, StringSplitOptions.TrimEntries);
            var exact = candidates.FirstOrDefault(model =>
                string.Equals(model.Provider, parts[0], StringComparison.OrdinalIgnoreCase)
                && string.Equals(model.Id, parts[1], StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        return candidates.FirstOrDefault(model => string.Equals(model.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(model => string.Equals(model.Name, normalized, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(model => model.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase) || model.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
