using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace PiAgentDesktop;

internal sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>
/// Downloads a newer release and hands the file swap to a small detached helper that
/// runs <em>after</em> this process has exited.
///
/// Why a helper at all: Windows will not let a running program overwrite its own
/// executable. The helper is spawned but deliberately NOT assigned to the job object,
/// so it is not killed when the job closes together with the app. That was verified
/// experimentally: only processes explicitly assigned to the job (or descending from
/// one) die with it - a directly spawned child is in no job at all, because the app
/// itself is not a job member.
/// </summary>
internal static class Updater
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"PiAgentDesktop/{AppInfo.Version}");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static string StagingRoot => Path.Combine(Path.GetTempPath(), "PiAgentDesktop-update");

    public static string StagedDirectory(string version) => Path.Combine(StagingRoot, version);

    public static bool IsStaged(string version) =>
        File.Exists(Path.Combine(StagedDirectory(version), "PiAgentDesktop.exe"));

    /// <summary>
    /// "portable" or "requires-dotnet8", baked in at publish time by the release
    /// workflow. Without it (a local build) we prefer the self-contained bundle,
    /// which runs everywhere.
    /// </summary>
    public static string BundleKind => ReadBundleKind() ?? "portable";

    private static string? ReadBundleKind()
    {
        try
        {
            return typeof(Updater).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "PiBundleKind", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the release asset matching how this build was published, so a "lite" user
    /// is never handed the 58 MB bundle (or the other way round).
    /// </summary>
    public static string? SelectAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        foreach (var kind in new[] { BundleKind, "portable" })
        {
            var needle = $"-win-x64-{kind}.zip";
            var match = assets.FirstOrDefault(a =>
                a.Name.EndsWith(needle, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match.Url;
            }
        }

        return null;
    }

    /// <summary>Downloads and unpacks the release into the staging directory.</summary>
    public static async Task<bool> DownloadAsync(string version, string url, Log log, CancellationToken token)
    {
        var target = StagedDirectory(version);
        var zip = Path.Combine(StagingRoot, $"PiAgentDesktop-{version}.zip");

        try
        {
            Directory.CreateDirectory(StagingRoot);
            log.Info($"Downloading update {version} ({BundleKind}) from {url}");

            using (var response = await Http
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var destination = File.Create(zip);
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            }

            var megabytes = new FileInfo(zip).Length / (1024.0 * 1024.0);
            log.Info($"Downloaded {megabytes:0.0} MB; extracting.");

            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
            File.Delete(zip);

            if (!File.Exists(Path.Combine(target, "PiAgentDesktop.exe")))
            {
                log.Error("The downloaded package has no PiAgentDesktop.exe.");
                return false;
            }

            log.Info($"Update {version} staged at {target}");
            return true;
        }
        catch (Exception error)
        {
            log.Warn($"Update download failed: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes the swap helper and starts it. The helper blocks until this process is
    /// gone, then copies the staged files over the installation and relaunches.
    /// </summary>
    public static bool ScheduleApply(string version, Log log)
    {
        var staging = StagedDirectory(version);
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var helperPath = Path.Combine(StagingRoot, $"apply-{version}.cmd");
        var helperLog = Path.Combine(AppPaths.LogDirectory, "update.log");

        if (!IsStaged(version))
        {
            log.Warn($"No staged update for {version}; nothing to apply.");
            return false;
        }

        // Plain ASCII inside the script on purpose: cmd.exe runs with the OEM code page.
        var script = $"""
@echo off
rem Written by Pi Agent Desktop {AppInfo.Version}; installs {version} after it exits.
setlocal
set "APP={appDirectory}"
set "NEW={staging}"
set "LOG={helperLog}"
set "PID={Environment.ProcessId}"
set /a tries=0

echo [%DATE% %TIME%] waiting for pid %PID% to exit >> "%LOG%"
:wait
set /a tries+=1
if %tries% gtr 180 goto giveup
tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)

echo [%DATE% %TIME%] backing up the current executable >> "%LOG%"
copy /y "%APP%\PiAgentDesktop.exe" "%APP%\PiAgentDesktop.exe.bak" >> "%LOG%" 2>&1

echo [%DATE% %TIME%] installing {version} >> "%LOG%"
robocopy "%NEW%" "%APP%" /E /R:5 /W:2 /NFL /NDL /NJH /NJS >> "%LOG%" 2>&1
if errorlevel 8 (
  echo [%DATE% %TIME%] robocopy failed; PiAgentDesktop.exe.bak can be restored >> "%LOG%"
) else (
  echo [%DATE% %TIME%] updated to {version} >> "%LOG%"
)

start "" "%APP%\PiAgentDesktop.exe"
echo [%DATE% %TIME%] relaunched >> "%LOG%"
rmdir /s /q "%NEW%" 2>nul
exit /b 0

:giveup
echo [%DATE% %TIME%] gave up waiting for pid %PID% >> "%LOG%"
exit /b 1
""";

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Directory.CreateDirectory(StagingRoot);
            File.WriteAllText(helperPath, script, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = StagingRoot,
            };

            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(helperPath);

            // Not assigned to the job on purpose: it has to outlive this process.
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                log.Error("Could not start the update helper.");
                return false;
            }

            log.Info($"Update helper started (pid {helper.Id}); {version} installs after exit. Log: {helperLog}");
            return true;
        }
        catch (Exception error)
        {
            log.Error("Failed to schedule the update", error);
            return false;
        }
    }
}
