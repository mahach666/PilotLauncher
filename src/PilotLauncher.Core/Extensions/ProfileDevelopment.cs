using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PilotLauncher.Core.Accounts;
using PilotLauncher.Core.Catalog;
using PilotLauncher.Core.Logging;
using PilotLauncher.Core.Storage;

namespace PilotLauncher.Core.Extensions;

public interface IProfileLocator
{
    /// <summary>The profile folder of the account, or null when it never signed in.</summary>
    string? Find(string userName);

    /// <summary>Like <see cref="Find"/>, but creates the profile when there is none.</summary>
    string GetOrCreate(string userName);
}

/// <summary>Profile folders through the ProfileList registry key and userenv's CreateProfile.</summary>
public sealed class ProfileLocator : IProfileLocator
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const int HresultAlreadyExists = unchecked((int)0x800700B7);

    public string? Find(string userName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{SidOf(userName)}");
        return key?.GetValue("ProfileImagePath", null, RegistryValueOptions.None) is string path ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    public string GetOrCreate(string userName)
    {
        if (Find(userName) is { } existing)
            return existing;
        var path = new StringBuilder(260);
        var hr = CreateProfile(SidOf(userName), userName, path, (uint)path.Capacity);
        if (hr != 0 && hr != HresultAlreadyExists)
            throw new Win32Exception(hr & 0xFFFF, $"Could not create the profile of '{userName}' (HRESULT 0x{hr:X8}).");
        return Find(userName) ?? throw new DirectoryNotFoundException($"The profile of '{userName}' was created but is not listed.");
    }

    private static string SidOf(string userName) => new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateProfile(string userSid, string userName, StringBuilder profilePath, uint size);
}

/// <summary>
/// The elevated half: links <c>&lt;profile&gt;\AppData\Local\ASCON\&lt;product&gt;\Development</c> of an isolated account to a
/// folder in the launcher's store, so the launcher (running as a normal user) can manage that account's extensions
/// without the account's profile being open to it. Only launcher accounts, known products and one fixed target are accepted.
/// </summary>
public sealed class ProfileDevelopmentAdmin
{
    private readonly IProfileLocator _profiles;
    private readonly Action<string, string> _grantModify;

    /// <param name="grantModify">Gives the account change rights on a folder (directory, account); replaced in tests.</param>
    public ProfileDevelopmentAdmin(IProfileLocator profiles, Action<string, string>? grantModify = null)
    {
        _profiles = profiles;
        _grantModify = grantModify ?? GrantModify;
    }

    public static string DevelopmentIn(string profile, Edition edition) =>
        Path.Combine(profile, "AppData", "Local", "ASCON", edition.RegistryName, "Development");

    public void Link(string userName, string product, string target, string storeRoot)
    {
        if (!AccountNaming.IsLauncherAccount(userName))
            throw new AdminOperationException($"'{userName}' is not an account created by PilotLauncher; refusing to touch it.");
        var edition = Editions.All.FirstOrDefault(e => string.Equals(e.RegistryName, product, StringComparison.Ordinal))
                      ?? throw new AdminOperationException($"Unknown product '{product}'.");
        var expected = Path.GetFullPath(Path.Combine(storeRoot, ".profiles", userName, "Development"));
        if (!string.Equals(Path.GetFullPath(target), expected, StringComparison.OrdinalIgnoreCase))
            throw new AdminOperationException($"The target must be {expected}.");
        if (!Directory.Exists(target) || Junction.IsLink(target))
            throw new AdminOperationException($"The target folder {target} does not exist or is a link.");

        var development = DevelopmentIn(_profiles.GetOrCreate(userName), edition);
        Directory.CreateDirectory(Path.GetDirectoryName(development)!);

        if (Junction.IsLink(development))
        {
            if (string.Equals(Path.GetFullPath(Junction.Target(development) ?? ""), expected, StringComparison.OrdinalIgnoreCase))
            {
                _grantModify(target, userName);
                return;
            }

            Junction.Remove(development);
        }
        else if (Directory.Exists(development))
        {
            // the account used Pilot before: whatever it has in Development moves to the shared folder
            foreach (var entry in Directory.EnumerateFileSystemEntries(development).ToList())
            {
                var name = Path.GetFileName(entry);
                var destination = Path.Combine(target, name);
                if (Path.Exists(destination))
                    throw new AdminOperationException($"«{name}» already exists in {target}; move it away and try again.");
                if (Directory.Exists(entry))
                    Directory.Move(entry, destination);
                else
                    File.Move(entry, destination);
            }

            Directory.Delete(development, recursive: false);
        }

        Junction.Create(development, target);
        _grantModify(target, userName);
    }

    /// <summary>Removes the Development links of an account before its profile is deleted, so the delete cannot reach through them.</summary>
    public void UnlinkAll(string userName)
    {
        if (!AccountNaming.IsLauncherAccount(userName))
            throw new AdminOperationException($"'{userName}' is not an account created by PilotLauncher; refusing to touch it.");
        if (_profiles.Find(userName) is not { } profile)
            return;
        var ascon = Path.Combine(profile, "AppData", "Local", "ASCON");
        if (!Directory.Exists(ascon))
            return;
        foreach (var product in Directory.EnumerateDirectories(ascon))
        {
            var development = Path.Combine(product, "Development");
            if (Junction.IsLink(development))
                Junction.Remove(development);
        }
    }

    private static void GrantModify(string directory, string userName)
    {
        var account = new NTAccount(Environment.MachineName, userName);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

/// <summary>The launcher's half: asks the elevated copy to link an isolated account's profile to the store, once per account.</summary>
public sealed class ElevatedProfileLinker : IProfileLinker
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly VersionStore _store;
    private readonly IAccountProvisioner _accounts;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _runElevated;
    private readonly ILauncherLog _log;

    public ElevatedProfileLinker(VersionStore store, IAccountProvisioner accounts, ILauncherLog log,
        Func<IReadOnlyList<string>, CancellationToken, Task>? runElevated = null)
    {
        _store = store;
        _accounts = accounts;
        _log = log;
        _runElevated = runElevated ?? ElevatedRunner.RunAsync;
    }

    public bool IsLinked(DevelopmentTarget target) =>
        target.Profile is { } p && File.Exists(MarkerPath(p)) && ReadMarker(MarkerPath(p)) == (p.UserName, p.Edition.RegistryName);

    public async Task LinkAsync(DevelopmentTarget target, CancellationToken ct)
    {
        var profile = target.Profile ?? throw new InvalidOperationException("This target is not an isolated profile.");
        // the account must exist (and have a profile) before its Development folder can be linked
        await _accounts.EnsureAsync(profile.UserName, AccountNaming.FullName(profile.Edition, profile.Version), ct).ConfigureAwait(false);
        _log.Info($"Linking the Development folder of {profile.UserName} to {target.Directory}; asking for elevation.");
        await _runElevated(
            ["link-development", "--user", profile.UserName, "--product", profile.Edition.RegistryName,
             "--target", target.Directory, "--store", _store.Root], ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath(profile))!);
        File.WriteAllText(MarkerPath(profile), JsonSerializer.Serialize(
            new { User = profile.UserName, Product = profile.Edition.RegistryName, LinkedUtc = DateTime.UtcNow }, Json));
    }

    private string MarkerPath(IsolatedProfile profile) => Path.Combine(_store.ProfileDir(profile.UserName), "link.json");

    private static (string?, string?) ReadMarker(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return (doc.RootElement.GetProperty("User").GetString(), doc.RootElement.GetProperty("Product").GetString());
    }
}
