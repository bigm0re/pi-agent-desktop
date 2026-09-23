using System.Reflection;

namespace PiAgentDesktop;

/// <summary>
/// Identity of this build. Surfaced in the startup log line, the tray menu and the
/// window title so two builds can be told apart at a glance.
/// </summary>
internal static class AppInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Drop any "+<commit>" suffix a source-link build may append.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
