using System.Diagnostics;
using System.Text.RegularExpressions;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Launch;

/// <summary>Where Pilot keeps its settings and logs inside a profile: <c>&lt;profile&gt;\AppData\Roaming\ASCON\&lt;product&gt;</c>.</summary>
public static class ProfileData
{
    public static string RoamingFolder(string profile, Edition edition) => Path.Combine(profile, "AppData", "Roaming", "ASCON", edition.RegistryName);
}

/// <summary>
/// Gives the main Windows user access to the Pilot data folder of an isolated account (logs, settings), so the launcher can open
/// it. The isolated account owns its profile and may change the rights on it itself, so the change is made by a short helper
/// process run under that account: no administrator rights and no Windows prompt are involved.
/// </summary>
public interface IProfileAccess
{
    /// <returns>True when the folder exists and the main user may change what is in it.</returns>
    Task<bool> GrantOwnerAsync(string account, string password, Edition edition, string ownerSid, CancellationToken ct);
}

public sealed class ProfileAccessGranter : IProfileAccess
{
    private static readonly Regex Sid = new(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex Product = new(@"^[A-Za-z0-9][A-Za-z0-9 -]*$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The command line of the helper: it creates the folder if the profile has none yet and gives the main user change rights
    /// on it and on everything inside. <c>%APPDATA%</c> is the one of the account the helper runs under. Only a SID and a product
    /// folder name of a known shape are put into it.
    /// </summary>
    public static string BuildArguments(Edition edition, string ownerSid)
    {
        if (!Sid.IsMatch(ownerSid))
            throw new ArgumentException($"'{ownerSid}' is not a SID.", nameof(ownerSid));
        if (!Product.IsMatch(edition.RegistryName))
            throw new ArgumentException($"'{edition.RegistryName}' is not a plain folder name.", nameof(edition));
        var folder = $@"%APPDATA%\ASCON\{edition.RegistryName}";
        // /s makes cmd drop the outer quotes and keep the inner ones as they are
        return $"/d /s /c \"md \"{folder}\" 2>nul & icacls \"{folder}\" /grant \"*{ownerSid}:(OI)(CI)M\" >nul\"";
    }

    public async Task<bool> GrantOwnerAsync(string account, string password, Edition edition, string ownerSid, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), BuildArguments(edition, ownerSid))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
            UserName = account,
            Domain = ".",
            PasswordInClearText = password,
            LoadUserProfile = true, // the profile is made here when this is the first sign-in of the account
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The helper did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The helper of {account} did not finish in {Timeout.TotalSeconds:0} seconds.");
        }

        return process.ExitCode == 0;
    }
}
