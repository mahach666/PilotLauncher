using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Storage;

/// <summary>
/// Where the launcher keeps its own data (per Windows user) and where unpacked Pilot versions live (per machine).
/// </summary>
public sealed class LauncherPaths
{
    public const string PortableFlagFile = "portable.flag";

    /// <summary>Per-user data: settings, credentials of the isolated accounts, catalog and release notes caches, logs.</summary>
    public string DataDir { get; }

    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string CredentialsFile => Path.Combine(DataDir, "credentials.json");
    public string CatalogCacheFile => Path.Combine(DataDir, "release-listing.html");
    public string NotesCacheFile => Path.Combine(DataDir, "release-notes.html");
    public string BetaCatalogCacheFile => Path.Combine(DataDir, "beta-listing.html");
    public string BetaNotesCacheFile => Path.Combine(DataDir, "beta-notes.html");
    public string LogsDir => Path.Combine(DataDir, "logs");

    public LauncherPaths(string dataDir) => DataDir = dataDir;

    /// <summary>A <c>portable.flag</c> file next to the executable keeps all data in <c>data\</c> next to it.</summary>
    public static LauncherPaths Resolve(string exeDirectory)
    {
        if (File.Exists(Path.Combine(exeDirectory, PortableFlagFile)))
            return new LauncherPaths(Path.Combine(exeDirectory, "data"));
        return new LauncherPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilotLauncher"));
    }
}

/// <summary>Layout of the version store. Created by one user, readable by the isolated accounts.</summary>
public sealed class VersionStore
{
    /// <summary>
    /// %ProgramData% gives every local user read access and lets only the creator change what it created,
    /// so isolated accounts can run the files but cannot alter them.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PilotLauncher", "versions");

    public string Root { get; }

    public VersionStore(string root) => Root = root;

    public string TempDir => Path.Combine(Root, ".tmp");

    /// <summary>One shared copy of every downloaded extension release; versions only link to it.</summary>
    public string ExtensionsDir => Path.Combine(Root, ".extensions");

    /// <summary>Per isolated account: the real folder behind the account's <c>Development</c> link (see <see cref="ProfileDevelopmentDir"/>).</summary>
    public string ProfilesDir => Path.Combine(Root, ".profiles");

    public string ProfileDir(string accountName) => Path.Combine(ProfilesDir, accountName);

    public string ProfileDevelopmentDir(string accountName) => Path.Combine(ProfileDir(accountName), "Development");

    public string VersionDir(Edition edition, Version version) => Path.Combine(Root, edition.FolderName, version.ToString());

    public string AppDir(Edition edition, Version version) => Path.Combine(VersionDir(edition, version), "app");

    public string ManifestPath(Edition edition, Version version) => Path.Combine(VersionDir(edition, version), VersionManifest.FileName);

    /// <summary>Versions that have a manifest. Half-installed folders (no manifest) are not listed.</summary>
    public IEnumerable<StoredVersion> Enumerate()
    {
        if (!Directory.Exists(Root))
            yield break;
        foreach (var edition in Editions.All)
        {
            var editionDir = Path.Combine(Root, edition.FolderName);
            if (!Directory.Exists(editionDir))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(editionDir))
            {
                if (!Version.TryParse(Path.GetFileName(dir), out var version))
                    continue;
                var manifestPath = Path.Combine(dir, VersionManifest.FileName);
                if (!File.Exists(manifestPath))
                    continue;
                yield return new StoredVersion(edition, version, VersionManifest.Load(manifestPath), dir);
            }
        }
    }

    public StoredVersion? Find(Edition edition, Version version) =>
        Enumerate().FirstOrDefault(v => v.Edition.Id == edition.Id && v.Version == version);

    /// <summary>
    /// Removes leftovers of interrupted installs. Only folders untouched for <paramref name="olderThan"/> go, so an install
    /// running in another launcher on the same store is not disturbed.
    /// </summary>
    public void CleanTemp(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempDir))
            return;
        foreach (var dir in Directory.EnumerateDirectories(TempDir))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > olderThan)
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The first file of the folder that another program holds: a running Pilot has its exe and libraries open. Asking for write
    /// access (nothing is written) is what tells it; reading, or renaming the folder, is allowed while a program runs from it.
    /// Null when nothing is in use.
    /// </summary>
    public static string? FindFileInUse(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing violation, lock violation
            {
                return file;
            }
            catch (UnauthorizedAccessException)
            {
                // a read-only file or no right to write: nothing can be said about it, it is not counted
            }
        }

        return null;
    }

    /// <summary>Where a deleted version waits for its files to be removed (see <see cref="CleanLeftovers"/>).</summary>
    public string TrashDir => Path.Combine(Root, ".trash");

    /// <summary>
    /// Removes what deleting and installing leave behind when they are interrupted: versions that were moved out of the store but
    /// not erased yet, version folders without a manifest (a version folder is only ever put in place complete, so one without
    /// a manifest is the rest of a delete that stopped half way), and staging folders of installs older than
    /// <paramref name="stagingOlderThan"/>. A folder that is still in use is left for the next time.
    /// </summary>
    public void CleanLeftovers(TimeSpan stagingOlderThan)
    {
        if (Directory.Exists(TrashDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(TrashDir))
                TryDelete(dir);
        }

        if (!Directory.Exists(Root))
            return;
        foreach (var edition in Editions.All)
        {
            var editionDir = Path.Combine(Root, edition.FolderName);
            if (!Directory.Exists(editionDir))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(editionDir))
            {
                var name = Path.GetFileName(dir);
                var broken = Version.TryParse(name, out _) && !File.Exists(Path.Combine(dir, VersionManifest.FileName));
                var staging = name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                              && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > stagingOlderThan;
                if (broken || staging)
                    TryDelete(dir);
            }
        }
    }

    /// <summary>
    /// Makes room for installing a version: a complete one in the way is an error, the broken rest of an earlier delete is removed.
    /// </summary>
    public void EnsureAbsent(Edition edition, Version version)
    {
        var dir = VersionDir(edition, version);
        if (!Directory.Exists(dir))
            return;
        if (File.Exists(ManifestPath(edition, version)))
            throw new InvalidOperationException($"{edition.DisplayName} {version} is already in the store.");
        Directory.Delete(dir, recursive: true);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use or locked for a moment (a scanner, a client of that version): it is tried again at the next start
        }
    }
}

public sealed record StoredVersion(Edition Edition, Version Version, VersionManifest Manifest, string Directory)
{
    public string AppDir => Path.Combine(Directory, "app");
    public string ExePath => Path.Combine(AppDir, Manifest.ExeRelativePath);
}
