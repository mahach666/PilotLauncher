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
}

public sealed record StoredVersion(Edition Edition, Version Version, VersionManifest Manifest, string Directory)
{
    public string AppDir => Path.Combine(Directory, "app");
    public string ExePath => Path.Combine(AppDir, Manifest.ExeRelativePath);
}
