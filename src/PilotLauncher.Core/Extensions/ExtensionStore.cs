using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Extensions;

/// <summary>
/// One shared copy of every downloaded extension release: <c>&lt;root&gt;\&lt;id&gt;\&lt;tag&gt;\&lt;FolderName&gt;\...</c>.
/// Versions do not get copies, their <c>Development</c> folders hold junctions to the folder inside. A release stays while
/// at least one link refers to it (<c>refs\&lt;key&gt;.json</c> next to the folder), so the 540 MB of DynamoPilot exist once.
/// </summary>
public sealed class ExtensionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;
    private readonly ILauncherLog _log;

    public ExtensionStore(string root, ILauncherLog log)
    {
        _root = root;
        _log = log;
    }

    public string Root => _root;

    public static string SafeTag(string tag) =>
        string.Concat(tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public string TagDir(string id, string tag) => Path.Combine(_root, id, SafeTag(tag));

    public string ContentDir(ExtensionSource source, string tag) => Path.Combine(TagDir(source.Id, tag), source.FolderName);

    public bool IsInstalled(ExtensionSource source, string tag) => File.Exists(Path.Combine(TagDir(source.Id, tag), "package.json"));

    /// <summary>Id and tag of a folder inside the store (a junction target), or false when the path is somewhere else.</summary>
    public bool TryParseContentPath(string? path, out string id, out string tag)
    {
        id = tag = "";
        if (string.IsNullOrEmpty(path))
            return false;
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0].StartsWith('_'))
            return false;
        id = parts[0];
        tag = parts[1];
        return true;
    }

    /// <summary>Makes sure the release is in the store: downloads it, checks its SHA-256 and unpacks it. Nothing is kept but the unpacked folder.</summary>
    public async Task EnsureInstalledAsync(
        ExtensionSource source, ExtensionRelease release, FileDownloader downloader, DiskSpace disk, string tempDir,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (IsInstalled(source, release.Tag))
            return;

        Directory.CreateDirectory(_root);
        disk.EnsureFree(_root, release.Size * 2 + (64L << 20));
        var work = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "asset.zip");
            var downloadProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p =>
                progress.Report(new InstallProgress(InstallStage.Downloading, p.Total is > 0 ? (double)p.Received / p.Total.Value : null)));
            _log.Info($"Downloading extension {source.DisplayName} {release.Tag}");
            await downloader.DownloadAsync(release.DownloadUri, zip, downloadProgress, ct).ConfigureAwait(false);

            await VerifyDigestAsync(zip, release, ct).ConfigureAwait(false);
            progress?.Report(new InstallProgress(InstallStage.ExtractingSetup, null));
            await Task.Run(() => Unpack(zip, source, release, disk), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException ex)
            {
                // cleanup must not hide the result; the next start removes old temporary folders
                _log.Warn($"Could not remove temporary folder {work}: {ex.Message}");
            }
        }
    }

    private static async Task VerifyDigestAsync(string zip, ExtensionRelease release, CancellationToken ct)
    {
        if (release.Sha256 is null)
            return; // shortcut: releases published before GitHub reported digests cannot be verified; HTTPS is all there is
        await using var stream = File.OpenRead(zip);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Контрольная сумма {release.AssetName} не совпала: получено {actual}, ожидалось {release.Sha256}.");
    }

    private void Unpack(string zipPath, ExtensionSource source, ExtensionRelease release, DiskSpace disk)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var layout = ZipLayout.Detect(archive);
        if (layout.RootFolder is { } rooted && !string.Equals(rooted, source.FolderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"В архиве {release.AssetName} папка «{rooted}», а Pilot ждёт «{source.FolderName}».");
        disk.EnsureFree(_root, layout.UnpackedBytes + (64L << 20));

        var tagDir = TagDir(source.Id, release.Tag);
        var staging = tagDir + ".partial";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            archive.ExtractToDirectory(layout.RootFolder is null ? Path.Combine(staging, source.FolderName) : staging);
            if (!Directory.Exists(Path.Combine(staging, source.FolderName)))
                throw new InvalidDataException($"После распаковки {release.AssetName} нет папки {source.FolderName}.");
            Directory.CreateDirectory(Path.Combine(staging, "refs"));
            File.WriteAllText(Path.Combine(staging, "package.json"), JsonSerializer.Serialize(
                new { release.Tag, source.Repo, release.AssetName, release.Sha256, InstalledUtc = DateTime.UtcNow }, Json));
            if (Directory.Exists(tagDir))
                Directory.Delete(tagDir, recursive: true); // a half-finished earlier attempt without package.json
            Directory.Move(staging, tagDir);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }

        _log.Info($"Unpacked extension {source.DisplayName} {release.Tag} ({layout.UnpackedBytes / (1 << 20)} MB).");
    }

    // --- who uses a release ------------------------------------------------------------------------------------------

    public void AddRef(string id, string tag, string key, string developmentDir)
    {
        var refs = Path.Combine(TagDir(id, tag), "refs");
        Directory.CreateDirectory(refs);
        File.WriteAllText(RefPath(id, tag, key), JsonSerializer.Serialize(new { Development = developmentDir, Utc = DateTime.UtcNow }, Json));
    }

    public void RemoveRef(string id, string tag, string key)
    {
        var path = RefPath(id, tag, key);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Forgets every reference with this key, in every release (the account or profile is gone).</summary>
    public void RemoveRefsOf(string key)
    {
        if (!Directory.Exists(_root))
            return;
        // only the refs folders are looked at: the unpacked extensions beside them hold thousands of files
        foreach (var idDir in Directory.EnumerateDirectories(_root).Where(d => !Path.GetFileName(d).StartsWith('_')))
        {
            foreach (var tagDir in Directory.EnumerateDirectories(idDir))
            {
                var path = Path.Combine(tagDir, "refs", SafeTag(key) + ".json");
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    /// <summary>Deletes the releases of this extension that no link uses any more. Files still in use are left for the next time.</summary>
    public IReadOnlyList<string> CollectUnused(string id)
    {
        var removed = new List<string>();
        var idDir = Path.Combine(_root, id);
        if (!Directory.Exists(idDir))
            return removed;
        foreach (var tagDir in Directory.EnumerateDirectories(idDir).ToList())
        {
            var refs = Path.Combine(tagDir, "refs");
            if (!File.Exists(Path.Combine(tagDir, "package.json")) || (Directory.Exists(refs) && Directory.EnumerateFiles(refs).Any()))
                continue;
            try
            {
                Directory.Delete(tagDir, recursive: true);
                removed.Add(Path.GetFileName(tagDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not remove the unused extension release {tagDir} (a Pilot may still use it): {ex.Message}");
            }
        }

        return removed;
    }

    public IReadOnlyList<string> InstalledTags(string id)
    {
        var idDir = Path.Combine(_root, id);
        return Directory.Exists(idDir)
            ? Directory.EnumerateDirectories(idDir).Where(d => File.Exists(Path.Combine(d, "package.json"))).Select(d => Path.GetFileName(d)).ToList()
            : [];
    }

    private string RefPath(string id, string tag, string key) => Path.Combine(TagDir(id, tag), "refs", SafeTag(key) + ".json");
}

/// <summary>Shape of an extension zip: with one top-level folder (as the repositories publish them) or flat.</summary>
public readonly record struct ZipLayout(string? RootFolder, long UnpackedBytes)
{
    public static ZipLayout Detect(ZipArchive archive)
    {
        var tops = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/').Split('/', 2)[0])
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasLooseFile = archive.Entries.Any(e => e.Name.Length > 0 && !e.FullName.Replace('\\', '/').Contains('/'));
        return new ZipLayout(tops.Count == 1 && !hasLooseFile ? tops[0] : null, archive.Entries.Sum(e => e.Length));
    }
}
