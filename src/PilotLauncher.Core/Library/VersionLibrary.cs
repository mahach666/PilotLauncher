using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Library;

/// <summary>One line in the version list: what is known about an edition + version from the three sources.</summary>
public sealed record VersionEntry(
    Edition Edition,
    Version Version,
    ReleaseEntry? Release,
    StoredVersion? Stored,
    SystemInstall? System,
    LocalSetup? Setup = null)
{
    public bool IsInSystem => System is not null;
    public bool IsInStore => Stored is not null;

    /// <summary>Can be started without downloading anything.</summary>
    public bool IsReady => IsInSystem || IsInStore;

    public bool CanDownload => !IsReady && Release is not null;

    /// <summary>A setup file from the installer folder can be unpacked, no internet needed.</summary>
    public bool CanInstallFromFile => !IsReady && Setup is not null;
}

/// <summary>
/// A copy was deleted but the isolated Windows user made for it (and its profile) is still there: Windows keeps a profile open
/// while anything uses it, or the Windows prompt was declined. Nothing is lost; the user is reused if the version is installed again.
/// </summary>
public sealed class ProfileNotRemovedException : Exception
{
    public ProfileNotRemovedException(string account, Exception inner)
        : base(Describe(account, inner), inner) => Account = account;

    public string Account { get; }

    private static string Describe(string account, Exception inner) =>
        inner is OperationCanceledException
            ? $"Копия удалена, но данные версии остались: вы отклонили запрос Windows, поэтому временный пользователь Windows {account} и его профиль не удалены."
            : $"Копия удалена, но данные версии остались: временный пользователь Windows {account} и его профиль не удалены. {inner.Message} " +
              "Если окон Pilot этой версии нет, Windows, скорее всего, ещё держит профиль открытым; помогает перезагрузка.";
}

/// <summary>Merges the download center listing, the store and the system installations.</summary>
public sealed class VersionLibrary
{
    private readonly VersionStore _store;
    private readonly SystemInstallDetector _detector;
    private readonly VersionInstaller _installer;
    private readonly IAccountProvisioner _accounts;
    private readonly Func<Edition, Version, string> _accountName;
    private readonly ILauncherLog _log;

    public VersionLibrary(
        VersionStore store,
        SystemInstallDetector detector,
        VersionInstaller installer,
        IAccountProvisioner accounts,
        Func<Edition, Version, string> accountName,
        ILauncherLog log)
    {
        _store = store;
        _detector = detector;
        _installer = installer;
        _accounts = accounts;
        _accountName = accountName;
        _log = log;
    }

    public VersionStore Store => _store;

    /// <summary>Newest first. Catalog entries may be empty when the download center was never reachable.</summary>
    public IReadOnlyList<VersionEntry> Build(IEnumerable<ReleaseEntry> catalog, IEnumerable<LocalSetup>? localSetups = null)
    {
        var releases = catalog.ToDictionary(r => (r.Edition.Id, r.Version));
        var setups = (localSetups ?? []).ToDictionary(s => (s.Edition.Id, s.Version));
        var stored = _store.Enumerate().ToDictionary(s => (s.Edition.Id, s.Version));
        var system = _detector.Detect().ToDictionary(s => (s.Edition.Id, s.Version));

        return releases.Keys.Concat(setups.Keys).Concat(stored.Keys).Concat(system.Keys)
            .Distinct()
            .Select(key => new VersionEntry(
                Editions.Get(key.Id),
                key.Version,
                releases.GetValueOrDefault(key),
                stored.GetValueOrDefault(key),
                system.GetValueOrDefault(key),
                setups.GetValueOrDefault(key)))
            .OrderBy(e => e.Edition.Id)
            .ThenByDescending(e => e.Version)
            .ToList();
    }

    /// <summary>
    /// The version the "what changed" view compares <paramref name="selected"/> with by default: the next older one,
    /// else the next newer one. It does not depend on the order of the list, which puts favorites first.
    /// </summary>
    public static Version? ComparisonPartner(IEnumerable<Version> versions, Version selected)
    {
        var others = versions.Where(v => v != selected).ToList();
        return others.Where(v => v < selected).DefaultIfEmpty().Max() ?? others.DefaultIfEmpty().Min();
    }

    public Task DownloadAsync(VersionEntry entry, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        _installer.InstallAsync(
            entry.Release ?? throw new InvalidOperationException($"{entry.Edition.DisplayName} {entry.Version} is not in the download center."),
            progress, ct);

    /// <summary>Unpacks the entry's setup file from the installer folder; the file stays where it is.</summary>
    public Task InstallFromSetupFolderAsync(VersionEntry entry, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        _installer.InstallFromFileAsync(
            (entry.Setup ?? throw new InvalidOperationException($"{entry.Edition.DisplayName} {entry.Version} has no setup file in the installer folder.")).Path,
            entry.Edition, entry.Version, progress, ct);

    /// <summary>Edition and version of a setup file; null when neither the name nor the file itself tells.</summary>
    public SetupFile? IdentifySetup(string path) => SetupFileInspector.Identify(path, _store.TempDir);

    /// <summary>Unpacks a setup file the user picked.</summary>
    public Task InstallFromFileAsync(SetupFile file, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        _installer.InstallFromFileAsync(file.Path, file.Edition, file.Version, progress, ct);

    public Task DownloadToFolderAsync(VersionEntry entry, string folder, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        _installer.DownloadToFolderAsync(
            entry.Release ?? throw new InvalidOperationException($"{entry.Edition.DisplayName} {entry.Version} is not in the download center."),
            folder, progress, ct);

    public Task CopySystemAsync(VersionEntry entry, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        _installer.CopySystemInstallAsync(
            entry.System ?? throw new InvalidOperationException("This version is not installed in the system."),
            progress, ct);

    /// <summary>
    /// Removes the stored copy. The folder goes first: if a client still runs from it, nothing else has been touched.
    /// The system installation is never modified. A folder that is already gone (an earlier attempt removed it and failed
    /// later) is not an error, so the rest can still be done.
    /// </summary>
    /// <exception cref="ProfileNotRemovedException">The copy is deleted, but its Windows user or profile could not be.</exception>
    public async Task DeleteStoredAsync(VersionEntry entry, bool deleteAccount, CancellationToken ct)
    {
        var stored = entry.Stored ?? throw new InvalidOperationException("This version is not in the store.");
        try
        {
            if (Directory.Exists(stored.Directory))
                Directory.Delete(stored.Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not delete {stored.Directory}. Close the Pilot windows of this version and try again. ({ex.Message})", ex);
        }

        _log.Info($"Deleted {entry.Edition.DisplayName} {entry.Version} from the store.");
        if (!deleteAccount)
            return;
        var account = _accountName(entry.Edition, entry.Version);
        try
        {
            await _accounts.DeleteAsync(account, deleteProfile: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdminOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            _log.Error($"The copy is deleted, but the account {account} could not be", ex);
            throw new ProfileNotRemovedException(account, ex);
        }
    }

    /// <summary>Total size of a stored version, for the details panel.</summary>
    public static long SizeOnDisk(StoredVersion stored) =>
        Directory.EnumerateFiles(stored.Directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
