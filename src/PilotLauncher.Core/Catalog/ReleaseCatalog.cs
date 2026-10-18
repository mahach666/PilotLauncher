namespace PilotLauncher.Core.Catalog;

/// <param name="Error">Why the live listing could not be loaded; set only when <paramref name="FromCache"/> is true.</param>
public sealed record CatalogResult(IReadOnlyList<ReleaseEntry> Entries, bool FromCache, string? Error);

/// <summary>Loads the release listing and keeps the last good copy on disk for offline start.</summary>
public sealed class ReleaseCatalog
{
    public static readonly Uri DefaultBaseUri = new("https://pilot.ascon.ru/release/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _cachePath;

    public ReleaseCatalog(HttpClient http, string cachePath, Uri? baseUri = null)
    {
        _http = http;
        _cachePath = cachePath;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public Uri GetDownloadUri(ReleaseEntry entry) => new(_baseUri, Uri.EscapeDataString(entry.FileName));

    public async Task<CatalogResult> LoadAsync(CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(_baseUri, ct).ConfigureAwait(false);
            var entries = ReleaseListingParser.Parse(html);
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
            var cached = ReleaseListingParser.Parse(await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
            return new CatalogResult(cached, true, ex.Message);
        }
    }
}
