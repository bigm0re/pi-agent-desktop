using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgentDesktop;

/// <summary>Persisted user settings (config.json next to the log file).</summary>
internal sealed class AppConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // --- pi-web server -----------------------------------------------------
    public int Port { get; set; } = 30141;
    public int PortScanLimit { get; set; } = 10;
    public string HostName { get; set; } = "127.0.0.1";

    /// <summary>
    /// When true the shell attaches to a pi-web that is already listening instead of
    /// starting its own. Default is false: the desktop app must be self-sufficient.
    /// An instance started from a terminal dies with that terminal, which would take
    /// the app's UI down with it.
    /// </summary>
    public bool ReuseRunningServer { get; set; }

    /// <summary>
    /// Let the shell repair pi's npmCommand setting when it is missing or provably
    /// unusable on Windows. See <see cref="NpmEnvironment"/>.
    /// </summary>
    public bool EnsureNpmCommand { get; set; } = true;

    public string? NodePath { get; set; }
    public string? PiWebDir { get; set; }
    public string? PiAgentDir { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();

    // --- window / tray behaviour -------------------------------------------
    /// <summary>
    /// Start with no window at all. Default false so that double-clicking the exe
    /// shows the window; autostart passes --hidden explicitly, which is the only
    /// path that should sit silently in the tray.
    /// </summary>
    public bool StartHidden { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool PreloadWindow { get; set; } = true;
    public bool NotifyOnHide { get; set; } = true;
    public bool AutoStart { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 840;
    public bool WindowMaximized { get; set; }

    // --- robustness --------------------------------------------------------
    public bool RestartOnCrash { get; set; } = true;
    public int MaxRestarts { get; set; } = 5;
    public int ReadyTimeoutMs { get; set; } = 120_000;

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    public static AppConfig Load(string file, Log log)
    {
        AppConfig config;
        try
        {
            config = File.Exists(file)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), SerializerOptions) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception error)
        {
            log.Warn($"config.json could not be parsed, falling back to defaults: {error.Message}");
            config = new AppConfig();
        }

        config.FilePath = file;
        config.Normalize();
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            /* best effort */
        }
    }

    private void Normalize()
    {
        if (Port is < 1 or > 65535)
        {
            Port = 30141;
        }

        if (PortScanLimit is < 1 or > 100)
        {
            PortScanLimit = 10;
        }

        if (string.IsNullOrWhiteSpace(HostName))
        {
            HostName = "127.0.0.1";
        }

        if (WindowWidth < 640)
        {
            WindowWidth = 1280;
        }

        if (WindowHeight < 480)
        {
            WindowHeight = 840;
        }

        if (ReadyTimeoutMs < 5_000)
        {
            ReadyTimeoutMs = 120_000;
        }
    }
}
