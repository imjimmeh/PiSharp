using Microsoft.Extensions.Configuration;

namespace PiSharp.Compatibility.Settings;

public static class PiSettingsConfiguration
{
    public static IConfigurationRoot Build(PiAgentPaths paths)
        => new ConfigurationBuilder()
            .AddJsonFile(paths.GlobalSettingsPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.GlobalPiSharpSettingsPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.ProjectSettingsPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.ProjectPiSharpSettingsPath, optional: true, reloadOnChange: false)
            .Build();
}
