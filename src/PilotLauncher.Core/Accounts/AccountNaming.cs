using System.Security.Cryptography;
using System.Text;
using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Accounts;

/// <summary>Names of the local Windows accounts that isolate one version each.</summary>
public static class AccountNaming
{
    /// <summary>Windows (SAM) limit for local user names.</summary>
    public const int MaxLength = 20;

    /// <summary>
    /// Four hex characters derived from the SID of the Windows user running the launcher. Two Windows users sharing one PC
    /// get separate accounts (and passwords) for the same version instead of resetting each other's.
    /// </summary>
    public static string OwnerTag(string userSid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..4].ToLowerInvariant();

    /// <summary>For example <c>plab12E26x13x62219</c> for ICE Enterprise 26.13.0.62219.</summary>
    public static string UserName(string ownerTag, Edition edition, Version version)
    {
        var name = $"pl{ownerTag}{edition.Code}{version.Major}x{version.Minor}x{version.Revision}";
        if (name.Length > MaxLength)
            throw new ArgumentException($"Account name '{name}' for {edition.DisplayName} {version} is longer than {MaxLength} characters.");
        return name;
    }

    /// <summary>The elevated helper only touches accounts of this shape, never arbitrary user names.</summary>
    public static bool IsLauncherAccount(string userName) =>
        userName.Length <= MaxLength && LauncherAccount.IsMatch(userName);

    private static readonly System.Text.RegularExpressions.Regex LauncherAccount =
        new(@"^pl[0-9a-f]{4}[EIBC]\d{1,3}x\d{1,3}x\d{1,7}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string FullName(Edition edition, Version version) => $"Pilot {edition.DisplayName} {version}";

    public const string Description = "PilotLauncher isolated profile";
}
