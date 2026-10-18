using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PilotLauncher.Core.Extensions;

/// <param name="Sha256">From the asset's <c>digest</c> field; null when GitHub did not report one (older releases).</param>
public sealed record ExtensionRelease(
    string Tag, Version? Version, string AssetName, long Size, Uri DownloadUri, string? Sha256, DateTime? PublishedUtc, string? Notes)
{
    /// <summary>True when <paramref name="installedTag"/> is older than this release (or cannot be compared and differs).</summary>
    public bool IsNewerThan(string installedTag) =>
        TryParseTag(installedTag, out var installed) && Version is not null
            ? Version > installed
            : !string.Equals(Tag, installedTag, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);
}

/// <param name="Error">Why the release could not be refreshed; set only when the saved answer is used.</param>
public sealed record ExtensionReleaseResult(ExtensionRelease Release, DateTime FetchedUtc, string? Error);

/// <summary>
/// Asks the GitHub API for the latest release of an extension. The answer is saved with its ETag: it is reused for a few
/// hours, then asked again with <c>If-None-Match</c> (a "not modified" answer does not use up the hourly request limit),
/// and it is the fallback when GitHub cannot be reached.
/// </summary>
public sealed class GitHubReleaseClient
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
    public static readonly Uri DefaultApi = new("https://api.github.com/");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly Uri _api;

    public GitHubReleaseClient(HttpClient http, string cacheDir, Uri? api = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _api = api ?? DefaultApi;
    }

    private sealed record CacheFile(string? ETag, DateTime FetchedUtc, string Json);

    public async Task<ExtensionReleaseResult> GetLatestAsync(ExtensionSource source, bool force, CancellationToken ct)
    {
        var cachePath = Path.Combine(_cacheDir, $"extension-{source.Id}.json");
        var cache = await ReadCacheAsync(cachePath, ct).ConfigureAwait(false);
        if (cache is not null && !force && DateTime.UtcNow - cache.FetchedUtc < FreshFor)
            return new ExtensionReleaseResult(Parse(cache.Json, source), cache.FetchedUtc, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, $"repos/{source.Repo}/releases/latest"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (cache?.ETag is { } etag)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
            {
                await SaveCacheAsync(cachePath, cache with { FetchedUtc = DateTime.UtcNow }, ct).ConfigureAwait(false);
                return new ExtensionReleaseResult(Parse(cache.Json, source), DateTime.UtcNow, null);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
                throw new HttpRequestException("лимит запросов к GitHub исчерпан, повторите через час");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = Parse(json, source);
            var fresh = new CacheFile(response.Headers.ETag?.ToString(), DateTime.UtcNow, json);
            await SaveCacheAsync(cachePath, fresh, ct).ConfigureAwait(false);
            return new ExtensionReleaseResult(release, fresh.FetchedUtc, null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            if (cache is null)
                throw;
            return new ExtensionReleaseResult(Parse(cache.Json, source), cache.FetchedUtc, ex.Message);
        }
    }

    internal static ExtensionRelease Parse(string json, ExtensionSource source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("В ответе GitHub нет tag_name.");
        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), source.AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"В релизе {tag} репозитория {source.Repo} нет файла {source.AssetName}.");

        string? sha = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha = d["sha256:".Length..];
        DateTime? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var when) ? when.ToUniversalTime() : null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        ExtensionRelease.TryParseTag(tag, out var version);

        return new ExtensionRelease(tag, version, source.AssetName, asset.GetProperty("size").GetInt64(),
            new Uri(asset.GetProperty("browser_download_url").GetString()!), sha, published, notes);
    }

    private static async Task<CacheFile?> ReadCacheAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CacheFile>(stream, Json, ct).ConfigureAwait(false);
    }

    private static async Task SaveCacheAsync(string path, CacheFile cache, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(cache, Json), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
