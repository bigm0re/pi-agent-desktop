using System.Net.Http;
using System.Text.Json;

namespace PiAgentDesktop;

/// <summary>Result of one update check against the GitHub releases feed.</summary>
internal sealed record UpdateInfo(string Version, string Tag, string ReleaseUrl, string Notes, bool IsNewer);

/// <summary>
/// Asks GitHub for the latest release of this project and compares it with the running
/// build. This is intentionally only a <em>check</em>: replacing the running program's
/// own files is a separate, deliberate action.
/// </summary>
internal static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/bigm0re/pi-agent-desktop/releases/latest";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static UpdateChecker()
    {
        // The GitHub API rejects requests without a User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"PiAgentDesktop/{AppInfo.Version}");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Returns null when the check could not be completed (offline, rate limited…).</summary>
    public static async Task<UpdateInfo?> CheckAsync(Log log, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403 is the unauthenticated rate limit: not worth a scary log line.
                log.Warn($"Update check returned HTTP {(int)response.StatusCode}; will retry later.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;

            if (tag.Length == 0)
            {
                log.Warn("Update check: the latest release has no tag.");
                return null;
            }

            var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            var notes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;

            var version = Normalize(tag);
            var newer = IsNewer(version, AppInfo.Version);

            log.Info($"Update check: latest={version}, running={AppInfo.Version}, newer={newer}");
            return new UpdateInfo(version, tag, releaseUrl, notes, newer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            log.Warn($"Update check failed: {error.Message}");
            return null;
        }
    }

    /// <summary>True when <paramref name="candidate"/> is a strictly newer dotted version.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Normalize(candidate), out var latest))
        {
            return false;
        }

        if (!Version.TryParse(Normalize(current), out var running))
        {
            return false;
        }

        return latest > running;
    }

    /// <summary>Strips a leading "v" and any "-prerelease" / "+build" suffix.</summary>
    public static string Normalize(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');

        var cut = text.IndexOfAny(new[] { '-', '+' });
        return cut > 0 ? text[..cut] : text;
    }
}
