using PiSharp.Runtime;

namespace PiSharp.Cli.Commands;

public static class SlashCommandRegistryFactory
{
    public static SlashCommandRegistry Create(SessionRuntime runtime)
    {
        var commands = BuiltInSlashCommandCatalog.CreateRegistry();
        RegisterExtensionCommands(runtime, commands);
        RegisterSkillCommands(runtime, commands);
        RegisterPromptTemplateCommands(runtime, commands);
        return commands;
    }

    private static void RegisterSkillCommands(SessionRuntime runtime, SlashCommandRegistry commands)
    {
        foreach (var skill in runtime.Skills)
        {
            commands.Register(new SlashCommandDefinition(
                $"skill:{skill.Name}",
                skill.Description,
                async (context, args, token) =>
                {
                    var suffix = string.IsNullOrWhiteSpace(args) ? string.Empty : $" {args}";
                    await context.SubmitPromptAsyncOrDefault($"/skill:{skill.Name}{suffix}", token);
                    return new SlashCommandResult(true);
                },
                "skill"));
        }
    }

    private static void RegisterPromptTemplateCommands(SessionRuntime runtime, SlashCommandRegistry commands)
    {
        foreach (var template in runtime.PromptTemplates.Templates)
        {
            commands.Register(new SlashCommandDefinition(
                $"prompt:{template.Name}",
                template.Description ?? $"Run prompt template '{template.Name}'.",
                async (context, args, token) =>
                {
                    var expanded = runtime.PromptTemplates.FormatInvocation(template.Name, SplitTemplateArgs(args));
                    await context.SubmitPromptAsyncOrDefault(expanded, token);
                    return new SlashCommandResult(true);
                },
                "prompt-template"));
        }
    }

    private static string[] SplitTemplateArgs(string args)
        => string.IsNullOrWhiteSpace(args) ? [] : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void RegisterExtensionCommands(SessionRuntime runtime, SlashCommandRegistry commands)
    {
        if (runtime.ExtensionManager is null) return;
        commands.RegisterExtensions(runtime.ExtensionManager.Registry.Commands);
    }
}
