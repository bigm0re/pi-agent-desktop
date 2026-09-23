using System.Diagnostics;
using System.Text;

namespace PiAgentDesktop;

/// <summary>Filesystem locations used by the desktop shell (all under the user profile).</summary>
internal static class AppPaths
{
    public static string RoamingRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiAgentDesktop");

    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiAgentDesktop");

    public static string ConfigFile => Path.Combine(RoamingRoot, "config.json");

    public static string LogDirectory => Path.Combine(RoamingRoot, "logs");

    public static string LogFile => Path.Combine(LogDirectory, "desktop.log");

    public static string WebViewDataDirectory => Path.Combine(LocalRoot, "WebView2");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingRoot);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(LocalRoot);
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            /* best effort */
        }
    }

    public static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            /* best effort */
        }
    }
}

/// <summary>Minimal append-only rolling file logger (thread safe).</summary>
internal sealed class Log
{
    private const long MaxBytes = 5L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _file;
    private readonly bool _echo;

    public Log(string file, bool echo = false)
    {
        _file = file;
        _echo = echo;
        TryRotate();
    }

    public string FilePath => _file;

    public void Info(string message) => Write("info", message);

    public void Warn(string message) => Write("warn", message);

    public void Error(string message) => Write("error", message);

    public void Error(string message, Exception? exception) =>
        Write(
            "error",
            exception is null
                ? message
                : $"{message}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    /// <summary>Logs one line of child-process output (stderr is marked).</summary>
    public void Child(string prefix, string? line, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Write(isError ? "stderr" : "child", $"{prefix} {line.Trim()}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                System.IO.File.AppendAllText(_file, line, Encoding.UTF8);
            }
            catch
            {
                /* logging must never take the app down */
            }
        }

        if (_echo)
        {
            Console.Write(line);
        }
    }

    private void TryRotate()
    {
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var previous = Path.Combine(info.DirectoryName!, "desktop.1.log");
            System.IO.File.Delete(previous);
            System.IO.File.Move(_file, previous);
        }
        catch
        {
            /* best effort */
        }
    }
}
