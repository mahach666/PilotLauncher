using System.Net;
using System.Net.Http.Headers;

namespace PilotLauncher.Core.Install;

public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>Downloads a file to <c>destination</c> through <c>destination.part</c>, resuming a previous partial download.</summary>
public sealed class FileDownloader
{
    private readonly HttpClient _http;

    public FileDownloader(HttpClient http) => _http = http;

    public async Task DownloadAsync(Uri uri, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // the part file is not a prefix of the current file (or already complete) - start over
            File.Delete(partPath);
            await DownloadAsync(uri, destination, progress, ct).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();

        var resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;
        long? total = resumed
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            var received = existing;
            progress?.Report(new DownloadProgress(received, total));
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        var finalLength = new FileInfo(partPath).Length;
        if (total is { } expected && finalLength != expected)
            throw new IOException($"Download of {uri} ended at {finalLength} of {expected} bytes. Run it again to resume.");

        File.Move(partPath, destination, overwrite: true);
    }
}
