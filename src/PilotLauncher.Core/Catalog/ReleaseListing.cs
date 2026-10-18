using System.Globalization;
using System.Text.RegularExpressions;

namespace PilotLauncher.Core.Catalog;

/// <param name="SizeBytes">Size from the directory listing; the server rounds it to K/M/G, so it is approximate.</param>
public sealed record ReleaseEntry(Edition Edition, Version Version, string FileName, long SizeBytes, DateTime? Published);

/// <summary>Parses the nginx autoindex page at <c>https://pilot.ascon.ru/release/</c>.</summary>
public static class ReleaseListingParser
{
    private static readonly Regex Row = new(
        @"<a href=""(?<file>[^""]+)"">[^<]*</a>\s+(?<date>\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(?<size>\S+)",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<ReleaseEntry> Parse(string html)
    {
        var result = new List<ReleaseEntry>();
        foreach (Match m in Row.Matches(html))
        {
            // hrefs may be percent-encoded; only plain ASCII setup names are interesting
            var file = Uri.UnescapeDataString(m.Groups["file"].Value);
            if (!Editions.TryParseSetupFile(file, out var edition, out var version))
                continue;
            DateTime? published = DateTime.TryParseExact(m.Groups["date"].Value, "dd-MMM-yyyy HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            result.Add(new ReleaseEntry(edition, version, file, ParseSize(m.Groups["size"].Value), published));
        }

        return result
            .OrderBy(e => e.Edition.Id)
            .ThenByDescending(e => e.Version)
            .ToList();
    }

    /// <summary>"176M" gives 184549376 bytes, "3260" gives 3260, "-" gives 0.</summary>
    public static long ParseSize(string text)
    {
        if (text == "-" || text.Length == 0)
            return 0;
        var multiplier = 1L;
        var digits = text;
        switch (char.ToUpperInvariant(text[^1]))
        {
            case 'K': multiplier = 1L << 10; digits = text[..^1]; break;
            case 'M': multiplier = 1L << 20; digits = text[..^1]; break;
            case 'G': multiplier = 1L << 30; digits = text[..^1]; break;
        }

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? (long)(n * multiplier)
            : throw new FormatException($"Cannot parse size '{text}'.");
    }
}
