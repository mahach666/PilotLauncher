using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

/// <summary>A setup package found in the installer folder: a version that can be installed without the internet.</summary>
public sealed record LocalSetup(Edition Edition, Version Version, string Path, long SizeBytes);

public static class SetupFolderScanner
{
    /// <summary>
    /// Files named <c>Pilot-&lt;edition&gt;-setup_&lt;version&gt;.zip|exe</c> directly in <paramref name="folder"/>.
    /// If a version has both, the exe wins: it needs no unzipping. Throws if the folder cannot be read.
    /// </summary>
    public static IReadOnlyList<LocalSetup> Scan(string folder)
    {
        var found = new Dictionary<(EditionId, Version), LocalSetup>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!Editions.TryParseSetupName(System.IO.Path.GetFileName(path), out var edition, out var version, out var extension))
                continue;
            var key = (edition.Id, version);
            if (extension == ".zip" && found.ContainsKey(key))
                continue;
            found[key] = new LocalSetup(edition, version, path, new FileInfo(path).Length);
        }

        return found.Values.OrderBy(s => s.Edition.Id).ThenByDescending(s => s.Version).ToList();
    }
}
