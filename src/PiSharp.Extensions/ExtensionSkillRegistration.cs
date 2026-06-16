namespace PiSharp.Extensions;

public sealed record ExtensionSkillRegistration(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject);
