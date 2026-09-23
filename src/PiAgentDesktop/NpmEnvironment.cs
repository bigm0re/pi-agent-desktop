using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiAgentDesktop;

/// <summary>Outcome of inspecting (and possibly repairing) pi's npmCommand setting.</summary>
internal sealed record NpmCommandStatus(
    string SettingsPath,
    bool SettingsExists,
    bool SettingPresent,
    bool SettingUsable,
    string? SettingValue,
    bool Modified,
    string? Probe);

/// <summary>
/// Windows cannot spawn the npm CLI the way the pi agent tries to.
///
/// The agent runs <c>execFile("npm", ...)</c> (see <c>package-manager.js</c>), but on
/// Windows npm ships as <c>npm.cmd</c> / <c>npm.ps1</c> / a bash <c>npm</c> script —
/// there is no <c>npm.exe</c>. Node does not consult PATHEXT outside a shell, so the
/// spawn fails with ENOENT, and naming the shim explicitly is refused with EINVAL
/// (the CVE-2024-27980 guard against spawning .cmd/.bat without a shell).
///
/// Verified on this machine: adding the npm directory to PATH does NOT help, because
/// the directory simply contains no .exe. What does work is pointing the agent at a
/// shell-mediated invocation, which is what this class configures. The value is
/// deliberately machine independent (<c>["cmd","/c","npm"]</c>) so the same shell
/// works on any PC; only PATH has to be augmented per machine.
/// </summary>
internal static class NpmEnvironment
{
    private const string NpmCommandKey = "npmCommand";

    /// <summary>Machine independent replacement for the broken default <c>["npm"]</c>.</summary>
    private static readonly string[] PortableNpmCommand = { "cmd", "/c", "npm" };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string ResolveAgentDirectory(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.PiAgentDir))
        {
            return config.PiAgentDir!;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment!;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
    }

    public static string ResolveSettingsPath(AppConfig config) =>
        Path.Combine(ResolveAgentDirectory(config), "settings.json");

    /// <summary>
    /// Directories that make <c>cmd /c npm</c> resolvable in the child process. These
    /// are prepended to the pi-web child's PATH; they never fix ENOENT on their own,
    /// but the shell-mediated command needs npm to be discoverable.
    /// </summary>
    public static IReadOnlyList<string> GetPathAugmentations(AppConfig config, string? nodePath, Log log)
    {
        var directories = new List<string>();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            if (!directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(directory);
            }
        }

        var resolvedNode = nodePath;
        if (string.IsNullOrWhiteSpace(resolvedNode))
        {
            resolvedNode = PiWebLocator.FindNodeExecutable(config, log);
        }

        if (!string.IsNullOrWhiteSpace(resolvedNode))
        {
            Add(Path.GetDirectoryName(resolvedNode));
        }

        var npmShim = PiWebLocator.FindOnPath("npm.cmd");
        if (npmShim is not null)
        {
            Add(Path.GetDirectoryName(npmShim));
        }

        foreach (var prefix in PiWebLocator.GetNpmGlobalPrefixes())
        {
            Add(prefix);
        }

        return directories;
    }

    /// <summary>PATH for the pi-web child process: augmented directories first, then the inherited PATH.</summary>
    public static string BuildAugmentedPath(AppConfig config, string? nodePath, Log log)
    {
        var inherited = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var additions = GetPathAugmentations(config, nodePath, log);

        if (additions.Count == 0)
        {
            return inherited;
        }

        var prefix = string.Join(Path.PathSeparator, additions);
        return inherited.Length == 0 ? prefix : prefix + Path.PathSeparator + inherited;
    }

    /// <summary>
    /// Ensures pi's settings.json carries a usable npmCommand. Only fills a gap or
    /// repairs a value that provably cannot work on Windows; a plausible user-set
    /// value is left untouched.
    /// </summary>
    public static NpmCommandStatus EnsureNpmCommand(AppConfig config, string? nodePath, Log log)
    {
        var settingsPath = ResolveSettingsPath(config);

        if (!File.Exists(settingsPath))
        {
            log.Info($"settings.json 不存在（{settingsPath}），跳过 npmCommand 检查。");
            return new NpmCommandStatus(settingsPath, false, false, false, null, false, null);
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)) is not JsonObject parsed)
            {
                log.Warn($"{settingsPath} 顶层不是 JSON 对象，未做修改。");
                return new NpmCommandStatus(settingsPath, true, false, false, null, false, null);
            }

            root = parsed;
        }
        catch (Exception error)
        {
            log.Warn($"解析 {settingsPath} 失败，未做修改：{error.Message}");
            return new NpmCommandStatus(settingsPath, true, false, false, null, false, null);
        }

        var existing = root[NpmCommandKey];
        var present = existing is not null;
        var displayValue = present ? existing!.ToJsonString() : null;
        var usable = present && IsUsable(existing);

        if (!config.EnsureNpmCommand)
        {
            log.Info("EnsureNpmCommand 已关闭，不修改 npmCommand。");
            return new NpmCommandStatus(settingsPath, true, present, usable, displayValue, false, null);
        }

        if (usable)
        {
            log.Info($"npmCommand 已可用，保持不变：{displayValue}");
            return new NpmCommandStatus(settingsPath, true, true, true, displayValue, false, null);
        }

        log.Warn(present
            ? $"npmCommand 在本机不可用（{displayValue}），改为 {FormatCommand(PortableNpmCommand)}。"
            : $"settings.json 缺少 npmCommand，写入 {FormatCommand(PortableNpmCommand)}。");

        try
        {
            Backup(settingsPath, log);

            var replacement = new JsonArray();
            foreach (var part in PortableNpmCommand)
            {
                replacement.Add(JsonValue.Create(part));
            }

            root[NpmCommandKey] = replacement;
            WriteAtomically(settingsPath, root.ToJsonString(WriteOptions) + Environment.NewLine);
            log.Info($"已更新 {settingsPath}：npmCommand = {FormatCommand(PortableNpmCommand)}");
        }
        catch (Exception error)
        {
            log.Error($"写入 {settingsPath} 失败", error);
            return new NpmCommandStatus(settingsPath, true, present, false, displayValue, false, null);
        }

        // Verify the repaired pipeline end to end while we are here.
        var probe = Probe(config, nodePath, log);
        log.Info($"npm 自检（cmd /c npm --version）=> {probe}");

        return new NpmCommandStatus(
            settingsPath,
            true,
            true,
            true,
            FormatCommand(PortableNpmCommand),
            true,
            probe);
    }

    /// <summary>Runs the same npm invocation the agent would, through the augmented PATH.</summary>
    public static string Probe(AppConfig config, string? nodePath, Log log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("npm");
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["PATH"] = BuildAugmentedPath(config, nodePath, log);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "无法启动 cmd.exe";
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();

            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    /* ignore */
                }

                return "超时（20 秒）";
            }

            if (process.ExitCode == 0 && stdout.Length > 0)
            {
                return $"OK npm {stdout.Split('\n')[0].Trim()}";
            }

            var reason = stderr.Length > 0 ? stderr.Split('\n')[0].Trim() : $"退出码 {process.ExitCode}";
            return $"失败：{reason}";
        }
        catch (Exception error)
        {
            return $"失败：{error.Message}";
        }
    }

    private static bool IsUsable(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return false;
        }

        var command = (array[0] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // The stock value. Bare "npm" resolves to no .exe, and the .cmd/.bat shims are
        // rejected by Node's EINVAL guard, so this can never work on Windows.
        var fileName = Path.GetFileName(command);
        if (fileName.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("npm.bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Anything referencing a concrete file must have that file on this machine,
        // otherwise the setting was copied from a PC with a different layout.
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        // Bare executables such as cmd / node / pnpm / bun: assume the machine's PATH
        // knows about them; we only guarantee our own augmentation on top.
        return true;
    }

    private static void Backup(string settingsPath, Log log)
    {
        var backup = settingsPath + ".bak";
        if (File.Exists(backup))
        {
            return;
        }

        try
        {
            File.Copy(settingsPath, backup);
            log.Info($"已备份原设置到 {backup}");
        }
        catch (Exception error)
        {
            log.Warn($"备份 {settingsPath} 失败：{error.Message}");
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string FormatCommand(IEnumerable<string> parts) =>
        "[" + string.Join(",", parts.Select(p => $"\"{p}\"")) + "]";
}
