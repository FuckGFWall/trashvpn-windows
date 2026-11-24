using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Xehn.App;

internal static class AppReleaseUpdate
{
    public const string GitHubLatestRelease = "https://api.github.com/repos/FuckGFWall/trashvpn-windows/releases/latest";
    public const string DownloadPage = "https://itmanager.top/download.html";
    public static readonly Version CurrentVersion = Version.Parse(typeof(AppReleaseUpdate).Assembly.GetName().Version!.ToString(3));

    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (tag[0] is 'v' or 'V') tag = tag[1..];
        return Version.TryParse(tag, out var version) && version.Build >= 0 && version.Revision < 0 ? version : null;
    }

    public static async Task<Version> FetchVersionAsync(int mixedPort, CancellationToken cancellationToken)
    {
        if (mixedPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(mixedPort));
        // Always use the verified session's mixed port, in both proxy and TUN
        // modes. Neither system proxy settings nor a direct fallback are used.
        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true,
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XEHN/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync(GitHubLatestRelease, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseRelease(json.RootElement);
    }

    internal static Version ParseRelease(JsonElement release)
    {
        if (release.ValueKind == JsonValueKind.Object &&
            release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String &&
            ParseTag(tag.GetString()) is { } version)
            return version;
        throw new InvalidDataException("GitHub 未返回有效的正式版本号。");
    }

    internal sealed record History(DateOnly? LastAttempt = null, string? LatestTag = null)
    {
        private const string FileName = "app-update.json";

        public static History Load(string home)
        {
            try { return JsonSerializer.Deserialize<History>(File.ReadAllText(Path.Combine(home, FileName))) ?? new(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }

        public void Save(string home)
        {
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
