using System.Diagnostics;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Install;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Launch;

/// <summary>A started client the launcher keeps an eye on (for the title label and the running counter).</summary>
public sealed class RunningClient
{
    public RunningClient(Process process, Edition edition, Version version, bool isolated)
    {
        Process = process;
        Edition = edition;
        Version = version;
        Isolated = isolated;
        process.EnableRaisingEvents = true;
    }

    public Process Process { get; }
    public Edition Edition { get; }
    public Version Version { get; }
    public bool Isolated { get; }
    /// <summary>Appended to the client's window titles: just the version, the title already names the product.</summary>
    public string Label => Version.ToString();
}

public interface IClientProcessStarter
{
    /// <param name="account">Run under this local account with this password; null runs as the current user.</param>
    Process Start(string exePath, string workingDirectory, (string UserName, string Password)? account);
}

public sealed class ClientProcessStarter : IClientProcessStarter
{
    public Process Start(string exePath, string workingDirectory, (string UserName, string Password)? account)
    {
        var info = new ProcessStartInfo(exePath) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        if (account is { } a)
        {
            info.UserName = a.UserName;
            info.Domain = ".";
            info.PasswordInClearText = a.Password;
            info.LoadUserProfile = true;
            // Never touch info.Environment here: when it is untouched, Windows builds the environment from the target
            // user's profile. A copy of ours would hand the new process our APPDATA and break the isolation.
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exePath}.");
    }
}

public sealed class LaunchService
{
    private static readonly TimeSpan TitleInterval = TimeSpan.FromSeconds(1);

    private readonly IAccountProvisioner _accounts;
    private readonly CredentialStore _credentials;
    private readonly IClientProcessStarter _starter;
    private readonly IWindowApi _windows;
    private readonly string _ownerTag;
    private readonly ILauncherLog _log;
    private readonly IProfileAccess? _access;
    private readonly string? _ownerSid;

    /// <param name="access">Gives the main user access to the Pilot data of an isolated profile; null means it is not done.</param>
    /// <param name="ownerSid">SID of the Windows user running the launcher, the one that gets the access.</param>
    public LaunchService(
        IAccountProvisioner accounts,
        CredentialStore credentials,
        IClientProcessStarter starter,
        IWindowApi windows,
        string ownerTag,
        ILauncherLog log,
        IProfileAccess? access = null,
        string? ownerSid = null)
    {
        _accounts = accounts;
        _credentials = credentials;
        _starter = starter;
        _windows = windows;
        _ownerTag = ownerTag;
        _log = log;
        _access = access;
        _ownerSid = ownerSid;
    }

    public string AccountFor(Edition edition, Version version) => AccountNaming.UserName(_ownerTag, edition, version);

    /// <summary>Runs the system-wide installation as the current user: the regular Pilot, with the regular profile.</summary>
    public RunningClient LaunchSystem(SystemInstall system)
    {
        _log.Info($"Launching system {system.Edition.DisplayName} {system.Version} as the current user.");
        var process = _starter.Start(system.ExePath, system.InstallDir, null);
        return Track(new RunningClient(process, system.Edition, system.Version, isolated: false));
    }

    /// <summary>Runs a stored version under its own account, creating or repairing the account first (may ask for elevation).</summary>
    public async Task<RunningClient> LaunchIsolatedAsync(StoredVersion version, CancellationToken ct)
    {
        var user = AccountFor(version.Edition, version.Version);
        await _accounts.EnsureAsync(user, AccountNaming.FullName(version.Edition, version.Version), ct).ConfigureAwait(false);
        var password = _credentials.Get(user) ?? throw new InvalidOperationException($"No stored password for {user}.");
        await GrantOwnerAccessOnceAsync(version, user, password, ct).ConfigureAwait(false);
        _log.Info($"Launching {version.Edition.DisplayName} {version.Version} as {user}.");
        var process = _starter.Start(version.ExePath, Path.GetDirectoryName(version.ExePath)!, (user, password));
        return Track(new RunningClient(process, version.Edition, version.Version, isolated: true));
    }

    /// <summary>
    /// The profile of the account exists from its first sign-in, so the main user gets access to its Pilot data folder at that
    /// point, once: a note in the folder of the version says it is done. A failure is only logged (the version still starts) and
    /// is tried again at the next start.
    /// </summary>
    private async Task GrantOwnerAccessOnceAsync(StoredVersion version, string user, string password, CancellationToken ct)
    {
        if (_access is null || _ownerSid is null)
            return;
        var note = Path.Combine(version.Directory, OwnerAccessNote);
        var expected = $"{user} {_ownerSid}";
        try
        {
            if (File.Exists(note) && File.ReadAllText(note) == expected)
                return;
            if (await _access.GrantOwnerAsync(user, password, version.Edition, _ownerSid, ct).ConfigureAwait(false))
            {
                File.WriteAllText(note, expected);
                _log.Info($"The main user may open the Pilot data of {user}.");
            }
            else
            {
                _log.Warn($"Could not give the main user access to the Pilot data of {user}.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not give the main user access to the Pilot data of {user}: {ex.Message}");
        }
    }

    private const string OwnerAccessNote = "owner-access.txt";

    private RunningClient Track(RunningClient client)
    {
        var processId = client.Process.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await TitleWatcher.WatchAsync(processId, client.Label, _windows, () => client.Process.HasExited, TitleInterval, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // best effort: a client without a decorated title still works
                _log.Warn($"Title label for {client.Label} stopped: {ex.Message}");
            }
        });
        return client;
    }
}
