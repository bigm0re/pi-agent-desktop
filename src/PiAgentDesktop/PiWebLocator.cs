using System.Diagnostics;
using System.Text.Json;

namespace PiAgentDesktop;

internal sealed record PiWebInstallation(string Directory, string EntryScript, string Version, bool HasProductionBuild);

/// <summary>
/// Locates the two external pieces the shell needs: a real Node.js runtime
/// (node-pty native modules must load against the same ABI npm installed with)
/// and the installed pi-web package (which embeds the original pi agent core).
/// </summary>
internal static class PiWebLocator
{
    private const string PackageName = "@agegr/pi-web";
    private const string PackageFolderName = "pi-web";

    public static string? FindNodeExecutable(AppConfig config, Log log)
    {
        var candidates = new List<string?>();

        if (!string.IsNullOrWhiteSpace(config.NodePath))
        {
            candidates.Add(config.NodePath);
        }

        candidates.Add(Environment.GetEnvironmentVariable("PI_DESKTOP_NODE"));
        candidates.Add(FindOnPath("node.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        candidates.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
        candidates.Add(Path.Combine(programFilesX86, "nodejs", "node.exe"));
        candidates.Add(Path.Combine(localAppData, "Programs", "nodejs", "node.exe"));

        // nvm-windows layouts
        candidates.Add(Path.Combine(appData, "nvm", "current", "node.exe"));
        var nvmRoot = Path.Combine(appData, "nvm");
        if (Directory.Exists(nvmRoot))
        {
            foreach (var version in Directory.GetDirectories(nvmRoot, "v*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(version, "node.exe"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate is null || !File.Exists(candidate))
            {
                continue;
            }

            var version = TryGetNodeVersion(candidate);
            log.Info($"Node.js runtime: {candidate} ({version ?? "unknown version"})");

            if (version is not null && !IsSupportedNodeVersion(version))
            {
                log.Warn($"Node {version} is below the pi-web requirement (>= 22.19.0); the server may refuse to start.");
            }

            return candidate;
        }

        log.Error("No Node.js runtime found. Install Node.js 22.19+ or set config.nodePath.");
        return null;
    }

    public static PiWebInstallation? FindPiWeb(AppConfig config, Log log)
    {
        var candidates = new List<string?>();

        if (!string.IsNullOrWhiteSpace(config.PiWebDir))
        {
            candidates.Add(config.PiWebDir);
        }

        candidates.Add(Environment.GetEnvironmentVariable("PI_WEB_DIR"));

        // Bundled next to the shell (optional local install), then every global npm prefix.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "node_modules", "@agegr", PackageFolderName));
        foreach (var prefix in GetNpmGlobalPrefixes())
        {
            candidates.Add(Path.Combine(prefix, "node_modules", "@agegr", PackageFolderName));
        }

        foreach (var candidate in candidates)
        {
            var installation = TryDescribe(candidate);
            if (installation is null)
            {
                continue;
            }

            if (!installation.HasProductionBuild)
            {
                log.Warn($"pi-web {installation.Version} at {installation.Directory} has no .next build output; start may fail.");
            }

            log.Info($"pi-web runtime: {installation.Directory} (version {installation.Version})");
            return installation;
        }

        log.Error($"No {PackageName} installation found. Install it with: npm install -g {PackageName}");
        return null;
    }

    /// <summary>Absolute path of npm's JS entry point (used to run npm without a shell).</summary>
    public static string? FindNpmCommandScript(string? nodePath)
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(nodePath))
        {
            roots.Add(Path.GetDirectoryName(nodePath)!);
        }

        var onPath = FindOnPath("npm.cmd");
        if (onPath is not null)
        {
            roots.Add(Path.GetDirectoryName(onPath)!);
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<string> GetNpmGlobalPrefixes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fromNpmShim = FindOnPath("npm.cmd");
        if (fromNpmShim is not null)
        {
            var directory = Path.GetDirectoryName(fromNpmShim);
            if (directory is not null && seen.Add(directory))
            {
                yield return directory;
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roamingNpm = Path.Combine(appData, "npm");
        if (seen.Add(roamingNpm))
        {
            yield return roamingNpm;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesNpm = Path.Combine(programFiles, "nodejs");
        if (seen.Add(programFilesNpm))
        {
            yield return programFilesNpm;
        }
    }

    /// <summary>First match for <paramref name="fileName"/> on the inherited PATH.</summary>
    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                /* malformed PATH entries are skipped */
            }
        }

        return null;
    }

    private static PiWebInstallation? TryDescribe(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var packageJson = Path.Combine(directory, "package.json");
            var entry = Path.Combine(directory, "bin", "pi-web.js");
            if (!File.Exists(packageJson) || !File.Exists(entry))
            {
                return null;
            }

            var version = "unknown";
            using (var document = JsonDocument.Parse(File.ReadAllText(packageJson)))
            {
                if (document.RootElement.TryGetProperty("version", out var versionElement))
                {
                    version = versionElement.GetString() ?? "unknown";
                }
            }

            var hasBuild = File.Exists(Path.Combine(directory, ".next", "BUILD_ID"));
            return new PiWebInstallation(Path.GetFullPath(directory), entry, version, hasBuild);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetNodeVersion(string nodePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(nodePath, "-p process.versions.node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedNodeVersion(string version) =>
        Version.TryParse(version, out var parsed) && parsed >= new Version(22, 19, 0);
}
