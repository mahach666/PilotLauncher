namespace PilotLauncher.Core.Catalog;

/// <param name="Error">Why the live listing could not be loaded; set only when <paramref name="FromCache"/> is true.</param>
public sealed record CatalogResult(IReadOnlyList<ReleaseEntry> Entries, bool FromCache, string? Error);

/// <summary>Loads the release listing and keeps the last good copy on disk for offline start.</summary>
public sealed class ReleaseCatalog
{
    public static readonly Uri DefaultBaseUri = new("https://pilot.ascon.ru/release/");

    /// <summary>Pre-release builds; the same listing format as <see cref="DefaultBaseUri"/>.</summary>
    public static readonly Uri BetaBaseUri = new("https://pilot.ascon.ru/beta/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _cachePath;

    public ReleaseCatalog(HttpClient http, string cachePath, Uri? baseUri = null)
    {
        _http = http;
        _cachePath = cachePath;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public Uri GetDownloadUri(ReleaseEntry entry) => new(entry.Folder ?? _baseUri, Uri.EscapeDataString(entry.FileName));

    public async Task<CatalogResult> LoadAsync(CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(_baseUri, ct).ConfigureAwait(false);
            var entries = Parse(html);
            if (entries.Count == 0)
                throw new InvalidDataException("The release listing contains no Pilot client packages.");
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, html, ct).ConfigureAwait(false);
            File.Move(tmp, _cachePath, overwrite: true);
            return new CatalogResult(entries, false, null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            if (!File.Exists(_cachePath))
                throw;
            var cached = Parse(await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
            return new CatalogResult(cached, true, ex.Message);
        }
    }

    /// <summary>
    /// The releases and the pre-release builds that were not released: <c>/beta/</c> also holds most released versions,
    /// and those are downloaded from <c>/release/</c> as usual.
    /// </summary>
    public static IReadOnlyList<ReleaseEntry> WithPrereleases(IEnumerable<ReleaseEntry> releases, IEnumerable<ReleaseEntry> prereleases)
    {
        var result = releases.ToList();
        var released = result.Select(r => (r.Edition.Id, r.Version)).ToHashSet();
        result.AddRange(prereleases.Where(p => !released.Contains((p.Edition.Id, p.Version))));
        return result;
    }

    private List<ReleaseEntry> Parse(string html) => ReleaseListingParser.Parse(html).Select(e => e with { Folder = _baseUri }).ToList();
}
