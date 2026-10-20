using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Logging;

namespace PilotLauncher.Core.Sharing;

/// <summary>
/// The launcher's half of "shared folders": the default folder needs nothing, any other folder is opened to the isolated accounts
/// by the elevated copy (one Windows prompt for all changes made at once).
/// </summary>
public sealed class SharedFolderService
{
    private readonly string _ownerTag;
    private readonly CredentialStore _credentials;
    private readonly string _dataDir;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _runElevated;
    private readonly ILauncherLog _log;

    public SharedFolderService(
        string ownerTag, CredentialStore credentials, string dataDir,
        Func<IReadOnlyList<string>, CancellationToken, Task> runElevated, ILauncherLog log)
    {
        _ownerTag = ownerTag;
        _credentials = credentials;
        _dataDir = dataDir;
        _runElevated = runElevated;
        _log = log;
    }

    public static string DefaultFolder => SharedFolders.DefaultFolder;

    public static bool IsDefault(string path) => SharedFolders.SamePath(path, DefaultFolder);

    /// <summary>Creates the offered folder (no administrator rights needed) and returns its path.</summary>
    public string CreateDefault()
    {
        Directory.CreateDirectory(DefaultFolder);
        return DefaultFolder;
    }

    /// <summary>True when applying these changes asks Windows for administrator rights (some folder is not the default one).</summary>
    public static bool NeedsElevation(IEnumerable<string> added, IEnumerable<string> removed) =>
        added.Concat(removed).Any(p => !SharedFolders.SamePath(p, DefaultFolder));

    /// <exception cref="InvalidOperationException">A folder is not allowed; nothing was changed.</exception>
    public async Task ApplyAsync(IReadOnlyList<string> added, IReadOnlyList<string> removed, CancellationToken ct)
    {
        var customAdded = added.Where(p => !SharedFolders.SamePath(p, DefaultFolder)).Select(SharedFolders.Normalize).ToList();
        var customRemoved = removed.Where(p => !SharedFolders.SamePath(p, DefaultFolder)).Select(SharedFolders.Normalize).ToList();
        foreach (var folder in customAdded)
        {
            if (SharedFolders.Validate(folder, _dataDir) is { } problem)
                throw new InvalidOperationException($"{folder}: {problem}");
        }

        if (customAdded.Count == 0 && customRemoved.Count == 0)
            return;

        // the elevated copy works out the data folder to protect on its own, it is not passed on
        var arguments = new List<string> { "apply-shares", "--owner", _ownerTag };
        // the accounts that exist now join the group; accounts made later join it when they are created
        var users = _credentials.Names().Where(n => SharedFolders.BelongsTo(n, _ownerTag)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (users.Count > 0)
            arguments.AddRange(["--users", string.Join(',', users)]);
        if (customAdded.Count > 0)
            arguments.AddRange(["--add", string.Join('|', customAdded)]);
        if (customRemoved.Count > 0)
            arguments.AddRange(["--remove", string.Join('|', customRemoved)]);

        _log.Info($"Shared folders: opening {customAdded.Count}, closing {customRemoved.Count}; asking for elevation.");
        await _runElevated(arguments, ct).ConfigureAwait(false);
    }
}
