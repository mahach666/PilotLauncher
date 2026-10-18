using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Library;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Extensions;

/// <summary>Where the extensions of one version go: the <c>Development</c> folder that version's Pilot reads.</summary>
/// <param name="Directory">The real folder the launcher writes links into.</param>
/// <param name="Label">What the user sees: whose Development this is.</param>
/// <param name="RefKey">Identifies this target in the reference lists of the shared store.</param>
/// <param name="Profile">Set for an isolated account: Pilot reads the account's own profile, which must link to <paramref name="Directory"/> first.</param>
public sealed record DevelopmentTarget(string Directory, string Label, string RefKey, IsolatedProfile? Profile);

public sealed record IsolatedProfile(Edition Edition, Version Version, string UserName);

public interface IProfileLinker
{
    bool IsLinked(DevelopmentTarget target);

    /// <summary>Makes the account's Development folder a link to the target folder. Asks for elevation.</summary>
    Task LinkAsync(DevelopmentTarget target, CancellationToken ct);
}

public enum ExtensionState
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,

    /// <summary>A folder with the extension's name exists, but the launcher did not put it there.</summary>
    Foreign,

    /// <summary>The latest release could not be found (no network and nothing saved).</summary>
    Unknown,
}

public sealed record ExtensionStatus(
    ExtensionSource Source, ExtensionState State, string? InstalledTag, ExtensionRelease? Latest, string? Error);

/// <summary>Installs, updates and removes the extensions of one version by linking its Development folder to the shared store.</summary>
public sealed class ExtensionService
{
    private readonly VersionStore _store;
    private readonly ExtensionStore _extensions;
    private readonly GitHubReleaseClient _releases;
    private readonly FileDownloader _downloader;
    private readonly DiskSpace _disk;
    private readonly IProfileLinker _linker;
    private readonly string _localAppData;
    private readonly string _ownerTag;
    private readonly Func<Edition, Version, string> _accountName;
    private readonly ILauncherLog _log;

    public ExtensionService(
        VersionStore store, ExtensionStore extensions, GitHubReleaseClient releases, FileDownloader downloader, DiskSpace disk,
        IProfileLinker linker, string localAppData, string ownerTag, Func<Edition, Version, string> accountName, ILauncherLog log)
    {
        _store = store;
        _extensions = extensions;
        _releases = releases;
        _downloader = downloader;
        _disk = disk;
        _linker = linker;
        _localAppData = localAppData;
        _ownerTag = ownerTag;
        _accountName = accountName;
        _log = log;
    }

    public IReadOnlyList<ExtensionSource> Sources { get; init; } = ExtensionSource.Defaults;

    /// <summary>True when the account behind this target still has to be linked (the first install asks for elevation).</summary>
    public bool NeedsLink(DevelopmentTarget target) => target.Profile is not null && !_linker.IsLinked(target);

    /// <summary>
    /// The Development folder that "Run" of this version uses: the system installation reads the user's own profile, a
    /// copy in the store reads its isolated account's. Null when the version is not on this PC.
    /// </summary>
    public DevelopmentTarget? TargetFor(VersionEntry entry)
    {
        if (entry.System is not null)
        {
            return new DevelopmentTarget(
                Path.Combine(_localAppData, "ASCON", entry.Edition.RegistryName, "Development"),
                "установка в системе", $"sys-{_ownerTag}-{entry.Edition.Code}", null);
        }

        if (entry.Stored is null)
            return null;
        var account = _accountName(entry.Edition, entry.Version);
        return new DevelopmentTarget(_store.ProfileDevelopmentDir(account), "отдельный профиль", account,
            new IsolatedProfile(entry.Edition, entry.Version, account));
    }

    /// <summary>State of every extension in the target. Network trouble never throws: it shows up per extension.</summary>
    public async Task<IReadOnlyList<ExtensionStatus>> GetStatusAsync(DevelopmentTarget target, bool refresh, CancellationToken ct)
    {
        var result = new List<ExtensionStatus>();
        foreach (var source in Sources)
        {
            ExtensionRelease? latest = null;
            string? error = null;
            try
            {
                var answer = await _releases.GetLatestAsync(source, refresh, ct).ConfigureAwait(false);
                latest = answer.Release;
                error = answer.Error;
            }
            catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            var link = ExtensionLinks.Inspect(target.Directory, source, _extensions);
            var state = link.State switch
            {
                LinkState.Foreign => ExtensionState.Foreign,
                LinkState.Ours when latest is null => ExtensionState.UpToDate,
                LinkState.Ours => latest.IsNewerThan(link.Tag!) ? ExtensionState.UpdateAvailable : ExtensionState.UpToDate,
                _ => latest is null ? ExtensionState.Unknown : ExtensionState.NotInstalled,
            };
            result.Add(new ExtensionStatus(source, state, link.Tag, latest, error));
        }

        return result;
    }

    /// <summary>
    /// Installs (or updates to) <paramref name="release"/>. A folder the launcher did not create is only replaced when
    /// <paramref name="takeOverForeign"/> says so: it is moved aside, not deleted.
    /// </summary>
    public async Task InstallAsync(
        DevelopmentTarget target, ExtensionSource source, ExtensionRelease release, bool takeOverForeign,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var before = ExtensionLinks.Inspect(target.Directory, source, _extensions);
        if (before.State == LinkState.Foreign && !takeOverForeign)
            throw new ForeignExtensionException(source, before.Path);

        await _extensions.EnsureInstalledAsync(source, release, _downloader, _disk, _store.TempDir, progress, ct).ConfigureAwait(false);

        Directory.CreateDirectory(target.Directory);
        if (target.Profile is not null && !_linker.IsLinked(target))
        {
            progress?.Report(new InstallProgress(InstallStage.Preparing, null));
            await _linker.LinkAsync(target, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress(InstallStage.Finishing, null));
        if (before.State == LinkState.Foreign)
            MoveForeignAside(before.Path, source);
        ExtensionLinks.Remove(target.Directory, source, _extensions);
        Junction.Create(Path.Combine(target.Directory, source.FolderName), _extensions.ContentDir(source, release.Tag));
        _extensions.AddRef(source.Id, release.Tag, target.RefKey, target.Directory);
        _log.Info($"{source.DisplayName} {release.Tag} linked into {target.Directory}.");

        if (before.State == LinkState.Ours && before.Tag is { } oldTag && !string.Equals(oldTag, ExtensionStore.SafeTag(release.Tag), StringComparison.Ordinal))
            _extensions.RemoveRef(source.Id, oldTag, target.RefKey);
        _extensions.CollectUnused(source.Id);
    }

    /// <summary>Removes the link; the shared copy goes away too when nothing else uses it. Folders not made by the launcher stay.</summary>
    public Task RemoveAsync(DevelopmentTarget target, ExtensionSource source, CancellationToken ct)
    {
        var before = ExtensionLinks.Inspect(target.Directory, source, _extensions);
        switch (before.State)
        {
            case LinkState.None:
                return Task.CompletedTask;
            case LinkState.Foreign:
                throw new ForeignExtensionException(source, before.Path);
        }

        ExtensionLinks.Remove(target.Directory, source, _extensions);
        _extensions.RemoveRef(source.Id, before.Tag!, target.RefKey);
        _extensions.CollectUnused(source.Id);
        _log.Info($"{source.DisplayName} removed from {target.Directory}.");
        return Task.CompletedTask;
    }

    /// <summary>The account of a deleted version is gone: drop its Development folder and everything it referred to.</summary>
    public void ForgetAccount(string accountName)
    {
        var dir = _store.ProfileDir(accountName);
        if (Directory.Exists(dir))
            Junction.DeleteTree(dir); // junctions inside are removed as links, never entered
        _extensions.RemoveRefsOf(accountName);
        foreach (var source in Sources)
            _extensions.CollectUnused(source.Id);
    }

    private void MoveForeignAside(string path, ExtensionSource source)
    {
        if (Junction.IsLink(path))
        {
            Junction.Remove(path);
            return;
        }

        var aside = Path.Combine(_extensions.Root, "_replaced", $"{source.FolderName}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
        Directory.Move(path, aside);
        _log.Info($"The existing folder {path} was moved to {aside}.");
    }
}

public sealed class ForeignExtensionException : IOException
{
    public ExtensionSource Extension { get; }

    public ForeignExtensionException(ExtensionSource source, string path)
        : base($"Папка {path} уже есть и создана не лаунчером. Лаунчер её не трогает: удалите её сами или разрешите заменить.")
    {
        Extension = source;
    }
}
