using System.Security.Authentication;

namespace PilotLauncher.Core.Notes;

/// <param name="FetchedUtc">When the page was downloaded.</param>
/// <param name="Error">Why the page could not be refreshed; set only when the saved copy older than <see cref="ReleaseNotesCatalog.MaxAge"/> is shown.</param>
/// <param name="CertificateProblem">The refresh failed because the site's certificate was not accepted.</param>
public sealed record NotesResult(IReadOnlyList<ReleaseNotes> Releases, DateTime FetchedUtc, string? Error, bool CertificateProblem = false);

/// <summary>
/// Loads the release notes page. The server sends no ETag or Last-Modified, so the saved copy is trusted for a day
/// and only then downloaded again; when the download fails the older copy is shown with the reason.
/// </summary>
public sealed class ReleaseNotesCatalog
{
    public static readonly Uri DefaultPageUri = new("https://help.pilotems.com/ru/Content/RelNotes.htm");
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly string _cachePath;

    public ReleaseNotesCatalog(HttpClient http, string cachePath, Uri? pageUri = null)
    {
        _http = http;
        _cachePath = cachePath;
        PageUri = pageUri ?? DefaultPageUri;
    }

    public Uri PageUri { get; }

    public async Task<NotesResult> LoadAsync(CancellationToken ct)
    {
        var cached = await ReadCacheAsync(ct).ConfigureAwait(false);
        if (cached is not null && DateTime.UtcNow - cached.FetchedUtc < MaxAge)
            return cached;

        try
        {
            var html = await _http.GetStringAsync(PageUri, ct).ConfigureAwait(false);
            var releases = ReleaseNotesParser.Parse(html);
            if (releases.Count == 0)
                throw new InvalidDataException("The page contains no release notes.");
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, html, ct).ConfigureAwait(false);
            File.Move(tmp, _cachePath, overwrite: true);
            return new NotesResult(releases, DateTime.UtcNow, null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            if (cached is null)
                throw;
            return cached with { Error = Describe(ex), CertificateProblem = IsCertificateProblem(ex) };
        }
    }

    /// <summary>
    /// Why the page could not be loaded, for the user. help.pilotems.com is served with a certificate of the Russian Trusted Root CA,
    /// which Windows does not trust until its root certificate is installed.
    /// </summary>
    public static string Describe(Exception ex) => ex.InnerException is AuthenticationException tls
        ? $"сертификат сайта не прошёл проверку ({tls.Message}). Сайт использует сертификат Минцифры России, которому Windows не доверяет без установки корневого сертификата."
        : ex.Message;

    /// <summary>The request failed in the TLS handshake because the server certificate was rejected.</summary>
    public static bool IsCertificateProblem(Exception ex) => ex.InnerException is AuthenticationException;

    /// <summary>The saved page; null when there is none or it holds no releases (then it is as good as absent).</summary>
    private async Task<NotesResult?> ReadCacheAsync(CancellationToken ct)
    {
        if (!File.Exists(_cachePath))
            return null;
        var releases = ReleaseNotesParser.Parse(await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
        return releases.Count == 0 ? null : new NotesResult(releases, File.GetLastWriteTimeUtc(_cachePath), null);
    }
}
