using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PiAgentDesktop;

/// <summary>
/// Samples who is holding memory.
///
/// The shell process is normally tiny (tens of MB), so when a machine runs out of
/// memory the cause is almost always one of two other things: the agent's node tree
/// (pi-web, next, and every extension/subagent it spawned) or the WebView2 browser
/// processes. From the outside all of them look like "Pi Agent Desktop", which is why
/// a slow leak is impossible to attribute without this breakdown.
/// </summary>
internal static class MemoryReport
{
    public static string FilePath => Path.Combine(AppPaths.LogDirectory, "memory.log");

    /// <summary>Builds one report. Never throws; missing processes are simply skipped.</summary>
    public static string Capture(JobObject? job)
    {
        var text = new StringBuilder();
        text.AppendLine($"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ---");

        long tracked = 0;

        // 1. The shell itself.
        try
        {
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            var workingSet = self.WorkingSet64 / (1024 * 1024);
            tracked += workingSet;
            text.AppendLine(
                $"shell      : {workingSet} MB working set, {self.PrivateMemorySize64 / (1024 * 1024)} MB private, " +
                $"{self.HandleCount} handles, {self.Threads.Count} threads, managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        }
        catch
        {
            text.AppendLine("shell      : unavailable");
        }

        // 2. The agent tree. Every node process we spawned is in the job, so this is
        //    exact - it includes leaked servers and extension/subagent processes.
        var members = new List<string>();
        long agentTotal = 0;

        foreach (var pid in job?.GetProcessIds() ?? Array.Empty<int>())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Refresh();
                var workingSet = process.WorkingSet64 / (1024 * 1024);
                agentTotal += workingSet;
                members.Add($"{process.ProcessName}({pid})={workingSet}MB");
            }
            catch
            {
                /* exited between enumeration and inspection */
            }
        }

        tracked += agentTotal;
        text.AppendLine($"agent tree : {members.Count} process(es), {agentTotal} MB");
        if (members.Count > 0)
        {
            text.AppendLine($"             {string.Join(", ", members)}");
        }

        // 3. WebView2. The runtime does not expose a parent link we can query without
        //    WMI, so this counts every WebView2 application on the machine.
        long webViewTotal = 0;
        var webViewCount = 0;

        try
        {
            foreach (var process in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    process.Refresh();
                    webViewTotal += process.WorkingSet64 / (1024 * 1024);
                    webViewCount++;
                }
                catch
                {
                    /* ignore */
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            /* ignore */
        }

        tracked += webViewTotal;
        text.AppendLine($"webview2   : {webViewCount} process(es), {webViewTotal} MB (every WebView2 app on this machine)");

        // 4. Physical memory, so the numbers can be read in context.
        try
        {
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            text.AppendLine($"      machine: {available} MB available to this process");
        }
        catch
        {
            /* ignore */
        }

        text.AppendLine($"tracked    : {tracked} MB");
        return text.ToString();
    }

    /// <summary>
    /// Writes a report to memory.log and one summary line into the main log, then
    /// returns the full text so callers can decide whether to warn.
    /// </summary>
    public static string Append(JobObject? job, Log log, string reason)
    {
        var report = Capture(job);

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            File.AppendAllText(FilePath, report + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            /* best effort */
        }

        log.Info($"memory ({reason}): {Summarise(report)}");
        return report;
    }

    public static string Summarise(string report)
    {
        var parts = new List<string>();

        foreach (var label in new[] { "shell", "agent tree", "webview2", "tracked" })
        {
            var match = Regex.Match(report, $@"^{Regex.Escape(label)}\s*:\s*(.*)$", RegexOptions.Multiline);
            if (match.Success)
            {
                parts.Add($"{label}={match.Groups[1].Value.Trim()}");
            }
        }

        return string.Join(" | ", parts);
    }

    /// <summary>Reads the "tracked: N MB" total, or 0 when it cannot be parsed.</summary>
    public static long TrackedMegabytes(string report)
    {
        var match = Regex.Match(report, @"^tracked\s*:\s*(\d+) MB", RegexOptions.Multiline);
        return match.Success && long.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}
