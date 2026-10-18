using System.Net;
using System.Net.Http.Headers;

namespace PilotLauncher.Core.Tests;

internal static class TestSupport
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PilotLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteQuietly(string dir)
    {
        // junctions made by the extension tests would make a plain recursive delete fail
        Core.Extensions.Junction.DeleteTree(dir);
    }

    /// <summary>Setup executables the integration tests unpack; absent on machines that do not have them.</summary>
    public static readonly (string Path, string Edition, string Version)[] LocalSetups =
    [
        (@"C:\Users\HP\Downloads\Pilot-ICE-Enterprise-setup_26.9.0.61368\Pilot-ICE-Enterprise-setup.exe", "IceEnterprise", "26.9.0.61368"),
        (@"C:\Users\HP\Downloads\Pilot-ICE-Enterprise-setup_25.12.0.57162\Pilot-ICE-Enterprise-setup.exe", "IceEnterprise", "25.12.0.57162"),
    ];
}

/// <summary>Serves bytes (or a text body) and honours <c>Range: bytes=n-</c>; records what was asked for.</summary>
internal sealed class FakeFileHandler : HttpMessageHandler
{
    private readonly Func<byte[]> _content;
    public List<string?> RangeRequests { get; } = [];
    public bool IgnoreRange { get; init; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; init; }

    public FakeFileHandler(byte[] content) => _content = () => content;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Override?.Invoke(request) is { } custom)
            return Task.FromResult(custom);

        var data = _content();
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        RangeRequests.Add(range is null ? null : $"{range.From}-");
        if (range?.From is { } from && !IgnoreRange)
        {
            if (from >= data.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data, (int)from, data.Length - (int)from),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}
