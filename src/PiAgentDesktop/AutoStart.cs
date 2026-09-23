using Microsoft.Win32;

namespace PiAgentDesktop;

/// <summary>Per-user "start with Windows" support via the HKCU Run key.</summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PiAgentDesktop";

    public static void Apply(bool enabled, Log log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                log.Warn("HKCU Run key is unavailable; auto start not applied.");
                return;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    log.Warn("Cannot resolve the executable path; auto start not applied.");
                    return;
                }

                key.SetValue(ValueName, $"\"{executable}\" --hidden");
                log.Info($"Auto start enabled: \"{executable}\" --hidden");
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                log.Info("Auto start disabled.");
            }
        }
        catch (Exception error)
        {
            log.Warn($"Failed to update the auto start registry value: {error.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
