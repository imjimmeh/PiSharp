using PiSharp.Extensions;

namespace PiSharp.Cli.Commands;

public sealed record SlashCommandDefinition(
    string Name,
    string Description,
    Func<SlashCommandContext, string, CancellationToken, Task<SlashCommandResult>> Execute,
    string SourceId = "builtin");

public sealed record SlashCommandResult(bool Handled, string? Message = null, bool IsError = false, bool ShouldExit = false);

public sealed class SlashCommandRegistry
{
    private readonly Dictionary<string, SlashCommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SlashCommandDefinition> Commands
        => _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Register(SlashCommandDefinition command)
    {
        if (string.IsNullOrWhiteSpace(command.Name)) throw new ArgumentException("Command name is required.", nameof(command));
        var name = command.Name.TrimStart('/');
        if (command.SourceId == "builtin")
        {
            _commands[name] = command with { Name = name };
            return;
        }

        var existingForBase = _commands.Values.Where(existing => string.Equals(BaseName(existing.Name), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existingForBase.Length == 0)
        {
            _commands[name] = command with { Name = name };
            return;
        }

        var existingPlainExtension = existingForBase.FirstOrDefault(existing => existing.SourceId != "builtin" && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingPlainExtension is not null)
        {
            _commands.Remove(existingPlainExtension.Name);
            _commands[$"{name}:1"] = existingPlainExtension with { Name = $"{name}:1" };
        }

        var invocationName = $"{name}:{NextSuffix(name)}";
        _commands[invocationName] = command with { Name = invocationName };
    }

    private int NextSuffix(string baseName)
    {
        var suffixes = _commands.Values
            .Where(command => string.Equals(BaseName(command.Name), baseName, StringComparison.OrdinalIgnoreCase))
            .Select(command => NumericSuffix(command.Name))
            .Where(suffix => suffix is not null)
            .Select(suffix => suffix!.Value)
            .ToArray();
        return suffixes.Length == 0 ? 1 : suffixes.Max() + 1;
    }

    private static string BaseName(string name)
    {
        var index = name.LastIndexOf(':');
        return index > 0 && int.TryParse(name[(index + 1)..], out _) ? name[..index] : name;
    }

    private static int? NumericSuffix(string name)
    {
        var index = name.LastIndexOf(':');
        return index > 0 && int.TryParse(name[(index + 1)..], out var suffix) ? suffix : null;
    }

    public void RegisterExtensions(IEnumerable<OwnedExtensionRegistration<ExtensionCommandRegistration>> registrations)
    {
        foreach (var command in registrations)
        {
            var registration = command.Value;
            Register(new SlashCommandDefinition(registration.Name, registration.Description, async (_, args, token) =>
            {
                await registration.Handler(args, token);
                return new SlashCommandResult(true, $"/{registration.Name} handled by {command.SourceId}.");
            }, command.SourceId));
        }
    }

    public IReadOnlyList<string> Complete(string input, int limit = 12)
    {
        var prefix = input.TrimStart().TrimStart('/');
        return FuzzyMatcher.Filter(Commands, prefix, command => command.Name)
            .Select(command => $"/{command.Name}")
            .Take(limit)
            .ToArray();
    }

    public async Task<SlashCommandResult> ExecuteAsync(string input, SlashCommandContext context, CancellationToken cancellationToken)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal)) return new SlashCommandResult(false);
        var body = trimmed[1..];
        var parts = body.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.FirstOrDefault() ?? string.Empty;
        var args = parts.Length > 1 ? parts[1] : string.Empty;
        if (!_commands.TryGetValue(name, out var command)) return new SlashCommandResult(false, $"Unknown command: /{name}", true);
        return await command.Execute(context with { CommandName = name }, args, cancellationToken);
    }
}
