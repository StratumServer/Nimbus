using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nimbus.Shared;

namespace Nimbus.Proxy;

internal static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/trevorftp/Nimbus/releases/latest";
    private const string ReleasesUrl = "https://github.com/trevorftp/Nimbus/releases";

    public static void StartBackgroundCheck()
    {
        _ = Task.Run(async () =>
        {
            // Brief delay so the startup banner prints first.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nimbus-Proxy/{NimbusProtocol.NimbusVersion}");

                var release = await http.GetFromJsonAsync<GitHubRelease>(ApiUrl).ConfigureAwait(false);
                if (release?.TagName is not { Length: > 0 } tag) return;

                if (IsNewer(NimbusProtocol.NimbusVersion, tag))
                    Log.Warn($"update available: {NimbusProtocol.NimbusVersion} → {tag.TrimStart('v')}  {ReleasesUrl}");
            }
            catch { /* network unavailable or rate-limited — silently skip */ }
        });
    }

    private static bool IsNewer(string current, string latest)
    {
        current = current.TrimStart('v');
        latest  = latest.TrimStart('v');

        var currentBase = current.Split('-')[0];
        var latestBase  = latest.Split('-')[0];

        if (!Version.TryParse(currentBase, out var cv)) return false;
        if (!Version.TryParse(latestBase,  out var lv)) return false;

        if (lv > cv) return true;
        // Same numeric version but current is a pre-release and latest is a proper release.
        if (lv == cv && current.Contains('-') && !latest.Contains('-')) return true;
        return false;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }
}
